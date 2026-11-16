using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Libraries;
using PlexBridge.Lyrics;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

public sealed class LibrarySettingsInput
{
    public int ServerId { get; set; }
    public string? ContainerFolder { get; set; }
    public bool UseManualMaps { get; set; }
    public List<LyricsMapInput> Maps { get; set; } = [];
}

public sealed record LibraryServerForm(
    int ServerId,
    int Slot,
    string Name,
    string BaseUrl,
    bool HasToken,
    string? ContainerFolder,
    bool UseManualMaps,
    IReadOnlyList<LibraryPathMap> Maps,
    IReadOnlyList<LibrarySectionMatch> Sections);

public sealed record LibraryCheckReport(
    string ContainerFolder,
    IReadOnlyList<LibrarySectionMatch> Sections,
    IReadOnlyList<LibraryPathMap> Maps);

/// <summary>
/// Checks every movie, show, and music library against the container mount.
/// The Plex token is used only as a request header and is not stored or logged.
/// </summary>
public sealed class LibraryPathService(
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexReadClient libraries,
    LibraryFileClient files,
    IAdminGate admin)
{
    public async Task<IReadOnlyList<LibraryServerForm>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await admin.RequireUserIdAsync(cancellationToken);
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var servers = await db.PlexServers.AsNoTracking().OrderBy(server => server.Slot).ThenBy(server => server.Id).ToListAsync(cancellationToken);
        var mounts = await db.LibraryMounts.AsNoTracking().ToListAsync(cancellationToken);
        var settings = await db.LyricsSettings.AsNoTracking().ToListAsync(cancellationToken);
        return servers.Select(server =>
        {
            var mount = mounts.FirstOrDefault(item => item.PlexServerConnectionId == server.Id);
            var setting = settings.FirstOrDefault(item => item.PlexServerConnectionId == server.Id);
            return new LibraryServerForm(
                server.Id,
                server.Slot,
                server.Name,
                server.BaseUrl,
                !string.IsNullOrEmpty(server.ProtectedToken),
                LibraryMountStore.ReadContainer(mount, setting),
                LibraryMountStore.ReadManual(mount, setting),
                LibraryPathMapper.MapsFromJson(LyricsPaths.ToJson(LibraryMountStore.ReadMaps(mount, setting))),
                LibraryPathMapper.MatchesFromJson(mount?.SectionMatchesJson));
        }).ToList();
    }

    public async Task<LibraryCheckReport> CheckAsync(int serverId, string? containerFolder, CancellationToken cancellationToken = default)
    {
        await admin.RequireUserIdAsync(cancellationToken);
        var container = CleanContainer(containerFolder);
        var (uri, token) = await OpenAsync(serverId, cancellationToken);
        var sections = await libraries.GetSectionsAsync(uri, token, cancellationToken);
        var results = new List<LibrarySectionMatch>();
        var maps = new List<LibraryPathMap>();
        foreach (var section in sections)
        {
            var leaf = LibraryFileClient.LeafType(section.Type);
            if (leaf is null)
                continue;
            if (section.Locations.Count == 0)
            {
                results.Add(new LibrarySectionMatch(section.Key, section.Title, section.Type, LibraryMapStatus.NotFound, []));
                continue;
            }

            var samples = await SampleAsync(uri, token, section.Key, leaf, section.Locations, cancellationToken);
            if (!LibraryPathMapper.TryMatch(section.Locations, container, samples, Directory.Exists, File.Exists, out var locations, out var found, out var error, LibraryPathMapper.ListEntries))
                throw new PlexBridgeException(error);
            var prefixes = Prefixes(section.Locations);
            maps = LibraryPathMapper.MergeMaps(maps, prefixes, found).ToList();
            results.Add(new LibrarySectionMatch(section.Key, section.Title, section.Type, LibraryPathMapper.SectionStatus(locations), locations));
        }

        if (!LyricsPaths.TryNormalize(maps.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList(), out var normalized, out var normalizeError))
            throw new PlexBridgeException(normalizeError);
        return new LibraryCheckReport(
            container,
            results,
            normalized.Select(map => new LibraryPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList());
    }

    public async Task<LibraryCheckReport> SaveAsync(LibrarySettingsInput input, CancellationToken cancellationToken = default)
    {
        await admin.RequireUserIdAsync(cancellationToken);
        if (input.UseManualMaps)
            return await SaveManualAsync(input, cancellationToken);

        var report = await CheckAsync(input.ServerId, input.ContainerFolder, cancellationToken);
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var lyrics = await LoadOrCreateLyricsAsync(db, input.ServerId, cancellationToken);
        var mount = await db.LibraryMounts.FirstOrDefaultAsync(item => item.PlexServerConnectionId == input.ServerId, cancellationToken);
        var existing = LibraryMountStore.ReadMaps(mount, lyrics)
            .Select(map => new LibraryPathMap(map.PlexPrefix, map.ContainerPrefix))
            .ToList();
        IReadOnlyList<LibraryPathMap> merged = existing;
        var anyFound = false;
        foreach (var section in report.Sections)
        {
            var found = section.Locations
                .Where(location => location.Status == LibraryMapStatus.Ok && !string.IsNullOrWhiteSpace(location.ContainerPrefix))
                .Select(location => new LibraryPathMap(NormalizedPrefix(location.PlexLocation), location.ContainerPrefix!))
                .Where(map => map.PlexPrefix.Length > 0)
                .ToList();
            if (found.Count > 0)
                anyFound = true;
            merged = LibraryPathMapper.MergeMaps(merged, Prefixes(section.Locations.Select(location => location.PlexLocation).ToList()), found);
        }

        var sourceJson = mount is not null && (mount.UseManualMaps || HasMaps(mount.PathMapsJson))
            ? mount.PathMapsJson
            : lyrics.PathMapsJson;
        var json = anyFound
            ? LyricsPaths.ToJson(merged.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList())
            : sourceJson;
        if (anyFound && !LyricsPaths.TryNormalize(LyricsPaths.FromJson(json), out _, out var error))
            throw new PlexBridgeException(error);

        lyrics.PathMapsJson = string.IsNullOrWhiteSpace(json) ? "[]" : json;
        lyrics.ContainerFolder = report.ContainerFolder;
        lyrics.UseManualMaps = false;
        var music = report.Sections.FirstOrDefault(section => section.Type.Equals("artist", StringComparison.OrdinalIgnoreCase));
        if (music is not null)
        {
            lyrics.LocationMatchesJson = LyricsLibraryMapper.MatchesToJson(music.Locations.Select(ToLyricsMatch).ToList());
            if (!string.IsNullOrWhiteSpace(lyrics.MusicSectionKey) && lyrics.MusicSectionKey == music.SectionKey)
                lyrics.MusicSectionTitle = music.Title;
        }

        await LibraryMountStore.UpsertAsync(db, input.ServerId, report.ContainerFolder, false, lyrics.PathMapsJson, LibraryPathMapper.MatchesToJson(report.Sections), true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return report with { Maps = LibraryPathMapper.MapsFromJson(lyrics.PathMapsJson) };
    }

    private async Task<LibraryCheckReport> SaveManualAsync(LibrarySettingsInput input, CancellationToken cancellationToken)
    {
        var maps = input.Maps.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList();
        if (!LyricsPaths.TryNormalize(maps, out var normalized, out var error))
            throw new PlexBridgeException(error);
        if (normalized.Count == 0)
            throw new PlexBridgeException("Add a Plex prefix and a container prefix, or turn off manual prefixes.");
        var container = string.IsNullOrWhiteSpace(input.ContainerFolder) ? null : CleanContainer(input.ContainerFolder);
        var json = LyricsPaths.ToJson(normalized);

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var lyrics = await LoadOrCreateLyricsAsync(db, input.ServerId, cancellationToken);
        lyrics.PathMapsJson = json;
        lyrics.ContainerFolder = container;
        lyrics.UseManualMaps = true;
        lyrics.LocationMatchesJson = null;
        await LibraryMountStore.UpsertAsync(db, input.ServerId, container, true, json, null, true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new LibraryCheckReport(
            container ?? "",
            [],
            normalized.Select(map => new LibraryPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList());
    }

    private async Task<List<string>> SampleAsync(Uri uri, string token, string sectionKey, string leaf, IReadOnlyList<string> locations, CancellationToken cancellationToken)
    {
        var samples = new List<string>();
        var start = 0;
        for (var page = 0; page < LibraryPathMapper.MaxPages; page++)
        {
            var batch = await files.ReadPageAsync(uri, token, sectionKey, leaf, start, cancellationToken);
            foreach (var item in batch.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.FilePath))
                    samples.Add(item.FilePath);
            }

            if (batch.Items.Count < files.ItemPageSize)
                break;
            start += batch.Items.Count;
            if (batch.Total is int total && start >= total)
                break;
            if (LibraryPathMapper.HasEnoughSamples(locations, samples))
                break;
        }

        return samples;
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

    private static async Task<LyricsSetting> LoadOrCreateLyricsAsync(ApplicationDbContext db, int serverId, CancellationToken cancellationToken)
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

    private static string CleanContainer(string? containerFolder)
    {
        var folder = LibraryPathMapper.ResolveContainer(containerFolder);
        if (!LyricsPaths.TryNormalizeContainer(folder, out var container, out var error))
            throw new PlexBridgeException(error);
        return container;
    }

    private static List<string> Prefixes(IReadOnlyList<string> locations)
    {
        var prefixes = new List<string>();
        foreach (var location in locations)
        {
            if (LyricsPaths.TryNormalizePlexPrefix(location, out var prefix, out _))
                prefixes.Add(prefix);
        }

        return prefixes;
    }

    private static bool HasMaps(string? json)
        => !string.IsNullOrWhiteSpace(json) && json.Trim() != "[]";

    private static string NormalizedPrefix(string location)
        => LyricsPaths.TryNormalizePlexPrefix(location, out var prefix, out _) ? prefix : "";

    private static LyricsLocationMatch ToLyricsMatch(LibraryLocationMatch match)
        => new(match.PlexLocation, match.ContainerPrefix, match.Status, match.SamplesChecked, match.SamplesFound);
}
