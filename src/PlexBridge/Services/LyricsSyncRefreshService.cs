using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

/// <summary>
/// Refreshes Plex for lyrics files that were written here and have not shown up yet.
/// One refresh per album each pass. Pending rows are in the database, so a restart continues the wait.
/// </summary>
public sealed class LyricsSyncRefreshService(
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexLyricsClient plex,
    TimeProvider clock,
    ILogger<LyricsSyncRefreshService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Lyrics sync refresh could not finish ({ExceptionType}).", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RefreshDueAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RefreshDueCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RefreshNowAsync(int itemId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshOneAsync(itemId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshDueCoreAsync(CancellationToken cancellationToken)
    {
        List<int> dueIds;
        var now = clock.GetUtcNow();
        await using (var db = await databases.CreateDbContextAsync(cancellationToken))
        {
            var cutoff = now.AddDays(-1);
            var finished = await db.LyricsSyncItems
                .Where(item => item.Status == LyricsSyncStatus.Done)
                .ToListAsync(cancellationToken);
            var stale = finished.Where(item => item.WrittenUtc < cutoff).ToList();
            if (stale.Count > 0)
            {
                db.LyricsSyncItems.RemoveRange(stale);
                await db.SaveChangesAsync(cancellationToken);
            }

            var waiting = await db.LyricsSyncItems.AsNoTracking()
                .Where(item => item.Status == LyricsSyncStatus.Waiting)
                .ToListAsync(cancellationToken);
            dueIds = waiting
                .Where(item => item.NextRefreshUtc <= now || item.DeadlineUtc <= now)
                .Select(item => item.Id)
                .ToList();
        }

        if (dueIds.Count == 0)
            return;

        await using var writeDb = await databases.CreateDbContextAsync(cancellationToken);
        var due = await writeDb.LyricsSyncItems.Where(item => dueIds.Contains(item.Id)).ToListAsync(cancellationToken);
        foreach (var serverGroup in due.GroupBy(item => item.PlexServerConnectionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var setting = await writeDb.LyricsSettings.AsNoTracking()
                .FirstOrDefaultAsync(item => item.PlexServerConnectionId == serverGroup.Key, cancellationToken);
            var interval = LyricsSyncQueue.NormalizeInterval(setting?.SyncIntervalMinutes);
            if (await TryOpenAsync(writeDb, serverGroup.Key, cancellationToken) is not { } opened)
            {
                foreach (var row in serverGroup)
                    Schedule(row, now, interval);
                continue;
            }

            var groups = serverGroup.GroupBy(item => GroupKey(item)).ToList();
            logger.LogInformation(
                "Lyrics sync refresh for server {ServerId}: {Albums} albums, {Tracks} tracks.",
                serverGroup.Key,
                groups.Count,
                serverGroup.Count());
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = group.ToList();
                var refreshKey = RefreshKey(rows);
                try
                {
                    await plex.RefreshAsync(opened.Uri, opened.Token, refreshKey, cancellationToken);
                }
                catch (Exception exception) when (exception is PlexRequestException or PlexBridgeException)
                {
                    logger.LogInformation(
                        "Lyrics sync refresh could not reach server {ServerId} ({ExceptionType}).",
                        serverGroup.Key,
                        exception.GetType().Name);
                    foreach (var row in rows)
                        Schedule(row, now, interval);
                    continue;
                }

                foreach (var row in rows)
                {
                    PlexCatalogItem? track = null;
                    try
                    {
                        track = await plex.GetTrackAsync(opened.Uri, opened.Token, row.RatingKey, cancellationToken);
                    }
                    catch (Exception exception) when (exception is PlexRequestException or PlexBridgeException)
                    {
                        logger.LogInformation(
                            "Lyrics sync refresh could not read a track on server {ServerId} ({ExceptionType}).",
                            serverGroup.Key,
                            exception.GetType().Name);
                    }

                    if (track?.HasLyricsStream == true)
                        row.Status = LyricsSyncStatus.Done;
                    else
                        Schedule(row, now, interval);
                }
            }
        }

        await writeDb.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> RefreshOneAsync(int itemId, CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var row = await db.LyricsSyncItems.FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken)
            ?? throw new PlexBridgeException("That lyrics file is not waiting anymore.");
        if (row.Status == LyricsSyncStatus.Done)
            return "Plex shows a lyrics stream.";

        var opened = await TryOpenAsync(db, row.PlexServerConnectionId, cancellationToken)
            ?? throw new PlexBridgeException("That server has no token yet.");
        var now = clock.GetUtcNow();
        var setting = await db.LyricsSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.PlexServerConnectionId == row.PlexServerConnectionId, cancellationToken);
        var interval = LyricsSyncQueue.NormalizeInterval(setting?.SyncIntervalMinutes);
        try
        {
            await plex.RefreshAsync(opened.Uri, opened.Token, RefreshKey([row]), cancellationToken);
            var track = await plex.GetTrackAsync(opened.Uri, opened.Token, row.RatingKey, cancellationToken);
            if (track?.HasLyricsStream == true)
            {
                row.Status = LyricsSyncStatus.Done;
                await db.SaveChangesAsync(cancellationToken);
                return "Plex shows a lyrics stream.";
            }
        }
        catch (Exception exception) when (exception is PlexRequestException or PlexBridgeException)
        {
            logger.LogInformation(
                "Lyrics sync refresh could not reach server {ServerId} ({ExceptionType}).",
                row.PlexServerConnectionId,
                exception.GetType().Name);
            throw new PlexBridgeException("The metadata refresh did not complete.");
        }

        Schedule(row, now, interval);
        await db.SaveChangesAsync(cancellationToken);
        return row.Status == LyricsSyncStatus.Missed
            ? "Not picked up by Plex yet."
            : "Waiting for sync.";
    }

    private async Task<(Uri Uri, string Token)?> TryOpenAsync(ApplicationDbContext db, int serverId, CancellationToken cancellationToken)
    {
        var server = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == serverId, cancellationToken);
        if (server is null || string.IsNullOrEmpty(server.ProtectedToken))
            return null;
        if (!PlexServerUrl.TryNormalize(server.BaseUrl, out var normalized, out _))
            return null;
        try
        {
            return (new Uri(normalized), protector.Unprotect(server.ProtectedToken));
        }
        catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
        {
            logger.LogWarning("Lyrics sync refresh skipped server {ServerId} because the saved token could not be read.", serverId);
            return null;
        }
    }

    private static void Schedule(LyricsSyncItem row, DateTimeOffset now, int interval)
    {
        row.AttemptCount++;
        if (now >= row.DeadlineUtc)
            row.Status = LyricsSyncStatus.Missed;
        else
        {
            row.Status = LyricsSyncStatus.Waiting;
            row.NextRefreshUtc = now.AddMinutes(interval);
        }
    }

    private static string GroupKey(LyricsSyncItem item)
        => IsKey(item.AlbumKey) ? "album:" + item.AlbumKey : "track:" + item.RatingKey;

    private static string RefreshKey(IReadOnlyList<LyricsSyncItem> rows)
    {
        var album = rows.Select(row => row.AlbumKey).FirstOrDefault(IsKey);
        return album ?? rows[0].RatingKey;
    }

    private static bool IsKey(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 12 && value.All(char.IsDigit);
}
