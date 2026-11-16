using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Libraries;
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
    public string? SectionKey { get; set; }
    public string? SectionTitle { get; set; }
    public string? ContainerFolder { get; set; }
    public bool UseManualMaps { get; set; }
    public List<LyricsMapInput> Maps { get; set; } = [];
    public List<LyricsLocationMatch> Matches { get; set; } = [];
}

public sealed record LyricsLibraryReport(
    string SectionKey,
    string SectionTitle,
    string ContainerFolder,
    IReadOnlyList<LyricsLocationMatch> Matches,
    IReadOnlyList<LyricsPathMap> Maps);

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
    string? SectionKey,
    string? SectionTitle,
    string? ContainerFolder,
    bool UseManualMaps,
    IReadOnlyList<LyricsLocationMatch> Matches);

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
        var servers = await db.PlexServers.AsNoTracking().OrderBy(server => server.Slot).ThenBy(server => server.Id).ToListAsync(cancellationToken);
        var settings = await db.LyricsSettings.AsNoTracking().ToListAsync(cancellationToken);
        var mounts = await db.LibraryMounts.AsNoTracking().ToListAsync(cancellationToken);
        return servers.Select(server => Form(
            server,
            settings.FirstOrDefault(item => item.PlexServerConnectionId == server.Id),
            mounts.FirstOrDefault(item => item.PlexServerConnectionId == server.Id))).ToList();
    }

    public async Task SaveAsync(LyricsSettingsInput input, CancellationToken cancellationToken = default)
    {
        if (input.IntervalHours is < 1 or > 168)
            throw new PlexBridgeException("Check newly added tracks every 1 to 168 hours.");
        var maps = input.Maps.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList();
        if (!LyricsPaths.TryNormalize(maps, out var normalized, out var error))
            throw new PlexBridgeException(error);
        if (input.UseManualMaps && normalized.Count == 0)
            throw new PlexBridgeException("Add a Plex prefix and a container prefix, or turn off manual prefixes.");
        var section = CleanSection(input.SectionKey);
        var container = CleanContainer(input.ContainerFolder);

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var row = await LoadOrCreateAsync(db, input.ServerId, cancellationToken);
        var mount = await db.LibraryMounts.FirstOrDefaultAsync(item => item.PlexServerConnectionId == input.ServerId, cancellationToken);
        var turningOn = input.AutoCheck && !row.AutoCheckEnabled;
        row.PathMapsJson = LyricsPaths.ToJson(normalized);
        row.MusicSectionKey = section;
        row.MusicSectionTitle = CleanTitle(input.SectionTitle);
        row.ContainerFolder = container;
        row.UseManualMaps = input.UseManualMaps;
        row.LocationMatchesJson = input.UseManualMaps || input.Matches.Count == 0
            ? null
            : LyricsLibraryMapper.MatchesToJson(input.Matches);
        ApplySchedule(row, input.AutoCheck, input.IntervalHours, turningOn);
        var replaceSections = input.UseManualMaps || (!string.IsNullOrEmpty(section) && input.Matches.Count > 0);
        var sections = input.UseManualMaps || string.IsNullOrEmpty(section) || input.Matches.Count == 0
            ? null
            : LibraryMountStore.MergeSection(mount?.SectionMatchesJson, MusicSection(section, row.MusicSectionTitle, input.Matches));
        await LibraryMountStore.UpsertAsync(db, input.ServerId, container, input.UseManualMaps, row.PathMapsJson, sections, replaceSections, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LyricsLibraryReport> MapLibraryAsync(int serverId, string? sectionKey, string? containerFolder, CancellationToken cancellationToken = default)
    {
        var section = CleanSection(sectionKey) ?? throw new PlexBridgeException("Choose a music library.");
        var container = CleanContainer(LibraryPathMapper.ResolveContainer(containerFolder))
            ?? throw new PlexBridgeException("The container path has to be a full path, such as /libraries.");

        var (uri, token) = await OpenAsync(serverId, cancellationToken);
        var sections = await libraries.GetSectionsAsync(uri, token, cancellationToken);
        var library = sections.FirstOrDefault(item => item.Key == section && item.Type.Equals("artist", StringComparison.OrdinalIgnoreCase))
            ?? throw new PlexBridgeException("That music library was not found.");
        if (library.Locations.Count == 0)
            throw new PlexBridgeException("Plex did not report a folder for that library.");

        var samples = new List<string>();
        var start = 0;
        for (var page = 0; page < LyricsLibraryMapper.MaxPages; page++)
        {
            var batch = await music.ReadTrackPageAsync(uri, token, section, start, cancellationToken);
            foreach (var item in batch.Items)
            {
                if (item.Type.Equals("track", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.FilePath))
                    samples.Add(item.FilePath);
            }

            if (batch.Items.Count < music.TrackPageSize)
                break;
            start += batch.Items.Count;
            if (batch.Total is int total && start >= total)
                break;
            if (LibraryPathMapper.HasEnoughSamples(library.Locations, samples))
                break;
        }

        if (!LibraryPathMapper.TryMatch(library.Locations, container, samples, Directory.Exists, File.Exists, out var matches, out var maps, out var error, LibraryPathMapper.ListEntries))
            throw new PlexBridgeException(error);

        return new LyricsLibraryReport(
            library.Key,
            library.Title,
            container,
            matches.Select(match => new LyricsLocationMatch(match.PlexLocation, match.ContainerPrefix, match.Status, match.SamplesChecked, match.SamplesFound)).ToList(),
            maps.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList());
    }

    public async Task<LyricsLibraryReport> SaveLibraryAsync(LyricsSettingsInput input, CancellationToken cancellationToken = default)
    {
        if (input.IntervalHours is < 1 or > 168)
            throw new PlexBridgeException("Check newly added tracks every 1 to 168 hours.");
        var report = await MapLibraryAsync(input.ServerId, input.SectionKey, input.ContainerFolder, cancellationToken);

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var row = await LoadOrCreateAsync(db, input.ServerId, cancellationToken);
        var mount = await db.LibraryMounts.FirstOrDefaultAsync(item => item.PlexServerConnectionId == input.ServerId, cancellationToken);
        var turningOn = input.AutoCheck && !row.AutoCheckEnabled;
        var existing = LibraryMountStore.ReadMaps(mount, row)
            .Select(map => new LibraryPathMap(map.PlexPrefix, map.ContainerPrefix))
            .ToList();
        var checkedPrefixes = new List<string>();
        foreach (var match in report.Matches)
        {
            if (LyricsPaths.TryNormalizePlexPrefix(match.PlexLocation, out var prefix, out _))
                checkedPrefixes.Add(prefix);
        }

        var merged = report.Maps.Count == 0
            ? existing
            : LibraryPathMapper.MergeMaps(
                existing,
                checkedPrefixes,
                report.Maps.Select(map => new LibraryPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList());
        if (report.Maps.Count > 0)
            row.PathMapsJson = LyricsPaths.ToJson(merged.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList());
        row.MusicSectionKey = report.SectionKey;
        row.MusicSectionTitle = report.SectionTitle;
        row.ContainerFolder = report.ContainerFolder;
        row.UseManualMaps = false;
        row.LocationMatchesJson = LyricsLibraryMapper.MatchesToJson(report.Matches);
        ApplySchedule(row, input.AutoCheck, input.IntervalHours, turningOn);
        await LibraryMountStore.UpsertAsync(
            db,
            input.ServerId,
            report.ContainerFolder,
            false,
            row.PathMapsJson,
            LibraryMountStore.MergeSection(mount?.SectionMatchesJson, MusicSection(report.SectionKey, report.SectionTitle, report.Matches)),
            true,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return report;
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

    private async Task<LyricsSetting> LoadOrCreateAsync(ApplicationDbContext db, int serverId, CancellationToken cancellationToken)
    {
        var server = await db.PlexServers.FirstOrDefaultAsync(item => item.Id == serverId, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        var row = await db.LyricsSettings.FirstOrDefaultAsync(item => item.PlexServerConnectionId == server.Id, cancellationToken);
        if (row is not null)
            return row;
        row = new LyricsSetting { PlexServerConnectionId = server.Id };
        db.LyricsSettings.Add(row);
        return row;
    }

    private static void ApplySchedule(LyricsSetting row, bool autoCheck, int intervalHours, bool turningOn)
    {
        row.AutoCheckEnabled = autoCheck;
        row.AutoCheckIntervalHours = intervalHours;
        if (turningOn)
            row.LastAutoCheckUtc = DateTimeOffset.UtcNow;
    }

    private static string? CleanSection(string? sectionKey)
    {
        if (string.IsNullOrWhiteSpace(sectionKey))
            return null;
        var key = sectionKey.Trim();
        if (key.Length is 0 or > 10 || !key.All(char.IsDigit))
            throw new PlexBridgeException("The library id is not valid.");
        return key;
    }

    private static string? CleanContainer(string? containerFolder)
    {
        if (string.IsNullOrWhiteSpace(containerFolder))
            return null;
        if (!LyricsPaths.TryNormalizeContainer(containerFolder, out var container, out var error))
            throw new PlexBridgeException(error);
        return container;
    }

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var text = title.Trim();
        return text.Length > 200 ? text[..200] : text;
    }

    private static LibrarySectionMatch MusicSection(string sectionKey, string? title, IReadOnlyList<LyricsLocationMatch> matches)
        => new(
            sectionKey,
            string.IsNullOrWhiteSpace(title) ? "Music" : title,
            "artist",
            matches.Count > 0 && matches.All(match => match.Status == LyricsMapStatus.Ok) ? LibraryMapStatus.Ok : LibraryMapStatus.NotFound,
            matches.Select(match => new LibraryLocationMatch(match.PlexLocation, match.ContainerPrefix, match.Status, match.SamplesChecked, match.SamplesFound)).ToList());

    private static LyricsServerForm Form(PlexServerConnection server, LyricsSetting? setting, LibraryMount? mount)
    {
        return new LyricsServerForm(
            server.Id,
            server.Slot,
            server.Name,
            server.BaseUrl,
            !string.IsNullOrEmpty(server.ProtectedToken),
            setting?.AutoCheckEnabled ?? false,
            setting?.AutoCheckIntervalHours is > 0 and <= 168 ? setting.AutoCheckIntervalHours : 12,
            setting?.LastAutoCheckUtc,
            LibraryMountStore.ReadMaps(mount, setting),
            setting?.MusicSectionKey,
            setting?.MusicSectionTitle,
            LibraryMountStore.ReadContainer(mount, setting),
            LibraryMountStore.ReadManual(mount, setting),
            LyricsLibraryMapper.MatchesFromJson(setting?.LocationMatchesJson));
    }
}
