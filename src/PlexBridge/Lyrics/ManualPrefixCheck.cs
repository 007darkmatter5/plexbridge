namespace PlexBridge.Lyrics;

/// <summary>
/// Checks a manual prefix against the container mount.
/// The Plex folder is not expected to exist here. A synced copy is enough:
/// the container folder has to exist, be writable, and hold a sample of the tracks Plex lists.
/// </summary>
public static class ManualPrefixCheck
{
    public const int SamplesPerMap = 5;

    public static bool TryCheck(
        IReadOnlyList<LyricsPathMap> maps,
        IReadOnlyList<string> samplePaths,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists,
        Func<string, bool> canWrite,
        out IReadOnlyList<LyricsLocationMatch> matches,
        out string error)
    {
        matches = [];
        if (!LyricsPaths.TryNormalize(maps, out var normalized, out error))
            return false;
        if (normalized.Count == 0)
        {
            error = "Add a Plex prefix and a container prefix, or turn off manual prefixes.";
            return false;
        }

        var prefixes = normalized.Select(map => map.PlexPrefix).ToList();
        var results = new List<LyricsLocationMatch>(normalized.Count);
        foreach (var map in normalized)
        {
            if (!directoryExists(map.ContainerPrefix))
            {
                results.Add(new LyricsLocationMatch(map.PlexPrefix, map.ContainerPrefix, LyricsMapStatus.ContainerMissing, 0, 0));
                continue;
            }

            if (!canWrite(map.ContainerPrefix))
            {
                results.Add(new LyricsLocationMatch(map.PlexPrefix, map.ContainerPrefix, LyricsMapStatus.NotWritable, 0, 0));
                continue;
            }

            var samples = SamplesFor(map.PlexPrefix, prefixes, samplePaths);
            var checkedCount = 0;
            var found = 0;
            foreach (var sample in samples)
            {
                if (!LyricsPaths.TryMap(sample, [map], out var full, out _))
                    continue;
                checkedCount++;
                if (fileExists(full))
                    found++;
            }

            var status = found > 0 ? LyricsMapStatus.Ok : LyricsMapStatus.NoFiles;
            results.Add(new LyricsLocationMatch(map.PlexPrefix, map.ContainerPrefix, status, checkedCount, found));
        }

        matches = results;
        error = "";
        return true;
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
            if (chosen.Count >= SamplesPerMap)
                break;
        }

        return chosen;
    }
}
