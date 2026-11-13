using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

public sealed record PlexLinkView(bool Linked, string? Username, string? Key = null);

public sealed record PlexPinView(string AuthorizeUrl, DateTimeOffset ExpiresUtc);

public enum PlexPinState
{
    Pending,
    Authorized,
    Expired,
    Canceled,
    SlowDown,
    Failed
}

public sealed record PlexPinPollView(PlexPinState State, string Message, PlexLinkView? Link);

public sealed record ProbeAttemptView(string Uri, string Outcome, string Detail);

public sealed record ServerProbeView(bool Connected, string? ChosenUri, IReadOnlyList<ProbeAttemptView> Attempts);

public sealed class ActivePlexPin
{
    public required long PinId { get; init; }
    public required string Code { get; init; }
    public required string ClientIdentifier { get; init; }
    public required DateTimeOffset ExpiresUtc { get; init; }
    public required string AuthorizeUrl { get; init; }
    public DateTimeOffset LastPollUtc { get; set; } = DateTimeOffset.MinValue;
}

public enum PinPollDecision
{
    Missing,
    Expired,
    TooSoon,
    Ready
}

/// <summary>In-memory PIN sessions. The code never goes into the database or the logs.</summary>
public sealed class PlexPinCoordinator(TimeProvider clock)
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, ActivePlexPin> _pins = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Start(string userId, ActivePlexPin pin)
    {
        lock (_gate)
            _pins[userId] = pin;
    }

    public PinPollDecision BeginPoll(string userId)
    {
        lock (_gate)
        {
            if (!_pins.TryGetValue(userId, out var pin))
                return PinPollDecision.Missing;
            if (clock.GetUtcNow() >= pin.ExpiresUtc)
            {
                _pins.Remove(userId);
                return PinPollDecision.Expired;
            }
            if (clock.GetUtcNow() - pin.LastPollUtc < MinimumInterval)
                return PinPollDecision.TooSoon;
            pin.LastPollUtc = clock.GetUtcNow();
            return PinPollDecision.Ready;
        }
    }

    public bool TryGet(string userId, out ActivePlexPin pin)
    {
        lock (_gate)
            return _pins.TryGetValue(userId, out pin!);
    }

    public void Clear(string userId)
    {
        lock (_gate)
            _pins.Remove(userId);
    }
}

public sealed class PlexAccountService(
    IAdminGate admin,
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexAuthClient auth,
    PlexReadClient media,
    PlexServerService servers,
    PlexPinCoordinator pins,
    PlexProbeLimits limits,
    TimeProvider clock,
    ILogger<PlexAccountService> logger)
{
    public async Task<PlexLinkView> GetLinkAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var link = await db.PlexAccountLinks.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        return new PlexLinkView(link?.ProtectedAccountToken is not null, link?.Username);
    }

    public async Task<PlexPinView> StartPinAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        var clientId = await GetOrCreateClientIdAsync(userId, cancellationToken);
        var created = await auth.CreatePinAsync(clientId, cancellationToken);
        var url = PlexAuthorizeUrl.Build(clientId, created.Code);
        if (url.Contains("authToken", StringComparison.OrdinalIgnoreCase) || !url.StartsWith("https://app.plex.tv/auth#?", StringComparison.Ordinal))
            throw new PlexBridgeException("The Plex authorization link could not be built.");
        pins.Start(userId, new ActivePlexPin
        {
            PinId = created.Id,
            Code = created.Code,
            ClientIdentifier = clientId,
            ExpiresUtc = created.ExpiresUtc,
            AuthorizeUrl = url
        });
        logger.LogInformation("Started a Plex PIN for the admin.");
        return new PlexPinView(url, created.ExpiresUtc);
    }

    public async Task<PlexPinPollView> PollPinAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        var decision = pins.BeginPoll(userId);
        if (decision == PinPollDecision.Missing)
            return new PlexPinPollView(PlexPinState.Canceled, "There is no Plex authorization in progress.", null);
        if (decision == PinPollDecision.Expired)
            return new PlexPinPollView(PlexPinState.Expired, "The Plex authorization expired. Start it again.", null);
        if (decision == PinPollDecision.TooSoon)
            return new PlexPinPollView(PlexPinState.Pending, "Waiting for you to authorize PlexBridge on Plex.", null);
        if (!pins.TryGet(userId, out var pin))
            return new PlexPinPollView(PlexPinState.Canceled, "There is no Plex authorization in progress.", null);

        try
        {
            var status = await auth.PollPinAsync(pin.ClientIdentifier, pin.PinId, cancellationToken);
            if (!status.Claimed || string.IsNullOrEmpty(status.AccountToken))
                return new PlexPinPollView(PlexPinState.Pending, "Waiting for you to authorize PlexBridge on Plex.", null);

            string? username = null;
            string? plexUserId = null;
            try
            {
                var profile = await auth.GetUserAsync(pin.ClientIdentifier, status.AccountToken, cancellationToken);
                username = profile.Username;
                plexUserId = profile.PlexUserId;
            }
            catch (PlexRequestException)
            {
                username = "Plex account";
            }

            await StoreAccountTokenAsync(userId, pin.ClientIdentifier, status.AccountToken, username, plexUserId, cancellationToken);
            pins.Clear(userId);
            logger.LogInformation("Plex account link saved for the admin.");
            return new PlexPinPollView(PlexPinState.Authorized, "Plex account connected.", new PlexLinkView(true, username));
        }
        catch (PlexRequestException exception) when (exception.Message.Contains("slow down", StringComparison.OrdinalIgnoreCase))
        {
            return new PlexPinPollView(PlexPinState.SlowDown, exception.Message, null);
        }
        catch (PlexRequestException exception)
        {
            return new PlexPinPollView(PlexPinState.Failed, SecretRedactor.ForDisplay(exception.Message, pin.Code), null);
        }
    }

    public async Task CancelPinAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        pins.Clear(userId);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        pins.Clear(userId);
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var link = await db.PlexAccountLinks.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (link is null)
            return;
        link.ProtectedAccountToken = null;
        link.Username = null;
        link.PlexUserId = null;
        link.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Removed the stored Plex account link.");
    }

    public async Task<IReadOnlyList<DiscoveredServerView>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var (clientId, token) = await RequireAccountAsync(cancellationToken);
        var owned = await auth.GetOwnedServersAsync(clientId, token, cancellationToken);
        return PlexAuthJson.WithoutSecrets(owned);
    }

    public async Task<IReadOnlyList<PlexConnectionCandidate>> CandidatesAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var server = await FindOwnedAsync(machineId, cancellationToken);
        return PlexConnectionOrder.Order(server.Connections);
    }

    public async Task<ConnectionCheckSession> BeginCheckAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var server = await FindOwnedAsync(machineId, cancellationToken);
        var session = ConnectionCheckSession.Create(machineId, PlexConnectionOrder.Order(server.Connections), server.HttpsRequired);
        session.ProbeLimit = limits.Limit;
        return session;
    }

    public async Task<ProbeAttemptView> ProbeCandidateAsync(string machineId, string uri, CancellationToken cancellationToken = default)
    {
        var server = await FindOwnedAsync(machineId, cancellationToken);
        var normalized = PlexServerUrl.Normalize(uri);
        if (!normalized.Ok || !Listed(server, normalized.Normalized))
            throw new PlexBridgeException("That address is not one of the connections Plex listed for this server.");
        return await ProbeOneAsync(normalized.Normalized, server.AccessToken, cancellationToken);
    }

    public async Task<ServerProbeView> ProbeAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var uris = await CandidatesAsync(machineId, cancellationToken);
        var attempts = new List<ProbeAttemptView>();
        foreach (var candidate in uris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts.Add(await ProbeCandidateAsync(machineId, candidate.Uri, cancellationToken));
        }
        if (attempts.Count == 0)
            attempts.Add(new ProbeAttemptView("", "Unreachable", "Plex did not list a connection that can be checked."));
        var reachable = attempts.Where(attempt => attempt.Outcome == "Reachable").Select(attempt => attempt.Uri).ToList();
        return new ServerProbeView(reachable.Count > 0, null, attempts);
    }

    public async Task<PlexServerSummary> SaveDiscoveredAsync(string machineId, string baseUrl, int? replaceServerId, string? name, int slot, CancellationToken cancellationToken = default)
    {
        var server = await FindOwnedAsync(machineId, cancellationToken);
        var normalized = PlexServerUrl.Normalize(baseUrl);
        if (!normalized.Ok)
            throw new PlexBridgeException(normalized.Error);
        if (!Listed(server, normalized.Normalized))
            throw new PlexBridgeException("That address is not one of the connections Plex listed for this server.");
        var check = await ProbeOneAsync(normalized.Normalized, server.AccessToken, cancellationToken);
        if (check.Outcome != "Reachable")
            throw new PlexBridgeException(check.Detail);
        var label = string.IsNullOrWhiteSpace(name) ? server.Name : name.Trim();
        return await servers.SaveAsync(replaceServerId, label, normalized.Normalized, server.AccessToken, cancellationToken, slot);
    }

    private async Task<ProbeAttemptView> ProbeOneAsync(string uri, string token, CancellationToken cancellationToken)
    {
        var normalized = PlexServerUrl.Normalize(uri);
        if (!normalized.Ok)
            return new ProbeAttemptView(uri, "Not allowed", normalized.Error);
        try
        {
            await PlexDeadline.Run(
                limits.Limit,
                cancellationToken,
                probeToken => media.GetIdentityAsync(PlexServerUrl.ToUri(normalized.Normalized), token, probeToken));
            return new ProbeAttemptView(normalized.Normalized, "Reachable", "Read-only identity check succeeded.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlexRequestException exception)
        {
            var detail = SecretRedactor.ForDisplay(exception.Message, token);
            var outcome = detail.Contains("certificate", StringComparison.OrdinalIgnoreCase) ? "Certificate"
                : detail.Contains("rejected", StringComparison.OrdinalIgnoreCase) ? "Token rejected"
                : detail.Contains("timeout", StringComparison.OrdinalIgnoreCase) || detail.Contains("canceled", StringComparison.OrdinalIgnoreCase) || detail.Contains("deadline", StringComparison.OrdinalIgnoreCase) ? "Timed out"
                : detail.Contains("not allowed", StringComparison.OrdinalIgnoreCase) ? "Not allowed"
                : "Unreachable";
            return new ProbeAttemptView(normalized.Normalized, outcome, detail);
        }
    }

    private static bool Listed(OwnedPlexServer server, string normalized)
        => server.Connections.Any(connection =>
            PlexServerUrl.Normalize(connection.Uri) is { Ok: true } parsed
            && string.Equals(parsed.Normalized, normalized, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<OwnedPlexServer>? _owned;

    private async Task<OwnedPlexServer> FindOwnedAsync(string machineId, CancellationToken cancellationToken)
    {
        if (_owned is null)
        {
            var (clientId, token) = await RequireAccountAsync(cancellationToken);
            try
            {
                _owned = await PlexDeadline.Run(
                    limits.Limit,
                    cancellationToken,
                    probeToken => auth.GetOwnedServersAsync(clientId, token, probeToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PlexRequestException exception)
            {
                throw new PlexBridgeException(SecretRedactor.ForDisplay(exception.Message, token));
            }
        }

        return _owned.FirstOrDefault(server => server.ClientIdentifier == machineId)
            ?? throw new PlexBridgeException("That server is not in the owned servers Plex returned.");
    }

    private async Task<(string ClientId, string Token)> RequireAccountAsync(CancellationToken cancellationToken)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var link = await db.PlexAccountLinks.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (link?.ProtectedAccountToken is null)
            throw new PlexBridgeException("Connect the Plex account first.");
        try
        {
            return (link.ClientIdentifier, protector.Unprotect(link.ProtectedAccountToken));
        }
        catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
        {
            logger.LogWarning("The stored Plex account link could not be decrypted.");
            throw new PlexBridgeException("The saved Plex account link can't be read. Connect the account again.");
        }
    }

    private async Task<string> GetOrCreateClientIdAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var link = await db.PlexAccountLinks.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (link is not null)
            return link.ClientIdentifier;
        link = new PlexAccountLink
        {
            UserId = userId,
            ClientIdentifier = Guid.NewGuid().ToString("D"),
            UpdatedUtc = clock.GetUtcNow()
        };
        db.PlexAccountLinks.Add(link);
        await db.SaveChangesAsync(cancellationToken);
        return link.ClientIdentifier;
    }

    private async Task StoreAccountTokenAsync(string userId, string clientId, string token, string? username, string? plexUserId, CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var link = await db.PlexAccountLinks.FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (link is null)
        {
            link = new PlexAccountLink { UserId = userId, ClientIdentifier = clientId };
            db.PlexAccountLinks.Add(link);
        }
        link.ClientIdentifier = clientId;
        link.ProtectedAccountToken = protector.Protect(token);
        link.Username = string.IsNullOrWhiteSpace(username) ? "Plex account" : username.Trim();
        link.PlexUserId = plexUserId;
        link.UpdatedUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
