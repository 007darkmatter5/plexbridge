namespace PlexBridge.Plex;

public enum MigrationCategory
{
    Watched = 1,
    Progress = 2,
    Ratings = 4,
    Playlists = 8
}

public enum MigrationRowKind
{
    WillApply,
    Conflict,
    UnmatchedSource,
    UnmatchedDestination,
    Skipped
}

/// <summary>One library item used for matching. RatingKey is local to the server it was read from.</summary>
public sealed record MigrationItem(
    string RatingKey,
    string Title,
    string Type,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? ShowTitle,
    int ViewCount,
    long ViewOffset,
    decimal? UserRating,
    IReadOnlyList<PlexContentId> Guids,
    string? ParentRatingKey,
    string? GrandparentRatingKey,
    IReadOnlyList<PlexContentId>? GrandparentGuids = null,
    IReadOnlyList<PlexContentId>? ParentGuids = null,
    string? ParentTitle = null,
    long Duration = 0,
    string? Library = null,
    string? FileName = null)
{
    public IReadOnlyList<PlexContentId> ShowIds => GrandparentGuids ?? [];
    public IReadOnlyList<PlexContentId> AlbumIds => ParentGuids ?? [];
    public bool Watched => ViewCount > 0;

    public string Label
    {
        get
        {
            if (Type == "episode" && SeasonNumber is not null && EpisodeNumber is not null)
            {
                var show = string.IsNullOrWhiteSpace(ShowTitle) ? "Show" : ShowTitle;
                return $"{show} S{SeasonNumber:00}E{EpisodeNumber:00} · {Title}";
            }

            return Year is null ? Title : $"{Title} ({Year})";
        }
    }
}

public sealed record MigrationPlaylist(
    string RatingKey,
    string Title,
    string PlaylistType,
    bool Smart,
    IReadOnlyList<MigrationItem> Items);

public sealed record MigrationCatalog(IReadOnlyList<MigrationItem> Items, IReadOnlyList<MigrationPlaylist> Playlists);

public sealed record MigrationPair(MigrationItem Source, MigrationItem Destination);

public sealed record MigrationPlanRow(
    string Key,
    MigrationRowKind Kind,
    string Category,
    string Action,
    string Title,
    string Detail,
    string? SourceValue,
    string? DestinationValue,
    string? TargetRatingKey,
    string? PlaylistRatingKey,
    string? PlaylistType,
    string? PlaylistTitle,
    long? ProgressMs,
    decimal? Rating);

/// <summary>Counts only. The browser renders this, never the full row list.</summary>
public sealed record MigrationPlanSummary(
    int MarkWatched,
    int ClearWatched,
    int SetProgress,
    int SetRating,
    int PlaylistChanges,
    int WillApply,
    int Conflicts,
    int UnmatchedSource,
    int UnmatchedDestination,
    int AlreadySame,
    int OtherSkipped,
    int OutOfDateItems,
    int ConflictItems,
    int UpToDateItems);

public enum ComparisonResult
{
    UpToDate,
    OutOfDate,
    Missing,
    Conflict,
    Skipped,
    OnlyOnDestination
}

/// <summary>One source/destination pair for the comparison table. Action keys are the writes this row controls.</summary>
public sealed record ComparisonRow(
    string Key,
    string ItemType,
    ComparisonResult Result,
    string SourceTitle,
    string SourceDetail,
    string SourceState,
    string DestinationTitle,
    string DestinationDetail,
    string DestinationState,
    string Difference,
    string ActionKeys,
    string ParentKey,
    string Library = "",
    string MatchNote = "",
    string UnmatchedDetail = "")
{
    public IReadOnlyList<string> Keys => string.IsNullOrEmpty(ActionKeys)
        ? []
        : ActionKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

public sealed record ComparisonPage(int Total, int Skip, int Take, IReadOnlyList<ComparisonRow> Rows);

/// <summary>One compared field inside an expanded item row. Action keys are empty when the values already match.</summary>
public sealed record MetricLine(string Name, string SourceValue, string DestinationValue, bool Matches, string ActionKeys)
{
    public IReadOnlyList<string> Keys => string.IsNullOrEmpty(ActionKeys)
        ? []
        : ActionKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>One source playlist member shown when that playlist row is expanded. An empty action key is listed and not written.</summary>
public sealed record PlaylistItemLine(string Title, string Detail, string Status, string ActionKey)
{
    public bool HasAction => ActionKey.Length > 0;
}

public readonly record struct MigrationPageBatch(PlexSectionInfo Section, int Page, int SectionCount, IReadOnlyList<MigrationItem> Items);

public sealed record MigrationDetailPage(int Total, int Skip, int Take, IReadOnlyList<MigrationPlanRow> Rows);

public sealed record MigrationPlan(
    string Id,
    string Fingerprint,
    int SourceServerId,
    int DestinationServerId,
    string SourceName,
    string DestinationName,
    IReadOnlyList<MigrationPlanRow> Rows,
    MigrationPlanSummary Summary,
    IReadOnlyList<ComparisonRow>? Comparisons = null)
{
    public const int PageSize = 50;

    public int WillApply => Summary.WillApply;
    public int Conflicts => Summary.Conflicts;
    public int UnmatchedSource => Summary.UnmatchedSource;
    public int UnmatchedDestination => Summary.UnmatchedDestination;
    public int Skipped => Summary.AlreadySame + Summary.OtherSkipped;
    public bool CanApply => Summary.WillApply + Summary.Conflicts > 0;

    public MigrationDetailPage Page(string? filter, int skip, int take)
    {
        take = Math.Clamp(take, 1, PageSize);
        if (skip < 0)
            skip = 0;
        var matched = new List<MigrationPlanRow>();
        foreach (var row in Rows)
        {
            if (Matches(row, filter))
                matched.Add(row);
        }

        if (skip > matched.Count)
            skip = matched.Count;
        var page = matched.Skip(skip).Take(take).ToList();
        return new MigrationDetailPage(matched.Count, skip, take, page);
    }

    /// <summary>Rough retained size of the stored rows. Already-same items are counts only and are not included.</summary>
    public long EstimatedBytes()
    {
        long bytes = 512;
        foreach (var row in Rows)
        {
            bytes += 96;
            bytes += Chars(row.Key) + Chars(row.Title) + Chars(row.Detail) + Chars(row.SourceValue) + Chars(row.DestinationValue);
            bytes += Chars(row.TargetRatingKey) + Chars(row.PlaylistTitle) + Chars(row.PlaylistType) + Chars(row.PlaylistRatingKey);
            bytes += Chars(row.Category) + Chars(row.Action);
        }

        if (Comparisons is not null)
        {
            foreach (var row in Comparisons)
            {
                bytes += 64;
                bytes += Chars(row.Key) + Chars(row.SourceTitle) + Chars(row.SourceDetail) + Chars(row.SourceState);
                bytes += Chars(row.DestinationTitle) + Chars(row.DestinationDetail) + Chars(row.DestinationState);
                bytes += Chars(row.Difference) + Chars(row.ActionKeys) + Chars(row.ParentKey);
            }
        }

        return bytes;
    }

    public ComparisonPage ComparisonPage(string? result, string? type, string? search, int skip, int take, string? playlistKey = null)
        => Slice(Comparisons ?? [], result, type, search, skip, take, playlistKey);

    public static ComparisonPage Slice(IReadOnlyList<ComparisonRow> rows, string? result, string? type, string? search, int skip, int take, string? playlistKey = null)
    {
        take = Math.Clamp(take, 1, PageSize);
        if (skip < 0)
            skip = 0;
        var matched = new List<ComparisonRow>();
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(playlistKey))
            {
                if (row.ParentKey == playlistKey && SearchHit(row, search))
                    matched.Add(row);
                continue;
            }

            if (!string.IsNullOrEmpty(row.ParentKey))
                continue;
            if (!ResultHit(row, result) || !TypeHit(row, type) || !SearchHit(row, search))
                continue;
            matched.Add(row);
        }

        if (skip > matched.Count)
            skip = matched.Count;
        return new ComparisonPage(matched.Count, skip, take, matched.Skip(skip).Take(take).ToList());
    }

    private static bool ResultHit(ComparisonRow row, string? result)
        => result switch
        {
            null or "" or "out-of-date" or "changes" => row.Result == ComparisonResult.OutOfDate,
            "missing" => row.Result == ComparisonResult.Missing,
            "up-to-date" => row.Result == ComparisonResult.UpToDate,
            "conflict" => row.Result == ComparisonResult.Conflict,
            "only-on-destination" => row.Result == ComparisonResult.OnlyOnDestination,
            "skipped" => row.Result == ComparisonResult.Skipped,
            "all" => true,
            _ => row.Result == ComparisonResult.OutOfDate
        };

    private static bool TypeHit(ComparisonRow row, string? type)
        => string.IsNullOrEmpty(type) || type == "all" || row.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase);

    private static bool SearchHit(ComparisonRow row, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return row.SourceTitle.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.DestinationTitle.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.SourceDetail.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int Chars(string? value) => (value?.Length ?? 0) * 2;

    private static bool Matches(MigrationPlanRow row, string? filter)
        => filter switch
        {
            null or "" or "changes" => row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict,
            "watched" => row.Category == "Watched" && row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict,
            "progress" => row.Category == "Progress" && row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict,
            "rating" => row.Category == "Rating" && row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict,
            "playlist" => row.Category == "Playlist" && row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict,
            "unmatched-source" => row.Kind == MigrationRowKind.UnmatchedSource,
            "unmatched-destination" => row.Kind == MigrationRowKind.UnmatchedDestination,
            "skipped" => row.Kind == MigrationRowKind.Skipped,
            _ => row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict
        };
}

public sealed record MigrationApplyItem(string Title, string Action, string Outcome, string? Detail);

public sealed record MigrationApplyProgress(string Status, int Index, int Total, MigrationApplyItem? Item);

/// <summary>One page of a preview read. The role is Source or Destination. No URL or token is included.</summary>
public sealed record MigrationReadProgress(
    string Role,
    string ServerName,
    string Phase,
    string Library,
    string State,
    int Page,
    int ItemsRead);

public sealed record MigrationPhaseView(
    string Role,
    string Phase,
    string Library,
    string State,
    int Page,
    int ItemsRead,
    double ElapsedSeconds);

public sealed record MigrationApplyResult(
    int Applied,
    int Failed,
    int Skipped,
    bool Canceled,
    IReadOnlyList<MigrationApplyItem> Items);
