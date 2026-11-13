namespace PlexBridge.Plex;

public enum MigrationMatchKind
{
    Matched,
    Unmatched,
    Ambiguous
}

public sealed record MigrationMatch(
    MigrationItem Source,
    MigrationItem? Destination,
    MigrationMatchKind Kind,
    string? Reason,
    string? Via = null,
    string? Diagnosis = null);

/// <summary>
/// Pairs items by IMDb, TMDB, TVDB, MusicBrainz, and Plex content ids.
/// Episodes and tracks can also pair through a matched parent plus their season, episode, or track index.
/// Titles, file paths, and rating keys are never compared.
/// </summary>
public static partial class MigrationMatcher
{
    public static IReadOnlyList<MigrationMatch> Match(IReadOnlyList<MigrationItem> source, IReadOnlyList<MigrationItem> destination)
    {
        var index = Index(destination);
        var matches = new List<MigrationMatch>(source.Count);
        foreach (var item in source)
            matches.Add(MatchOne(item, source, destination, index));
        return matches;
    }

    public static MigrationMatch MatchOne(
        MigrationItem item,
        IReadOnlyList<MigrationItem> source,
        IReadOnlyList<MigrationItem> destination,
        IReadOnlyDictionary<string, List<MigrationItem>>? index = null)
    {
        index ??= Index(destination);
        var direct = Hits(item, index);
        if (direct.Count > 1)
            return new MigrationMatch(item, null, MigrationMatchKind.Ambiguous, "More than one destination item shares this id.");
        if (direct.Count == 1)
            return new MigrationMatch(item, direct[0].Item, MigrationMatchKind.Matched, null, direct[0].Via);

        if (item.Type == "episode")
            return MatchEpisode(item, source, destination, index);
        if (item.Type == "season")
            return MatchSeason(item, source, destination, index);
        if (item.Type == "track")
            return MatchTrack(item, source, destination, index);

        return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "No shared IMDb, TMDB, TVDB, or Plex content id.", null, "Source ids: " + DescribeIds(item));
    }

    private static MigrationMatch MatchEpisode(
        MigrationItem item,
        IReadOnlyList<MigrationItem> source,
        IReadOnlyList<MigrationItem> destination,
        IReadOnlyDictionary<string, List<MigrationItem>> index)
    {
        var showIds = EffectiveShowIds(item);
        var pool = new List<MigrationItem>();
        if (showIds.Count > 0)
        {
            pool.AddRange(destination.Where(candidate =>
                candidate.Type == "episode" && SharesId(showIds, EffectiveShowIds(candidate))));
        }

        if (!string.IsNullOrEmpty(item.GrandparentRatingKey))
        {
            var show = source.FirstOrDefault(candidate => candidate.Type == "show" && candidate.RatingKey == item.GrandparentRatingKey);
            if (show is not null && show.Guids.Count > 0)
            {
                var shows = Hits(show, index);
                if (shows.Count > 1)
                    return new MigrationMatch(item, null, MigrationMatchKind.Ambiguous, "More than one destination show shares this id.");
                if (shows.Count == 1)
                {
                    var showKey = shows[0].Item.RatingKey;
                    pool.AddRange(destination.Where(candidate =>
                        candidate.Type == "episode"
                        && (candidate.GrandparentRatingKey == showKey || SharesId(show.Guids, EffectiveShowIds(candidate)))));
                }
            }
        }

        pool = pool.GroupBy(candidate => candidate.RatingKey, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var (season, episode) = Numbers(item);
        if (season is not null && episode is not null)
        {
            var byNumber = pool.Where(candidate => candidate.SeasonNumber == season && candidate.EpisodeNumber == episode).ToList();
            if (byNumber.Count == 1)
                return new MigrationMatch(item, byNumber[0], MigrationMatchKind.Matched, null, "season/episode");
            if (byNumber.Count > 1)
                return new MigrationMatch(item, null, MigrationMatchKind.Ambiguous, "More than one destination episode has this show id and episode number.");
        }

        if (TryEpisodeCode(item.FileName, out var fileSeason, out var fileEpisode))
        {
            var byFile = pool.Where(candidate => SameFileCode(candidate, fileSeason, fileEpisode)).ToList();
            if (byFile.Count == 1)
                return new MigrationMatch(item, byFile[0], MigrationMatchKind.Matched, null, "file name");
            if (byFile.Count > 1)
                return new MigrationMatch(item, null, MigrationMatchKind.Ambiguous, "More than one destination episode in this show has this file name.");
        }

        var reason = pool.Count == 0
            ? "No destination episode shares this show id."
            : "No destination episode has this show id and episode number.";
        return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, reason, null, Diagnose(item, pool));
    }

    private static MigrationMatch MatchSeason(
        MigrationItem item,
        IReadOnlyList<MigrationItem> source,
        IReadOnlyList<MigrationItem> destination,
        IReadOnlyDictionary<string, List<MigrationItem>> index)
    {
        if (item.SeasonNumber is null || string.IsNullOrEmpty(item.ParentRatingKey))
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "The season has no number, so its title was not used.");

        var show = source.FirstOrDefault(candidate => candidate.Type == "show" && candidate.RatingKey == item.ParentRatingKey);
        if (show is null || show.Guids.Count == 0)
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "The show has no shared id, so the season title was not used.");

        var shows = Hits(show, index);
        if (shows.Count != 1)
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "The show has no shared id, so the season title was not used.");

        var seasons = destination.Where(candidate =>
            candidate.Type == "season"
            && candidate.ParentRatingKey == shows[0].Item.RatingKey
            && candidate.SeasonNumber == item.SeasonNumber).ToList();
        if (seasons.Count == 1)
            return new MigrationMatch(item, seasons[0], MigrationMatchKind.Matched, null);
        return new MigrationMatch(item, null, seasons.Count > 1 ? MigrationMatchKind.Ambiguous : MigrationMatchKind.Unmatched,
            seasons.Count > 1 ? "More than one destination season has this number." : "No destination season has this show id and season number.");
    }

    private static MigrationMatch MatchTrack(
        MigrationItem item,
        IReadOnlyList<MigrationItem> source,
        IReadOnlyList<MigrationItem> destination,
        IReadOnlyDictionary<string, List<MigrationItem>> index)
    {
        if (item.EpisodeNumber is null)
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "The track has no track number, so its title was not used.");

        if (item.AlbumIds.Count > 0)
        {
            var byAlbum = destination.Where(candidate =>
                candidate.Type == "track"
                && candidate.EpisodeNumber == item.EpisodeNumber
                && SharesId(item.AlbumIds, candidate.AlbumIds)).ToList();
            if (byAlbum.Count == 1)
                return new MigrationMatch(item, byAlbum[0], MigrationMatchKind.Matched, null);
            if (byAlbum.Count > 1)
                return new MigrationMatch(item, null, MigrationMatchKind.Ambiguous, "More than one destination track has this album id and track number.");
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "No destination track has this album id and track number.");
        }

        if (string.IsNullOrEmpty(item.ParentRatingKey))
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "The track has no album id, so its title was not used.");

        var album = source.FirstOrDefault(candidate => candidate.Type == "album" && candidate.RatingKey == item.ParentRatingKey);
        if (album is null || album.Guids.Count == 0)
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "The album has no shared id, so the track title was not used.");

        var albums = Hits(album, index);
        if (albums.Count != 1)
            return new MigrationMatch(item, null, MigrationMatchKind.Unmatched, "The album has no shared id, so the track title was not used.");

        var tracks = destination.Where(candidate =>
            candidate.Type == "track"
            && candidate.ParentRatingKey == albums[0].Item.RatingKey
            && candidate.EpisodeNumber == item.EpisodeNumber).ToList();
        if (tracks.Count == 1)
            return new MigrationMatch(item, tracks[0], MigrationMatchKind.Matched, null);
        return new MigrationMatch(item, null, tracks.Count > 1 ? MigrationMatchKind.Ambiguous : MigrationMatchKind.Unmatched,
            tracks.Count > 1 ? "More than one destination track has this number." : "No destination track has this album id and track number.");
    }

    internal static Dictionary<string, List<MigrationItem>> Index(IReadOnlyList<MigrationItem> items)
    {
        var index = new Dictionary<string, List<MigrationItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var guid in item.Guids)
            {
                if (!index.TryGetValue(guid.Key, out var list))
                    index[guid.Key] = list = [];
                if (list.TrueForAll(existing => existing.RatingKey != item.RatingKey))
                    list.Add(item);
            }
        }

        return index;
    }

    private sealed record GuidHit(MigrationItem Item, string Via);

    private static List<GuidHit> Hits(MigrationItem item, IReadOnlyDictionary<string, List<MigrationItem>> index)
    {
        var hits = new List<GuidHit>();
        foreach (var guid in item.Guids)
        {
            if (!index.TryGetValue(guid.Key, out var list))
                continue;
            var via = ViaOf(guid.Provider);
            foreach (var candidate in list)
            {
                if (candidate.Type != item.Type)
                    continue;
                var existing = hits.FindIndex(hit => hit.Item.RatingKey == candidate.RatingKey);
                if (existing < 0)
                    hits.Add(new GuidHit(candidate, via));
                else if (Rank(via) < Rank(hits[existing].Via))
                    hits[existing] = new GuidHit(candidate, via);
            }
        }

        return hits;
    }

    private static string ViaOf(string provider) => provider switch
    {
        "imdb" => "imdb",
        "tvdb" => "tvdb",
        "tmdb" => "tmdb",
        "musicbrainz" => "musicbrainz",
        _ => "plex"
    };

    private static int Rank(string via) => via switch
    {
        "imdb" => 0,
        "tvdb" => 1,
        "tmdb" => 2,
        "musicbrainz" => 3,
        _ => 4
    };

    private static List<PlexContentId> EffectiveShowIds(MigrationItem item)
    {
        var ids = new List<PlexContentId>(item.ShowIds);
        foreach (var guid in item.Guids)
        {
            if (PlexGuids.TryLegacyParts(guid, out var showId, out _, out _) && ids.TrueForAll(existing => existing.Key != showId.Key))
                ids.Add(showId);
        }

        return ids;
    }

    private static (int? Season, int? Episode) Numbers(MigrationItem item)
    {
        if (item.SeasonNumber is not null && item.EpisodeNumber is not null)
            return (item.SeasonNumber, item.EpisodeNumber);
        foreach (var guid in item.Guids)
        {
            if (PlexGuids.TryLegacyParts(guid, out _, out var season, out var episode))
                return (season, episode);
        }

        return (item.SeasonNumber, item.EpisodeNumber);
    }

    internal static bool TryEpisodeCode(string? fileName, out int season, out int episode)
    {
        season = 0;
        episode = 0;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        var match = EpisodeCode().Match(fileName);
        if (!match.Success)
            match = XCode().Match(fileName);
        if (!match.Success)
            return false;
        return int.TryParse(match.Groups[1].Value, out season) && int.TryParse(match.Groups[2].Value, out episode);
    }

    private static bool SameFileCode(MigrationItem item, int season, int episode)
    {
        if (TryEpisodeCode(item.FileName, out var fileSeason, out var fileEpisode))
            return fileSeason == season && fileEpisode == episode;
        return string.IsNullOrEmpty(item.FileName) && item.SeasonNumber == season && item.EpisodeNumber == episode;
    }

    private static string Diagnose(MigrationItem source, IReadOnlyList<MigrationItem> pool)
    {
        var lines = new List<string> { "Source ids: " + DescribeIds(source) };
        if (source.Type != "episode")
            return lines[0];
        if (pool.Count == 0)
        {
            lines.Add("No destination episode shares this show id.");
            return string.Join('\n', lines);
        }

        lines.Add("Closest on destination:");
        foreach (var candidate in pool
            .Select(item => (Item: item, Distance: Distance(source, item)))
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Item.SeasonNumber ?? int.MaxValue)
            .ThenBy(item => item.Item.EpisodeNumber ?? int.MaxValue)
            .Take(5)
            .Select(item => item.Item))
        {
            var where = candidate.SeasonNumber is int season && candidate.EpisodeNumber is int episode
                ? "S" + season.ToString("00") + "E" + episode.ToString("00") + " · "
                : "";
            lines.Add(where + candidate.Title + " — " + DescribeIds(candidate));
        }

        return string.Join('\n', lines);
    }

    private static string DescribeIds(MigrationItem item)
    {
        var parts = new List<string>();
        foreach (var id in item.Guids)
            parts.Add(id.Provider + ":" + id.Value);
        foreach (var id in EffectiveShowIds(item))
        {
            if (parts.Contains("show " + id.Provider + ":" + id.Value))
                continue;
            if (item.Guids.Any(guid => guid.Key == id.Key))
                continue;
            parts.Add("show " + id.Provider + ":" + id.Value);
        }

        if (TryEpisodeCode(item.FileName, out var season, out var episode))
            parts.Add("file S" + season.ToString("00") + "E" + episode.ToString("00"));
        else if (!string.IsNullOrWhiteSpace(item.FileName))
            parts.Add("file " + item.FileName);
        return parts.Count == 0 ? "(none)" : string.Join(" · ", parts);
    }

    private static int Distance(MigrationItem source, MigrationItem candidate)
    {
        int? season = source.SeasonNumber;
        int? episode = source.EpisodeNumber;
        if (TryEpisodeCode(source.FileName, out var fileSeason, out var fileEpisode))
        {
            season = fileSeason;
            episode = fileEpisode;
        }

        if (season is null || episode is null || candidate.SeasonNumber is null || candidate.EpisodeNumber is null)
            return 100_000;
        return Math.Abs(season.Value - candidate.SeasonNumber.Value) * 1000 + Math.Abs(episode.Value - candidate.EpisodeNumber.Value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?i)(?:^|[^a-z0-9])S(\d{1,3})E(\d{1,4})(?:[^0-9]|$)")]
    private static partial System.Text.RegularExpressions.Regex EpisodeCode();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?i)(?:^|[^a-z0-9])(\d{1,2})x(\d{1,4})(?:[^0-9]|$)")]
    private static partial System.Text.RegularExpressions.Regex XCode();

    private static bool SharesId(IReadOnlyList<PlexContentId> left, IReadOnlyList<PlexContentId> right)
    {
        foreach (var id in left)
        {
            foreach (var other in right)
            {
                if (id.Key == other.Key)
                    return true;
            }
        }

        return false;
    }
}
