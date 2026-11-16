using PlexBridge.Watchdog;

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

/// <summary>Per-server watchdog switches. Created when the admin saves the Watchdog page. Tokens are not stored here.</summary>
public class WatchdogSetting
{
    public int Id { get; set; }
    public int PlexServerConnectionId { get; set; }
    public PlexServerConnection? Server { get; set; }
    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = WatchdogDefaults.PollSeconds;
    public int OffsetStallSeconds { get; set; } = WatchdogDefaults.OffsetStallSeconds;
    public int SlowTranscodeSeconds { get; set; } = WatchdogDefaults.SlowTranscodeSeconds;
    public int TranscodeMissingSeconds { get; set; } = WatchdogDefaults.TranscodeMissingSeconds;
    public int LongBufferingSeconds { get; set; } = WatchdogDefaults.LongBufferingSeconds;
    public string RecoveryMode { get; set; } = WatchdogRecovery.ReportOnly;
    public string? LogFolder { get; set; }
    public bool UseNotifications { get; set; } = true;
}

/// <summary>One stalled stream. The Plex token is never copied onto this row.</summary>
public class WatchdogIncident
{
    public int Id { get; set; }
    public int? PlexServerConnectionId { get; set; }
    public PlexServerConnection? Server { get; set; }
    public string ServerName { get; set; } = "";
    public int ServerSlot { get; set; }
    public string PlaySessionId { get; set; } = "";
    public string? SessionKey { get; set; }
    public string? TranscodeSessionId { get; set; }
    public string DisplayTitle { get; set; } = "";
    public string? AccountTitle { get; set; }
    public string? PlayerProduct { get; set; }
    public string? PlayerPlatform { get; set; }
    public string? PlayerDevice { get; set; }
    public string? Guids { get; set; }
    public string? RatingKey { get; set; }
    public string? FilePath { get; set; }
    public string? PartId { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? VideoResolution { get; set; }
    public int? BitrateKbps { get; set; }
    public string PlayDecision { get; set; } = "";
    public bool? HardwareTranscode { get; set; }
    public bool? HardwareFullPipeline { get; set; }
    public bool SubtitleBurn { get; set; }
    public string? SubtitleCodec { get; set; }
    public string StallRule { get; set; } = "";
    public string Resolution { get; set; } = IncidentResolutions.StillStuck;
    public string TimelineJson { get; set; } = "[]";
    public string? LogExcerpt { get; set; }
    public string? ProposedAction { get; set; }
    public string? ActionKind { get; set; }
    public string ActionStatus { get; set; } = WatchdogActionStatus.None;
    public string? ActionDetail { get; set; }
    public DateTimeOffset? ActionUtc { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }
}

/// <summary>Per-server lyrics path maps and the optional new-track check. Tokens are not stored here.</summary>
public class LyricsSetting
{
    public int Id { get; set; }
    public int PlexServerConnectionId { get; set; }
    public PlexServerConnection? Server { get; set; }
    public string PathMapsJson { get; set; } = "[]";
    /// <summary>Artist library chosen for lyrics. Null when the row only has saved prefixes.</summary>
    public string? MusicSectionKey { get; set; }
    /// <summary>Title of the artist library, so the page can show the name without the section key.</summary>
    public string? MusicSectionTitle { get; set; }
    /// <summary>Container folder last used from the Lyrics page. The libraries mount is the shared copy.</summary>
    public string? ContainerFolder { get; set; }
    /// <summary>True when Advanced prefixes replace the library mapping.</summary>
    public bool UseManualMaps { get; set; }
    /// <summary>Last library-folder check. Null until a library is checked.</summary>
    public string? LocationMatchesJson { get; set; }
    public bool AutoCheckEnabled { get; set; }
    public int AutoCheckIntervalHours { get; set; } = 12;
    public DateTimeOffset? LastAutoCheckUtc { get; set; }
}

/// <summary>
/// Per-server media mount. Any feature that writes next to a file uses these path maps.
/// The Plex token is not stored here.
/// </summary>
public class LibraryMount
{
    public int Id { get; set; }
    public int PlexServerConnectionId { get; set; }
    public PlexServerConnection? Server { get; set; }
    /// <summary>Folder mounted in this container, usually /libraries. /music still works when that is what was saved.</summary>
    public string? ContainerFolder { get; set; }
    /// <summary>True when Advanced prefixes replace the automatic library mapping.</summary>
    public bool UseManualMaps { get; set; }
    public string PathMapsJson { get; set; } = "[]";
    /// <summary>Last check of each movie, show, and music library. Null until a check is saved.</summary>
    public string? SectionMatchesJson { get; set; }
}

public static class ServerStatuses
{
    public const string NotChecked = "Not checked";
    public const string Online = "Online";
    public const string Unauthorized = "Token rejected";
    public const string Unreachable = "Unreachable";
    public const string Error = "Error";
}
