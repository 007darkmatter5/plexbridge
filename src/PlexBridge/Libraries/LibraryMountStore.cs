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

    public static IReadOnlyList<LyricsPathMap> ReadMaps(LibraryMount? mount, LyricsSetting? setting)
    {
        if (mount is not null && (mount.UseManualMaps || HasMaps(mount.PathMapsJson)))
            return ToLyrics(LibraryPathMapper.MapsFromJson(mount.PathMapsJson));
        return LyricsPaths.FromJson(setting?.PathMapsJson);
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
}
