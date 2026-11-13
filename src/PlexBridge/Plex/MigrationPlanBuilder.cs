using System.Security.Cryptography;
using System.Text;

namespace PlexBridge.Plex;

public static class MigrationPlanBuilder
{
    private static readonly HashSet<string> LeafTypes = new(StringComparer.Ordinal) { "movie", "episode", "track" };
    private static readonly HashSet<string> RatingTypes = new(StringComparer.Ordinal) { "movie", "episode", "track", "show", "season", "album", "artist" };

    public static MigrationBuilt Build(
        MigrationCatalog source,
        MigrationCatalog destination,
        IReadOnlySet<MigrationCategory> categories)
    {
        var rows = new List<MigrationPlanRow>();
        var comparisons = new List<ComparisonRow>();
        var counts = new Counts();
        var matches = MigrationMatcher.Match(source.Items, destination.Items);
        var pairedDest = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in matches)
        {
            if (match.Kind == MigrationMatchKind.Matched && match.Destination is not null)
            {
                pairedDest.Add(match.Destination.RatingKey);
                var before = rows.Count;
                AddStateRows(rows, counts, match.Source, match.Destination, categories);
                comparisons.Add(ComparePair(match.Source, match.Destination, rows, before, counts) with { MatchNote = MatchNote(match) });
                continue;
            }

            if (match.Kind == MigrationMatchKind.Ambiguous)
            {
                var note = Note(MigrationRowKind.Skipped, "Match", match.Source.Label, match.Reason ?? "This item was not copied.");
                Add(rows, counts, note);
                comparisons.Add(Side(match.Source, null, ComparisonResult.Skipped, note.Detail, ""));
                continue;
            }

            var missing = Note(MigrationRowKind.UnmatchedSource, "Match", match.Source.Label, match.Reason ?? "No destination item shares an id.");
            Add(rows, counts, missing);
            comparisons.Add(Side(match.Source, null, ComparisonResult.Missing, "Missing on destination.", "") with { UnmatchedDetail = match.Diagnosis ?? "" });
        }

        foreach (var item in destination.Items)
        {
            if (pairedDest.Contains(item.RatingKey))
                continue;
            if (!LeafTypes.Contains(item.Type) && item.Type is not ("show" or "album" or "artist"))
                continue;
            Add(rows, counts, Note(MigrationRowKind.UnmatchedDestination, "Match", item.Label, "Nothing on the source shares this id. It was left as it is."));
            comparisons.Add(Side(null, item, ComparisonResult.OnlyOnDestination, "Only on destination.", ""));
        }

        if (categories.Contains(MigrationCategory.Playlists))
        {
            AddPlaylists(rows, counts, source, destination, matches);
            AppendPlaylistComparisons(comparisons, rows, counts, source, destination);
        }

        StampPlaylistMembership(comparisons, source);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (seen.Add(rows[i].Key))
                continue;
            rows[i] = rows[i] with { Key = rows[i].Key + "|" + i };
            seen.Add(rows[i].Key);
        }

        return new MigrationBuilt(rows, counts.ToSummary(), comparisons);
    }

    public sealed record MigrationBuilt(IReadOnlyList<MigrationPlanRow> Rows, MigrationPlanSummary Summary, IReadOnlyList<ComparisonRow> Comparisons);

    private sealed class Counts
    {
        public int MarkWatched;
        public int ClearWatched;
        public int SetProgress;
        public int SetRating;
        public int PlaylistChanges;
        public int WillApply;
        public int Conflicts;
        public int UnmatchedSource;
        public int UnmatchedDestination;
        public int AlreadySame;
        public int OtherSkipped;
        public int OutOfDateItems;
        public int ConflictItems;
        public int UpToDateItems;

        public MigrationPlanSummary ToSummary()
            => new(MarkWatched, ClearWatched, SetProgress, SetRating, PlaylistChanges, WillApply, Conflicts, UnmatchedSource, UnmatchedDestination, AlreadySame, OtherSkipped, OutOfDateItems, ConflictItems, UpToDateItems);
    }

    private static void Add(List<MigrationPlanRow> rows, Counts counts, MigrationPlanRow row)
    {
        rows.Add(row);
        switch (row.Kind)
        {
            case MigrationRowKind.WillApply:
                counts.WillApply++;
                break;
            case MigrationRowKind.Conflict:
                counts.Conflicts++;
                break;
            case MigrationRowKind.UnmatchedSource:
                counts.UnmatchedSource++;
                break;
            case MigrationRowKind.UnmatchedDestination:
                counts.UnmatchedDestination++;
                break;
            default:
                counts.OtherSkipped++;
                break;
        }

        if (row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict)
        {
            switch (row.Action)
            {
                case "scrobble": counts.MarkWatched++; break;
                case "unscrobble": counts.ClearWatched++; break;
                case "progress": counts.SetProgress++; break;
                case "rate": counts.SetRating++; break;
                case "playlist-create":
                case "playlist-add": counts.PlaylistChanges++; break;
            }
        }
    }

    public static string Fingerprint(MigrationCatalog source, MigrationCatalog destination)
    {
        var lines = new List<string>(source.Items.Count + destination.Items.Count + 8);
        foreach (var item in source.Items)
            lines.Add("S|" + State(item));
        foreach (var item in destination.Items)
            lines.Add("D|" + State(item));
        foreach (var playlist in source.Playlists)
            lines.Add("SP|" + PlaylistState(playlist));
        foreach (var playlist in destination.Playlists)
            lines.Add("DP|" + PlaylistState(playlist));
        lines.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var newline = Encoding.UTF8.GetBytes("\n");
        foreach (var line in lines)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(line));
            hash.AppendData(newline);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AddStateRows(List<MigrationPlanRow> rows, Counts counts, MigrationItem source, MigrationItem destination, IReadOnlySet<MigrationCategory> categories)
    {
        if (categories.Contains(MigrationCategory.Watched) && LeafTypes.Contains(source.Type))
            AddWatched(rows, counts, source, destination);
        if (categories.Contains(MigrationCategory.Progress) && LeafTypes.Contains(source.Type))
            AddProgress(rows, counts, source, destination);
        if (categories.Contains(MigrationCategory.Ratings) && RatingTypes.Contains(source.Type))
            AddRating(rows, counts, source, destination);
    }

    private static void AddWatched(List<MigrationPlanRow> rows, Counts counts, MigrationItem source, MigrationItem destination)
    {
        if (source.Watched == destination.Watched)
        {
            counts.AlreadySame++;
            return;
        }

        var clearing = destination.Watched && !source.Watched;
        Add(rows, counts, State(
            clearing ? MigrationRowKind.Conflict : MigrationRowKind.WillApply,
            "Watched",
            source.Watched ? "scrobble" : "unscrobble",
            source,
            destination,
            WatchedText(source.Watched),
            WatchedText(destination.Watched),
            clearing
                ? "Conflict. The destination is watched and the source is not. The source wins unless you clear this row."
                : "Will mark the destination watched. Plex does not keep the original watch date.",
            null,
            null));
    }

    private static void AddProgress(List<MigrationPlanRow> rows, Counts counts, MigrationItem source, MigrationItem destination)
    {
        if (source.ViewOffset == destination.ViewOffset)
        {
            counts.AlreadySame++;
            return;
        }

        var conflict = destination.ViewOffset > 0 && source.ViewOffset != destination.ViewOffset;
        Add(rows, counts, State(
            conflict ? MigrationRowKind.Conflict : MigrationRowKind.WillApply,
            "Progress",
            "progress",
            source,
            destination,
            Clock(source.ViewOffset),
            Clock(destination.ViewOffset),
            conflict
                ? "Conflict. The destination has a different stop time. The source wins unless you clear this row."
                : "Will set the destination stop time.",
            source.ViewOffset,
            null));
    }

    private static void AddRating(List<MigrationPlanRow> rows, Counts counts, MigrationItem source, MigrationItem destination)
    {
        if (SameRating(source.UserRating, destination.UserRating))
        {
            counts.AlreadySame++;
            return;
        }

        var conflict = destination.UserRating is not null;
        Add(rows, counts, State(
            conflict ? MigrationRowKind.Conflict : MigrationRowKind.WillApply,
            "Rating",
            "rate",
            source,
            destination,
            Stars(source.UserRating),
            Stars(destination.UserRating),
            conflict
                ? "Conflict. The destination already has a rating. The source wins unless you clear this row."
                : "Will set the destination rating.",
            null,
            source.UserRating));
    }

    private static void AddPlaylists(List<MigrationPlanRow> rows, Counts counts, MigrationCatalog source, MigrationCatalog destination, IReadOnlyList<MigrationMatch> matches)
    {
        var bySourceKey = matches
            .Where(match => match.Kind == MigrationMatchKind.Matched && match.Destination is not null)
            .GroupBy(match => match.Source.RatingKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Destination!, StringComparer.Ordinal);

        foreach (var playlist in source.Playlists)
        {
            if (playlist.Smart || playlist.PlaylistType is not ("video" or "audio"))
            {
                Add(rows, counts, new MigrationPlanRow(
                    Key: "playlist|skip|" + playlist.RatingKey,
                    Kind: MigrationRowKind.Skipped,
                    Category: "Playlist",
                    Action: "playlist-skip",
                    Title: playlist.Title,
                    Detail: playlist.Smart
                        ? "Smart playlists are not copied. Their filters point at the source library."
                        : "Only video and audio playlists are copied.",
                    SourceValue: playlist.PlaylistType,
                    DestinationValue: null,
                    TargetRatingKey: null,
                    PlaylistRatingKey: null,
                    PlaylistType: playlist.PlaylistType,
                    PlaylistTitle: playlist.Title,
                    ProgressMs: null,
                    Rating: null));
                continue;
            }

            var existing = destination.Playlists
                .Where(candidate => !candidate.Smart
                    && string.Equals(candidate.PlaylistType, playlist.PlaylistType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Title, playlist.Title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (existing.Count > 1)
            {
                Add(rows, counts, Note(MigrationRowKind.Skipped, "Playlist", playlist.Title, "More than one destination playlist has this title."));
                continue;
            }

            var destItems = existing.Count == 1 ? existing[0].Items.Select(item => item.RatingKey).ToHashSet(StringComparer.Ordinal) : [];
            var matched = new List<MigrationItem>();
            var unmatched = 0;
            foreach (var member in playlist.Items)
            {
                if (!bySourceKey.TryGetValue(member.RatingKey, out var destinationItem))
                {
                    var single = MigrationMatcher.MatchOne(member, source.Items, destination.Items);
                    if (single.Kind == MigrationMatchKind.Matched)
                        destinationItem = single.Destination;
                }

                if (destinationItem is null)
                {
                    unmatched++;
                    Add(rows, counts, Note(MigrationRowKind.UnmatchedSource, "Playlist", playlist.Title + " · " + member.Label, "This playlist item has no matching destination item, so it was left out."));
                    continue;
                }

                if (!destItems.Contains(destinationItem.RatingKey) && matched.TrueForAll(existing => existing.RatingKey != destinationItem.RatingKey))
                    matched.Add(destinationItem);
            }

            if (matched.Count == 0)
            {
                Add(rows, counts, Note(MigrationRowKind.Skipped, "Playlist", playlist.Title, unmatched == 0
                    ? "Every matched item is already in the destination playlist."
                    : "No playlist item matched a destination item."));
                continue;
            }

            var playlistId = existing.Count == 1 ? existing[0].RatingKey : null;
            var first = true;
            foreach (var item in matched)
            {
                var create = playlistId is null && first;
                Add(rows, counts, new MigrationPlanRow(
                    Key: (create ? "playlist|create|" : "playlist|add|") + playlist.PlaylistType + "|" + playlist.Title + "|" + item.RatingKey,
                    Kind: MigrationRowKind.WillApply,
                    Category: "Playlist",
                    Action: create ? "playlist-create" : "playlist-add",
                    Title: playlist.Title + " · " + item.Label,
                    Detail: create
                        ? "Will create the playlist on the destination and add this matched item. Items that exist only on the destination are left in place."
                        : "Will add this matched item. Items that exist only on the destination are left in place.",
                    SourceValue: playlist.Title,
                    DestinationValue: playlistId is null ? "New playlist" : "Existing playlist",
                    TargetRatingKey: item.RatingKey,
                    PlaylistRatingKey: playlistId,
                    PlaylistType: playlist.PlaylistType,
                    PlaylistTitle: playlist.Title,
                    ProgressMs: null,
                    Rating: null));
                first = false;
            }
        }
    }

    public static ComparisonRow LiveClassify(MigrationItem source, MigrationMatch match, IReadOnlySet<MigrationCategory> categories, string library)
    {
        var rows = new List<MigrationPlanRow>();
        var counts = new Counts();
        ComparisonRow row;
        if (match.Kind == MigrationMatchKind.Matched && match.Destination is not null)
        {
            AddStateRows(rows, counts, source, match.Destination, categories);
            row = ComparePair(source, match.Destination, rows, 0, counts);
        }
        else if (match.Kind == MigrationMatchKind.Ambiguous)
            row = Side(source, null, ComparisonResult.Skipped, match.Reason ?? "This item was not copied.", "");
        else
            row = Side(source, null, ComparisonResult.Missing, "Missing on destination.", "") with { UnmatchedDetail = match.Diagnosis ?? "" };
        return row with { Library = library, MatchNote = MatchNote(match) };
    }

    private static string MatchNote(MigrationMatch match)
        => match.Kind == MigrationMatchKind.Matched && !string.IsNullOrEmpty(match.Via) ? "matched by " + match.Via : "";

    private static ComparisonRow ComparePair(MigrationItem source, MigrationItem destination, List<MigrationPlanRow> rows, int before, Counts counts)
    {
        var mine = new List<MigrationPlanRow>();
        for (var i = before; i < rows.Count; i++)
        {
            if (rows[i].Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict)
                mine.Add(rows[i]);
        }

        var conflict = false;
        var diffs = new List<string>();
        foreach (var row in mine)
        {
            if (row.Kind == MigrationRowKind.Conflict)
                conflict = true;
            diffs.Add(row.Action switch
            {
                "scrobble" => "watched on source, unwatched here",
                "unscrobble" => "watched here, unwatched on source",
                "progress" => "progress " + Percent(source) + " vs " + Percent(destination),
                "rate" => "rating " + Stars(source.UserRating) + " vs " + (destination.UserRating is null ? "none" : Stars(destination.UserRating)),
                _ => row.Detail
            });
        }

        var result = conflict ? ComparisonResult.Conflict : mine.Count > 0 ? ComparisonResult.OutOfDate : ComparisonResult.UpToDate;
        if (result == ComparisonResult.Conflict)
            counts.ConflictItems++;
        else if (result == ComparisonResult.OutOfDate)
            counts.OutOfDateItems++;
        else
            counts.UpToDateItems++;

        return new ComparisonRow(
            source.RatingKey + "|" + destination.RatingKey,
            source.Type,
            result,
            source.Title,
            Meta(source),
            StateText(source),
            destination.Title,
            Meta(destination),
            StateText(destination),
            diffs.Count == 0 ? "Up to date" : string.Join("; ", diffs),
            string.Join('\n', mine.Select(row => row.Key)),
            "",
            source.Library ?? "");
    }

    private static ComparisonRow Side(MigrationItem? source, MigrationItem? destination, ComparisonResult result, string difference, string actionKeys)
    {
        var item = source ?? destination;
        return new ComparisonRow(
            (source?.RatingKey ?? "d") + "|" + (destination?.RatingKey ?? "s"),
            item?.Type ?? "",
            result,
            source?.Title ?? "",
            source is null ? "" : Meta(source),
            source is null ? "" : StateText(source),
            destination?.Title ?? "",
            destination is null ? "" : Meta(destination),
            destination is null ? "" : StateText(destination),
            difference,
            actionKeys,
            "",
            source?.Library ?? destination?.Library ?? "");
    }

    private static void AppendPlaylistComparisons(List<ComparisonRow> comparisons, List<MigrationPlanRow> rows, Counts counts, MigrationCatalog source, MigrationCatalog destination)
    {
        foreach (var playlist in source.Playlists)
        {
            var related = rows.Where(row => row.Category == "Playlist" && (row.PlaylistTitle == playlist.Title || row.Title == playlist.Title)).ToList();
            var actions = related.Where(row => row.Kind == MigrationRowKind.WillApply).Select(row => row.Key).ToList();
            var existing = destination.Playlists.FirstOrDefault(candidate =>
                !candidate.Smart
                && string.Equals(candidate.Title, playlist.Title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.PlaylistType, playlist.PlaylistType, StringComparison.OrdinalIgnoreCase));
            ComparisonResult result;
            string difference;
            if (playlist.Smart || playlist.PlaylistType is not ("video" or "audio"))
            {
                result = ComparisonResult.Skipped;
                difference = playlist.Smart
                    ? "Smart playlists are not copied. Their filters point at the source library."
                    : "Only video and audio playlists are copied.";
            }
            else if (actions.Count == 0 && existing is not null)
            {
                result = ComparisonResult.UpToDate;
                difference = "Up to date";
                counts.UpToDateItems++;
            }
            else if (existing is null && actions.Count > 0)
            {
                result = ComparisonResult.OutOfDate;
                difference = "Missing playlist · " + actions.Count + (actions.Count == 1 ? " item to add" : " items to add");
                counts.OutOfDateItems++;
            }
            else if (actions.Count > 0)
            {
                result = ComparisonResult.OutOfDate;
                difference = actions.Count + (actions.Count == 1 ? " item to add" : " items to add");
                counts.OutOfDateItems++;
            }
            else
            {
                result = ComparisonResult.Skipped;
                difference = "No playlist item matched a destination item.";
            }

            var parentKey = "playlist|" + playlist.PlaylistType + "|" + playlist.Title;
            comparisons.Add(new ComparisonRow(
                parentKey,
                "playlist",
                result,
                playlist.Title,
                playlist.PlaylistType,
                playlist.Items.Count + (playlist.Items.Count == 1 ? " item" : " items"),
                existing?.Title ?? "",
                existing is null ? "" : existing.PlaylistType,
                existing is null ? "" : existing.Items.Count + (existing.Items.Count == 1 ? " item" : " items"),
                difference,
                string.Join('\n', actions),
                ""));

            foreach (var member in playlist.Items)
            {
                var childAction = actions.FirstOrDefault(key => key.EndsWith("|" + member.RatingKey, StringComparison.Ordinal)) ?? "";
                var childResult = string.IsNullOrEmpty(childAction) ? ComparisonResult.UpToDate : ComparisonResult.OutOfDate;
                if (related.Any(row => row.Kind == MigrationRowKind.UnmatchedSource && row.Title.Contains(member.Label, StringComparison.Ordinal)))
                    childResult = ComparisonResult.Missing;
                comparisons.Add(new ComparisonRow(
                    parentKey + "|" + member.RatingKey,
                    member.Type,
                    childResult,
                    member.Title,
                    Meta(member),
                    StateText(member),
                    childResult == ComparisonResult.Missing ? "" : member.Title,
                    "",
                    "",
                    childResult switch
                    {
                        ComparisonResult.Missing => "No matching destination item.",
                        ComparisonResult.OutOfDate => "Will add this matched item.",
                        _ => "Already in the playlist."
                    },
                    childAction,
                    parentKey));
            }
        }
    }

    private static void StampPlaylistMembership(List<ComparisonRow> comparisons, MigrationCatalog source)
    {
        var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var playlist in source.Playlists)
        {
            if (playlist.Smart)
                continue;
            foreach (var member in playlist.Items)
            {
                if (!names.TryGetValue(member.RatingKey, out var list))
                    names[member.RatingKey] = list = [];
                if (list.Count < 3)
                    list.Add(playlist.Title);
            }
        }

        for (var i = 0; i < comparisons.Count; i++)
        {
            var row = comparisons[i];
            if (row.ItemType == "playlist" || string.IsNullOrEmpty(row.ParentKey))
            {
                var sourceKey = row.Key.Split('|')[0];
                if (!names.TryGetValue(sourceKey, out var list) || list.Count == 0 || string.IsNullOrEmpty(row.SourceState))
                    continue;
                comparisons[i] = row with { SourceState = row.SourceState + " · " + string.Join(", ", list) };
            }
        }
    }

    private static string Meta(MigrationItem item)
    {
        if (item.Type == "episode" && item.SeasonNumber is not null && item.EpisodeNumber is not null)
            return (string.IsNullOrWhiteSpace(item.ShowTitle) ? "Show" : item.ShowTitle) + $" S{item.SeasonNumber:00}E{item.EpisodeNumber:00}";
        if (item.Type == "track")
        {
            var artist = string.IsNullOrWhiteSpace(item.ShowTitle) ? "" : item.ShowTitle;
            var album = item.ParentTitle ?? "";
            if (artist.Length > 0 && album.Length > 0)
                return artist + " · " + album;
            return artist.Length > 0 ? artist : album;
        }

        return item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static string StateText(MigrationItem item)
        => (item.Watched ? "Watched" : "Not watched") + " · " + Percent(item) + " · " + Stars(item.UserRating);

    private static string Percent(MigrationItem item)
    {
        if (item.ViewOffset <= 0)
            return "0%";
        if (item.Duration > 0)
            return Math.Clamp((int)Math.Round(100.0 * item.ViewOffset / item.Duration), 0, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        return Clock(item.ViewOffset);
    }

    private static MigrationPlanRow State(
        MigrationRowKind kind,
        string category,
        string action,
        MigrationItem source,
        MigrationItem destination,
        string? sourceValue,
        string? destinationValue,
        string detail,
        long? progress,
        decimal? rating)
        => new(
            Key: category.ToLowerInvariant() + "|" + source.RatingKey + "|" + destination.RatingKey,
            Kind: kind,
            Category: category,
            Action: action,
            Title: source.Label,
            Detail: detail,
            SourceValue: sourceValue,
            DestinationValue: destinationValue,
            TargetRatingKey: destination.RatingKey,
            PlaylistRatingKey: null,
            PlaylistType: null,
            PlaylistTitle: null,
            ProgressMs: progress,
            Rating: rating);

    private static MigrationPlanRow Note(MigrationRowKind kind, string category, string title, string detail)
        => new(category.ToLowerInvariant() + "|note|" + kind + "|" + title, kind, category, "none", title, detail, null, null, null, null, null, null, null, null);

    private static string State(MigrationItem item)
        => string.Join('|',
            item.RatingKey,
            item.Type,
            item.ViewCount,
            item.ViewOffset,
            item.UserRating?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            item.SeasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            item.EpisodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            item.ParentRatingKey ?? "",
            item.GrandparentRatingKey ?? "",
            string.Join(',', item.Guids.Select(guid => guid.Key).Order(StringComparer.Ordinal)),
            string.Join(',', item.ShowIds.Select(guid => guid.Key).Order(StringComparer.Ordinal)),
            string.Join(',', item.AlbumIds.Select(guid => guid.Key).Order(StringComparer.Ordinal)));

    private static string PlaylistState(MigrationPlaylist playlist)
        => playlist.Title + "|" + playlist.PlaylistType + "|" + playlist.Smart + "|" + string.Join(',', playlist.Items.Select(item => item.RatingKey));

    private static bool SameRating(decimal? left, decimal? right)
    {
        if (left is null && right is null)
            return true;
        if (left is null || right is null)
            return false;
        return Math.Abs(left.Value - right.Value) < 0.05m;
    }

    private static string WatchedText(bool watched) => watched ? "Watched" : "Not watched";

    private static string Stars(decimal? rating)
        => rating is null ? "No rating" : rating.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Metrics the preview can copy for a matched movie, episode, or track.
    /// Tracks use the same fields as movies and episodes. Play count is not one of them:
    /// watched is only whether <see cref="MigrationItem.ViewCount"/> is greater than zero.
    /// A metric that has no value on either side is left out.
    /// </summary>
    public static IReadOnlyList<MetricLine> ItemMetrics(
        MigrationItem source,
        MigrationItem destination,
        IReadOnlySet<MigrationCategory> categories,
        IReadOnlyList<MigrationPlaylist> sourcePlaylists,
        IReadOnlyList<MigrationPlaylist> destinationPlaylists,
        IReadOnlyList<MigrationPlanRow>? playlistRows = null)
    {
        var lines = new List<MetricLine>();
        if (!LeafTypes.Contains(source.Type))
            return lines;
        if (categories.Contains(MigrationCategory.Watched))
        {
            var same = source.Watched == destination.Watched;
            lines.Add(new MetricLine(
                "Watched status",
                WatchedText(source.Watched),
                WatchedText(destination.Watched),
                same,
                same ? "" : "watched|" + source.RatingKey + "|" + destination.RatingKey));
        }

        if (categories.Contains(MigrationCategory.Progress) && (source.ViewOffset > 0 || destination.ViewOffset > 0))
        {
            var same = source.ViewOffset == destination.ViewOffset;
            lines.Add(new MetricLine(
                "Viewing progress",
                ProgressText(source),
                ProgressText(destination),
                same,
                same ? "" : "progress|" + source.RatingKey + "|" + destination.RatingKey));
        }

        if (categories.Contains(MigrationCategory.Ratings) && (source.UserRating is not null || destination.UserRating is not null))
        {
            var same = SameRating(source.UserRating, destination.UserRating);
            lines.Add(new MetricLine(
                "Your rating",
                Stars(source.UserRating),
                Stars(destination.UserRating),
                same,
                same ? "" : "rating|" + source.RatingKey + "|" + destination.RatingKey));
        }

        if (categories.Contains(MigrationCategory.Playlists))
        {
            var sourceNames = PlaylistNames(sourcePlaylists, source.RatingKey);
            var destNames = PlaylistNames(destinationPlaylists, destination.RatingKey);
            if (sourceNames.Count > 0 || destNames.Count > 0)
            {
                var missing = sourceNames.Where(name => !destNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                var destText = destNames.Count == 0 ? "None" : string.Join(", ", destNames);
                if (missing.Count > 0)
                    destText = destNames.Count == 0
                        ? "missing " + string.Join(", ", missing)
                        : destText + " · missing " + string.Join(", ", missing);
                var keys = playlistRows is null
                    ? ""
                    : string.Join('\n', playlistRows
                        .Where(row => row.Action is "playlist-create" or "playlist-add" && row.TargetRatingKey == destination.RatingKey)
                        .Select(row => row.Key));
                lines.Add(new MetricLine(
                    "Playlist membership",
                    sourceNames.Count == 0 ? "None" : string.Join(", ", sourceNames),
                    destText,
                    missing.Count == 0,
                    missing.Count == 0 ? "" : keys));
            }
        }

        return lines;
    }

    private static List<string> PlaylistNames(IReadOnlyList<MigrationPlaylist> playlists, string ratingKey)
    {
        var names = new List<string>();
        foreach (var playlist in playlists)
        {
            if (playlist.Smart || playlist.PlaylistType is not ("video" or "audio"))
                continue;
            if (playlist.Items.Any(item => item.RatingKey == ratingKey) && names.Count < 8)
                names.Add(playlist.Title);
        }

        return names;
    }

    private static string ProgressText(MigrationItem item)
    {
        if (item.ViewOffset <= 0)
            return "No progress";
        var clock = Clock(item.ViewOffset);
        if (item.Duration <= 0)
            return clock;
        var percent = Math.Clamp((int)Math.Round(100.0 * item.ViewOffset / item.Duration), 0, 100);
        return percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "% (" + clock + ")";
    }

    public static string Clock(long milliseconds)
    {
        if (milliseconds < 0)
            milliseconds = 0;
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }
}
