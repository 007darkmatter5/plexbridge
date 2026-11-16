using System.Text;
using System.Text.RegularExpressions;

namespace PlexBridge.Lyrics;

/// <summary>
/// Writes the sidecar Plex documents for local lyrics.
/// Timed lyrics go in a UTF-8 .lrc. Plain lyrics go in a UTF-8 .txt.
/// An existing .lrc or .txt with the same base name is left alone.
/// </summary>
public static partial class LyricsSidecar
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".alac", ".wma", ".ape", ".wv", ".m4b", ".mp4", ".dsf", ".dff"
    };

    public static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path));

    public static bool HasTimestamps(string? lyrics)
        => !string.IsNullOrWhiteSpace(lyrics) && Timestamp().IsMatch(lyrics);

    public static string SidecarPath(string audioPath, bool synced)
        => Path.ChangeExtension(audioPath, synced ? ".lrc" : ".txt");

    public static bool HasExisting(string audioPath)
    {
        var directory = Path.GetDirectoryName(audioPath);
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName) || !Directory.Exists(directory))
            return false;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!Path.GetFileNameWithoutExtension(file).Equals(baseName, StringComparison.OrdinalIgnoreCase))
                continue;
            var extension = Path.GetExtension(file);
            if (extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase) || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string FormatLrc(string artist, string album, string title, int durationSeconds, string syncedBody)
    {
        var body = syncedBody.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        var header = new StringBuilder();
        if (!body.Contains("[ar:", StringComparison.OrdinalIgnoreCase))
            header.Append("[ar:").Append(Tag(artist)).Append("]\n");
        if (!body.Contains("[ti:", StringComparison.OrdinalIgnoreCase))
            header.Append("[ti:").Append(Tag(title)).Append("]\n");
        if (!string.IsNullOrWhiteSpace(album) && !body.Contains("[al:", StringComparison.OrdinalIgnoreCase))
            header.Append("[al:").Append(Tag(album)).Append("]\n");
        if (durationSeconds > 0 && !body.Contains("[length:", StringComparison.OrdinalIgnoreCase))
        {
            header.Append("[length:")
                .Append(durationSeconds / 60)
                .Append(':')
                .Append((durationSeconds % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .Append("]\n");
        }

        if (header.Length > 0)
            header.Append('\n');
        return header + body + "\n";
    }

    public static string FormatPlain(string plain)
        => plain.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim() + "\n";

    public static bool TryWrite(string audioPath, bool synced, string contents, out string writtenPath, out string error)
    {
        writtenPath = "";
        if (!IsAudioFile(audioPath))
        {
            error = "The mapped file is not an audio track.";
            return false;
        }

        if (HasExisting(audioPath))
        {
            error = "A lyrics file is already next to this track.";
            return false;
        }

        var target = SidecarPath(audioPath, synced);
        try
        {
            using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, Utf8);
            writer.Write(contents);
            writtenPath = target;
            error = "";
            return true;
        }
        catch (IOException) when (File.Exists(target) || HasExisting(audioPath))
        {
            error = "A lyrics file is already next to this track.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = "The lyrics file could not be written.";
            return false;
        }
    }

    private static string Tag(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]")]
    private static partial Regex Timestamp();
}

public static class DirectoryWrite
{
    public static bool CanWrite(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(directory + "\0");
            return Native.Access(bytes, 2) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "access", SetLastError = true)]
        public static extern int Access(byte[] pathname, int mode);
    }
}
