using System.Text.Json;
using PlexBridge.Lyrics;

namespace PlexBridge.Libraries;

/// <summary>One Plex path prefix mapped onto a path inside this container.</summary>
public sealed record LibraryPathMap(string PlexPrefix, string ContainerPrefix);

/// <summary>Shown after one library folder is checked against the container mount.</summary>
public static class LibraryMapStatus
{
    public const string Ok = "OK";
    public const string NotFound = "Not found";
}

/// <summary>One Plex library folder and where it landed in the container, if anywhere.</summary>
public sealed record LibraryLocationMatch(
    string PlexLocation,
    string? ContainerPrefix,
    string Status,
    int SamplesChecked,
    int SamplesFound);

/// <summary>One movie, show, or music library and the check for each of its folders.</summary>
public sealed record LibrarySectionMatch(
    string SectionKey,
    string Title,
    string Type,
    string Status,
    IReadOnlyList<LibraryLocationMatch> Locations);

/// <summary>
/// Maps Plex library folders onto one container mount by the longest trailing path that exists there.
/// A Windows location such as M:\Music matches /libraries/music when that folder holds the files.
/// The Plex token is not used here.
/// </summary>
public static class LibraryPathMapper
{
    public const int SamplesPerLocation = 5;
    public const int MaxPages = 3;
    public const string DefaultContainer = "/libraries";
    public const string LegacyContainer = "/music";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string MatchesToJson(IReadOnlyList<LibrarySectionMatch> matches)
        => JsonSerializer.Serialize(matches, JsonOptions);

    public static IReadOnlyList<LibrarySectionMatch> MatchesFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<LibrarySectionMatch>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string MapsToJson(IReadOnlyList<LibraryPathMap> maps)
        => JsonSerializer.Serialize(maps, JsonOptions);

    public static IReadOnlyList<LibraryPathMap> MapsFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<LibraryPathMap>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Saved folder wins. Otherwise /libraries when that mount exists, then /music, then /libraries.
    /// </summary>
    public static string ResolveContainer(string? saved, Func<string, bool>? directoryExists = null)
    {
        if (!string.IsNullOrWhiteSpace(saved))
        {
            var text = saved.Trim().TrimEnd('/', '\\');
            return text.Length == 0 ? DefaultContainer : text;
        }
        directoryExists ??= Directory.Exists;
        if (directoryExists(DefaultContainer))
            return DefaultContainer;
        if (directoryExists(LegacyContainer))
            return LegacyContainer;
        return DefaultContainer;
    }

    public static bool HasEnoughSamples(IReadOnlyList<string> locations, IReadOnlyList<string> samplePaths)
    {
        var prefixes = CleanLocations(locations).Where(row => row.Prefix is not null).Select(row => row.Prefix!).ToList();
        if (prefixes.Count == 0)
            return true;
        return prefixes.All(prefix => SamplesFor(prefix, prefixes, samplePaths).Count >= SamplesPerLocation);
    }

    public static bool TryMatch(
        IReadOnlyList<string> locations,
        string? containerFolder,
        IReadOnlyList<string> samplePaths,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        out IReadOnlyList<LibraryLocationMatch> matches,
        out IReadOnlyList<LibraryPathMap> maps,
        out string error,
        Func<string, IReadOnlyList<string>>? listEntries = null)
    {
        matches = [];
        maps = [];
        var folder = string.IsNullOrWhiteSpace(containerFolder) ? DefaultContainer : containerFolder.Trim();
        if (!LyricsPaths.TryNormalizeContainer(folder, out var container, out error))
            return false;

        var rows = CleanLocations(locations);
        if (rows.Count == 0)
        {
            error = "Plex did not report a folder for that library.";
            return false;
        }

        var prefixes = rows.Where(row => row.Prefix is not null).Select(row => row.Prefix!).ToList();
        var results = new List<LibraryLocationMatch>();
        var found = new List<LibraryPathMap>();
        foreach (var row in rows)
        {
            if (row.Prefix is null)
            {
                results.Add(new LibraryLocationMatch(row.Raw, null, LibraryMapStatus.NotFound, 0, 0));
                continue;
            }

            var samples = SamplesFor(row.Prefix, prefixes, samplePaths);
            var segments = Segments(row.Prefix);
            string? bestContainer = null;
            var bestFound = 0;
            var bestChecked = 0;
            for (var length = segments.Count; length >= 1; length--)
            {
                var suffix = segments.Skip(segments.Count - length).ToList();
                var resolved = Resolve(container, suffix, directoryExists, listEntries);
                if (resolved is null)
                    continue;
                var (checkedCount, foundCount) = Score(row.Prefix, resolved, samples, fileExists, listEntries);
                if (foundCount <= 0)
                    continue;
                bestContainer = resolved;
                bestFound = foundCount;
                bestChecked = checkedCount;
                break;
            }

            var (directChecked, directFound) = Score(row.Prefix, container, samples, fileExists, listEntries);
            if (bestContainer is not null && bestFound > 0 && bestFound >= directFound)
            {
                results.Add(new LibraryLocationMatch(row.Raw, bestContainer, LibraryMapStatus.Ok, bestChecked, bestFound));
                found.Add(new LibraryPathMap(row.Prefix, bestContainer));
                continue;
            }

            if (directFound > 0)
            {
                results.Add(new LibraryLocationMatch(row.Raw, container, LibraryMapStatus.Ok, directChecked, directFound));
                found.Add(new LibraryPathMap(row.Prefix, container));
                continue;
            }

            results.Add(new LibraryLocationMatch(row.Raw, null, LibraryMapStatus.NotFound, Math.Max(bestChecked, directChecked), 0));
        }

        if (!TryNormalize(found, out var normalized, out error))
            return false;

        matches = results;
        maps = normalized;
        error = "";
        return true;
    }

    public static string SectionStatus(IReadOnlyList<LibraryLocationMatch> locations)
    {
        if (locations.Count == 0)
            return LibraryMapStatus.NotFound;
        return locations.All(location => location.Status == LibraryMapStatus.Ok)
            ? LibraryMapStatus.Ok
            : LibraryMapStatus.NotFound;
    }

    public static IReadOnlyList<LibraryPathMap> MergeMaps(
        IReadOnlyList<LibraryPathMap> existing,
        IReadOnlyList<string> checkedPrefixes,
        IReadOnlyList<LibraryPathMap> found)
    {
        if (found.Count == 0)
            return existing;
        var keep = existing.Where(map => checkedPrefixes.All(prefix => !LyricsPaths.SamePrefix(prefix, map.PlexPrefix))).ToList();
        keep.AddRange(found);
        return keep;
    }

    public static IReadOnlyList<string> ListEntries(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return [];
            return Directory.GetFileSystemEntries(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryNormalize(IReadOnlyList<LibraryPathMap> maps, out IReadOnlyList<LibraryPathMap> normalized, out string error)
    {
        var lyrics = maps.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList();
        if (!LyricsPaths.TryNormalize(lyrics, out var clean, out error))
        {
            normalized = [];
            return false;
        }

        normalized = clean.Select(map => new LibraryPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList();
        return true;
    }

    private static List<(string Raw, string? Prefix)> CleanLocations(IReadOnlyList<string> locations)
    {
        var rows = new List<(string Raw, string? Prefix)>();
        foreach (var location in locations)
        {
            var raw = (location ?? "").Trim();
            if (raw.Length == 0)
                continue;
            if (!LyricsPaths.TryNormalizePlexPrefix(raw, out var prefix, out _))
            {
                rows.Add((raw, null));
                continue;
            }

            if (rows.Any(row => row.Prefix is not null && LyricsPaths.SamePrefix(row.Prefix, prefix)))
                continue;
            rows.Add((raw, prefix));
        }

        return rows;
    }

    private static List<string> SamplesFor(string prefix, IReadOnlyList<string> prefixes, IReadOnlyList<string> samplePaths)
    {
        var chosen = new List<string>();
        foreach (var path in samplePaths)
        {
            if (!LyricsPaths.IsFileUnder(path, prefix))
                continue;
            if (prefixes.Any(other => other.Length > prefix.Length && LyricsPaths.IsFileUnder(path, other)))
                continue;
            chosen.Add(path);
            if (chosen.Count >= SamplesPerLocation)
                break;
        }

        return chosen;
    }

    private static List<string> Segments(string plexPrefix)
    {
        var text = plexPrefix.Replace('\\', '/').TrimEnd('/');
        var parts = new List<string>();
        foreach (var segment in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsDrive(segment) || segment is "." or ".." || segment.Contains(':') || segment.Contains('\\'))
                continue;
            parts.Add(segment);
        }

        return parts;
    }

    private static bool IsDrive(string segment)
        => segment.Length == 2 && char.IsLetter(segment[0]) && segment[1] == ':';

    private static string? Resolve(
        string container,
        IReadOnlyList<string> suffix,
        Func<string, bool> directoryExists,
        Func<string, IReadOnlyList<string>>? listEntries)
    {
        if (suffix.Count == 0)
            return null;
        var exact = Join(container, suffix);
        if (directoryExists(exact))
            return exact;
        if (listEntries is null)
            return null;

        var current = container;
        foreach (var segment in suffix)
        {
            var child = Join(current, [segment]);
            if (directoryExists(child))
            {
                current = child;
                continue;
            }

            string? match = null;
            foreach (var entry in listEntries(current))
            {
                if (!string.Equals(NameOf(entry), segment, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!directoryExists(entry))
                    continue;
                match = Trim(entry);
                break;
            }

            if (match is null)
                return null;
            current = match;
        }

        return current;
    }

    private static (int Checked, int Found) Score(
        string plexPrefix,
        string containerPrefix,
        IReadOnlyList<string> samples,
        Func<string, bool> fileExists,
        Func<string, IReadOnlyList<string>>? listEntries)
    {
        if (!TryNormalize([new LibraryPathMap(plexPrefix, containerPrefix)], out var normalized, out _))
            return (0, 0);
        var lyrics = normalized.Select(map => new LyricsPathMap(map.PlexPrefix, map.ContainerPrefix)).ToList();
        var checkedCount = 0;
        var found = 0;
        foreach (var sample in samples)
        {
            if (!LyricsPaths.TryMap(sample, lyrics, out var full, out _))
                continue;
            checkedCount++;
            if (FileFound(containerPrefix, full, fileExists, listEntries))
                found++;
        }

        return (checkedCount, found);
    }

    private static bool FileFound(
        string containerPrefix,
        string full,
        Func<string, bool> fileExists,
        Func<string, IReadOnlyList<string>>? listEntries)
    {
        if (fileExists(full))
            return true;
        if (listEntries is null)
            return false;

        var root = containerPrefix.Replace('\\', '/').TrimEnd('/');
        var candidate = full.Replace('\\', '/');
        if (!candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        var remainder = candidate[(root.Length + 1)..];
        var parts = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
            return false;

        var current = containerPrefix;
        for (var index = 0; index < parts.Length; index++)
        {
            var child = Join(current, [parts[index]]);
            var last = index == parts.Length - 1;
            if (last)
            {
                if (fileExists(child))
                    return true;
                foreach (var entry in listEntries(current))
                {
                    if (!string.Equals(NameOf(entry), parts[index], StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (fileExists(entry))
                        return true;
                }

                return false;
            }

            if (DirectoryExists(child, listEntries))
            {
                current = child;
                continue;
            }

            string? match = null;
            foreach (var entry in listEntries(current))
            {
                if (!string.Equals(NameOf(entry), parts[index], StringComparison.OrdinalIgnoreCase))
                    continue;
                match = Trim(entry);
                break;
            }

            if (match is null)
                return false;
            current = match;
        }

        return false;
    }

    private static bool DirectoryExists(string path, Func<string, IReadOnlyList<string>> listEntries)
    {
        var parent = ParentOf(path);
        var name = NameOf(path);
        if (parent is null)
            return false;
        foreach (var entry in listEntries(parent))
        {
            if (string.Equals(NameOf(entry), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Join(string container, IReadOnlyList<string> segments)
    {
        var windows = container.Contains('\\') || (container.Length >= 2 && char.IsLetter(container[0]) && container[1] == ':');
        var current = windows ? container.TrimEnd('\\', '/') : container.TrimEnd('/');
        foreach (var segment in segments)
        {
            if (windows)
                current += "\\" + segment;
            else
                current += "/" + segment;
        }

        return current;
    }

    private static string NameOf(string path)
    {
        var text = path.Replace('\\', '/').TrimEnd('/');
        var slash = text.LastIndexOf('/');
        return slash >= 0 ? text[(slash + 1)..] : text;
    }

    private static string? ParentOf(string path)
    {
        var text = path.Replace('\\', '/').TrimEnd('/');
        var slash = text.LastIndexOf('/');
        if (slash <= 0)
            return slash == 0 ? "/" : null;
        return text[..slash];
    }

    private static string Trim(string path)
        => path.Replace('\\', '/').TrimEnd('/');
}

/// <summary>The closed library dropdown shows the library name, never the section key.</summary>
public static class LibraryNames
{
    public static string Label(string? key, string? savedTitle, IEnumerable<(string Key, string Title)> libraries)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Choose a library";
        foreach (var library in libraries)
        {
            if (!string.Equals(library.Key, key, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(library.Title))
                return library.Title;
        }

        if (!string.IsNullOrWhiteSpace(savedTitle))
            return savedTitle;
        return "Saved library";
    }
}
