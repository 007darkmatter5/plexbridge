namespace PlexBridge.Data;

/// <summary>One of the two configured Plex Media Servers. The token column is data-protection ciphertext.</summary>
public class PlexServerConnection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? ProtectedToken { get; set; }
    public string? MachineIdentifier { get; set; }
    public string? ServerVersion { get; set; }
    public string? FriendlyName { get; set; }
    public string Status { get; set; } = ServerStatuses.NotChecked;
    /// <summary>1 or 2. Zero means an older row that has not been placed yet.</summary>
    public int Slot { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<LibrarySnapshot> Libraries { get; set; } = [];
}

/// <summary>Last read-only library inventory for a server. Media titles are not stored.</summary>
public class LibrarySnapshot
{
    public int Id { get; set; }
    public int PlexServerConnectionId { get; set; }
    public PlexServerConnection? Server { get; set; }
    public string SectionKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public int ItemCount { get; set; }
    public string LocationsJson { get; set; } = "[]";
    public DateTimeOffset RefreshedUtc { get; set; }
}

/// <summary>One admin's plex.tv link. The account token column is data-protection ciphertext.</summary>
public class PlexAccountLink
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string ClientIdentifier { get; set; } = "";
    public string? ProtectedAccountToken { get; set; }
    public string? Username { get; set; }
    public string? PlexUserId { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>One confirmed migration apply. Tokens and server URLs are not stored here.</summary>
public class MigrationAudit
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int SourceServerId { get; set; }
    public int DestinationServerId { get; set; }
    public string Categories { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? PlexUserUuid { get; set; }
    public string? PlexUserName { get; set; }
    public int AppliedCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public List<MigrationAuditItem> Items { get; set; } = [];
}

public class MigrationAuditItem
{
    public int Id { get; set; }
    public int MigrationAuditId { get; set; }
    public MigrationAudit? Audit { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
}

public static class ServerStatuses
{
    public const string NotChecked = "Not checked";
    public const string Online = "Online";
    public const string Unauthorized = "Token rejected";
    public const string Unreachable = "Unreachable";
    public const string Error = "Error";
}
