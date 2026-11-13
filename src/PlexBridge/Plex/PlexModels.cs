namespace PlexBridge.Plex;

public sealed record PlexIdentityInfo(string? MachineIdentifier, string? Version, string? FriendlyName)
{
    public PlexIdentityInfo WithFriendlyName(string? friendlyName)
        => string.IsNullOrWhiteSpace(friendlyName) ? this : this with { FriendlyName = friendlyName };
}

public sealed record PlexSectionInfo(string Key, string Title, string Type, IReadOnlyList<string> Locations);

public sealed record PlexLibraryItem(
    string RatingKey,
    string Title,
    int? Year,
    string Type,
    string? ImdbId,
    string? TmdbId,
    string? TvdbId);

public sealed record PlexItemPage(int Offset, int Total, IReadOnlyList<PlexLibraryItem> Items);

public sealed record PlexServerSummary(
    int Id,
    string Name,
    string BaseUrl,
    bool HasToken,
    bool TokenUnreadable,
    string? MachineIdentifier,
    string? ServerVersion,
    string? FriendlyName,
    string Status,
    string? LastError,
    DateTimeOffset? LastCheckedUtc,
    IReadOnlyList<LibrarySnapshotSummary> Libraries,
    int Slot);

public sealed record LibrarySnapshotSummary(
    int Id,
    string SectionKey,
    string Title,
    string Type,
    int ItemCount,
    IReadOnlyList<string> Locations,
    DateTimeOffset RefreshedUtc);

public sealed record PlexConnectionTestResult(
    bool Ok,
    string Message,
    string? ServerVersion,
    string? FriendlyName,
    string? MachineIdentifier,
    int? LibraryCount);

public sealed record LibraryPreview(int Start, int PageSize, int Total, IReadOnlyList<PlexLibraryItem> Items);
