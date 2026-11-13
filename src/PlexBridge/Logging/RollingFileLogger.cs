using System.Text.RegularExpressions;

namespace PlexBridge.Logging;

/// <summary>Appends scrubbed log lines to a rolling file. Query strings are removed so a token in a URL cannot be written.</summary>
public sealed partial class RollingFileLoggerProvider(string path) : ILoggerProvider
{
    public const long RollBytes = 5 * 1024 * 1024;
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(categoryName, path, _gate);

    public void Dispose()
    {
    }

    private sealed partial class RollingFileLogger(string category, string path, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var text = Scrub(formatter(state, exception));
            if (exception is not null)
                text = Scrub(text + " " + exception.GetType().Name);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {logLevel} {category}: {text}{Environment.NewLine}";
            lock (gate)
            {
                try
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > RollBytes)
                    {
                        var rolled = path + ".1";
                        File.Delete(rolled);
                        File.Move(path, rolled);
                    }

                    File.AppendAllText(path, line);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var stripped = UrlQuery().Replace(text, "$1");
        stripped = TokenHeader().Replace(stripped, "token=[redacted]");
        stripped = TokenQuery().Replace(stripped, "$1[redacted]");
        return stripped;
    }

    [GeneratedRegex(@"(https?://[^\s?]+)\?[^\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"X-Plex-Token\s*[:=]\s*\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenHeader();

    [GeneratedRegex(@"(token=)[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenQuery();
}
