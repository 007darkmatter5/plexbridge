using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

/// <summary>
/// When a server opts in, looks up lyrics for tracks Plex added after that opt-in.
/// It does not backfill the library and it does not replace lyrics that are already there.
/// </summary>
public sealed class LyricsAutoCheckService(
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexReadClient libraries,
    LyricsWorkflow workflow,
    LyricsJobs jobs,
    LyricsSyncQueue sync,
    ILogger<LyricsAutoCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Lyrics auto-check could not finish ({ExceptionType}).", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var due = await db.LyricsSettings.AsNoTracking()
            .Where(item => item.AutoCheckEnabled)
            .ToListAsync(cancellationToken);
        foreach (var setting in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interval = TimeSpan.FromHours(setting.AutoCheckIntervalHours is >= 1 and <= 168 ? setting.AutoCheckIntervalHours : 12);
            if (setting.LastAutoCheckUtc is DateTimeOffset last && DateTimeOffset.UtcNow - last < interval)
                continue;
            if (jobs.IsServerBusy(setting.PlexServerConnectionId))
                continue;
            await CheckServerAsync(setting, cancellationToken);
        }
    }

    private async Task CheckServerAsync(LyricsSetting setting, CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var server = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == setting.PlexServerConnectionId, cancellationToken);
        if (server is null || string.IsNullOrEmpty(server.ProtectedToken))
            return;
        if (!PlexServerUrl.TryNormalize(server.BaseUrl, out var normalized, out _))
            return;

        string token;
        try
        {
            token = protector.Unprotect(server.ProtectedToken);
        }
        catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
        {
            logger.LogWarning("Lyrics auto-check skipped server {ServerId} because the saved token could not be read.", server.Id);
            return;
        }

        var since = setting.LastAutoCheckUtc ?? DateTimeOffset.UtcNow;
        var mount = await db.LibraryMounts.AsNoTracking().FirstOrDefaultAsync(item => item.PlexServerConnectionId == server.Id, cancellationToken);
        var maps = PlexBridge.Libraries.LibraryMountStore.ReadActiveMaps(mount, setting);
        if (maps.Count == 0)
        {
            await TouchAsync(setting.PlexServerConnectionId, advance: true, cancellationToken);
            return;
        }

        var uri = new Uri(normalized);
        IReadOnlyList<PlexSectionInfo> sections;
        try
        {
            sections = await libraries.GetSectionsAsync(uri, token, cancellationToken);
        }
        catch (Exception exception) when (exception is PlexRequestException or PlexBridgeException)
        {
            logger.LogInformation("Lyrics auto-check could not read libraries for server {ServerId}.", server.Id);
            return;
        }

        var wrote = 0;
        var failed = false;
        foreach (var section in sections.Where(item => item.Type.Equals("artist", StringComparison.OrdinalIgnoreCase)))
        {
            List<LyricsTrackRow> rows;
            try
            {
                rows = await workflow.ScanAsync(
                    uri,
                    token,
                    section.Key,
                    null,
                    null,
                    maps,
                    since.ToUnixTimeSeconds(),
                    newestFirst: true,
                    progress: null,
                    canWrite: null,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is PlexRequestException or PlexBridgeException)
            {
                failed = true;
                logger.LogInformation("Lyrics auto-check could not read one library on server {ServerId}.", server.Id);
                continue;
            }

            if (rows.Any(row => row.Status is LyricsStatus.LookupFailed or LyricsStatus.NotReachable or LyricsStatus.NotWritable))
                failed = true;
            var ready = rows.Where(row => row.CanSelect && row.Selected).ToList();
            if (ready.Count == 0)
                continue;
            try
            {
                await workflow.PublishAsync(uri, token, ready, null, cancellationToken);
                await sync.RecordAsync(server.Id, ready, cancellationToken);
                wrote += ready.Count(row => row.ApplyResult?.StartsWith("Wrote ", StringComparison.Ordinal) == true);
            }
            catch (Exception exception) when (exception is PlexRequestException or PlexBridgeException)
            {
                failed = true;
            }
        }

        logger.LogInformation("Lyrics auto-check for server {ServerId} wrote {Count} files.", server.Id, wrote);
        await TouchAsync(setting.PlexServerConnectionId, advance: !failed, cancellationToken);
    }

    private async Task TouchAsync(int serverId, bool advance, CancellationToken cancellationToken)
    {
        if (!advance)
            return;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var row = await db.LyricsSettings.FirstOrDefaultAsync(item => item.PlexServerConnectionId == serverId, cancellationToken);
        if (row is null)
            return;
        row.LastAutoCheckUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
