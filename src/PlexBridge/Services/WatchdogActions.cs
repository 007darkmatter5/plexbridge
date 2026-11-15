using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Watchdog;

namespace PlexBridge.Services;

public sealed class WatchdogSessionEnds
{
    private readonly ConcurrentDictionary<string, byte> _ids = new(StringComparer.Ordinal);

    public void Mark(int serverId, string playSessionId)
        => _ids.TryAdd(serverId.ToString() + "\n" + playSessionId, 0);

    public bool Consume(int serverId, string playSessionId)
        => _ids.TryRemove(serverId.ToString() + "\n" + playSessionId, out _);
}

public sealed record WatchdogActionResult(bool Ok, string Message);

/// <summary>Stops a stuck transcode and ends the play session. Report-only calls this from the button. Auto calls it once.</summary>
public sealed class WatchdogActions(
    IDbContextFactory<ApplicationDbContext> databases,
    PlexSessionClient sessions,
    ISecretProtector protector,
    WatchdogSessionEnds ends,
    TimeProvider clock,
    ILogger<WatchdogActions> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WatchdogActionResult> ExecuteAsync(int incidentId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await databases.CreateDbContextAsync(cancellationToken);
            var incident = await db.WatchdogIncidents.FirstOrDefaultAsync(item => item.Id == incidentId, cancellationToken);
            if (incident is null)
                return new WatchdogActionResult(false, "That incident was not found.");
            if (incident.Resolution != IncidentResolutions.StillStuck)
                return new WatchdogActionResult(false, "That stream is no longer stuck.");
            if (incident.ActionStatus == WatchdogActionStatus.Done)
                return new WatchdogActionResult(true, "That action was already taken.");
            if (!PlexWatchdogPolicy.IsSafeId(incident.PlaySessionId))
                return new WatchdogActionResult(false, "This incident has no usable session id.");
            if (incident.PlexServerConnectionId is not int serverId)
                return new WatchdogActionResult(false, "That server is no longer saved.");

            var server = await db.PlexServers.FirstOrDefaultAsync(item => item.Id == serverId, cancellationToken);
            if (server is null)
                return new WatchdogActionResult(false, "That server is no longer saved.");

            string token;
            try
            {
                token = Unprotect(server);
            }
            catch (PlexBridgeException exception)
            {
                return new WatchdogActionResult(false, exception.Message);
            }

            var kind = incident.ActionKind;
            if (string.IsNullOrEmpty(kind))
            {
                var plan = RecoveryPlanner.Propose(
                    WatchdogRecovery.ReportOnly,
                    incident.StallRule != StallRules.TranscodeDisappeared && PlexWatchdogPolicy.IsSafeId(incident.TranscodeSessionId),
                    incident.TranscodeSessionId);
                kind = plan?.Kind ?? RecoveryPlanner.TerminateOnly;
                incident.ActionKind = kind;
                incident.ProposedAction ??= plan?.Text;
            }

            var stopId = kind == RecoveryPlanner.StopAndTerminate ? incident.TranscodeSessionId : null;
            string? stopNote = null;
            try
            {
                var uri = PlexServerUrl.ToUri(server.BaseUrl);
                if (PlexWatchdogPolicy.IsSafeId(stopId))
                {
                    try
                    {
                        await sessions.StopTranscodeAsync(uri, token, stopId!, cancellationToken);
                    }
                    catch (PlexRequestException exception)
                    {
                        stopNote = "The transcode stop failed. " + exception.Message;
                    }
                }

                await sessions.TerminateAsync(uri, token, incident.PlaySessionId, cancellationToken);
            }
            catch (PlexRequestException exception)
            {
                var detail = SecretRedactor.ForDisplay((stopNote is null ? "" : stopNote + " ") + exception.Message, token);
                incident.ActionStatus = WatchdogActionStatus.Failed;
                incident.ActionDetail = detail;
                incident.ActionUtc = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Watchdog recovery for incident {IncidentId} on server {ServerId} failed.", incident.Id, server.Id);
                return new WatchdogActionResult(false, detail);
            }

            ends.Mark(server.Id, incident.PlaySessionId);
            incident.ActionStatus = WatchdogActionStatus.Done;
            incident.ActionUtc = clock.GetUtcNow();
            incident.ActionDetail = SecretRedactor.ForDisplay(
                stopNote is null
                    ? (kind == RecoveryPlanner.StopAndTerminate ? "Stopped the transcode and ended the play session." : "Ended the play session.")
                    : stopNote + " The play session was ended.",
                token);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Watchdog recovery for incident {IncidentId} on server {ServerId} ended the session.", incident.Id, server.Id);
            return new WatchdogActionResult(true, incident.ActionDetail ?? "Ended the play session.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Unprotect(PlexServerConnection entity)
    {
        if (string.IsNullOrEmpty(entity.ProtectedToken))
            throw new PlexBridgeException("A Plex token is required.");
        try
        {
            return protector.Unprotect(entity.ProtectedToken);
        }
        catch (Exception exception) when (exception is CryptographicException)
        {
            logger.LogWarning("A stored Plex token could not be decrypted for server {ServerId}.", entity.Id);
            throw new PlexBridgeException("The saved token can't be read. Enter it again. This usually means the keys folder changed.");
        }
    }
}
