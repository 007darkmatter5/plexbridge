namespace PlexBridge.Lyrics;

/// <summary>Statuses shown on the Lyrics page. The first five are the ones a scan always distinguishes.</summary>
public static class LyricsStatus
{
    public const string Synced = "Synced found";
    public const string Plain = "Plain only";
    public const string NotFound = "Not found";
    public const string Instrumental = "Instrumental";
    public const string AlreadyHas = "Already has lyrics";
    public const string NotReachable = "Path not reachable";
    public const string NotWritable = "Path not writable";
    public const string LookupFailed = "Lookup failed";
    public const string WaitingForSync = "Waiting for sync";
    public const string NotPickedUp = "Not picked up by Plex yet";

    public static bool CanWrite(string status) => status is Synced or Plain;
}

/// <summary>Rows kept until Plex reports a lyrics stream for a file this app wrote.</summary>
public static class LyricsSyncStatus
{
    public const string Waiting = "Waiting for sync";
    public const string Done = "Plex has the lyrics";
    public const string Missed = "Not picked up by Plex yet";
}

/// <summary>One Plex path prefix mapped onto a path inside this container.</summary>
public sealed record LyricsPathMap(string PlexPrefix, string ContainerPrefix);

/// <summary>Shown after a library folder is checked against the container mount.</summary>
public static class LyricsMapStatus
{
    public const string Ok = "OK";
    public const string NotFound = "Not found";
    public const string ContainerMissing = "Container folder missing";
    public const string NotWritable = "Not writable";
    public const string NoFiles = "No sample files";
}

/// <summary>One Plex library folder and where it landed in the container, if anywhere.</summary>
public sealed record LyricsLocationMatch(
    string PlexLocation,
    string? ContainerPrefix,
    string Status,
    int SamplesChecked,
    int SamplesFound);

public sealed record LyricsQuery(string Title, string Artist, string Album, int DurationSeconds);

public sealed record LyricsProgress(string Status, int Done, int Total);

/// <summary>
/// How long to wait after a metadata refresh before re-reading the track.
/// Plex applies the refresh in the background, so the lyrics stream is not in the first response.
/// </summary>
public sealed class LyricsRefreshTiming
{
    public TimeSpan FirstWait { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan SecondWait { get; init; } = TimeSpan.FromSeconds(5);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>One music row from Plex. File path is the path Plex reports, not the container path.</summary>
public sealed record PlexCatalogItem(
    string RatingKey,
    string Type,
    string Title,
    string Artist,
    string Album,
    string AlbumKey,
    string ArtistKey,
    string? FilePath,
    int DurationSeconds,
    long? AddedAtUnix,
    bool HasLyricsStream,
    bool HasLocalLyrics,
    string? LyricsCodec);

public sealed record PlexCatalogPage(int? Total, IReadOnlyList<PlexCatalogItem> Items);

/// <summary>One track in a lyrics scan. The lyrics text stays on the server and is not rendered.</summary>
public sealed class LyricsTrackRow
{
    public string RatingKey { get; set; } = "";
    public string AlbumKey { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? PlexPath { get; set; }
    public string? ContainerPath { get; set; }
    public int DurationSeconds { get; set; }
    public long? AddedAtUnix { get; set; }
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool CanSelect { get; set; }
    public bool Selected { get; set; }
    public bool Synced { get; set; }
    public string? LyricsBody { get; set; }
    public string? ApplyResult { get; set; }
    public bool? PlexShowsLyrics { get; set; }
    public bool? PlexShowsLocalLyrics { get; set; }

    public LyricsTrackRow View() => new()
    {
        RatingKey = RatingKey,
        AlbumKey = AlbumKey,
        Artist = Artist,
        Album = Album,
        Title = Title,
        FileName = FileName,
        PlexPath = PlexPath,
        ContainerPath = ContainerPath,
        DurationSeconds = DurationSeconds,
        AddedAtUnix = AddedAtUnix,
        Status = Status,
        Detail = Detail,
        CanSelect = CanSelect,
        Selected = Selected,
        Synced = Synced,
        ApplyResult = ApplyResult,
        PlexShowsLyrics = PlexShowsLyrics,
        PlexShowsLocalLyrics = PlexShowsLocalLyrics
    };
}
