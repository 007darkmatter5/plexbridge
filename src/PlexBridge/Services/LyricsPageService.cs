using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

public sealed class LyricsMapInput
{
    public string PlexPrefix { get; set; } = "";
    public string ContainerPrefix { get; set; } = "";
}

public sealed class LyricsSettingsInput
{
    public int ServerId { get; set; }
    public bool AutoCheck { get; set; }
    public int IntervalHours { get; set; } = 12;
    public List<LyricsMapInput> Maps { get; set; } = [];
}

public sealed record LyricsServerForm(
    int ServerId,
    int Slot,
    string Name,
    string BaseUrl,
    bool HasToken,
    bool AutoCheck,
    int IntervalHours,
    DateTimeOffset? LastAutoCheckUtc,
    IReadOnlyList<LyricsPathMap> Maps,
    IReadOnlyList<string> LibraryLocations);

public sealed class LyricsPageService(
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexReadClient libraries,
    PlexLyricsClient music,
    IAdminGate admin,
    LyricsJobs jobs)
{
    public async Task<LyricsJobSnapshot> StartScanAsync(int serverId, string sectionKey, string? artistKey, string? albumKey, string libraryName, CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        return jobs.StartScan(userId, serverId, sectionKey, artistKey, albumKey, libraryName);
    }

    public async Task<LyricsJobSnapshot?> LatestAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        return jobs.Latest(userId);
    }

    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        jobs.Cancel(userId, jobId);
    }

    public async Task SelectAsync(string jobId, IReadOnlyCollection<string> ratingKeys, bool selected, CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        jobs.Select(userId, jobId, ratingKeys, selected);
    }

    public async Task<LyricsJobSnapshot?> StartApplyAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        return jobs.StartApply(userId, jobId);
    }

    public async Task<string> RefreshAlbumAsync(string jobId, string albumKey, CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        return await jobs.RefreshAlbumAsync(userId, jobId, albumKey, cancellationToken);
    }

    public async Task<IReadOnlyList<LyricsServerForm>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var servers = await db.PlexServers.AsNoTracking().Include(server => server.Libraries).OrderBy(server => server.Slot).ThenBy(server => server.Id).ToListAsync(cancellationToken);
        var settings = await db.LyricsSettings.AsNoTracking().ToListAsync(cancellationToken);
        return servers.Select(server => Form(server, settings.FirstOrDefault(item => item.PlexServerConnectionId == server.Id))).ToList();
    }

    public async Task SaveAsync(LyricsSettingsInput input, CancellationToken cancellationToken = default)
    {
        if (input.IntervalHours is < 1 or > 168)
            throw new PlexBridgeException("Check newly added tracks every 1 to 168 hours.");
        var maps = input.Maps.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList();
        if (!LyricsPaths.TryNormalize(maps, out var normalized, out var error))
            throw new PlexBridgeException(error);

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var server = await db.PlexServers.FirstOrDefaultAsync(item => item.Id == input.ServerId, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        var row = await db.LyricsSettings.FirstOrDefaultAsync(item => item.PlexServerConnectionId == server.Id, cancellationToken);
        var turningOn = input.AutoCheck && (row is null || !row.AutoCheckEnabled);
        if (row is null)
        {
            row = new LyricsSetting { PlexServerConnectionId = server.Id };
            db.LyricsSettings.Add(row);
        }

        row.PathMapsJson = LyricsPaths.ToJson(normalized);
        row.AutoCheckEnabled = input.AutoCheck;
        row.AutoCheckIntervalHours = input.IntervalHours;
        if (turningOn)
            row.LastAutoCheckUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlexSectionInfo>> MusicLibrariesAsync(int serverId, CancellationToken cancellationToken = default)
    {
        var (uri, token) = await OpenAsync(serverId, cancellationToken);
        var sections = await libraries.GetSectionsAsync(uri, token, cancellationToken);
        return sections.Where(section => section.Type.Equals("artist", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<IReadOnlyList<PlexCatalogItem>> ArtistsAsync(int serverId, string sectionKey, CancellationToken cancellationToken = default)
    {
        var (uri, token) = await OpenAsync(serverId, cancellationToken);
        return await music.ListAsync(uri, token, sectionKey, "8", null, null, false, null, null, cancellationToken);
    }

    public async Task<IReadOnlyList<PlexCatalogItem>> AlbumsAsync(int serverId, string sectionKey, string? artistKey, CancellationToken cancellationToken = default)
    {
        var (uri, token) = await OpenAsync(serverId, cancellationToken);
        return await music.ListAsync(uri, token, sectionKey, "9", artistKey, null, false, null, null, cancellationToken);
    }

    private async Task<(Uri Uri, string Token)> OpenAsync(int serverId, CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var server = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == serverId, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        if (!PlexServerUrl.TryNormalize(server.BaseUrl, out var normalized, out _))
            throw new PlexBridgeException("The saved server URL is not valid.");
        if (string.IsNullOrEmpty(server.ProtectedToken))
            throw new PlexBridgeException("A Plex token is required.");
        try
        {
            return (new Uri(normalized), protector.Unprotect(server.ProtectedToken));
        }
        catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
        {
            throw new PlexBridgeException("The saved token can't be read. Enter it again.");
        }
    }

    private static LyricsServerForm Form(PlexServerConnection server, LyricsSetting? setting)
    {
        var locations = server.Libraries
            .Where(library => library.Type.Equals("artist", StringComparison.OrdinalIgnoreCase))
            .SelectMany(library => ReadLocations(library.LocationsJson))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new LyricsServerForm(
            server.Id,
            server.Slot,
            server.Name,
            server.BaseUrl,
            !string.IsNullOrEmpty(server.ProtectedToken),
            setting?.AutoCheckEnabled ?? false,
            setting?.AutoCheckIntervalHours is > 0 and <= 168 ? setting.AutoCheckIntervalHours : 12,
            setting?.LastAutoCheckUtc,
            LyricsPaths.FromJson(setting?.PathMapsJson),
            locations);
    }

    private static IEnumerable<string> ReadLocations(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
