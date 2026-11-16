using System.Text.Json;

namespace PlexBridge.Lyrics;

/// <summary>
/// Translates the path Plex reports into a path inside this container.
/// The longest matching prefix wins, and the result has to stay under the container prefix.
/// </summary>
public static class LyricsPaths
{
    public const int MaxMaps = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] BlockedContainers =
    [
        "/config", "/app", "/bin", "/etc", "/usr", "/proc", "/sys", "/root", "/dev", "/lib", "/lib64", "/sbin", "/boot", "/run"
    ];

    public static string ToJson(IReadOnlyList<LyricsPathMap> maps)
        => JsonSerializer.Serialize(maps, JsonOptions);

    public static IReadOnlyList<LyricsPathMap> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<LyricsPathMap>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool TryNormalize(IReadOnlyList<LyricsPathMap> maps, out IReadOnlyList<LyricsPathMap> normalized, out string error)
    {
        normalized = [];
        var clean = new List<LyricsPathMap>();
        foreach (var map in maps)
        {
            var plex = (map.PlexPrefix ?? "").Trim();
            var container = (map.ContainerPrefix ?? "").Trim();
            if (plex.Length == 0 && container.Length == 0)
                continue;
            if (plex.Length == 0 || container.Length == 0)
            {
                error = "Each path mapping needs a Plex prefix and a container prefix.";
                return false;
            }

            if (!TryCleanPrefix(plex, container: false, out var plexPrefix, out error))
                return false;
            if (!TryCleanPrefix(container, container: true, out var containerPrefix, out error))
                return false;
            if (clean.Any(existing => PrefixEquals(existing.PlexPrefix, plexPrefix)))
            {
                error = "Two mappings use the same Plex prefix.";
                return false;
            }

            clean.Add(new LyricsPathMap(plexPrefix, containerPrefix));
        }

        if (clean.Count > MaxMaps)
        {
            error = "Save at most 32 path mappings for one server.";
            return false;
        }

        normalized = clean;
        error = "";
        return true;
    }

    public static bool TryNormalizeContainer(string value, out string prefix, out string error)
        => TryCleanPrefix(value, container: true, out prefix, out error);

    public static bool TryNormalizePlexPrefix(string value, out string prefix, out string error)
        => TryCleanPrefix(value, container: false, out prefix, out error);

    internal static bool IsFileUnder(string? path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(prefix))
            return false;
        var candidate = path.Trim();
        if (candidate.Contains('\0') || candidate.Any(char.IsControl))
            return false;
        if (!MatchesPrefix(candidate, prefix))
            return false;
        var remainder = Remainder(candidate, prefix);
        return !string.IsNullOrEmpty(remainder) && !remainder.Contains("..", StringComparison.Ordinal);
    }

    internal static bool SamePrefix(string left, string right)
        => PrefixEquals(left, right);

    public static bool TryMap(string? plexPath, IReadOnlyList<LyricsPathMap> maps, out string containerPath, out string error)
    {
        containerPath = "";
        if (string.IsNullOrWhiteSpace(plexPath))
        {
            error = "Plex did not report a file path for this track.";
            return false;
        }

        if (maps.Count == 0)
        {
            error = "No path mapping is saved for this server.";
            return false;
        }

        if (!TryNormalize(maps, out var normalized, out error))
            return false;

        var candidate = plexPath.Trim();
        if (candidate.Contains('\0') || candidate.Any(char.IsControl))
        {
            error = "The Plex path is not usable.";
            return false;
        }

        LyricsPathMap? best = null;
        var bestLength = -1;
        foreach (var map in normalized)
        {
            if (!MatchesPrefix(candidate, map.PlexPrefix))
                continue;
            if (map.PlexPrefix.Length > bestLength)
            {
                best = map;
                bestLength = map.PlexPrefix.Length;
            }
        }

        if (best is null)
        {
            error = "The Plex path does not start with a saved mapping.";
            return false;
        }

        var remainder = Remainder(candidate, best.PlexPrefix);
        if (remainder is null || remainder.Contains("..", StringComparison.Ordinal))
        {
            error = "The Plex path is not usable.";
            return false;
        }

        if (remainder.Length == 0)
        {
            error = "The Plex path is a folder, not a file.";
            return false;
        }

        var containerIsWindows = IsWindowsPath(best.ContainerPrefix);
        var joined = containerIsWindows
            ? best.ContainerPrefix.TrimEnd('\\', '/') + "\\" + remainder.TrimStart('/').Replace('/', '\\')
            : best.ContainerPrefix.TrimEnd('/') + "/" + remainder.TrimStart('/');

        string full;
        string root;
        try
        {
            full = Path.GetFullPath(joined);
            root = Path.GetFullPath(best.ContainerPrefix);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The mapped path is not usable.";
            return false;
        }

        var comparison = containerIsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separator = containerIsWindows ? '\\' : '/';
        var rootWithSeparator = root.EndsWith(separator) ? root : root + separator;
        if (!full.StartsWith(rootWithSeparator, comparison))
        {
            error = "The mapped path leaves the container folder.";
            return false;
        }

        containerPath = full;
        error = "";
        return true;
    }

    public static string FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static bool TryCleanPrefix(string value, bool container, out string prefix, out string error)
    {
        prefix = "";
        error = "";
        if (value.Length > 400 || value.Contains('\0') || value.Any(char.IsControl))
        {
            error = "A path prefix is too long or contains a control character.";
            return false;
        }

        var windows = IsWindowsPath(value);
        if (!windows && !value.StartsWith('/'))
        {
            error = container
                ? "The container path has to be a full path, such as /libraries."
                : "The Plex path has to be a full path, such as /mnt/user/media/music or D:\\Music.";
            return false;
        }

        var text = windows ? value.Replace('/', '\\').TrimEnd('\\') : value.Replace('\\', '/').TrimEnd('/');
        if (windows && text.Length == 2 && text[1] == ':')
            text += "\\";
        if (!windows && text.Length == 0)
            text = "/";

        if (text.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
        {
            error = "A path prefix cannot contain ..";
            return false;
        }

        if (!container && text == "/")
        {
            error = "Map a library folder, not the whole Plex filesystem.";
            return false;
        }

        if (container && IsBlockedContainer(text))
        {
            error = "That container path is not a media folder. Use the mounted libraries share, such as /libraries.";
            return false;
        }

        prefix = text;
        return true;
    }

    private static bool IsBlockedContainer(string prefix)
    {
        var normalized = prefix.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
            return true;
        foreach (var blocked in BlockedContainers)
        {
            if (normalized.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.StartsWith(blocked + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool PrefixEquals(string left, string right)
    {
        var comparison = IsWindowsPath(left) || IsWindowsPath(right)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(NormalizeCompare(left), NormalizeCompare(right), comparison);
    }

    private static bool MatchesPrefix(string path, string prefix)
    {
        var pathText = NormalizeCompare(path);
        var prefixText = NormalizeCompare(prefix);
        var comparison = IsWindowsPath(path) || IsWindowsPath(prefix)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (pathText.Equals(prefixText, comparison))
            return true;
        return pathText.StartsWith(prefixText + "/", comparison);
    }

    private static string? Remainder(string path, string prefix)
    {
        var pathText = NormalizeCompare(path);
        var prefixText = NormalizeCompare(prefix);
        if (pathText.Length < prefixText.Length)
            return null;
        return pathText[prefixText.Length..].TrimStart('/');
    }

    private static string NormalizeCompare(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    private static bool IsWindowsPath(string path)
        => path.Contains('\\') || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');
}
