using System.Globalization;
using System.Text.RegularExpressions;
using PlexBridge.Logging;

namespace PlexBridge.Watchdog;

/// <summary>
/// Reads the tail of the Plex server log and the transcoder logs. A missing or unreadable folder returns nothing.
/// Log timestamps are compared in the container's local time, so TZ should match the Plex server.
/// </summary>
public static partial class PlexLogTail
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    private const int TailBytes = 512 * 1024;
    private const int MaxLines = 30;
    private const int MaxLine = 400;

    public static IReadOnlyList<string> Excerpt(string? folder, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return [];

        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        if (!Directory.Exists(full))
            return [];

        var files = new List<string>();
        var serverLog = Path.Combine(full, "Plex Media Server.log");
        if (File.Exists(serverLog))
            files.Add(serverLog);
        try
        {
            files.AddRange(Directory.EnumerateFiles(full, "Plex Transcoder*.log"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Lines(files, at);
        }

        return Lines(files, at);
    }

    public static string? Merge(string? existing, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return string.IsNullOrWhiteSpace(existing) ? null : existing;
        var kept = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing))
            kept.AddRange(existing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var line in lines)
        {
            if (!kept.Contains(line, StringComparer.Ordinal))
                kept.Add(line);
        }

        if (kept.Count > MaxLines)
            kept = kept.TakeLast(MaxLines).ToList();
        var text = string.Join('\n', kept);
        return text.Length == 0 ? null : text;
    }

    private static IReadOnlyList<string> Lines(IEnumerable<string> files, DateTimeOffset at)
    {
        var local = at.ToLocalTime().DateTime;
        var found = new List<string>();
        foreach (var file in files.Distinct(StringComparer.Ordinal))
        {
            string text;
            try
            {
                text = ReadTail(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || !IsWarningOrError(line) || !Stamp().IsMatch(line))
                    continue;
                if (!TryStamp(line, out var stamped) || (stamped - local).Duration() > Window)
                    continue;
                var scrubbed = WatchdogText.Clip(RollingFileLoggerProvider.Scrub(line), MaxLine);
                if (scrubbed.Length == 0 || found.Contains(scrubbed, StringComparer.Ordinal))
                    continue;
                found.Add(scrubbed);
                if (found.Count >= MaxLines)
                    return found;
            }
        }

        return found;
    }

    private static bool TryStamp(string line, out DateTime stamped)
    {
        stamped = default;
        var match = Stamp().Match(line);
        if (!match.Success)
            return false;
        var text = match.Groups["month"].Value + " " + match.Groups["day"].Value + ", " + match.Groups["year"].Value + " " + match.Groups["clock"].Value;
        return DateTime.TryParseExact(text, "MMM d, yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamped);
    }

    private static bool IsWarningOrError(string line)
        => line.Contains(" ERROR ", StringComparison.Ordinal) || line.Contains(" WARN ", StringComparison.Ordinal) || line.Contains(" CRITICAL ", StringComparison.Ordinal);

    private static string ReadTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (start > 0)
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0)
                text = text[(newline + 1)..];
        }

        return text;
    }

    [GeneratedRegex(@"^(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+(?<day>\d{1,2}),\s+(?<year>\d{4})\s+(?<clock>\d{2}:\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();
}
