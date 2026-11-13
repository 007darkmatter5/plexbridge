using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

public sealed record MigrationPlexUserChoice(string Key, string Name, bool Admin, bool Protected, bool Guest);

/// <summary>
/// Short-lived server tokens for one Plex user. The PIN is never stored.
/// Tokens are encrypted even in this cache and are never written to the log.
/// </summary>
public sealed class MigrationUserTokens(ISecretProtector protector, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Remember(string adminUserId, string plexUserKey, string name, string userToken, IReadOnlyDictionary<string, string> serverTokens)
    {
        var servers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in serverTokens)
            servers[pair.Key] = protector.Protect(pair.Value);
        var entry = new Entry(name, protector.Protect(userToken), servers, clock.GetUtcNow().Add(Lifetime));
        lock (_gate)
            _entries[Key(adminUserId, plexUserKey)] = entry;
    }

    public bool TryGet(string adminUserId, string plexUserKey, out string name, out IReadOnlyDictionary<string, string> serverTokens)
    {
        name = "";
        serverTokens = new Dictionary<string, string>();
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(adminUserId, plexUserKey), out entry))
                return false;
            if (entry.Expires <= clock.GetUtcNow())
            {
                _entries.Remove(Key(adminUserId, plexUserKey));
                return false;
            }
        }

        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entry.Servers)
            plain[pair.Key] = protector.Unprotect(pair.Value);
        name = entry.Name;
        serverTokens = plain;
        return true;
    }

    public void Forget(string adminUserId, string plexUserKey)
    {
        lock (_gate)
            _entries.Remove(Key(adminUserId, plexUserKey));
    }

    private static string Key(string adminUserId, string plexUserKey) => adminUserId + "\n" + plexUserKey;

    private sealed record Entry(string Name, string ProtectedUserToken, Dictionary<string, string> Servers, DateTimeOffset Expires);
}

/// <summary>Lists Plex Home users and turns a choice into per-server tokens. Call this on the page, not from the background job.</summary>
public sealed class MigrationPlexUsers(
    IAdminGate admin,
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexAuthClient auth,
    PlexPinCoordinator pins,
    MigrationUserTokens tokens,
    ILogger<MigrationPlexUsers> logger)
{
    public async Task<IReadOnlyList<MigrationPlexUserChoice>> ListAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        var (clientId, accountToken) = await AccountAsync(userId, cancellationToken);
        IReadOnlyList<PlexHomeUser> home;
        try
        {
            home = await auth.ListHomeUsersAsync(clientId, accountToken, cancellationToken);
        }
        catch (PlexRequestException exception)
        {
            throw new PlexBridgeException(SecretRedactor.ForDisplay(exception.Message, accountToken));
        }

        var choices = home
            .Select(user => new MigrationPlexUserChoice(user.Uuid, user.Name, user.Admin, user.Protected, false))
            .ToList();
        if (choices.All(choice => !choice.Admin))
        {
            var profile = await SafeProfileAsync(clientId, accountToken, cancellationToken);
            var uuid = profile.PlexUserId;
            if (!string.IsNullOrWhiteSpace(uuid) && choices.All(choice => !string.Equals(choice.Key, uuid, StringComparison.OrdinalIgnoreCase)))
                choices.Insert(0, new MigrationPlexUserChoice(uuid, profile.Username ?? "Admin", true, false, false));
        }

        logger.LogInformation("Listed {Count} Plex home users.", choices.Count);
        return choices;
    }

    public async Task<string> ActivateAsync(string plexUserKey, string? pin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plexUserKey))
            throw new PlexBridgeException("Choose a Plex user.");
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        if (string.IsNullOrEmpty(pin) && tokens.TryGet(userId, plexUserKey, out var cachedName, out _))
            return cachedName;

        var (clientId, accountToken) = await AccountAsync(userId, cancellationToken);
        var home = await auth.ListHomeUsersAsync(clientId, accountToken, cancellationToken);
        var match = home.FirstOrDefault(user => string.Equals(user.Uuid, plexUserKey, StringComparison.OrdinalIgnoreCase));
        if (match is not null && match.Protected && string.IsNullOrWhiteSpace(pin))
            throw new PlexBridgeException("Enter the PIN for " + match.Name + ".");

        string userToken;
        try
        {
            userToken = await auth.SwitchHomeUserAsync(clientId, accountToken, plexUserKey, pin, cancellationToken);
        }
        catch (PlexRequestException) when (!string.IsNullOrEmpty(pin))
        {
            throw new PlexBridgeException("That PIN was not accepted.");
        }
        catch (PlexRequestException exception)
        {
            throw new PlexBridgeException(SecretRedactor.ForDisplay(exception.Message, accountToken));
        }

        IReadOnlyDictionary<string, string> servers;
        try
        {
            servers = await auth.GetServerAccessTokensAsync(clientId, userToken, cancellationToken);
        }
        catch (PlexRequestException exception)
        {
            throw new PlexBridgeException(SecretRedactor.ForDisplay(exception.Message, userToken));
        }

        var name = match?.Name ?? "Plex user";
        tokens.Remember(userId, plexUserKey, name, userToken, servers);
        logger.LogInformation("Cached server access for Plex user {PlexUserId}. {ServerCount} servers.", plexUserKey, servers.Count);
        return name;
    }

    public async Task<PlexPinView> StartGuestPinAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        var (clientId, _) = await AccountAsync(userId, cancellationToken);
        var created = await auth.CreatePinAsync(clientId, cancellationToken);
        var url = PlexAuthorizeUrl.Build(clientId, created.Code);
        pins.Start(GuestKey(userId), new ActivePlexPin
        {
            PinId = created.Id,
            Code = created.Code,
            ClientIdentifier = clientId,
            ExpiresUtc = created.ExpiresUtc,
            AuthorizeUrl = url
        });
        return new PlexPinView(url, created.ExpiresUtc);
    }

    public async Task<PlexPinPollView> PollGuestPinAsync(CancellationToken cancellationToken = default)
    {
        var userId = await admin.RequireUserIdAsync(cancellationToken);
        var decision = pins.BeginPoll(GuestKey(userId));
        if (decision == PinPollDecision.Missing)
            return new PlexPinPollView(PlexPinState.Canceled, "There is no Plex sign-in in progress.", null);
        if (decision == PinPollDecision.Expired)
            return new PlexPinPollView(PlexPinState.Expired, "The Plex sign-in expired. Start it again.", null);
        if (decision == PinPollDecision.TooSoon)
            return new PlexPinPollView(PlexPinState.Pending, "Waiting for that Plex account to authorize PlexBridge.", null);
        if (!pins.TryGet(GuestKey(userId), out var pin))
            return new PlexPinPollView(PlexPinState.Canceled, "There is no Plex sign-in in progress.", null);

        var status = await auth.PollPinAsync(pin.ClientIdentifier, pin.PinId, cancellationToken);
        if (!status.Claimed || string.IsNullOrEmpty(status.AccountToken))
            return new PlexPinPollView(PlexPinState.Pending, "Waiting for that Plex account to authorize PlexBridge.", null);

        var profile = await SafeProfileAsync(pin.ClientIdentifier, status.AccountToken, cancellationToken);
        var uuid = string.IsNullOrWhiteSpace(profile.PlexUserId) ? "guest" : profile.PlexUserId;
        var servers = await auth.GetServerAccessTokensAsync(pin.ClientIdentifier, status.AccountToken, cancellationToken);
        var name = profile.Username ?? "Plex account";
        var key = "guest:" + uuid;
        tokens.Remember(userId, key, name, status.AccountToken, servers);
        pins.Clear(GuestKey(userId));
        logger.LogInformation("A separate Plex account is ready for migration. User {PlexUserId}.", uuid);
        return new PlexPinPollView(PlexPinState.Authorized, name, new PlexLinkView(true, name, key));
    }

    private async Task<(string ClientId, string Token)> AccountAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var link = await db.PlexAccountLinks.AsNoTracking().FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (link?.ProtectedAccountToken is null)
            throw new PlexBridgeException("Connect the Plex account before choosing a home user.");
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

    private async Task<PlexAccountProfile> SafeProfileAsync(string clientId, string token, CancellationToken cancellationToken)
    {
        try
        {
            return await auth.GetUserAsync(clientId, token, cancellationToken);
        }
        catch (PlexRequestException)
        {
            return new PlexAccountProfile("Plex account", null);
        }
    }

    private static string GuestKey(string userId) => userId + "|migration-guest";
}
