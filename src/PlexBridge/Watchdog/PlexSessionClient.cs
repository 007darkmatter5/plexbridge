using Microsoft.Extensions.Logging;
using PlexBridge.Plex;

namespace PlexBridge.Watchdog;

/// <summary>Reads sessions and, only when a recovery action is allowed, stops a transcode or ends a play session.</summary>
public sealed class PlexSessionClient(IPlexTransport reads, IPlexWriteTransport writes, IPlexAddressGuard addresses, ILogger<PlexSessionClient> logger)
{
    public async Task<IReadOnlyList<WatchedSession>> GetSessionsAsync(Uri baseUri, string token, DateTimeOffset at, CancellationToken cancellationToken)
    {
        logger.LogInformation("Watchdog read sessions.");
        var json = await PlexExchange.SendAsync(
            reads,
            addresses,
            HttpMethod.Get,
            baseUri,
            token,
            "/status/sessions",
            PlexWatchdogPolicy.EnsureAllowed,
            followSameHostRedirect: true,
            cancellationToken);
        return PlexSessionParser.Parse(json, at);
    }

    public Task TerminateAsync(Uri baseUri, string token, string sessionId, CancellationToken cancellationToken)
    {
        if (!PlexWatchdogPolicy.IsSafeId(sessionId))
            throw new PlexRequestException("The session id is not valid.");
        var relative = "/status/sessions/terminate?sessionId=" + Uri.EscapeDataString(sessionId)
            + "&reason=" + Uri.EscapeDataString(RecoveryPlanner.TerminateReason);
        logger.LogInformation("Watchdog terminate session.");
        return PlexExchange.SendAsync(
            writes,
            addresses,
            HttpMethod.Get,
            baseUri,
            token,
            relative,
            PlexWatchdogPolicy.EnsureAllowed,
            followSameHostRedirect: false,
            cancellationToken);
    }

    public Task StopTranscodeAsync(Uri baseUri, string token, string sessionId, CancellationToken cancellationToken)
    {
        var session = PlexWatchdogPolicy.SafeId(sessionId) ?? throw new PlexRequestException("The transcode session id is not valid.");
        var relative = "/video/:/transcode/universal/stop?session=" + Uri.EscapeDataString(session);
        logger.LogInformation("Watchdog stop transcode.");
        return PlexExchange.SendAsync(
            writes,
            addresses,
            HttpMethod.Get,
            baseUri,
            token,
            relative,
            PlexWatchdogPolicy.EnsureAllowed,
            followSameHostRedirect: false,
            cancellationToken);
    }
}
