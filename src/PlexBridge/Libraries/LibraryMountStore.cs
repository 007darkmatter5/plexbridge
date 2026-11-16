using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Lyrics;

namespace PlexBridge.Libraries;

/// <summary>Reads and writes the shared library mount without touching tokens, slots, or keys.</summary>
public static class LibraryMountStore
{
    public static async Task UpsertAsync(
        ApplicationDbContext db,
        int serverId,
        string? container,
        bool manual,
        string pathMapsJson,
        string? sectionMatchesJson,
        bool replaceSections,
        CancellationToken cancellationToken)
    {
        var row = await db.LibraryMounts.FirstOrDefaultAsync(item => item.PlexServerConnectionId == serverId, cancellationToken);
        if (row is null)
        {
            row = new LibraryMount { PlexServerConnectionId = serverId };
            db.LibraryMounts.Add(row);
        }

        row.ContainerFolder = container;
        row.UseManualMaps = manual;
        row.PathMapsJson = string.IsNullOrWhiteSpace(pathMapsJson) ? "[]" : pathMapsJson;
        if (replaceSections)
            row.SectionMatchesJson = sectionMatchesJson;
    }

    /// <summary>The prefixes saved on the server. This list stays when the manual switch is off.</summary>
    public static IReadOnlyList<LyricsPathMap> ReadMaps(LibraryMount? mount, LyricsSetting? setting)
    {
        if (mount is not null && (mount.UseManualMaps || HasMaps(mount.PathMapsJson)))
            return ToLyrics(LibraryPathMapper.MapsFromJson(mount.PathMapsJson));
        return LyricsPaths.FromJson(setting?.PathMapsJson);
    }

    /// <summary>
    /// The prefixes writes actually use. The manual switch decides.
    /// On: the saved prefixes. Off: saved prefixes, except a folder the last automatic check marked not found.
    /// A row that has never been checked keeps its saved prefixes.
    /// </summary>
    public static IReadOnlyList<LyricsPathMap> ReadActiveMaps(LibraryMount? mount, LyricsSetting? setting)
    {
        var stored = ReadMaps(mount, setting);
        if (ReadManual(mount, setting))
            return stored;

        var judged = JudgedPrefixes(mount, setting);
        if (judged.Count == 0)
            return stored;

        return stored.Where(map =>
        {
            var verdict = judged.FirstOrDefault(item => item.Prefix is not null && LyricsPaths.SamePrefix(item.Prefix, map.PlexPrefix));
            return verdict.Prefix is null || verdict.Ok;
        }).ToList();
    }

    public static string? ReadContainer(LibraryMount? mount, LyricsSetting? setting)
    {
        if (mount is not null && (mount.UseManualMaps || mount.ContainerFolder is not null || HasMaps(mount.PathMapsJson)))
            return mount.ContainerFolder;
        return setting?.ContainerFolder;
    }

    public static bool ReadManual(LibraryMount? mount, LyricsSetting? setting)
        => mount is not null ? mount.UseManualMaps : setting?.UseManualMaps ?? false;

    public static string MergeSection(string? existingJson, LibrarySectionMatch section)
    {
        var list = LibraryPathMapper.MatchesFromJson(existingJson)
            .Where(item => !string.Equals(item.SectionKey, section.SectionKey, StringComparison.Ordinal))
            .ToList();
        list.Add(section);
        return LibraryPathMapper.MatchesToJson(list);
    }

    private static bool HasMaps(string? json)
        => !string.IsNullOrWhiteSpace(json) && json.Trim() != "[]";

    private static IReadOnlyList<LyricsPathMap> ToLyrics(IReadOnlyList<LibraryPathMap> maps)
        => maps.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList();

    private readonly record struct PrefixVerdict(string? Prefix, bool Ok);

    private static List<PrefixVerdict> JudgedPrefixes(LibraryMount? mount, LyricsSetting? setting)
    {
        var rows = new List<PrefixVerdict>();
        void Add(string? location, string status)
        {
            if (string.IsNullOrWhiteSpace(location))
                return;
            if (!LyricsPaths.TryNormalizePlexPrefix(location, out var prefix, out _))
                return;
            var ok = status == LyricsMapStatus.Ok || status == LibraryMapStatus.Ok;
            var index = rows.FindIndex(item => item.Prefix is not null && LyricsPaths.SamePrefix(item.Prefix, prefix));
            if (index < 0)
                rows.Add(new PrefixVerdict(prefix, ok));
            else if (ok)
                rows[index] = new PrefixVerdict(prefix, true);
        }

        foreach (var section in LibraryPathMapper.MatchesFromJson(mount?.SectionMatchesJson))
        {
            foreach (var location in section.Locations)
                Add(location.PlexLocation, location.Status);
        }

        foreach (var match in LyricsLibraryMapper.MatchesFromJson(setting?.LocationMatchesJson))
            Add(match.PlexLocation, match.Status);
        return rows;
    }
}
