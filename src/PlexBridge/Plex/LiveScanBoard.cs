namespace PlexBridge.Plex;

/// <summary>One line of the live SOURCE | DESTINATION | RESULT table.</summary>
public sealed record ScanLine(
    string Key,
    string Section,
    bool Header,
    string SourceText,
    string DestinationText,
    string ResultText,
    ComparisonResult? Result,
    string ItemType,
    string ActionKeys,
    string Tone,
    IReadOnlyList<MetricLine>? Metrics = null,
    string SourceDetail = "",
    string DestinationDetail = "",
    bool SourceAbsent = false,
    bool DestinationAbsent = false,
    IReadOnlyList<PlaylistItemLine>? PlaylistItems = null,
    bool Settled = false,
    bool JustApplied = false,
    string MatchNote = "",
    string UnmatchedDetail = "")
{
    public IReadOnlyList<string> Keys => Settled || string.IsNullOrEmpty(ActionKeys)
        ? []
        : ActionKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public IReadOnlyList<MetricLine> MetricRows => Metrics ?? [];
    public IReadOnlyList<PlaylistItemLine> Items => PlaylistItems ?? [];
    public bool Expandable => MetricRows.Any(metric => !metric.Matches) || Items.Count > 0;
    public bool NoAction => !Expandable && Keys.Count == 0 && Result is ComparisonResult.OnlyOnDestination or ComparisonResult.Missing;
}

public sealed record ScanPage(int Total, int Skip, int Take, IReadOnlyList<ScanLine> Rows);

/// <summary>One library group. Rows are the current page inside that library, not a global list.</summary>
public sealed record LibrarySection(
    string Name,
    string SourceLabel,
    string DestinationLabel,
    string Summary,
    bool Scanning,
    int Total,
    int Skip,
    IReadOnlyList<ScanLine> Rows,
    string ActionKeys,
    string SummaryNote = "")
{
    public IReadOnlyList<string> Keys => string.IsNullOrEmpty(ActionKeys)
        ? []
        : ActionKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The comparison table while a preview is reading. Source rows appear in library order.
/// A destination cell stays "scanning…" until that destination library has been indexed.
/// </summary>
public sealed record AppliedObservation(
    string ActionKey,
    bool Reflected,
    string? PlaylistTitle,
    string? PlaylistDetail,
    bool? Watched,
    long? ViewOffset,
    decimal? UserRating);

public sealed class LiveScanBoard
{
    private static readonly string[] Order = ["Movies", "TV episodes", "Music tracks", "Other Videos", "Playlists"];
    private readonly List<Section> _sections = [];
    private readonly List<MigrationItem> _sourceItems = [];
    private readonly List<MigrationItem> _destItems = [];
    private readonly HashSet<string> _matchedDest = new(StringComparer.Ordinal);
    private Dictionary<string, List<MigrationItem>>? _destIndex;
    private IReadOnlySet<MigrationCategory> _categories;
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private bool _sourceClosed;
    private bool _destClosed;

    public LiveScanBoard(IReadOnlySet<MigrationCategory>? categories = null)
    {
        _categories = categories ?? new HashSet<MigrationCategory>();
    }

    public void Use(IReadOnlySet<MigrationCategory> categories) => _categories = categories;

    public int OutOfDate { get; private set; }
    public int Conflict { get; private set; }
    public int Missing { get; private set; }
    public int UpToDate { get; private set; }
    public int OnlyDestination { get; private set; }
    public int Skipped { get; private set; }

    public MigrationPlanSummary ToSummary()
        => new(0, 0, 0, 0, 0, 0, Conflict, Missing, OnlyDestination, UpToDate, Skipped, OutOfDate, Conflict, UpToDate);

    public void ApplyProgress(MigrationReadProgress update)
    {
        if (update.Phase is "Matching" or "Building plan" or "Catalog" || update.Phase.Length == 0)
            return;
        var key = update.Role + "|" + update.Phase + "|" + update.Library;
        if (update.State == "Waiting")
        {
            if (_finished.Contains(key) || !_waiting.Add(key))
                return;
            Expect(update.Role, update.Phase);
        }
        else if (update.State == "Reading")
            Side(Ensure(update.Phase), update.Role).Reading = true;
        else if (update.State == "Done")
        {
            if (!_finished.Add(key))
                return;
            FinishLibrary(update.Role, update.Phase, update.ItemsRead);
        }
        else if (update.State == "Failed")
            Fail(update.Role, update.Phase);
    }

    public void Expect(string role, string section)
    {
        var side = Side(Ensure(section), role);
        side.Expected++;
        side.Listed = true;
    }

    public void IngestSource(string section, IReadOnlyList<MigrationItem> items, bool confirmed)
    {
        var slot = Ensure(section);
        foreach (var item in items)
        {
            var row = slot.SourceRows.FirstOrDefault(existing => existing.SourceKey == item.RatingKey);
            if (row is null)
            {
                row = new ItemRow { SourceKey = item.RatingKey, LineKey = item.RatingKey, ItemType = item.Type };
                slot.SourceRows.Add(row);
                _sourceItems.Add(item);
            }

            row.Item = item;
            row.SourceConfirmed = confirmed;
            ShowSource(row, item, section, confirmed);
            if (confirmed && Ready(slot, destination: true))
                ResolveItem(slot, row);
        }
    }

    public void IngestDestination(string section, IReadOnlyList<MigrationItem> items)
    {
        var slot = Ensure(section);
        foreach (var item in items)
        {
            if (slot.DestKeys.Add(item.RatingKey))
            {
                slot.DestItems.Add(item);
                _destItems.Add(item);
                _destIndex = null;
            }
        }

        if (Ready(slot, destination: true))
            Rematch(slot);
    }

    private void Rematch(Section section)
    {
        foreach (var row in section.SourceRows)
        {
            if (!row.SourceConfirmed || row.Item is null)
                continue;
            if (row.DestResolved && row.Result != ComparisonResult.Missing)
                continue;
            if (row.Result is not null)
                Count(row.Result.Value, -1);
            row.Result = null;
            row.DestResolved = false;
            ResolveItem(section, row);
        }

        if (!Ready(section, destination: false))
            return;
        foreach (var row in section.DestOnly)
        {
            if (row.Result is not null)
                Count(row.Result.Value, -1);
            if (row.LineKey.StartsWith("d|", StringComparison.Ordinal))
                _matchedDest.Remove(row.LineKey[2..]);
        }

        section.DestOnly.Clear();
        section.DestOnlyAppended = false;
        AppendDestinationOnly(section);
    }

    public void AddPlaylist(string role, MigrationPlaylist playlist, bool confirmed)
    {
        var slot = Ensure("Playlists");
        if (role == "Destination")
        {
            if (slot.DestPlaylists.TrueForAll(existing => existing.RatingKey != playlist.RatingKey))
                slot.DestPlaylists.Add(playlist);
            if (Ready(slot, destination: true))
                ResolvePlaylists(slot);
            return;
        }

        var key = "playlist|" + playlist.PlaylistType + "|" + playlist.Title;
        var row = slot.SourceRows.FirstOrDefault(existing => existing.SourceKey == key);
        if (row is null)
        {
            row = new ItemRow { SourceKey = key, LineKey = key, ItemType = "playlist" };
            slot.SourceRows.Add(row);
        }

        row.Playlist = playlist;
        row.SourceTitle = playlist.Title;
        row.SourceConfirmed = confirmed;
        row.SourceText = confirmed ? playlist.Title : "scanning " + playlist.Title;
        row.SourceDetail = confirmed ? playlist.PlaylistType + " playlist" : "";
        if (confirmed && Ready(slot, destination: true))
            ResolvePlaylist(slot, row);
    }

    public void FinishLibrary(string role, string section, int count)
    {
        var slot = Ensure(section);
        var side = Side(slot, role);
        if (side.Expected == 0)
            side.Expected = 1;
        side.Finished++;
        side.Count += count;
        side.Reading = false;
        TryComplete(slot);
    }

    public void Fail(string role, string section)
    {
        var slot = Ensure(section);
        var side = Side(slot, role);
        side.Failed = true;
        side.Reading = false;
        TryComplete(slot);
    }

    public void CloseLibraries(string role)
    {
        if (role == "Destination")
            _destClosed = true;
        else
            _sourceClosed = true;
        foreach (var section in _sections)
            TryComplete(section);
    }

    public void Stamp(IReadOnlyList<ComparisonRow>? comparisons, IReadOnlyList<MigrationPlanRow>? planRows = null)
    {
        if (comparisons is null)
            return;
        foreach (var comparison in comparisons)
        {
            if (!string.IsNullOrEmpty(comparison.ParentKey))
                continue;
            var row = Find(comparison.Key);
            if (row is null)
                continue;
            Remember(row, comparison.Result);
            row.ActionKeys = comparison.ActionKeys;
            row.Result = comparison.Result;
            row.ResultText = ResultLine(comparison.Result, comparison.Difference);
            row.Tone = ToneOf(comparison.Result);
            row.DestResolved = true;
            row.SourceConfirmed = true;
            if (row.Item is not null)
                ShowSource(row, row.Item, SectionName(row), true);
            else if (!string.IsNullOrEmpty(comparison.SourceTitle))
                row.SourceText = comparison.SourceTitle;
            if (row.Partner is not null)
                ShowDestination(row, row.Partner, SectionName(row));
            else if (row.Result == ComparisonResult.Missing || string.IsNullOrEmpty(comparison.DestinationTitle))
                ShowDestination(row, null, SectionName(row));
            else
                row.DestinationText = comparison.DestinationTitle;
            if (!string.IsNullOrEmpty(comparison.DestinationTitle))
                row.DestTitle = comparison.DestinationTitle;
            RefreshMetrics(row, planRows);
            if (row.Playlist is not null)
                DescribePlaylist(row, planRows);
            row.MatchNote = comparison.MatchNote;
            row.UnmatchedDetail = comparison.UnmatchedDetail;
            if (row.Result == ComparisonResult.Missing && row.Playlist is null)
                row.ResultText = NotOnDestination;
        }
    }

    public void NoteVerified(string actionKey, bool verified)
    {
        if (verified)
        {
            NoteSettled(actionKey);
            return;
        }

        foreach (var section in _sections)
        {
            foreach (var row in section.SourceRows.Concat(section.DestOnly))
            {
                if (!Holds(row, actionKey))
                    continue;
                row.ResultText = "not reflected";
                row.Tone = "conflict";
            }
        }
    }

    public void NoteSettled(string actionKey) => NoteApplied(new AppliedObservation(actionKey, true, null, null, null, null, null));

    public void NoteApplied(AppliedObservation update)
    {
        foreach (var section in _sections)
        {
            foreach (var row in section.SourceRows.Concat(section.DestOnly))
            {
                if (!Holds(row, update.ActionKey))
                    continue;
                if (update.Reflected)
                    DropKey(row, update.ActionKey);
                if (!string.IsNullOrEmpty(update.PlaylistTitle))
                {
                    row.DestinationAbsent = false;
                    row.DestinationText = update.PlaylistTitle;
                    row.DestTitle = update.PlaylistTitle;
                    row.DestinationDetail = update.PlaylistDetail ?? "";
                }

                TouchMetric(row, update);
                FinishRow(row);
            }
        }
    }

    private static void DropKey(ItemRow row, string actionKey)
    {
        row.ActionKeys = string.Join('\n', SplitKeys(row.ActionKeys).Where(key => !key.Equals(actionKey, StringComparison.Ordinal)));
        row.Metrics = row.Metrics.Select(metric => metric.Keys.Contains(actionKey, StringComparer.Ordinal)
            ? metric with { ActionKeys = string.Join('\n', metric.Keys.Where(key => !string.Equals(key, actionKey, StringComparison.Ordinal))) }
            : metric).ToList();
        row.PlaylistItems = row.PlaylistItems.Select(item => string.Equals(item.ActionKey, actionKey, StringComparison.Ordinal)
            ? item with { ActionKey = "", Status = "matched on destination ✓" }
            : item).ToList();
    }

    private static void TouchMetric(ItemRow row, AppliedObservation update)
    {
        var name = MetricName(update.ActionKey);
        if (name is null)
            return;
        var value = name switch
        {
            "Watched status" => update.Watched == true ? "Watched" : "Not watched",
            "Viewing progress" => update.ViewOffset is > 0 ? MigrationPlanBuilder.Clock(update.ViewOffset.Value) : "No progress",
            "Your rating" => update.UserRating is null ? "No rating" : update.UserRating.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
            _ => ""
        };
        row.Metrics = row.Metrics.Select(metric => metric.Name == name
            ? metric with { DestinationValue = value, Matches = update.Reflected, ActionKeys = update.Reflected ? "" : metric.ActionKeys }
            : metric).ToList();
    }

    private static string? MetricName(string actionKey)
    {
        var prefix = actionKey.Split('|')[0];
        return prefix switch
        {
            "watched" => "Watched status",
            "progress" => "Viewing progress",
            "rating" => "Your rating",
            _ => null
        };
    }

    private void FinishRow(ItemRow row)
    {
        if (RowActionKeys(row).Any())
        {
            var next = row.Result == ComparisonResult.Conflict ? ComparisonResult.Conflict : ComparisonResult.OutOfDate;
            Remember(row, next);
            row.Result = next;
            row.Settled = false;
            row.JustApplied = false;
            row.Tone = "out";
            row.ResultText = row.ItemType == "playlist" && row.Metrics.Count == 0 ? "playlist mismatch" : ShortResult(row);
            return;
        }

        Remember(row, ComparisonResult.UpToDate);
        row.Result = ComparisonResult.UpToDate;
        row.Settled = true;
        row.JustApplied = true;
        row.ActionKeys = "";
        row.Tone = "match";
        row.ResultText = "matching";
    }

    private static bool Holds(ItemRow row, string actionKey)
        => SplitKeys(row.ActionKeys).Contains(actionKey, StringComparer.Ordinal)
            || row.Metrics.Any(metric => metric.Keys.Contains(actionKey, StringComparer.Ordinal))
            || row.PlaylistItems.Any(item => item.ActionKey.Equals(actionKey, StringComparison.Ordinal));

    private static string[] SplitKeys(string keys)
        => keys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> ChangeKeys(string? section, string? filter, string? search)
    {
        var keys = new List<string>();
        foreach (var slot in Ordered())
        {
            if (!string.IsNullOrEmpty(section) && slot.Name != section)
                continue;
            foreach (var row in slot.SourceRows)
            {
                if (filter is not (null or "" or "all") && !RowShown(row, filter))
                    continue;
                if (!string.IsNullOrWhiteSpace(search) && !SearchHit(row, search))
                    continue;
                keys.AddRange(RowActionKeys(row));
            }
        }

        return keys;
    }

    public void ReleaseItems()
    {
        _sourceItems.Clear();
        _destItems.Clear();
        _destIndex = null;
        foreach (var section in _sections)
        {
            section.DestItems.Clear();
            section.DestPlaylists.Clear();
            foreach (var row in section.SourceRows.Concat(section.DestOnly))
            {
                row.Item = null;
                row.Partner = null;
                row.Playlist = null;
            }
        }
    }

    public int LineCount()
    {
        var count = 0;
        foreach (var section in Ordered())
        {
            if (!Shown(section))
                continue;
            count += 1 + section.SourceRows.Count + section.DestOnly.Count;
        }

        return count;
    }

    public IReadOnlyList<LibrarySection> Libraries(
        string? filter,
        string? search,
        IReadOnlyDictionary<string, int>? skips,
        IReadOnlySet<string>? expanded,
        IReadOnlySet<string>? collapsed)
    {
        var sections = new List<LibrarySection>();
        foreach (var section in Ordered())
        {
            if (!Shown(section))
                continue;
            var items = FilteredItems(section, filter, null, search);
            var skip = skips is not null && skips.TryGetValue(section.Name, out var requested) ? requested : 0;
            if (skip < 0)
                skip = 0;
            if (skip > items.Count)
                skip = items.Count;
            var scanning = section.Source.Reading || section.Destination.Reading;
            var open = expanded is not null && expanded.Contains(section.Name) && (collapsed is null || !collapsed.Contains(section.Name));
            var rows = open ? items.Skip(skip).Take(MigrationPlan.PageSize).ToList() : [];
            var sourceReady = Ready(section, destination: false);
            var destReady = Ready(section, destination: true);
            var status = LibraryStatus(section, sourceReady, destReady, scanning);
            sections.Add(new LibrarySection(
                section.Name,
                SourceExists(section) ? section.Name : "not on source",
                DestinationExists(section) ? section.Name : "not on destination",
                status.Text,
                scanning,
                items.Count,
                skip,
                rows,
                string.Join('\n', section.SourceRows.SelectMany(RowActionKeys)),
                status.Note));
        }

        return sections;
    }

    public ScanPage Page(string? filter, string? type, string? search, int skip, int take)
    {
        take = Math.Clamp(take, 1, MigrationPlan.PageSize);
        if (skip < 0)
            skip = 0;
        var showAll = filter is null or "" or "all";
        var searching = !string.IsNullOrWhiteSpace(search);
        var typing = !string.IsNullOrEmpty(type) && type != "all";
        var lines = new List<ScanLine>();
        foreach (var section in Ordered())
        {
            if (!Shown(section))
                continue;
            var items = FilteredItems(section, filter, type, search);

            if (items.Count == 0 && (searching || typing || !showAll))
                continue;
            lines.Add(Header(section));
            lines.AddRange(items);
        }

        if (skip > lines.Count)
            skip = lines.Count;
        var window = lines.Skip(skip).Take(take).ToList();
        if (window.Count > 0 && !window[0].Header)
        {
            var header = lines.FirstOrDefault(line => line.Header && line.Section == window[0].Section);
            if (header is not null)
                window.Insert(0, header);
        }

        return new ScanPage(lines.Count, skip, take, window);
    }

    private void TryComplete(Section section)
    {
        if (!Ready(section, destination: true))
            return;
        foreach (var row in section.SourceRows)
        {
            if (!row.SourceConfirmed || row.DestResolved)
                continue;
            if (row.Playlist is not null)
                ResolvePlaylist(section, row);
            else if (row.Item is not null)
                ResolveItem(section, row);
        }

        if (Ready(section, destination: false) && !section.DestOnlyAppended)
            AppendDestinationOnly(section);
    }

    private void ResolveItem(Section section, ItemRow row)
    {
        if (row.Item is null || row.DestResolved)
            return;
        _destIndex ??= MigrationMatcher.Index(_destItems);
        var match = MigrationMatcher.MatchOne(row.Item, _sourceItems, _destItems, _destIndex);
        var classified = MigrationPlanBuilder.LiveClassify(row.Item, match, _categories, section.Name);
        row.Partner = match.Destination;
        row.MatchNote = classified.MatchNote;
        row.UnmatchedDetail = classified.UnmatchedDetail;
        ApplyClassified(row, classified);
        if (match.Destination is not null)
            _matchedDest.Add(match.Destination.RatingKey);
    }

    private void ResolvePlaylists(Section section)
    {
        foreach (var row in section.SourceRows)
        {
            if (row.Playlist is not null && row.SourceConfirmed && !row.DestResolved)
                ResolvePlaylist(section, row);
        }
    }

    private void ResolvePlaylist(Section section, ItemRow row)
    {
        var playlist = row.Playlist;
        if (playlist is null || row.DestResolved)
            return;
        var existing = section.DestPlaylists.FirstOrDefault(candidate =>
            !candidate.Smart
            && string.Equals(candidate.Title, playlist.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.PlaylistType, playlist.PlaylistType, StringComparison.OrdinalIgnoreCase));
        ComparisonResult result;
        string difference;
        if (playlist.Smart || playlist.PlaylistType is not ("video" or "audio"))
        {
            result = ComparisonResult.Skipped;
            difference = playlist.Smart
                ? "Smart playlists are not copied."
                : "Only video and audio playlists are copied.";
        }
        else if (existing is null)
        {
            result = ComparisonResult.Missing;
            difference = "Missing on destination.";
        }
        else if (existing.Items.Count == playlist.Items.Count)
        {
            result = ComparisonResult.UpToDate;
            difference = "Up to date";
        }
        else
        {
            result = ComparisonResult.OutOfDate;
            difference = Math.Abs(playlist.Items.Count - existing.Items.Count) + " playlist items";
        }

        row.DestResolved = true;
        row.Result = result;
        row.ResultText = ResultLine(result, difference);
        row.Tone = ToneOf(result);
        row.DestTitle = existing?.Title ?? "";
        row.DestinationText = existing is null ? "not on destination" : existing.Title;
        row.DestinationAbsent = existing is null;
        row.DestinationDetail = existing is null ? "" : existing.PlaylistType + " playlist";
        Count(result, 1);
        DescribePlaylist(row, null);
    }

    private void AppendDestinationOnly(Section section)
    {
        section.DestOnlyAppended = true;
        if (section.Name == "Playlists")
        {
            foreach (var playlist in section.DestPlaylists)
            {
                var paired = section.SourceRows.Any(row => row.Playlist is not null
                    && string.Equals(row.Playlist.Title, playlist.Title, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.Playlist.PlaylistType, playlist.PlaylistType, StringComparison.OrdinalIgnoreCase));
                if (paired)
                    continue;
                section.DestOnly.Add(OnlyRow("playlist|" + playlist.PlaylistType + "|" + playlist.Title, playlist.Title, "playlist"));
            }

            return;
        }

        foreach (var item in section.DestItems)
        {
            if (!_matchedDest.Add(item.RatingKey))
                continue;
            section.DestOnly.Add(DestinationOnlyRow(item, section.Name));
        }
    }

    private ItemRow DestinationOnlyRow(MigrationItem item, string section)
    {
        var row = new ItemRow
        {
            SourceKey = "d|" + item.RatingKey,
            LineKey = "d|" + item.RatingKey,
            ItemType = item.Type,
            SourceConfirmed = true,
            DestResolved = true,
            Result = ComparisonResult.OnlyOnDestination,
            ResultText = "only on destination",
            Tone = "only"
        };
        ShowMissingSource(row);
        ShowDestination(row, item, section);
        Count(ComparisonResult.OnlyOnDestination, 1);
        return row;
    }

    private ItemRow OnlyRow(string key, string title, string type)
    {
        var row = new ItemRow
        {
            SourceKey = key,
            LineKey = key,
            ItemType = type,
            SourceConfirmed = true,
            DestResolved = true,
            SourceAbsent = true,
            SourceText = "not on source",
            DestinationText = title,
            DestTitle = title,
            Result = ComparisonResult.OnlyOnDestination,
            ResultText = "only on destination",
            Tone = "only"
        };
        Count(ComparisonResult.OnlyOnDestination, 1);
        return row;
    }

    private void ApplyClassified(ItemRow row, ComparisonRow classified)
    {
        if (row.Result is not null)
            Count(row.Result.Value, -1);
        row.LineKey = classified.Key;
        row.DestResolved = true;
        row.Result = classified.Result;
        row.ResultText = ResultLine(classified.Result, classified.Difference);
        row.Tone = ToneOf(classified.Result);
        row.ActionKeys = classified.ActionKeys;
        row.ItemType = classified.ItemType;
        row.DestTitle = classified.DestinationTitle;
        if (row.Partner is null)
        {
            ShowDestination(row, null, SectionName(row));
            row.ResultText = "missing on destination";
        }
        else
            ShowDestination(row, row.Partner, SectionName(row));
        Count(classified.Result, 1);
        RefreshMetrics(row, null);
        row.ResultText = ShortResult(row);
    }

    private void RefreshMetrics(ItemRow row, IReadOnlyList<MigrationPlanRow>? planRows)
    {
        if (row.Item is null || row.Partner is null || row.Playlist is not null)
            return;
        row.Metrics = MigrationPlanBuilder.ItemMetrics(row.Item, row.Partner, _categories, SourcePlaylists(), DestinationPlaylists(), planRows).ToList();
        var keys = row.Metrics.Where(metric => !metric.Matches).SelectMany(metric => metric.Keys).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count > 0)
            row.ActionKeys = string.Join('\n', keys);
        if (row.Metrics.Any(metric => !metric.Matches) && row.Result == ComparisonResult.UpToDate)
        {
            Count(ComparisonResult.UpToDate, -1);
            row.Result = ComparisonResult.OutOfDate;
            row.Tone = "out";
            Count(ComparisonResult.OutOfDate, 1);
        }

        row.ResultText = ShortResult(row);
    }

    private static string ShortResult(ItemRow row)
    {
        if (row.Result == ComparisonResult.Missing)
            return row.Playlist is null ? NotOnDestination : "missing on destination";
        if (row.Result == ComparisonResult.OnlyOnDestination)
            return "only on destination";
        if (row.Result == ComparisonResult.Skipped)
            return "skipped";
        var names = row.Metrics.Where(metric => !metric.Matches).Select(metric => metric.Name).ToList();
        if (names.Count == 0)
        {
            if (row.Result is ComparisonResult.OutOfDate or ComparisonResult.Conflict)
                return row.ItemType == "playlist" ? "playlist mismatch" : "mismatch";
            return "matching";
        }
        return string.Join(" + ", names.Select(name => MetricWord(name, names.Count))) + " mismatch";
    }

    private static string MetricWord(string name, int count) => name switch
    {
        "Watched status" => "watched",
        "Viewing progress" => count == 1 ? "watch progress" : "progress",
        "Your rating" => "rating",
        "Playlist membership" => "playlist",
        _ => name.ToLowerInvariant()
    };

    private (string Text, string Note) LibraryStatus(Section section, bool sourceReady, bool destReady, bool scanning)
    {
        if (scanning || !sourceReady || !destReady)
            return ("scanning…", "");
        var sourceExists = SourceExists(section);
        var destinationExists = DestinationExists(section);
        if (sourceExists && !destinationExists)
            return ("missing on destination", "");
        if (!sourceExists && destinationExists)
            return ("only on destination", "");
        var rows = section.SourceRows.Concat(section.DestOnly).ToList();
        var notes = new List<string>();
        var skipped = rows.Count(row => row.Result == ComparisonResult.Skipped);
        var only = rows.Count(row => row.Result == ComparisonResult.OnlyOnDestination);
        var missing = rows.Count(row => row.Result == ComparisonResult.Missing && !Actionable(row));
        if (skipped > 0)
            notes.Add(skipped + " skipped");
        if (only > 0)
            notes.Add(only + " only on destination");
        if (missing > 0)
            notes.Add(missing + " only on source");
        var note = string.Join(" · ", notes);
        if (rows.Any(Actionable))
            return ("Library exists on both · mismatches", note);
        return ("matching", note);
    }

    private static bool Actionable(ItemRow row) => !row.Settled && RowActionKeys(row).Any();

    private List<MigrationPlaylist> SourcePlaylists()
        => _sections.SelectMany(section => section.SourceRows).Where(row => row.Playlist is not null).Select(row => row.Playlist!).ToList();

    private List<MigrationPlaylist> DestinationPlaylists()
        => _sections.SelectMany(section => section.DestPlaylists).ToList();

    private void Remember(ItemRow row, ComparisonResult updated)
    {
        if (row.Result is not null && row.Result != updated)
        {
            Count(row.Result.Value, -1);
            Count(updated, 1);
        }
        else if (row.Result is null)
            Count(updated, 1);
    }

    private void Count(ComparisonResult result, int delta)
    {
        switch (result)
        {
            case ComparisonResult.OutOfDate: OutOfDate += delta; break;
            case ComparisonResult.Conflict: Conflict += delta; break;
            case ComparisonResult.Missing: Missing += delta; break;
            case ComparisonResult.UpToDate: UpToDate += delta; break;
            case ComparisonResult.OnlyOnDestination: OnlyDestination += delta; break;
            default: Skipped += delta; break;
        }
    }

    private ItemRow? Find(string key)
    {
        foreach (var section in _sections)
        {
            foreach (var row in section.SourceRows.Concat(section.DestOnly))
            {
                if (row.LineKey == key || row.SourceKey == key)
                    return row;
            }
        }

        return null;
    }

    private bool Ready(Section section, bool destination)
    {
        var side = destination ? section.Destination : section.Source;
        var closed = destination ? _destClosed : _sourceClosed;
        if (side.Failed)
            return true;
        if (side.Expected > 0)
            return side.Finished >= side.Expected;
        return closed;
    }

    private static bool SourceExists(Section section)
        => section.Source.Listed || section.SourceRows.Count > 0;

    private static bool DestinationExists(Section section)
        => section.Destination.Listed || section.DestItems.Count > 0 || section.DestOnly.Count > 0;

    private bool Shown(Section section)
        => section.Source.Expected > 0 || section.Destination.Expected > 0 || section.SourceRows.Count > 0 || section.DestOnly.Count > 0;

    private IEnumerable<Section> Ordered()
        => _sections.OrderBy(section =>
        {
            var index = Array.IndexOf(Order, section.Name);
            return index < 0 ? Order.Length : index;
        }).ThenBy(section => section.Name, StringComparer.Ordinal);

    private Section Ensure(string name)
    {
        var found = _sections.FirstOrDefault(section => section.Name == name);
        if (found is not null)
            return found;
        found = new Section(name);
        _sections.Add(found);
        return found;
    }

    private static SideProgress Side(Section section, string role)
        => role == "Destination" ? section.Destination : section.Source;

    private ScanLine Header(Section section)
    {
        var sourceReady = Ready(section, destination: false);
        var destReady = Ready(section, destination: true);
        return new ScanLine(
            "section|" + section.Name,
            section.Name,
            true,
            SideText(section.Source, sourceReady, section.Name),
            SideText(section.Destination, destReady, section.Name),
            HeaderResult(section, sourceReady, destReady),
            null,
            "",
            string.Join('\n', section.SourceRows.SelectMany(RowActionKeys)),
            sourceReady && destReady ? "section" : "scanning");
    }

    private string HeaderResult(Section section, bool sourceReady, bool destReady)
    {
        if (section.Source.Failed || section.Destination.Failed)
            return "failed";
        if (!sourceReady || !destReady)
            return "results";
        if (section.Name == "Playlists" && section.Destination.Count == 0 && section.Source.Count > 0)
            return "these playlists are not in destination";
        var parts = new List<string>();
        var rows = section.SourceRows.Concat(section.DestOnly).ToList();
        AddPart(parts, rows.Count(row => row.Result == ComparisonResult.OutOfDate), "out of date", "out of date");
        AddPart(parts, rows.Count(row => row.Result == ComparisonResult.Conflict), "conflict", "conflicts");
        AddPart(parts, rows.Count(row => row.Result == ComparisonResult.Missing), "missing", "missing");
        AddPart(parts, rows.Count(row => row.Result == ComparisonResult.OnlyOnDestination), "only on destination", "only on destination");
        AddPart(parts, rows.Count(row => row.Result == ComparisonResult.Skipped), "skipped", "skipped");
        AddPart(parts, rows.Count(row => row.Result == ComparisonResult.UpToDate), "matching", "matching");
        return parts.Count == 0 ? "nothing to compare" : string.Join(", ", parts);
    }

    private static void AddPart(List<string> parts, int count, string singular, string plural)
    {
        if (count > 0)
            parts.Add(count + " " + (count == 1 ? singular : plural));
    }

    private static string SideText(SideProgress side, bool ready, string name)
    {
        if (side.Failed)
            return name + " failed";
        if (ready)
            return name + " ✓ " + side.Count;
        return "scanning… " + name;
    }

    private static ScanLine Line(Section section, ItemRow row)
        => new(
            row.LineKey,
            section.Name,
            false,
            row.SourceText,
            row.DestResolved ? row.DestinationText : "scanning…",
            row.DestResolved && row.SourceConfirmed ? row.ResultText : "scanning",
            row.DestResolved && row.SourceConfirmed ? row.Result : null,
            row.ItemType,
            row.ActionKeys,
            row.DestResolved && row.SourceConfirmed ? row.Tone : "scanning",
            row.Metrics,
            row.SourceDetail,
            row.DestResolved ? row.DestinationDetail : "",
            row.SourceAbsent,
            row.DestResolved && row.DestinationAbsent,
            row.PlaylistItems,
            row.Settled,
            row.JustApplied,
            row.MatchNote,
            row.UnmatchedDetail);

    private static bool SearchHit(ItemRow row, string search)
        => row.SourceTitle.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.DestTitle.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.SourceText.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.SourceDetail.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.DestinationDetail.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool RowShown(ItemRow row, string? filter)
        => filter is "mismatches"
            ? Actionable(row)
            : ResultHit(row.Result, filter);

    private static bool ResultHit(ComparisonResult? result, string? filter)
        => filter switch
        {
            null or "" or "out-of-date" or "changes" => result == ComparisonResult.OutOfDate,
            "missing" => result == ComparisonResult.Missing,
            "up-to-date" => result == ComparisonResult.UpToDate,
            "conflict" => result == ComparisonResult.Conflict,
            "only-on-destination" => result == ComparisonResult.OnlyOnDestination,
            "skipped" => result == ComparisonResult.Skipped,
            "hide-only-on-destination" => result != ComparisonResult.OnlyOnDestination,
            "mismatches" => false,
            "all" => true,
            _ => result == ComparisonResult.OutOfDate
        };

    internal static string ResultLine(ComparisonResult result, string difference)
    {
        var detail = difference.Length > 90 ? difference[..87] + "…" : difference;
        return result switch
        {
            ComparisonResult.UpToDate => "matching",
            ComparisonResult.OutOfDate => "out of date: " + detail,
            ComparisonResult.Missing => "missing on destination",
            ComparisonResult.Conflict => "conflict",
            ComparisonResult.OnlyOnDestination => "only on destination",
            _ => "skipped: " + detail
        };
    }

    private static string ToneOf(ComparisonResult result) => result switch
    {
        ComparisonResult.UpToDate => "match",
        ComparisonResult.OutOfDate => "out",
        ComparisonResult.Conflict => "conflict",
        ComparisonResult.Missing => "missing",
        ComparisonResult.OnlyOnDestination => "only",
        _ => "skipped"
    };

    private string SectionName(ItemRow row)
    {
        foreach (var section in _sections)
        {
            if (section.SourceRows.Contains(row) || section.DestOnly.Contains(row))
                return section.Name;
        }

        return "";
    }

    private static void ShowMissingSource(ItemRow row)
    {
        row.SourceAbsent = true;
        row.SourceText = "not on source";
        row.SourceDetail = "";
        row.SourceTitle = "";
    }

    private static void ShowSource(ItemRow row, MigrationItem item, string section, bool confirmed)
    {
        var title = TitleLine(item);
        row.SourceAbsent = false;
        row.SourceTitle = title;
        row.SourceDetail = DetailLine(item, section);
        row.SourceText = confirmed ? title : "scanning " + item.Title;
    }

    private static void ShowDestination(ItemRow row, MigrationItem? item, string section)
    {
        if (item is null)
        {
            row.DestinationAbsent = true;
            row.DestinationText = "not on destination";
            row.DestinationDetail = "";
            row.DestTitle = "";
            return;
        }

        var title = TitleLine(item);
        row.DestinationAbsent = false;
        row.DestTitle = title;
        row.DestinationText = title;
        row.DestinationDetail = DetailLine(item, section);
    }

    private static string TitleLine(MigrationItem item)
        => item.Year is null ? item.Title : item.Title + " (" + item.Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    private static string DetailLine(MigrationItem item, string section)
    {
        var library = string.IsNullOrWhiteSpace(item.Library) ? section : item.Library;
        var kind = KindLine(item, section);
        var file = item.FileName ?? "";
        return string.Join(" · ", new[] { library, kind, file }.Where(part => part.Length > 0));
    }

    private static string KindLine(MigrationItem item, string section)
    {
        if (section == "Other Videos" || (item.Library?.Contains("other", StringComparison.OrdinalIgnoreCase) ?? false))
            return "other video";
        if (item.Type == "episode")
        {
            var code = item.SeasonNumber is not null && item.EpisodeNumber is not null
                ? " S" + item.SeasonNumber.Value.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "E" + item.EpisodeNumber.Value.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            var show = string.IsNullOrWhiteSpace(item.ShowTitle) ? "" : " " + item.ShowTitle;
            return "episode" + code + show;
        }

        if (item.Type == "track")
        {
            var artist = item.ShowTitle ?? "";
            var album = item.ParentTitle ?? "";
            var who = artist.Length > 0 && album.Length > 0 ? artist + " — " + album : artist + album;
            return who.Length == 0 ? "track" : "track " + who;
        }

        return item.Type == "movie" ? "movie" : item.Type;
    }

    private sealed class Section(string name)
    {
        public string Name { get; } = name;
        public SideProgress Source { get; } = new();
        public SideProgress Destination { get; } = new();
        public List<ItemRow> SourceRows { get; } = [];
        public List<ItemRow> DestOnly { get; } = [];
        public List<MigrationItem> DestItems { get; } = [];
        public List<MigrationPlaylist> DestPlaylists { get; } = [];
        public HashSet<string> DestKeys { get; } = new(StringComparer.Ordinal);
        public bool DestOnlyAppended { get; set; }
    }

    private sealed class SideProgress
    {
        public int Expected { get; set; }
        public bool Listed { get; set; }
        public int Finished { get; set; }
        public int Count { get; set; }
        public bool Failed { get; set; }
        public bool Reading { get; set; }
    }

    private List<ScanLine> FilteredItems(Section section, string? filter, string? type, string? search)
    {
        var showAll = filter is null or "" or "all";
        var searching = !string.IsNullOrWhiteSpace(search);
        var typing = !string.IsNullOrEmpty(type) && type != "all";
        var items = new List<ScanLine>();
        foreach (var row in section.SourceRows.Concat(section.DestOnly))
        {
            if (!showAll && !RowShown(row, filter))
                continue;
            if (typing && !row.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase))
                continue;
            if (searching && !SearchHit(row, search!))
                continue;
            items.Add(Line(section, row));
        }

        return items;
    }

    private sealed class ItemRow
    {
        public string SourceKey { get; set; } = "";
        public string LineKey { get; set; } = "";
        public string ItemType { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public string DestTitle { get; set; } = "";
        public string SourceText { get; set; } = "";
        public string SourceDetail { get; set; } = "";
        public string DestinationText { get; set; } = "scanning…";
        public string DestinationDetail { get; set; } = "";
        public bool SourceAbsent { get; set; }
        public bool DestinationAbsent { get; set; }
        public string ResultText { get; set; } = "scanning";
        public string ActionKeys { get; set; } = "";
        public string Tone { get; set; } = "scanning";
        public bool SourceConfirmed { get; set; }
        public bool DestResolved { get; set; }
        public ComparisonResult? Result { get; set; }
        public MigrationItem? Item { get; set; }
        public MigrationItem? Partner { get; set; }
        public MigrationPlaylist? Playlist { get; set; }
        public List<PlaylistItemLine> PlaylistItems { get; set; } = [];
        public List<MetricLine> Metrics { get; set; } = [];
        public bool Settled { get; set; }
        public bool JustApplied { get; set; }
        public string MatchNote { get; set; } = "";
        public string UnmatchedDetail { get; set; } = "";
    }

    internal const string NotOnDestination = "not on destination (file not present, nothing to sync)";

    private void DescribePlaylist(ItemRow row, IReadOnlyList<MigrationPlanRow>? planRows)
    {
        var playlist = row.Playlist;
        if (playlist is null)
            return;
        var section = _sections.FirstOrDefault(candidate => candidate.SourceRows.Contains(row));
        var existing = section?.DestPlaylists.FirstOrDefault(candidate =>
            !candidate.Smart
            && string.Equals(candidate.Title, playlist.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.PlaylistType, playlist.PlaylistType, StringComparison.OrdinalIgnoreCase));
        var present = existing?.Items.Select(item => item.RatingKey).ToHashSet(StringComparer.Ordinal) ?? [];
        _destIndex ??= MigrationMatcher.Index(_destItems);
        var lines = new List<PlaylistItemLine>();
        var keys = new List<string>();
        var writable = row.Result is not ComparisonResult.Skipped && !playlist.Smart && playlist.PlaylistType is "video" or "audio";
        foreach (var member in playlist.Items)
        {
            var detail = ArtistAlbum(member);
            var match = MigrationMatcher.MatchOne(member, _sourceItems, _destItems, _destIndex);
            var destination = match.Kind == MigrationMatchKind.Matched ? match.Destination : null;
            if (destination is null)
            {
                lines.Add(new PlaylistItemLine(member.Title, detail, "not found ✗", ""));
                continue;
            }

            MigrationPlanRow? planned = null;
            if (writable && planRows is not null && !present.Contains(destination.RatingKey))
            {
                planned = planRows.FirstOrDefault(candidate =>
                    candidate.Action is "playlist-create" or "playlist-add"
                    && string.Equals(candidate.PlaylistTitle, playlist.Title, StringComparison.Ordinal)
                    && string.Equals(candidate.PlaylistType, playlist.PlaylistType, StringComparison.OrdinalIgnoreCase)
                    && candidate.TargetRatingKey == destination.RatingKey);
            }

            if (planned is not null)
                keys.Add(planned.Key);
            var perItem = planned is not null && planned.Action == "playlist-add" && existing is not null ? planned.Key : "";
            lines.Add(new PlaylistItemLine(member.Title, detail, "matched on destination ✓", perItem));
        }

        row.PlaylistItems = lines;
        if (planRows is not null)
            row.ActionKeys = string.Join('\n', keys);
        row.SourceText = playlist.Title;
        row.SourceTitle = playlist.Title;
        row.SourceAbsent = false;
        row.SourceDetail = playlist.PlaylistType + " playlist · " + playlist.Items.Count + (playlist.Items.Count == 1 ? " item" : " items");
        row.ResultText = row.Result switch
        {
            ComparisonResult.UpToDate => "matching",
            ComparisonResult.Skipped => SkipReason(playlist, lines),
            ComparisonResult.OnlyOnDestination => "only on destination",
            _ => "playlist mismatch"
        };
    }

    private static string SkipReason(MigrationPlaylist playlist, List<PlaylistItemLine> lines)
    {
        if (playlist.Smart)
            return "skipped · smart playlist (Plex builds it automatically)";
        if (playlist.PlaylistType is not ("video" or "audio"))
            return "skipped · only video and audio playlists are copied";
        if (lines.Count > 0 && lines.All(line => line.Status.StartsWith("not found", StringComparison.Ordinal)))
            return playlist.PlaylistType == "audio"
                ? "skipped · no matching tracks on destination"
                : "skipped · no matching items on destination";
        return "skipped";
    }

    private static string ArtistAlbum(MigrationItem item)
    {
        var artist = item.ShowTitle ?? "";
        var album = item.ParentTitle ?? "";
        if (artist.Length > 0 && album.Length > 0)
            return artist + " — " + album;
        return artist + album;
    }

    private static IEnumerable<string> RowActionKeys(ItemRow row)
    {
        if (row.Settled)
            return [];
        var metricKeys = row.Metrics.Where(metric => !metric.Matches).SelectMany(metric => metric.Keys).Distinct(StringComparer.Ordinal).ToList();
        if (metricKeys.Count > 0)
            return metricKeys;
        return SplitKeys(row.ActionKeys);
    }
}
