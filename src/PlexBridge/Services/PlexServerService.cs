using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Services;

public sealed class PlexServerService(
    IDbContextFactory<ApplicationDbContext> databases,
    ISecretProtector protector,
    PlexReadClient plex,
    TimeProvider clock,
    ILogger<PlexServerService> logger)
{
    public async Task<IReadOnlyList<PlexServerSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        await PlaceUnslottedServersAsync(db, cancellationToken);
        var servers = await db.PlexServers
            .Include(server => server.Libraries)
            .OrderBy(server => server.Slot)
            .ThenBy(server => server.Id)
            .ToListAsync(cancellationToken);
        return servers.Select(ToSummary).ToList();
    }

    public async Task<PlexServerSummary?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var server = await db.PlexServers.Include(item => item.Libraries).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return server is null ? null : ToSummary(server);
    }

    public async Task<PlexServerSummary> SaveAsync(int? id, string name, string baseUrl, string? token, CancellationToken cancellationToken = default, int? slot = null)
    {
        var cleanName = CleanName(name);
        if (!PlexServerUrl.TryNormalize(baseUrl, out var normalizedUrl, out var urlError))
            throw new PlexBridgeException(urlError);

        var typedToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        if (typedToken is not null && !PlexTokens.IsPlausible(typedToken))
            throw new PlexBridgeException("The token doesn't look usable. Paste the X-Plex-Token value only.");

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        PlexServerConnection entity;
        if (id is null)
        {
            if (await db.PlexServers.CountAsync(cancellationToken) >= Auth.MaxServers)
                throw new PlexBridgeException("PlexBridge keeps two servers. Remove one before adding another.");
            if (typedToken is null)
                throw new PlexBridgeException("A Plex token is required.");
            entity = new PlexServerConnection { UpdatedUtc = clock.GetUtcNow() };
            db.PlexServers.Add(entity);
        }
        else
        {
            entity = await db.PlexServers.Include(item => item.Libraries).FirstOrDefaultAsync(item => item.Id == id.Value, cancellationToken)
                ?? throw new PlexBridgeException("That server was not found.");
        }

        var urlChanged = !string.Equals(entity.BaseUrl, normalizedUrl, StringComparison.Ordinal);
        var tokenChanged = typedToken is not null;
        entity.Name = cleanName;
        entity.BaseUrl = normalizedUrl;
        entity.UpdatedUtc = clock.GetUtcNow();
        if (typedToken is not null)
            entity.ProtectedToken = protector.Protect(typedToken);
        else if (string.IsNullOrEmpty(entity.ProtectedToken))
            throw new PlexBridgeException("A Plex token is required.");

        var targetSlot = slot ?? (entity.Slot is 1 or 2 ? entity.Slot : await NextFreeSlotAsync(db, entity.Id, cancellationToken));
        if (targetSlot is not (1 or 2))
            throw new PlexBridgeException("Choose server 1 or server 2.");
        var occupant = await db.PlexServers.FirstOrDefaultAsync(item => item.Slot == targetSlot && item.Id != entity.Id, cancellationToken);
        if (occupant is not null)
            throw new PlexBridgeException($"Server {targetSlot} is already {occupant.Name}. Confirm replace before saving over it.");
        entity.Slot = targetSlot;

        if (urlChanged)
        {
            entity.Libraries.Clear();
            entity.MachineIdentifier = null;
            entity.ServerVersion = null;
            entity.FriendlyName = null;
        }

        if (urlChanged || tokenChanged)
        {
            entity.Status = ServerStatuses.NotChecked;
            entity.LastError = null;
            entity.LastCheckedUtc = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Saved Plex server {ServerId}.", entity.Id);
        return ToSummary(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var entity = await db.PlexServers.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        db.PlexServers.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Removed Plex server {ServerId}.", id);
    }

    public async Task<PlexConnectionTestResult> TestAsync(int? id, string baseUrl, string? token, CancellationToken cancellationToken = default)
    {
        if (!PlexServerUrl.TryNormalize(baseUrl, out var normalizedUrl, out var urlError))
            return new PlexConnectionTestResult(false, urlError, null, null, null, null);

        string resolved;
        try
        {
            resolved = await ResolveTokenAsync(id, token, cancellationToken);
        }
        catch (PlexBridgeException exception)
        {
            return new PlexConnectionTestResult(false, exception.Message, null, null, null, null);
        }

        try
        {
            var uri = PlexServerUrl.ToUri(normalizedUrl);
            var identity = await plex.GetIdentityAsync(uri, resolved, cancellationToken);
            var sections = await plex.GetSectionsAsync(uri, resolved, cancellationToken);
            var message = SecretRedactor.ForDisplay(
                $"Connected, read only. {sections.Count} {(sections.Count == 1 ? "library" : "libraries")}.",
                resolved);
            await RememberCheckAsync(id, normalizedUrl, ServerStatuses.Online, null, identity, cancellationToken);
            return new PlexConnectionTestResult(true, message, identity.Version, identity.FriendlyName, identity.MachineIdentifier, sections.Count);
        }
        catch (PlexRequestException exception)
        {
            var safe = SecretRedactor.ForDisplay(exception.Message, resolved);
            var status = safe.Contains("rejected", StringComparison.OrdinalIgnoreCase) ? ServerStatuses.Unauthorized
                : safe.Contains("connect", StringComparison.OrdinalIgnoreCase) || safe.Contains("timeout", StringComparison.OrdinalIgnoreCase) || safe.Contains("resolved", StringComparison.OrdinalIgnoreCase)
                    ? ServerStatuses.Unreachable
                    : ServerStatuses.Error;
            await RememberCheckAsync(id, normalizedUrl, status, safe, null, cancellationToken);
            logger.LogWarning("Plex connection test failed for server {ServerId} with status {Status}.", id, status);
            return new PlexConnectionTestResult(false, safe, null, null, null, null);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Plex connection test failed ({ExceptionType}).", exception.GetType().Name);
            const string safe = "The connection test failed.";
            await RememberCheckAsync(id, normalizedUrl, ServerStatuses.Error, safe, null, cancellationToken);
            return new PlexConnectionTestResult(false, safe, null, null, null, null);
        }
    }

    public async Task<PlexServerSummary> RefreshInventoryAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var entity = await db.PlexServers.Include(item => item.Libraries).FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        var token = Unprotect(entity);
        var uri = PlexServerUrl.ToUri(entity.BaseUrl);

        try
        {
            var identity = await plex.GetIdentityAsync(uri, token, cancellationToken);
            var sections = await plex.GetSectionsAsync(uri, token, cancellationToken);
            var now = clock.GetUtcNow();
            entity.Libraries.Clear();
            foreach (var section in sections)
            {
                var page = await plex.GetPageAsync(uri, token, section.Key, 0, 0, cancellationToken);
                entity.Libraries.Add(new LibrarySnapshot
                {
                    SectionKey = section.Key,
                    Title = section.Title,
                    Type = section.Type,
                    ItemCount = page.Total,
                    LocationsJson = JsonSerializer.Serialize(section.Locations),
                    RefreshedUtc = now
                });
            }

            ApplyIdentity(entity, identity);
            entity.Status = ServerStatuses.Online;
            entity.LastError = null;
            entity.LastCheckedUtc = now;
            entity.UpdatedUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Refreshed inventory for Plex server {ServerId}: {LibraryCount} libraries.", id, sections.Count);
            return ToSummary(entity);
        }
        catch (PlexRequestException exception)
        {
            var safe = SecretRedactor.ForDisplay(exception.Message, token);
            entity.Status = safe.Contains("rejected", StringComparison.OrdinalIgnoreCase) ? ServerStatuses.Unauthorized : ServerStatuses.Error;
            entity.LastError = safe;
            entity.LastCheckedUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Inventory refresh failed for Plex server {ServerId}.", id);
            throw new PlexBridgeException(safe);
        }
        catch (PlexBridgeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Inventory refresh failed ({ExceptionType}) for Plex server {ServerId}.", exception.GetType().Name, id);
            throw new PlexBridgeException("The inventory could not be refreshed.");
        }
    }

    public async Task<LibraryPreview> PreviewAsync(int id, string sectionKey, int start, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var entity = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        if (!await db.LibrarySnapshots.AnyAsync(item => item.PlexServerConnectionId == id && item.SectionKey == sectionKey, cancellationToken))
            throw new PlexBridgeException("That library is not in the saved inventory. Refresh it first.");

        var token = Unprotect(entity);
        try
        {
            var page = await plex.GetPageAsync(PlexServerUrl.ToUri(entity.BaseUrl), token, sectionKey, start, pageSize, cancellationToken);
            return new LibraryPreview(start, pageSize, page.Total, page.Items);
        }
        catch (PlexRequestException exception)
        {
            throw new PlexBridgeException(SecretRedactor.ForDisplay(exception.Message, token));
        }
    }

    private async Task RememberCheckAsync(int? id, string normalizedUrl, string status, string? error, PlexIdentityInfo? identity, CancellationToken cancellationToken)
    {
        if (id is null)
            return;
        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var entity = await db.PlexServers.FirstOrDefaultAsync(item => item.Id == id.Value, cancellationToken);
        if (entity is null || !string.Equals(entity.BaseUrl, normalizedUrl, StringComparison.Ordinal))
            return;
        entity.Status = status;
        entity.LastError = error;
        entity.LastCheckedUtc = clock.GetUtcNow();
        if (identity is not null)
            ApplyIdentity(entity, identity);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyIdentity(PlexServerConnection entity, PlexIdentityInfo identity)
    {
        entity.MachineIdentifier = identity.MachineIdentifier;
        entity.ServerVersion = identity.Version;
        entity.FriendlyName = identity.FriendlyName;
    }

    private async Task<string> ResolveTokenAsync(int? id, string? typed, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(typed))
        {
            var token = typed.Trim();
            if (!PlexTokens.IsPlausible(token))
                throw new PlexBridgeException("The token doesn't look usable. Paste the X-Plex-Token value only.");
            return token;
        }

        if (id is null)
            throw new PlexBridgeException("A Plex token is required.");

        await using var db = await databases.CreateDbContextAsync(cancellationToken);
        var entity = await db.PlexServers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id.Value, cancellationToken)
            ?? throw new PlexBridgeException("That server was not found.");
        return Unprotect(entity);
    }

    private string Unprotect(PlexServerConnection entity)
    {
        if (string.IsNullOrEmpty(entity.ProtectedToken))
            throw new PlexBridgeException("A Plex token is required.");
        try
        {
            return protector.Unprotect(entity.ProtectedToken);
        }
        catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
        {
            logger.LogWarning("A stored Plex token could not be decrypted for server {ServerId}.", entity.Id);
            throw new PlexBridgeException("The saved token can't be read. Enter it again. This usually means the keys folder changed.");
        }
    }

    private PlexServerSummary ToSummary(PlexServerConnection entity)
    {
        var libraries = entity.Libraries
            .OrderBy(library => library.Title)
            .Select(library => new LibrarySnapshotSummary(
                library.Id,
                library.SectionKey,
                library.Title,
                library.Type,
                library.ItemCount,
                DecodeLocations(library.LocationsJson),
                library.RefreshedUtc))
            .ToList();
        var unreadable = false;
        if (!string.IsNullOrEmpty(entity.ProtectedToken))
        {
            try
            {
                _ = protector.Unprotect(entity.ProtectedToken);
            }
            catch (Exception exception) when (Crypto.IsProtectionFailure(exception))
            {
                unreadable = true;
            }
        }

        return new PlexServerSummary(
            entity.Id,
            entity.Name,
            entity.BaseUrl,
            !string.IsNullOrEmpty(entity.ProtectedToken),
            unreadable,
            entity.MachineIdentifier,
            entity.ServerVersion,
            entity.FriendlyName,
            entity.Status,
            entity.LastError,
            entity.LastCheckedUtc,
            libraries,
            entity.Slot);
    }

    private static async Task PlaceUnslottedServersAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.PlexServers.OrderBy(server => server.Id).ToListAsync(cancellationToken);
        var used = rows.Where(server => server.Slot is 1 or 2).Select(server => server.Slot).ToHashSet();
        var changed = false;
        foreach (var row in rows.Where(server => server.Slot is not (1 or 2)))
        {
            var next = !used.Contains(1) ? 1 : !used.Contains(2) ? 2 : 0;
            if (next == 0)
                continue;
            row.Slot = next;
            used.Add(next);
            changed = true;
        }
        if (changed)
            await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int> NextFreeSlotAsync(ApplicationDbContext db, int currentId, CancellationToken cancellationToken)
    {
        var used = await db.PlexServers.Where(server => server.Id != currentId && (server.Slot == 1 || server.Slot == 2)).Select(server => server.Slot).ToListAsync(cancellationToken);
        if (!used.Contains(1))
            return 1;
        if (!used.Contains(2))
            return 2;
        return 0;
    }

    private static IReadOnlyList<string> DecodeLocations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string CleanName(string name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length is 0 or > 80)
            throw new PlexBridgeException("Give the server a name up to 80 characters.");
        if (clean.Any(char.IsControl))
            throw new PlexBridgeException("The server name has characters that can't be used.");
        return clean;
    }
}
