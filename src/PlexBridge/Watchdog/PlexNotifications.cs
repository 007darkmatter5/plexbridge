using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PlexBridge.Plex;

namespace PlexBridge.Watchdog;

public static class PlexNotifications
{
    public static Uri WebSocketUri(Uri httpBase)
    {
        var withPath = new Uri(httpBase, "/:/websockets/notifications");
        var scheme = withPath.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new UriBuilder(withPath) { Scheme = scheme, Query = "" }.Uri;
    }

    public static bool IsStreamEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("NotificationContainer", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var inner in property.Value.EnumerateObject())
                {
                    if (!inner.Name.Equals("type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var type = inner.Value.ValueKind == JsonValueKind.String ? inner.Value.GetString() : null;
                    return type is "playing" or "transcodeSession";
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}

/// <summary>
/// Optional PMS notification socket. A playing or transcode event asks the poll loop to read sessions sooner.
/// The token is a header. A failed socket leaves polling in place.
/// </summary>
public sealed class PlexNotificationListener(IPlexAddressGuard addresses, ILogger logger)
{
    private readonly object _gate = new();
    private bool _wake;
    private string _status = "off";

    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public void RequestWake()
    {
        lock (_gate)
            _wake = true;
    }

    public bool ConsumeWake()
    {
        lock (_gate)
        {
            var pending = _wake;
            _wake = false;
            return pending;
        }
    }

    public void MarkOff()
    {
        lock (_gate)
            _status = "off";
    }

    public void MarkConnecting()
    {
        lock (_gate)
            _status = "connecting";
    }

    public async Task RunAsync(int serverId, Uri httpBase, string token, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await addresses.EnsureAllowedAsync(httpBase, cancellationToken);
                await ReadOnceAsync(httpBase, token, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                lock (_gate)
                    _status = "unavailable";
                logger.LogInformation("Watchdog notifications for server {ServerId} are unavailable ({ExceptionType}). Polling continues.", serverId, exception.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        MarkOff();
    }

    private async Task ReadOnceAsync(Uri httpBase, string token, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.SetRequestHeader("X-Plex-Token", token);
        socket.Options.SetRequestHeader("X-Plex-Product", "PlexBridge");
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(PlexNotifications.WebSocketUri(httpBase), connect.Token);
        lock (_gate)
            _status = "connected";

        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (message.Length + result.Count > 256 * 1024)
                    break;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text || message.Length == 0 || message.Length > 256 * 1024)
                continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            if (PlexNotifications.IsStreamEvent(text))
                RequestWake();
        }
    }
}
