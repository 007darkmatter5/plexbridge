using System.Net;
using System.Reflection;
using System.Text.Json;

namespace PlexBridge.Lyrics;

public enum LyricsLookupKind
{
    Synced,
    Plain,
    Instrumental,
    NotFound,
    Failed
}

public sealed record LyricsLookup(LyricsLookupKind Kind, string? Body, string Detail);

public sealed class LrclibLimits
{
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(400);
    public int MaxInFlight { get; init; } = 2;
    public Uri BaseUri { get; init; } = new("https://lrclib.net/");
    public static LrclibLimits Default { get; } = new();
}

/// <summary>
/// LRCLIB has no API key. /api/get matches artist, title, album, and duration.
/// A miss falls back to /api/search, which is scored locally. Calls stay spaced and limited.
/// </summary>
public sealed class LrclibClient
{
    public const string HttpName = "plexbridge-lrclib";
    public static readonly string UserAgent = BuildUserAgent();
    private const int MaxBodyBytes = 1_000_000;
    private readonly ILrclibTransport _transport;
    private readonly ILogger<LrclibClient> _logger;
    private readonly LrclibLimits _limits;
    private readonly LrclibPacer _pacer;

    public LrclibClient(ILrclibTransport transport, TimeProvider time, ILogger<LrclibClient> logger, LrclibLimits? limits = null)
    {
        _transport = transport;
        _logger = logger;
        _limits = limits ?? LrclibLimits.Default;
        _pacer = new LrclibPacer(time, _limits.MinInterval, _limits.MaxInFlight);
    }

    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Title) || string.IsNullOrWhiteSpace(query.Artist))
            return new LyricsLookup(LyricsLookupKind.NotFound, null, "The track has no artist or title to search with.");

        try
        {
            var exact = await GetAsync(query, cancellationToken);
            if (exact.Failed)
                return new LyricsLookup(LyricsLookupKind.Failed, null, exact.Error);
            if (exact.Record is not null)
            {
                var chosen = LyricsMatching.Choose(query, [exact.Record]);
                if (chosen.Kind == LyricsChoiceKind.Instrumental)
                    return new LyricsLookup(LyricsLookupKind.Instrumental, null, "LRCLIB marks this track as instrumental.");
                if (chosen.Record is not null)
                    return FromRecord(chosen.Record);
            }

            var search = await SearchAsync(query, cancellationToken);
            if (search.Failed)
                return new LyricsLookup(LyricsLookupKind.Failed, null, search.Error);
            var picked = LyricsMatching.Choose(query, search.Records);
            if (picked.Kind == LyricsChoiceKind.Instrumental)
                return new LyricsLookup(LyricsLookupKind.Instrumental, null, "LRCLIB marks this track as instrumental.");
            if (picked.Record is null)
                return new LyricsLookup(LyricsLookupKind.NotFound, null, "LRCLIB has no matching lyrics.");
            return FromRecord(picked.Record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation("LRCLIB lookup failed ({ExceptionType}).", exception.GetType().Name);
            return new LyricsLookup(LyricsLookupKind.Failed, null, "LRCLIB could not be reached.");
        }
    }

    private static LyricsLookup FromRecord(LrclibRecord record)
    {
        if (LyricsMatching.HasSynced(record))
            return new LyricsLookup(LyricsLookupKind.Synced, record.SyncedLyrics, "Synced lyrics from LRCLIB.");
        return new LyricsLookup(LyricsLookupKind.Plain, record.PlainLyrics, "Plain lyrics from LRCLIB. Plex reads these from a .txt file.");
    }

    private async Task<(LrclibRecord? Record, bool Failed, string Error)> GetAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var duration = query.DurationSeconds is >= 1 and <= 3600 ? query.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        var relative = BuildQuery("/api/get", query, duration);
        var response = await SendAsync(relative, cancellationToken);
        if (response.Status == HttpStatusCode.NotFound)
            return (null, false, "");
        if (response.Failed)
            return (null, true, response.Error);
        return (ParseObject(response.Body), false, "");
    }

    private async Task<(List<LrclibRecord> Records, bool Failed, string Error)> SearchAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        var relative = BuildQuery("/api/search", query, duration: "");
        var response = await SendAsync(relative, cancellationToken);
        if (response.Failed)
            return ([], true, response.Error);
        return (ParseArray(response.Body), false, "");
    }

    private static string BuildQuery(string path, LyricsQuery query, string duration)
    {
        var parts = new List<string>
        {
            "track_name=" + Uri.EscapeDataString(Trim(query.Title)),
            "artist_name=" + Uri.EscapeDataString(Trim(query.Artist))
        };
        if (!string.IsNullOrWhiteSpace(query.Album))
            parts.Add("album_name=" + Uri.EscapeDataString(Trim(query.Album)));
        if (duration.Length > 0)
            parts.Add("duration=" + duration);
        return path + "?" + string.Join('&', parts);
    }

    private static string Trim(string value)
        => value.Trim().Length <= 300 ? value.Trim() : value.Trim()[..300];

    private async Task<(HttpStatusCode Status, string Body, bool Failed, string Error)> SendAsync(string relative, CancellationToken cancellationToken)
    {
        if (!string.Equals(_limits.BaseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return (0, "", true, "LRCLIB could not be reached.");

        using var slot = await _pacer.WaitAsync(cancellationToken);
        var address = new Uri(_limits.BaseUri, relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        _logger.LogInformation("LRCLIB GET {Path}.", address.AbsolutePath);

        HttpResponseMessage response;
        try
        {
            response = await _transport.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogInformation("LRCLIB request failed ({ExceptionType}).", exception.GetType().Name);
            return (0, "", true, "LRCLIB could not be reached.");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                return (response.StatusCode, "", true, "LRCLIB could not be reached.");
            if (status == 429)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                if (retry > TimeSpan.FromSeconds(5))
                    retry = TimeSpan.FromSeconds(5);
                if (retry < TimeSpan.Zero)
                    retry = TimeSpan.FromSeconds(2);
                await Task.Delay(retry, cancellationToken);
                return await SendOnceAsync(relative, cancellationToken);
            }

            var body = await ReadBodyAsync(response, cancellationToken);
            if (body is null)
                return (response.StatusCode, "", true, "LRCLIB could not be reached.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (response.StatusCode, body, false, "");
            if (!response.IsSuccessStatusCode)
                return (response.StatusCode, "", true, "LRCLIB returned HTTP " + status + ".");
            return (response.StatusCode, body, false, "");
        }
    }

    private async Task<(HttpStatusCode Status, string Body, bool Failed, string Error)> SendOnceAsync(string relative, CancellationToken cancellationToken)
    {
        var address = new Uri(_limits.BaseUri, relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _transport.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        if (body is null || !response.IsSuccessStatusCode)
            return (response.StatusCode, "", true, "LRCLIB could not be reached.");
        return (response.StatusCode, body, false, "");
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var advertised = response.Content.Headers.ContentLength;
        if (advertised > MaxBodyBytes)
            return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaxBodyBytes)
            return null;
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static LrclibRecord? ParseObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return ReadRecord(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<LrclibRecord> ParseArray(string json)
    {
        var records = new List<LrclibRecord>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return records;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var record = ReadRecord(item);
                if (record is not null)
                    records.Add(record);
            }
        }
        catch (JsonException)
        {
            return records;
        }

        return records;
    }

    private static LrclibRecord? ReadRecord(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var title = Text(item, "trackName") ?? Text(item, "name") ?? "";
        if (title.Length == 0)
            return null;
        return new LrclibRecord(
            title,
            Text(item, "artistName") ?? "",
            Text(item, "albumName") ?? "",
            Number(item, "duration"),
            Bool(item, "instrumental"),
            Text(item, "plainLyrics"),
            Text(item, "syncedLyrics"));
    }

    private static string? Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : 0;
    }

    private static bool Bool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true",
            _ => false
        };
    }

    private static string BuildUserAgent()
    {
        var version = typeof(LrclibClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0-dev";
        var clean = version.Split('+')[0].Trim();
        if (clean.Length == 0)
            clean = "0.0.0-dev";
        return "PlexBridge/" + clean + " (https://github.com/007darkmatter5/plexbridge)";
    }
}

public interface ILrclibTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public sealed class HttpLrclibTransport(IHttpClientFactory factory) : ILrclibTransport
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => factory.CreateClient(LrclibClient.HttpName).SendAsync(request, cancellationToken);
}

internal sealed class LrclibPacer
{
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _spacing = new(1, 1);
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private DateTimeOffset _next;

    public LrclibPacer(TimeProvider time, TimeSpan interval, int maxInFlight)
    {
        _time = time;
        _interval = interval < TimeSpan.Zero ? TimeSpan.Zero : interval;
        _slots = new SemaphoreSlim(Math.Clamp(maxInFlight, 1, 4));
    }

    public async Task<IDisposable> WaitAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            await _spacing.WaitAsync(cancellationToken);
            try
            {
                var wait = _next - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, _time, cancellationToken);
                _next = _time.GetUtcNow() + _interval;
            }
            finally
            {
                _spacing.Release();
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }

        return new Release(_slots);
    }

    private sealed class Release(SemaphoreSlim slots) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slots.Release();
        }
    }
}
