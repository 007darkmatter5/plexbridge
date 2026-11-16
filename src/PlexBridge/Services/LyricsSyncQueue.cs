using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Lyrics;

namespace PlexBridge.Services;

/// <summary>
/// Remembers lyrics files that Plex has not picked up yet.
/// The Plex token is never written onto these rows.
/// </summary>
public sealed class LyricsSyncQueue(IDbContextFactory<ApplicationDbContext> databases, TimeProvider clock)
{
    public const int DefaultIntervalMinutes = 2;
    public const int DefaultWindowMinutes = 30;

    public async Task RecordAsync(int serverId, IReadOnlyList<LyricsTrackRow> rows, CancellationToken cancellationToken)
    {
        var wrote = rows.Where(row => row.ApplyResult?.StartsWith("Wrote ", StringComparison.Ordinal) == true).ToList();
        if (wrote.Count == 0)
            return;

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var setting = await db.LyricsSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.PlexServerConnectionId == serverId, cancellationToken);
        var interval = NormalizeInterval(setting?.SyncIntervalMinutes);
        var window = NormalizeWindow(setting?.SyncWindowMinutes, interval);
        var now = clock.GetUtcNow();
        foreach (var row in wrote)
        {
            if (!IsKey(row.RatingKey))
                continue;
            var existing = await db.LyricsSyncItems.FirstOrDefaultAsync(
                item => item.PlexServerConnectionId == serverId && item.RatingKey == row.RatingKey,
                cancellationToken);
            if (row.PlexShowsLyrics == true)
            {
                if (existing is not null)
                    existing.Status = LyricsSyncStatus.Done;
                continue;
            }

            if (existing is null)
            {
                existing = new LyricsSyncItem
                {
                    PlexServerConnectionId = serverId,
                    RatingKey = row.RatingKey
                };
                db.LyricsSyncItems.Add(existing);
            }

            existing.AlbumKey = IsKey(row.AlbumKey) ? row.AlbumKey : "";
            existing.Artist = Clip(row.Artist, 200);
            existing.Album = Clip(row.Album, 200);
            existing.Title = Clip(string.IsNullOrWhiteSpace(row.Title) ? "Track" : row.Title, 200);
            existing.FileName = Clip(row.FileName, 240);
            existing.Status = LyricsSyncStatus.Waiting;
            existing.WrittenUtc = now;
            existing.DeadlineUtc = now.AddMinutes(window);
            existing.NextRefreshUtc = now.AddMinutes(interval);
            existing.AttemptCount = Math.Max(existing.AttemptCount, 1);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LyricsSyncRow>> ListOpenAsync(CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var items = await db.LyricsSyncItems.AsNoTracking()
            .Where(item => item.Status == LyricsSyncStatus.Waiting || item.Status == LyricsSyncStatus.Missed)
            .ToListAsync(cancellationToken);
        items = items.OrderBy(item => item.WrittenUtc).ToList();
        if (items.Count == 0)
            return [];
        var servers = await db.PlexServers.AsNoTracking().ToListAsync(cancellationToken);
        return items.Select(item =>
        {
            var server = servers.FirstOrDefault(candidate => candidate.Id == item.PlexServerConnectionId);
            return new LyricsSyncRow(
                item.Id,
                item.PlexServerConnectionId,
                server?.Slot ?? 0,
                server?.Name ?? "Server",
                item.RatingKey,
                item.AlbumKey,
                item.Artist,
                item.Album,
                item.Title,
                item.FileName,
                item.Status,
                item.NextRefreshUtc,
                item.DeadlineUtc);
        }).ToList();
    }

    public static int NormalizeInterval(int? minutes)
        => minutes is >= 1 and <= 60 ? minutes.Value : DefaultIntervalMinutes;

    public static int NormalizeWindow(int? minutes, int interval)
    {
        var window = minutes is >= 2 and <= 240 ? minutes.Value : DefaultWindowMinutes;
        return window <= interval ? interval + 1 : window;
    }

    private static bool IsKey(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 12 && value.All(char.IsDigit);

    private static string Clip(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max];
    }
}
