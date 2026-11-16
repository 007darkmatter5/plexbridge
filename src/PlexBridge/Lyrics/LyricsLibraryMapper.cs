using System.Text.Json;

namespace PlexBridge.Lyrics;

/// <summary>
/// Maps each folder Plex reports for one music library onto a container mount.
/// A folder matches when a few of its tracks exist there. The Plex token is not used here.
/// </summary>
public static class LyricsLibraryMapper
{
    public const int SamplesPerLocation = 5;
    public const int MaxPages = 3;
    public const string DefaultContainer = "/music";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string MatchesToJson(IReadOnlyList<LyricsLocationMatch> matches)
        => JsonSerializer.Serialize(matches, JsonOptions);

    public static IReadOnlyList<LyricsLocationMatch> MatchesFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<LyricsLocationMatch>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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
        out IReadOnlyList<LyricsLocationMatch> matches,
        out IReadOnlyList<LyricsPathMap> maps,
        out string error)
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
        var results = new List<LyricsLocationMatch>();
        var found = new List<LyricsPathMap>();
        foreach (var row in rows)
        {
            if (row.Prefix is null)
            {
                results.Add(new LyricsLocationMatch(row.Raw, null, LyricsMapStatus.NotFound, 0, 0));
                continue;
            }

            var samples = SamplesFor(row.Prefix, prefixes, samplePaths);
            var segment = LastSegment(row.Prefix);
            var subChecked = 0;
            var subFound = 0;
            string? subContainer = null;
            if (segment is not null)
            {
                var child = JoinContainer(container, segment);
                if (directoryExists(child))
                {
                    (subChecked, subFound) = Score(row.Prefix, child, samples, fileExists);
                    if (subFound > 0)
                        subContainer = child;
                }
            }

            var (directChecked, directFound) = Score(row.Prefix, container, samples, fileExists);
            var useSubfolder = subFound > 0 && subFound >= directFound && subContainer is not null;
            if (useSubfolder)
            {
                results.Add(new LyricsLocationMatch(row.Raw, subContainer, LyricsMapStatus.Ok, subChecked, subFound));
                found.Add(new LyricsPathMap(row.Prefix, subContainer!));
                continue;
            }

            if (directFound > 0)
            {
                results.Add(new LyricsLocationMatch(row.Raw, container, LyricsMapStatus.Ok, directChecked, directFound));
                found.Add(new LyricsPathMap(row.Prefix, container));
                continue;
            }

            results.Add(new LyricsLocationMatch(row.Raw, null, LyricsMapStatus.NotFound, Math.Max(subChecked, directChecked), 0));
        }

        if (!LyricsPaths.TryNormalize(found, out var normalized, out error))
            return false;

        matches = results;
        maps = normalized;
        error = "";
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

    private static (int Checked, int Found) Score(string plexPrefix, string containerPrefix, IReadOnlyList<string> samples, Func<string, bool> fileExists)
    {
        if (!LyricsPaths.TryNormalize([new LyricsPathMap(plexPrefix, containerPrefix)], out var normalized, out _))
            return (0, 0);
        var checkedCount = 0;
        var found = 0;
        foreach (var sample in samples)
        {
            if (!LyricsPaths.TryMap(sample, normalized, out var full, out _))
                continue;
            checkedCount++;
            if (fileExists(full))
                found++;
        }

        return (checkedCount, found);
    }

    private static string? LastSegment(string plexPrefix)
    {
        var text = plexPrefix.Replace('\\', '/').TrimEnd('/');
        var slash = text.LastIndexOf('/');
        var segment = slash >= 0 ? text[(slash + 1)..] : text;
        if (segment.Length == 0 || segment is "." or ".." || segment.Contains(':') || segment.Contains('/') || segment.Contains('\\'))
            return null;
        return segment;
    }

    private static string JoinContainer(string container, string segment)
    {
        var windows = container.Contains('\\') || (container.Length >= 2 && char.IsLetter(container[0]) && container[1] == ':');
        if (windows)
            return container.TrimEnd('\\', '/') + "\\" + segment;
        return container.TrimEnd('/') + "/" + segment;
    }
}
