using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlexBridge.Data;
using PlexBridge.Plex;
using PlexBridge.Security;
using PlexBridge.Services;

namespace PlexBridge.Tests;

public class MigrationMatcherTests
{
    [Fact]
    public void Same_imdb_matches_across_different_titles_keys_and_paths()
    {
        var source = Movie("10", "Arrival", "tt2543164");
        var destination = Movie("77", "Arrival (remux)", "tt2543164");
        var match = Assert.Single(MigrationMatcher.Match([source], [destination]));
        Assert.Equal(MigrationMatchKind.Matched, match.Kind);
        Assert.Equal("77", match.Destination!.RatingKey);
    }

    [Fact]
    public void Same_title_without_a_shared_id_stays_unmatched()
    {
        var source = Movie("10", "Same Title", imdb: null);
        var destination = Movie("77", "Same Title", imdb: null);
        var match = Assert.Single(MigrationMatcher.Match([source], [destination]));
        Assert.Equal(MigrationMatchKind.Unmatched, match.Kind);
        Assert.Null(match.Destination);
    }

    [Fact]
    public void File_paths_are_not_ids_and_legacy_imdb_ids_are()
    {
        var parsed = PlexGuids.Parse(
        [
            "file:///media/films/same.mkv",
            "/media/films/same.mkv",
            "local://99",
            "com.plexapp.agents.imdb://tt0133093?lang=en",
            "plex://movie/5d776b59ad5437001f79c6f8"
        ]);
        Assert.Equal(2, parsed.Count);
        Assert.Contains(parsed, id => id.Key == "imdb:tt0133093");
        Assert.Contains(parsed, id => id.Key == "plex-movie:5d776b59ad5437001f79c6f8");

        var fromFile = PlexJsonParser.ParseMigrationItems("""
            {"MediaContainer":{"Metadata":[
              {"ratingKey":"1","title":"Same","type":"movie","Media":[{"Part":[{"file":"/media/same.mkv"}]}]},
              {"ratingKey":"2","title":"Same","type":"movie","Location":[{"path":"/media/same.mkv"}]}
            ]}}
            """);
        Assert.All(fromFile, item => Assert.Empty(item.Guids));
        Assert.Equal(MigrationMatchKind.Unmatched, MigrationMatcher.Match([fromFile[0]], [fromFile[1]]).Single().Kind);
    }

    [Fact]
    public void Episode_matches_through_the_show_id_and_indexes_not_the_title()
    {
        var sourceShow = Show("1", "100");
        var sourceEpisode = Episode("2", "1", 1, 2, "Pilot");
        var destinationShow = Show("9", "100");
        var destinationEpisode = Episode("8", "9", 1, 2, "A different title");
        var otherEpisode = Episode("7", "9", 1, 3, "Pilot");
        var matches = MigrationMatcher.Match(
            [sourceShow, sourceEpisode],
            [destinationShow, destinationEpisode, otherEpisode]);
        var episode = matches.Single(match => match.Source.RatingKey == "2");
        Assert.Equal(MigrationMatchKind.Matched, episode.Kind);
        Assert.Equal("8", episode.Destination!.RatingKey);
        Assert.Equal(MigrationMatchKind.Unmatched, MigrationMatcher.Match([otherEpisode], [sourceEpisode]).Single().Kind);
    }

    [Fact]
    public void Episode_matches_on_the_leaf_show_id_without_a_show_record()
    {
        var show = PlexGuids.Parse(["plex://show/abcd1234"]);
        var source = new MigrationItem("2", "Pilot", "episode", null, 1, 2, "Station", 1, 0, null, [], null, null, show, null);
        var destination = new MigrationItem("8", "Other name", "episode", null, 1, 2, "Station", 0, 0, null, [], "99", "40", show, null);
        var wrong = new MigrationItem("7", "Pilot", "episode", null, 1, 2, "Station", 0, 0, null, [], null, null, PlexGuids.Parse(["plex://show/ffff9999"]), null);
        var match = Assert.Single(MigrationMatcher.Match([source], [destination, wrong]), item => item.Source.RatingKey == "2");
        Assert.Equal(MigrationMatchKind.Matched, match.Kind);
        Assert.Equal("8", match.Destination!.RatingKey);
    }

    [Fact]
    public void Legacy_tvdb_episode_id_matches_a_plex_agent_episode()
    {
        var parsed = PlexJsonParser.ParseMigrationItems("""
            {"MediaContainer":{"Metadata":[{
              "ratingKey":"2","title":"The Funhouse","type":"episode","parentIndex":1,"index":50,
              "guid":"com.plexapp.agents.thetvdb://81189/1/50?lang=en",
              "grandparentGuid":"plex://show/ffff0000",
              "Guid":[{"id":"tvdb://424242"},{"id":"plex://episode/abcd1234"}],
              "Media":[{"Part":[{"file":"/tv/G I Joe - S01E50.mkv"}]}]
            }]}}
            """);
        var episode = Assert.Single(parsed);
        Assert.Contains(episode.Guids, id => id.Key == "tvdb:81189/1/50");
        Assert.Contains(episode.Guids, id => id.Key == "tvdb:424242");
        Assert.Contains(episode.ShowIds, id => id.Key == "plex-show:ffff0000");
        Assert.Contains(episode.ShowIds, id => id.Key == "tvdb:81189");
        Assert.Equal("G I Joe - S01E50.mkv", episode.FileName);
        Assert.DoesNotContain(episode.Guids, id => id.Key == "tvdb:81189");

        var source = episode with { ViewCount = 1 };
        var destination = new MigrationItem(
            "8", "The Funhouse", "episode", null, 2, 5, "G.I. Joe", 0, 0, null,
            PlexGuids.Parse(["plex://episode/9999aaaa", "com.plexapp.agents.thetvdb://424242?lang=en"]),
            null, "40", PlexGuids.Parse(["tvdb://81189"]), FileName: "Joe - S02E05.mkv");
        var match = Assert.Single(MigrationMatcher.Match([source], [destination]));
        Assert.Equal(MigrationMatchKind.Matched, match.Kind);
        Assert.Equal("8", match.Destination!.RatingKey);
        Assert.Equal("tvdb", match.Via);
    }

    [Fact]
    public void Episode_falls_back_to_the_show_and_then_the_file_name()
    {
        var show = PlexGuids.Parse(["tvdb://81189"]);
        var source = new MigrationItem("2", "The Funhouse", "episode", null, 1, 50, "G.I. Joe", 1, 0, null, PlexGuids.Parse(["plex://episode/abcd1234"]), null, "1", show, FileName: "G I Joe - S01E50.mkv");
        var sameOrder = new MigrationItem("8", "Other name", "episode", null, 1, 50, "G.I. Joe", 0, 0, null, PlexGuids.Parse(["com.plexapp.agents.thetvdb://81189/1/50?lang=en"]), null, "9", show, FileName: "different.mkv");
        var bySeason = Assert.Single(MigrationMatcher.Match([source], [sameOrder]));
        Assert.Equal(MigrationMatchKind.Matched, bySeason.Kind);
        Assert.Equal("season/episode", bySeason.Via);
        Assert.Equal("8", bySeason.Destination!.RatingKey);

        var otherOrder = sameOrder with { SeasonNumber = 2, EpisodeNumber = 5, FileName = "G.I. Joe - S01E50.mkv" };
        var nearby = otherOrder with { RatingKey = "7", EpisodeNumber = 6, FileName = "G.I. Joe - S02E06.mkv", Title = "Nearby" };
        var byFile = Assert.Single(MigrationMatcher.Match([source], [otherOrder, nearby]));
        Assert.Equal("8", byFile.Destination!.RatingKey);
        Assert.Equal("file name", byFile.Via);

        var twin = otherOrder with { RatingKey = "6", Title = "Copy" };
        var ambiguous = Assert.Single(MigrationMatcher.Match([source], [otherOrder, twin]));
        Assert.Equal(MigrationMatchKind.Ambiguous, ambiguous.Kind);
        Assert.Null(ambiguous.Destination);

        var unrelated = nearby with { FileName = "G.I. Joe - S02E04.mkv", SeasonNumber = 2, EpisodeNumber = 4, Guids = PlexGuids.Parse(["tvdb://999"]) };
        var missing = Assert.Single(MigrationMatcher.Match([source], [unrelated]));
        Assert.Equal(MigrationMatchKind.Unmatched, missing.Kind);
        Assert.Contains("plex-episode:abcd1234", missing.Diagnosis, StringComparison.Ordinal);
        Assert.Contains("show tvdb:81189", missing.Diagnosis, StringComparison.Ordinal);
        Assert.Contains("file S01E50", missing.Diagnosis, StringComparison.Ordinal);
        Assert.Contains("S02E04", missing.Diagnosis, StringComparison.Ordinal);
        Assert.Contains("tvdb:999", missing.Diagnosis, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_episode_is_not_a_mismatch_and_names_how_a_match_was_made()
    {
        var show = PlexGuids.Parse(["tvdb://81189"]);
        var source = new MigrationItem("2", "The Funhouse", "episode", null, 1, 50, "G.I. Joe", 1, 0, null, PlexGuids.Parse(["plex://episode/abcd1234", "tvdb://424242"]), null, "1", show, FileName: "G I Joe - S01E50.mkv");
        var destination = source with { RatingKey = "8", ViewCount = 0, Guids = PlexGuids.Parse(["com.plexapp.agents.thetvdb://424242?lang=en"]) };
        var missing = new MigrationItem("3", "Missing", "episode", null, 1, 51, "G.I. Joe", 0, 0, null, PlexGuids.Parse(["plex://episode/bbbb2222"]), null, "1", show, FileName: "G I Joe - S01E51.mkv");
        var nearby = new MigrationItem("9", "Nearby", "episode", null, 2, 4, "G.I. Joe", 0, 0, null, PlexGuids.Parse(["tvdb://999"]), null, "9", show, FileName: "G I Joe - S02E04.mkv");
        var board = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched });
        board.Expect("Source", "TV episodes");
        board.Expect("Destination", "TV episodes");
        board.IngestSource("TV episodes", [source, missing], confirmed: true);
        board.IngestDestination("TV episodes", [destination, nearby]);
        board.FinishLibrary("Source", "TV episodes", 2);
        board.FinishLibrary("Destination", "TV episodes", 2);
        var names = new HashSet<string> { "TV episodes" };
        var opened = board.Libraries("all", null, null, names, null).Single();
        var matched = opened.Rows.Single(row => row.SourceText == "The Funhouse");
        Assert.Equal("matched by tvdb", matched.MatchNote);
        Assert.Contains("watched", matched.ResultText, StringComparison.Ordinal);
        var absent = opened.Rows.Single(row => row.SourceText == "Missing");
        Assert.Equal(LiveScanBoard.NotOnDestination, absent.ResultText);
        Assert.Empty(absent.Keys);
        Assert.True(absent.NoAction);
        Assert.Contains("S02E04", absent.UnmatchedDetail, StringComparison.Ordinal);
        Assert.Contains("only on source", opened.SummaryNote, StringComparison.Ordinal);
        var filtered = board.Libraries("mismatches", null, null, names, null).Single();
        Assert.Contains("mismatches", filtered.Summary, StringComparison.Ordinal);
        Assert.Contains(filtered.Rows, row => row.SourceText == "The Funhouse");
        Assert.DoesNotContain(filtered.Rows, row => row.SourceText == "Missing");
    }

    [Fact]
    public void Two_destination_items_with_the_same_id_are_ambiguous()
    {
        var source = Movie("1", "Shared", "tt1234567");
        var matches = MigrationMatcher.Match([source], [Movie("2", "One", "tt1234567"), Movie("3", "Two", "tt1234567")]);
        Assert.Equal(MigrationMatchKind.Ambiguous, Assert.Single(matches).Kind);
    }

    [Fact]
    public void Track_matches_through_the_album_id_and_track_number()
    {
        var sourceAlbum = new MigrationItem("5", "Album", "album", null, null, null, null, 0, 0, null, PlexGuids.Parse(["plex://album/abcd1234"]), null, null);
        var sourceTrack = new MigrationItem("6", "Song", "track", null, null, 2, null, 0, 0, null, [], "5", null);
        var destinationAlbum = sourceAlbum with { RatingKey = "15" };
        var destinationTrack = sourceTrack with { RatingKey = "16", ParentRatingKey = "15", Title = "Other name" };
        var match = MigrationMatcher.Match([sourceAlbum, sourceTrack], [destinationAlbum, destinationTrack]).Single(item => item.Source.RatingKey == "6");
        Assert.Equal("16", match.Destination!.RatingKey);
    }

    [Fact]
    public void Plan_uses_source_wins_and_does_not_apply_an_unmatched_title()
    {
        var source = new List<MigrationItem>
        {
            Movie("10", "Arrival", "tt2543164", watched: 1, rating: 9),
            Movie("11", "Shared", "tt1234567", watched: 0, rating: 8, offset: 5_000),
            Movie("12", "Same Title", null, watched: 1)
        };
        var destination = new List<MigrationItem>
        {
            Movie("20", "Arrival copy", "tt2543164"),
            Movie("21", "Shared", "tt1234567", watched: 1, rating: 2, offset: 90_000),
            Movie("22", "Same Title", null, watched: 1)
        };
        var built = MigrationPlanBuilder.Build(
            new MigrationCatalog(source, []),
            new MigrationCatalog(destination, []),
            new HashSet<MigrationCategory> { MigrationCategory.Watched, MigrationCategory.Progress, MigrationCategory.Ratings });
        var rows = built.Rows;
        Assert.True(built.Summary.AlreadySame > 0);
        Assert.DoesNotContain(rows, row => row.Detail == "Already the same.");

        Assert.Contains(rows, row => row.Kind == MigrationRowKind.WillApply && row.Action == "scrobble" && row.TargetRatingKey == "20");
        Assert.Contains(rows, row => row.Kind == MigrationRowKind.Conflict && row.Action == "unscrobble" && row.TargetRatingKey == "21");
        Assert.Contains(rows, row => row.Kind == MigrationRowKind.Conflict && row.Action == "rate" && row.TargetRatingKey == "21" && row.Rating == 8);
        Assert.Contains(rows, row => row.Kind == MigrationRowKind.Conflict && row.Action == "progress" && row.TargetRatingKey == "21");
        Assert.Contains(rows, row => row.Kind == MigrationRowKind.UnmatchedSource && row.Title.StartsWith("Same Title", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Kind == MigrationRowKind.UnmatchedDestination && row.Title.StartsWith("Same Title", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Kind is MigrationRowKind.WillApply or MigrationRowKind.Conflict && row.Title.StartsWith("Same Title", StringComparison.Ordinal));
    }

    [Fact]
    public void Matching_rows_stay_collapsed_and_differences_expand_per_metric()
    {
        var categories = new HashSet<MigrationCategory> { MigrationCategory.Watched, MigrationCategory.Progress, MigrationCategory.Ratings, MigrationCategory.Playlists };
        var quiet = Movie("1", "Quiet", "tt0000001", watched: 1);
        var same = MigrationPlanBuilder.ItemMetrics(quiet, quiet with { RatingKey = "2" }, categories, [], []);
        Assert.Contains(same, metric => metric.Name == "Watched status" && metric.Matches && metric.Keys.Count == 0);
        Assert.DoesNotContain(same, metric => metric.Name is "Viewing progress" or "Your rating" or "Playlist membership");
        Assert.DoesNotContain(same, metric => !metric.Matches);

        var source = Movie("10", "Arrival", "tt2543164", watched: 1, rating: 9, offset: 45_000);
        var destination = Movie("20", "Arrival", "tt2543164");
        var friday = new MigrationPlaylist("50", "Friday", "video", false, [source]);
        var metrics = MigrationPlanBuilder.ItemMetrics(source, destination, categories, [friday], [], null);
        var watched = Assert.Single(metrics, metric => metric.Name == "Watched status");
        var progress = Assert.Single(metrics, metric => metric.Name == "Viewing progress");
        var rating = Assert.Single(metrics, metric => metric.Name == "Your rating");
        var membership = Assert.Single(metrics, metric => metric.Name == "Playlist membership");
        Assert.False(watched.Matches);
        Assert.Equal("Watched", watched.SourceValue);
        Assert.Equal("Not watched", watched.DestinationValue);
        Assert.Equal("watched|10|20", watched.ActionKeys);
        Assert.False(progress.Matches);
        Assert.Equal("progress|10|20", progress.ActionKeys);
        Assert.Equal("0:45", progress.SourceValue);
        Assert.Equal("No progress", progress.DestinationValue);
        Assert.False(rating.Matches);
        Assert.Equal("rating|10|20", rating.ActionKeys);
        Assert.False(membership.Matches);
        Assert.Equal("Friday", membership.SourceValue);
        Assert.Contains("missing Friday", membership.DestinationValue, StringComparison.Ordinal);

        var built = MigrationPlanBuilder.Build(
            new MigrationCatalog([source], [friday]),
            new MigrationCatalog([destination], []),
            categories);
        Assert.Contains(built.Rows, row => row.Key == watched.ActionKeys && row.Action == "scrobble" && row.TargetRatingKey == "20");
        Assert.Contains(built.Rows, row => row.Key == progress.ActionKeys && row.Action == "progress");
        Assert.Contains(built.Rows, row => row.Key == rating.ActionKeys && row.Action == "rate");
        var playlistKey = built.Rows.Single(row => row.Action == "playlist-create").Key;
        var withKeys = MigrationPlanBuilder.ItemMetrics(source, destination, categories, [friday], [], built.Rows);
        Assert.Equal(playlistKey, Assert.Single(withKeys, metric => metric.Name == "Playlist membership").ActionKeys);

        var track = new MigrationItem("6", "Song", "track", null, null, 2, "Artist", 1, 8_000, 5, PlexGuids.Parse(["plex://track/abcd"]), "5", null);
        var other = track with { RatingKey = "16", ViewCount = 0, ViewOffset = 0, UserRating = null };
        var trackMetrics = MigrationPlanBuilder.ItemMetrics(track, other, categories, [], []);
        Assert.Equal(["Watched status", "Viewing progress", "Your rating"], trackMetrics.Select(metric => metric.Name).ToArray());
        Assert.DoesNotContain(trackMetrics, metric => metric.Name.Contains("play count", StringComparison.OrdinalIgnoreCase));
        Assert.All(trackMetrics, metric => Assert.False(metric.Matches));
    }

    private static MigrationItem Movie(string key, string title, string? imdb, int watched = 0, decimal? rating = null, long offset = 0)
        => new(key, title, "movie", 2020, null, null, null, watched, offset, rating, imdb is null ? [] : PlexGuids.Parse(["imdb://" + imdb]), null, null);

    private static MigrationItem Show(string key, string tvdb)
        => new(key, "Station", "show", 2016, null, null, null, 0, 0, null, PlexGuids.Parse(["tvdb://" + tvdb]), null, null);

    private static MigrationItem Episode(string key, string showKey, int season, int episode, string title)
        => new(key, title, "episode", null, season, episode, "Station", 1, 0, null, [], null, showKey);
}

public class MigrationFlowTests
{
    private const string SourceToken = "source-token-do-not-leak-11";
    private const string DestinationToken = "dest-token-do-not-leak-22";
    private const string SourceUrl = "http://192.0.2.20:32400";
    private const string DestinationUrl = "http://100.64.8.2:32400";

    [Fact]
    public void Write_policy_accepts_only_the_migration_mutations()
    {
        Assert.True(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/:/scrobble?key=20&identifier=com.plexapp.plugins.library"));
        Assert.True(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/:/progress?key=20&identifier=com.plexapp.plugins.library&time=5000&state=stopped"));
        Assert.True(PlexWritePolicy.IsAllowed(HttpMethod.Put, "/:/rate?key=20&identifier=com.plexapp.plugins.library&rating=8.0"));
        Assert.True(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/:/progress?key=20&identifier=com.plexapp.plugins.library&time=5000&state=stopped"));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/:/timeline?key=20&identifier=com.plexapp.plugins.library&time=5000&state=stopped"));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/:/scrobble?key=20&identifier=com.plexapp.plugins.library&X-Plex-Token=" + SourceToken));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Post, "/library/sections/1/refresh"));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Delete, "/library/sections/1"));
        Assert.False(PlexWritePolicy.IsAllowed(HttpMethod.Get, "/video/:/transcode/universal/start?key=1"));
        Assert.False(PlexReadOnlyPolicy.IsAllowed(HttpMethod.Get, "/:/scrobble?key=20&identifier=com.plexapp.plugins.library"));
        Assert.True(PlexMigrationReadPolicy.IsAllowed(HttpMethod.Get, "/library/metadata/20"));
        Assert.True(PlexMigrationReadPolicy.IsAllowed(HttpMethod.Get, "/library/sections/1/all?includeGuids=1&X-Plex-Container-Start=0&X-Plex-Container-Size=2&type=4"));
        Assert.False(PlexMigrationReadPolicy.IsAllowed(HttpMethod.Get, "/library/sections/1/all?includeGuids=0&type=4"));
        Assert.False(PlexMigrationReadPolicy.IsAllowed(HttpMethod.Get, "/:/scrobble?key=20&identifier=com.plexapp.plugins.library"));
    }

    [Fact]
    public async Task Preview_does_not_write_and_apply_writes_only_confirmed_matches()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = Library();

        var plan = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched, MigrationCategory.Ratings]);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));
        Assert.All(fixture.Handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        var scrobble = plan.Rows.Single(row => row.Action == "scrobble" && row.Kind == MigrationRowKind.WillApply && row.TargetRatingKey == "20");
        var rating = plan.Rows.Single(row => row.Action == "rate" && row.TargetRatingKey == "21" && row.Kind == MigrationRowKind.Conflict);
        Assert.Contains(plan.Rows, row => row.Kind == MigrationRowKind.UnmatchedSource && row.Title == "Same Title");

        fixture.Handler.Requests.Clear();
        var empty = await fixture.Service.ApplyAsync(plan.Id, []);
        Assert.Equal(0, empty.Applied);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));

        var result = await fixture.Service.ApplyAsync(plan.Id, [scrobble.Key]);
        Assert.Equal(1, result.Applied);
        Assert.Equal(0, result.Failed);
        var writes = fixture.Handler.Requests.Where(request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal)).ToList();
        var write = Assert.Single(writes);
        Assert.Equal("/:/scrobble", write.Uri.AbsolutePath);
        Assert.Contains("key=20", write.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("key=21", write.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("key=22", write.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceToken, write.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(DestinationToken, write.Token);

        var rated = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Ratings]);
        var conflict = rated.Rows.Single(row => row.Key == rating.Key);
        fixture.Handler.Requests.Clear();
        var ratedResult = await fixture.Service.ApplyAsync(rated.Id, [conflict.Key]);
        Assert.Equal(1, ratedResult.Applied);
        var rate = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/:/rate");
        Assert.Contains("key=21", rate.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("rating=8.0", rate.Uri.Query, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Put, rate.Method);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/:/scrobble");
        await fixture.AssertPreservedAsync(source.Id, destination.Id);
    }

    [Fact]
    public async Task Progress_write_uses_stopped_progress_and_a_reread_can_verify_it()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        long written = -1;
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/:/progress")
            {
                var time = request.Uri.Query.Split('&').First(part => part.Contains("time=", StringComparison.Ordinal)).Split('=').Last();
                written = long.Parse(time, System.Globalization.CultureInfo.InvariantCulture);
                return Json("""{"MediaContainer":{}}""");
            }

            if (request.Uri.AbsolutePath == "/library/metadata/21")
                return Json("{\"MediaContainer\":{\"Metadata\":[{\"ratingKey\":\"21\",\"title\":\"Shared\",\"type\":\"movie\",\"viewOffset\":" + written.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}]}}");
            return Library()(request);
        };

        var plan = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Progress]);
        var progress = plan.Rows.Single(row => row.Action == "progress" && row.TargetRatingKey == "21");
        fixture.Handler.Requests.Clear();
        var verified = await fixture.Service.ApplyAsync(plan.Id, [progress.Key]);
        Assert.Equal("Verified", verified.Items.Single(item => item.Action == "progress").Outcome);
        var write = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/:/progress");
        Assert.Contains("key=21", write.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("identifier=com.plexapp.plugins.library", write.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("state=stopped", write.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("time=" + progress.ProgressMs, write.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/:/timeline");
        fixture.AssertNoSecret(SourceToken);

        written = -1;
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/:/progress")
                return Json("""{"MediaContainer":{}}""");
            if (request.Uri.AbsolutePath == "/library/metadata/21")
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"21","title":"Shared","type":"movie","viewOffset":0}]}}""");
            return Library()(request);
        };
        var missed = await fixture.Service.ApplyAsync(plan.Id, [progress.Key]);
        Assert.Equal("Not reflected", missed.Items.Single(item => item.Action == "progress").Outcome);
        Assert.Equal(0, missed.Applied);
    }

    [Fact]
    public async Task Apply_keeps_running_after_the_caller_scope_closes()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = Library();
        string jobId;
        string key;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MigrationService>();
            var started = await service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
            jobId = started.Id;
        }

        await WaitForJob(fixture, jobId, TimeSpan.FromSeconds(5));
        var page = fixture.Jobs.Page("admin", jobId, "mismatches", 0, 50);
        key = page!.Rows.SelectMany(row => row.Keys).First();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MigrationService>();
            await service.StartApplyAsync(jobId, [key]);
        }

        var seen = new List<string>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        MigrationJobSnapshot? last = null;
        while (DateTime.UtcNow < until)
        {
            last = fixture.Jobs.Latest("admin");
            if (!string.IsNullOrEmpty(last?.ApplyStatus))
                seen.Add(last.ApplyStatus);
            if (last?.ApplyState is "Completed" or "Failed")
                break;
            await Task.Delay(15);
        }

        Assert.NotNull(last);
        Assert.Equal("Completed", last.ApplyState);
        Assert.Contains(seen, status => status.Contains("Finished", StringComparison.Ordinal));
        Assert.Contains(last.ApplyItems ?? [], item => item.Outcome == "Applied" && item.Title.Contains("Arrival", StringComparison.Ordinal));
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/:/scrobble");
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.Query.Contains(SourceToken, StringComparison.Ordinal));
        fixture.AssertNoSecret(SourceToken);
        fixture.AssertNoSecret(DestinationToken);
    }

    [Fact]
    public async Task Apply_from_a_scope_with_no_circuit_reaches_progress_and_playlist_writes()
    {
        await using var fixture = new MigrationFixture(circuitOnly: true);
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = request =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/playlists")
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"77","title":"Friday","playlistType":"video"}]}}""");
            if (request.Uri.AbsolutePath == "/playlists" && request.Uri.IdnHost == "192.0.2.20")
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"50","title":"Friday","playlistType":"video","smart":false}]}}""");
            if (request.Uri.AbsolutePath == "/playlists/50/items")
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"10","title":"Arrival","type":"movie","Guid":[{"id":"imdb://tt2543164"}]}]}}""");
            if (request.Uri.AbsolutePath == "/playlists/77/items")
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"20","title":"Arrival copy","type":"movie"}]}}""");
            if (request.Uri.AbsolutePath == "/:/progress")
                return Json("""{"MediaContainer":{}}""");
            return Library()(request);
        };

        await using var circuit = fixture.Provider.CreateAsyncScope();
        fixture.UseCircuit(circuit.ServiceProvider);
        var service = circuit.ServiceProvider.GetRequiredService<MigrationService>();
        var started = await service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Progress, MigrationCategory.Playlists]);
        await WaitForJob(fixture, started.Id, TimeSpan.FromSeconds(5));
        var rows = fixture.Jobs.Rows("admin", started.Id) ?? [];
        var progress = rows.Single(row => row.Action == "progress" && row.TargetRatingKey == "21");
        var playlist = rows.Single(row => row.Action == "playlist-create");
        await service.StartApplyAsync(started.Id, [progress.Key, playlist.Key]);

        MigrationJobSnapshot? last = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            last = fixture.Jobs.Latest("admin");
            if (last?.ApplyState is "Completed" or "Failed")
                break;
            await Task.Delay(20);
        }

        Assert.NotNull(last);
        Assert.Equal("Completed", last.ApplyState);
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/:/progress");
        Assert.Contains(fixture.Handler.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/playlists");
        Assert.DoesNotContain(last.ApplyStatus, "off the circuit", StringComparison.Ordinal);
        fixture.AssertNoSecret(DestinationToken);
    }

    [Fact]
    public async Task A_fatal_apply_saves_an_audit_keeps_the_selection_and_names_the_reason()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/:/progress")
                throw new InvalidOperationException("progress write failed " + DestinationToken);
            return Library()(request);
        };

        string jobId;
        string key;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MigrationService>();
            var started = await service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Progress]);
            jobId = started.Id;
        }

        await WaitForJob(fixture, jobId, TimeSpan.FromSeconds(5));
        key = (fixture.Jobs.Rows("admin", jobId) ?? []).Single(row => row.Action == "progress" && row.TargetRatingKey == "21").Key;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MigrationService>();
            await service.SelectAsync(jobId, [key], true);
            await service.StartApplyAsync(jobId, [key]);
        }

        MigrationJobSnapshot? last = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            last = fixture.Jobs.Latest("admin");
            if (last?.ApplyState is "Completed" or "Failed")
                break;
            await Task.Delay(20);
        }

        Assert.NotNull(last);
        Assert.Equal("Failed", last.ApplyState);
        Assert.Contains("Apply failed:", last.ApplyStatus, StringComparison.Ordinal);
        Assert.Contains("progress write failed", last.ApplyStatus, StringComparison.Ordinal);
        Assert.Contains("Applied 0", last.ApplyStatus, StringComparison.Ordinal);
        Assert.Contains("Failed 1", last.ApplyStatus, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", last.ApplyStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(DestinationToken, last.ApplyStatus, StringComparison.Ordinal);
        Assert.Equal(key, Assert.Single(fixture.Jobs.Selection("admin", jobId)));
        await using var auditScope = fixture.Provider.CreateAsyncScope();
        var audit = await auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MigrationAudits.Include(item => item.Items).SingleAsync();
        Assert.Equal("Failed", audit.Outcome);
        Assert.Contains(audit.Items, item => (item.Detail ?? "").Contains("progress write failed", StringComparison.Ordinal));
        Assert.DoesNotContain(audit.Items, item => (item.Detail ?? "").Contains(DestinationToken, StringComparison.Ordinal));
        fixture.AssertNoSecret(DestinationToken);
    }

    [Fact]
    public void Home_user_requests_keep_the_pin_out_of_the_url()
    {
        var kid = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        Assert.True(PlexAuthPolicy.IsAllowed(HttpMethod.Get, new Uri("https://plex.tv/api/v2/home/users")));
        Assert.True(PlexAuthPolicy.IsAllowed(HttpMethod.Post, new Uri("https://plex.tv/api/v2/home/users/" + kid + "/switch")));
        Assert.False(PlexAuthPolicy.IsAllowed(HttpMethod.Post, new Uri("https://plex.tv/api/v2/home/users/" + kid + "/switch?pin=9876")));
        var users = PlexAuthJson.ParseHomeUsers("""
            {"users":[
              {"uuid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","title":"Ada","admin":true,"protected":false},
              {"uuid":"bbbbbbbb-cccc-dddd-eeee-ffffffffffff","title":"Kid","admin":false,"protected":true}
            ]}
            """);
        Assert.Equal("Ada", users.Single(user => user.Admin).Name);
        Assert.True(users.Single(user => user.Name == "Kid").Protected);
    }

    [Fact]
    public async Task A_home_user_uses_that_users_server_tokens_and_keeps_a_separate_plan()
    {
        const string kid = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        const string pin = "9876";
        const string kidSource = "kid-source-token-aaaa";
        const string kidDestination = "kid-dest-token-bbbb";
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.PlexServers.ToListAsync();
            rows.Single(row => row.Id == source.Id).MachineIdentifier = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            rows.Single(row => row.Id == destination.Id).MachineIdentifier = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            db.PlexAccountLinks.Add(new PlexAccountLink
            {
                UserId = "admin",
                ClientIdentifier = "plexbridge-client-0001",
                ProtectedAccountToken = protector.Protect("admin-account-token-ok"),
                Username = "Ada",
                PlexUserId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                UpdatedUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        fixture.AuthHandler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/api/v2/home/users")
            {
                return Json("""
                    {"users":[
                      {"uuid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","title":"Ada","admin":true,"protected":false},
                      {"uuid":"bbbbbbbb-cccc-dddd-eeee-ffffffffffff","title":"Kid","admin":false,"protected":true}
                    ]}
                    """);
            }
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/switch", StringComparison.Ordinal))
                return Json("""{"authToken":"switched-user-token1"}""");
            if (request.Uri.AbsolutePath == "/api/v2/resources")
            {
                return Json("""
                    [
                      {"name":"NAS","provides":"server","clientIdentifier":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","accessToken":"kid-source-token-aaaa"},
                      {"name":"Desktop","provides":"server","clientIdentifier":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","accessToken":"kid-dest-token-bbbb"}
                    ]
                    """);
            }
            return Json("""{"MediaContainer":{}}""");
        };
        fixture.Handler.Responder = Library();

        var users = fixture.Provider.CreateScope().ServiceProvider.GetRequiredService<MigrationPlexUsers>();
        var listed = await users.ListAsync();
        Assert.Contains(listed, person => person.Name == "Kid" && person.Protected);
        var name = await users.ActivateAsync(kid, pin);
        Assert.Equal("Kid", name);
        var switched = fixture.AuthHandler.Requests.Single(request => request.Uri.AbsolutePath.EndsWith("/switch", StringComparison.Ordinal));
        Assert.DoesNotContain("pin=", switched.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("pin=" + pin, switched.Body, StringComparison.Ordinal);

        var started = await fixture.Service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched], plexUserKey: kid, plexUserName: "Kid");
        await WaitForJob(fixture, started.Id, TimeSpan.FromSeconds(5), kid);
        Assert.Contains(fixture.Handler.Requests, request => request.Token == kidSource);
        Assert.Contains(fixture.Handler.Requests, request => request.Token == kidDestination);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Token == SourceToken || request.Token == DestinationToken);
        var rowKey = (fixture.Jobs.Rows("admin", started.Id) ?? []).First(row => row.Action == "scrobble").Key;
        await fixture.Service.SelectAsync(started.Id, [rowKey], true);
        Assert.Equal(rowKey, Assert.Single(fixture.Jobs.Selection("admin", started.Id)));
        Assert.Null(fixture.Jobs.Latest("admin"));
        Assert.Equal("Kid", fixture.Jobs.Latest("admin", kid)!.PlexUserName);

        await fixture.Service.StartApplyAsync(started.Id, [rowKey]);
        MigrationJobSnapshot? last = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            last = fixture.Jobs.Latest("admin", kid);
            if (last?.ApplyState is "Completed" or "Failed")
                break;
            await Task.Delay(20);
        }

        Assert.Equal("Completed", last!.ApplyState);
        Assert.Empty(fixture.Jobs.Selection("admin", started.Id));
        Assert.Null(fixture.Jobs.Latest("admin"));
        await using var auditScope = fixture.Provider.CreateAsyncScope();
        var audit = await auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MigrationAudits.SingleAsync();
        Assert.Equal("Kid", audit.PlexUserName);
        Assert.Equal(kid, audit.PlexUserUuid);
        fixture.AssertNoSecret(pin);
        fixture.AssertNoSecret(kidSource);
        fixture.AssertNoSecret(kidDestination);
        fixture.AssertNoSecret("switched-user-token1");
    }

    [Fact]
    public async Task A_changed_library_writes_nothing()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath.StartsWith("/library/metadata/", StringComparison.Ordinal)
                && request.Uri.IdnHost == "192.0.2.20")
                return Json(MetadataItem(request.Uri.IdnHost, request.Uri.AbsolutePath["/library/metadata/".Length..], arrivalWatched: false));
            if (request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal))
                return Json(Items(request.Uri.IdnHost, arrivalWatched: true));
            return Library()(request);
        };

        var plan = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
        fixture.Handler.Requests.Clear();
        var error = await Assert.ThrowsAsync<PlexBridgeException>(() => fixture.Service.ApplyAsync(plan.Id, plan.Rows.Select(row => row.Key).ToArray()));
        Assert.Contains("Nothing was written", error.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/library/metadata/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_item_does_not_stop_the_others_and_the_token_stays_out_of_the_audit()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/:/scrobble" && request.Uri.Query.Contains("key=25", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(DestinationToken)
                };
            if (request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal))
                return Json("""{"MediaContainer":{}}""");
            return Library()(request);
        };

        var plan = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
        var keys = plan.Rows.Where(row => row.Kind == MigrationRowKind.WillApply && row.Action == "scrobble").Select(row => row.Key).ToArray();
        Assert.Equal(3, keys.Length);
        var result = await fixture.Service.ApplyAsync(plan.Id, keys);
        Assert.Equal(2, result.Applied);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Items, item => item.Outcome == "Failed" && (item.Detail ?? "").StartsWith("Couldn't mark", StringComparison.Ordinal));
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.Query.Contains("key=20", StringComparison.Ordinal));
        Assert.Contains(fixture.Handler.Requests, request => request.Uri.Query.Contains("key=26", StringComparison.Ordinal));
        Assert.All(result.Items, item => Assert.DoesNotContain(DestinationToken, item.Detail ?? ""));
        await using var scope = fixture.Provider.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().MigrationAudits.Include(item => item.Items).SingleAsync();
        Assert.Equal(2, audit.AppliedCount);
        Assert.Equal(1, audit.FailedCount);
        var details = string.Join('\n', audit.Items.Select(item => item.Title + " " + item.Detail));
        Assert.DoesNotContain(DestinationToken, details, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceUrl, details, StringComparison.Ordinal);
        fixture.AssertNoSecret(DestinationToken);
    }

    [Fact]
    public async Task Unauthorized_preview_makes_no_request()
    {
        await using var fixture = new MigrationFixture { Admin = false };
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = Library();
        var error = await Assert.ThrowsAsync<PlexBridgeException>(() => fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]));
        Assert.Contains("admin", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task A_hung_read_ends_without_a_write()
    {
        await using var fixture = new MigrationFixture();
        fixture.Options.RequestTimeout = TimeSpan.FromMilliseconds(200);
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = request =>
            request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal) ? null! : Library()(request);
        fixture.Handler.AsyncResponder = async (request, _) =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal))
                return Library()(request);
            await Task.Delay(TimeSpan.FromSeconds(5));
            return Json(Items(request.Uri.IdnHost, true));
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<PlexBridgeException>(() => fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), started.Elapsed.ToString());
        Assert.Contains("Nothing was written", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Library_pages_are_followed()
    {
        var handler = new ScriptHandler();
        var items = Enumerable.Range(1, 5).Select(index =>
            "{\"ratingKey\":\"" + index + "\",\"title\":\"Film " + index + "\",\"type\":\"movie\",\"Guid\":[{\"id\":\"imdb://tt100000" + index + "\"}]}");
        handler.Responder = request =>
        {
            if (request.Uri.AbsolutePath == "/library/sections")
                return Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Films"}]}}""");
            var start = 0;
            foreach (var part in request.Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                const string marker = "X-Plex-Container-Start=";
                if (part.StartsWith(marker, StringComparison.Ordinal))
                    start = int.Parse(part[marker.Length..]);
            }
            var page = items.Skip(start).Take(2);
            return Json("{\"MediaContainer\":{\"Metadata\":[" + string.Join(',', page) + "]}}");
        };
        var reader = new PlexMigrationReader(
            new HandlerTransport(handler),
            new AllowAllAddressGuard(),
            new MigrationPreviewOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlexMigrationReader>.Instance);
        var catalog = await reader.ReadCatalogAsync(new Uri("http://100.64.8.2:32400/"), DestinationToken, false, 2, "Desktop", "Destination", null, CancellationToken.None, null);
        Assert.Equal(5, catalog.Items.Count);
        Assert.Equal(3, handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal)));
        Assert.All(
            handler.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal)),
            request => Assert.Contains("includeGuids=1", request.Uri.Query, StringComparison.Ordinal));
    }

    [Fact]
    public void A_verified_playlist_shows_the_destination_and_matches()
    {
        var tracks = Enumerable.Range(1, 5).Select(index => new MigrationItem(index.ToString(), "Track " + index, "track", null, null, index, "Soen", 0, 0, null, PlexGuids.Parse(["plex://track/aaa" + index.ToString("x")]), null, null, ParentTitle: "Lykaia")).ToList();
        var destTracks = tracks.Select(track => track with { RatingKey = "d" + track.RatingKey }).ToList();
        var playlist = new MigrationPlaylist("50", "Soen", "audio", false, tracks);
        var built = MigrationPlanBuilder.Build(
            new MigrationCatalog(tracks, [playlist]),
            new MigrationCatalog(destTracks, []),
            new HashSet<MigrationCategory> { MigrationCategory.Playlists });
        var board = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Playlists });
        board.Expect("Source", "Playlists");
        board.Expect("Destination", "Playlists");
        board.IngestSource("Music tracks", tracks, confirmed: true);
        board.IngestDestination("Music tracks", destTracks);
        board.AddPlaylist("Source", playlist, confirmed: true);
        board.FinishLibrary("Source", "Playlists", 1);
        board.FinishLibrary("Destination", "Playlists", 0);
        board.Stamp(built.Comparisons, built.Rows);
        var before = board.Libraries("all", null, null, new HashSet<string> { "Playlists" }, null).Single(section => section.Name == "Playlists");
        var row = before.Rows.Single(item => item.SourceText == "Soen");
        Assert.Equal("not on destination", row.DestinationText);
        Assert.True(row.Keys.Count >= 5);
        var first = row.Keys[0];
        board.NoteApplied(new AppliedObservation(first, true, "Soen", "audio playlist · 1 item", null, null, null));
        var partial = board.Libraries("mismatches", null, null, new HashSet<string> { "Playlists" }, null).Single(section => section.Name == "Playlists").Rows.Single(item => item.SourceText == "Soen");
        Assert.Equal("Soen", partial.DestinationText);
        Assert.Equal("audio playlist · 1 item", partial.DestinationDetail);
        Assert.Equal("playlist mismatch", partial.ResultText);
        Assert.DoesNotContain(first, partial.Keys);
        foreach (var key in row.Keys.Skip(1))
            board.NoteApplied(new AppliedObservation(key, true, "Soen", "audio playlist · 5 items", null, null, null));
        var done = board.Libraries("all", null, null, new HashSet<string> { "Playlists" }, null).Single(section => section.Name == "Playlists");
        var matched = done.Rows.Single(item => item.SourceText == "Soen");
        Assert.Empty(board.Libraries("mismatches", null, null, new HashSet<string> { "Playlists" }, null).Single(section => section.Name == "Playlists").Rows);
        Assert.Equal("matching", matched.ResultText);
        Assert.True(matched.JustApplied);
        Assert.True(matched.Settled);
        Assert.Empty(matched.Keys);
        Assert.Equal("audio playlist · 5 items", matched.DestinationDetail);
        Assert.False(matched.DestinationAbsent);
        Assert.Equal("matching", done.Summary);

        var movie = new MigrationItem("10", "Arrival", "movie", 2016, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt2543164"]), null, null);
        var other = movie with { RatingKey = "20", ViewCount = 0 };
        var watched = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched, MigrationCategory.Progress });
        watched.Expect("Source", "Movies");
        watched.Expect("Destination", "Movies");
        watched.IngestSource("Movies", [movie with { ViewOffset = 45_000 }], confirmed: true);
        watched.IngestDestination("Movies", [other]);
        watched.FinishLibrary("Source", "Movies", 1);
        watched.FinishLibrary("Destination", "Movies", 1);
        var open = watched.Libraries("all", null, null, new HashSet<string> { "Movies" }, null).Single().Rows.Single();
        var progress = open.Keys.Single(key => key.StartsWith("progress|", StringComparison.Ordinal));
        watched.NoteApplied(new AppliedObservation(progress, false, null, null, false, 0, null));
        var still = watched.Libraries("mismatches", null, null, new HashSet<string> { "Movies" }, null).Single().Rows.Single();
        Assert.Contains("progress", still.ResultText, StringComparison.Ordinal);
        Assert.Contains(progress, still.Keys);
        Assert.Contains(still.MetricRows, metric => metric.Name == "Viewing progress" && metric.DestinationValue == "No progress" && !metric.Matches);
    }

    [Fact]
    public void A_mismatch_header_always_has_a_row_under_mismatches()
    {
        var track = new MigrationItem("1", "Track 1", "track", null, null, 1, "Soen", 0, 0, null, PlexGuids.Parse(["plex://track/aaa1"]), null, null, ParentTitle: "Lykaia");
        var destTrack = track with { RatingKey = "d1" };
        var unmatched = new MigrationItem("9", "Unmatched", "track", null, null, 1, "Band", 0, 0, null, [], null, null);
        var smart = new MigrationPlaylist("51", "All Music", "audio", true, []);
        var fresh = new MigrationPlaylist("52", "Fresh", "audio", false, [unmatched]);
        var real = new MigrationPlaylist("50", "Soen", "audio", false, [track]);
        var movie = new MigrationItem("10", "Arrival", "movie", 2016, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt2543164"]), null, null);
        var destMovie = movie with { RatingKey = "20", ViewCount = 0 };
        var clip = new MigrationItem("90", "Home video", "movie", 2013, null, null, null, 0, 0, null, PlexGuids.Parse(["imdb://tt9999999"]), null, null);
        var categories = new HashSet<MigrationCategory> { MigrationCategory.Watched, MigrationCategory.Playlists };
        var built = MigrationPlanBuilder.Build(
            new MigrationCatalog([track, unmatched, movie], [smart, fresh, real]),
            new MigrationCatalog([destTrack, destMovie, clip], []),
            categories);
        var board = new LiveScanBoard(categories);
        board.Expect("Source", "Movies");
        board.Expect("Destination", "Movies");
        board.Expect("Source", "Other Videos");
        board.Expect("Destination", "Other Videos");
        board.Expect("Source", "Playlists");
        board.Expect("Destination", "Playlists");
        board.IngestSource("Movies", [movie], confirmed: true);
        board.IngestDestination("Movies", [destMovie]);
        board.FinishLibrary("Source", "Movies", 1);
        board.FinishLibrary("Destination", "Movies", 1);
        board.IngestSource("Music tracks", [track, unmatched], confirmed: true);
        board.IngestDestination("Music tracks", [destTrack]);
        board.IngestDestination("Other Videos", [clip]);
        board.FinishLibrary("Source", "Other Videos", 0);
        board.FinishLibrary("Destination", "Other Videos", 1);
        board.AddPlaylist("Source", smart, confirmed: true);
        board.AddPlaylist("Source", fresh, confirmed: true);
        board.AddPlaylist("Source", real, confirmed: true);
        board.FinishLibrary("Source", "Playlists", 3);
        board.FinishLibrary("Destination", "Playlists", 0);
        board.Stamp(built.Comparisons, built.Rows);

        var names = new HashSet<string> { "Movies", "Music tracks", "Other Videos", "Playlists" };
        var all = board.Libraries("all", null, null, names, null);
        var filtered = board.Libraries("mismatches", null, null, names, null);
        foreach (var section in all)
        {
            var rows = filtered.Single(item => item.Name == section.Name).Rows;
            if (section.Summary.Contains("mismatches", StringComparison.Ordinal))
                Assert.NotEmpty(rows);
            else
                Assert.Empty(rows);
        }

        var playlists = all.Single(section => section.Name == "Playlists");
        Assert.Contains("mismatches", playlists.Summary, StringComparison.Ordinal);
        Assert.Contains(playlists.Rows, row => row.SourceText == "Soen" && row.Keys.Count > 0);
        Assert.Contains(playlists.Rows, row => row.SourceText == "All Music" && row.ResultText.StartsWith("skipped · smart playlist", StringComparison.Ordinal));
        Assert.Contains(playlists.Rows, row => row.SourceText == "Fresh" && row.ResultText.Contains("no matching tracks", StringComparison.Ordinal));
        var videos = all.Single(section => section.Name == "Other Videos");
        Assert.Equal("matching", videos.Summary);
        Assert.Contains("only on destination", videos.SummaryNote, StringComparison.Ordinal);
        Assert.Empty(videos.Keys);
        Assert.DoesNotContain("mismatches", videos.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_progress_is_one_row_per_library()
    {
        var phases = new List<MigrationPhaseView>
        {
            new("Source", "Movies", "Films", "Done", 4, 635, 1),
            new("Source", "Movies", "Kids", "Reading", 2, 40, 1),
            new("Destination", "Movies", "Films", "Done", 3, 600, 1),
            new("Source", "Music tracks", "Albums", "Done", 8, 1200, 1),
            new("Source", "Catalog", "", "Closed", 0, 0, 1),
            new("Destination", "Catalog", "", "Closed", 0, 0, 1),
            new("Source", "Playlists", "Playlists", "Waiting", 0, 0, 0)
        };
        var rows = PreviewProgress.Rows(phases);
        var movies = rows.Single(row => row.Label == "Movies");
        Assert.Equal("reading", movies.Source.Kind);
        Assert.Equal(675, movies.Source.Items);
        Assert.Equal("done", movies.Destination.Kind);
        Assert.Equal(600, movies.Destination.Items);
        var music = rows.Single(row => row.Label == "Music");
        Assert.Equal("done", music.Source.Kind);
        Assert.Equal("absent", music.Destination.Kind);
        var playlists = rows.Single(row => row.Label == "Playlists");
        Assert.Equal("waiting", playlists.Source.Kind);
        Assert.Equal("waiting", playlists.Destination.Kind);
        Assert.DoesNotContain(rows, row => row.Label == "Music tracks");
        Assert.Equal("6,000", PreviewProgress.Count(6000));
        var meter = PreviewProgress.Meter(rows);
        Assert.Equal(2, meter.Done);
        Assert.True(meter.Total >= meter.Done);
    }

    [Fact]
    public async Task Each_playlist_items_are_fetched_once()
    {
        var handler = new ScriptHandler
        {
            Responder = request =>
            {
                if (request.Uri.AbsolutePath == "/library/sections")
                    return Json("""{"MediaContainer":{"Directory":[]}}""");
                if (request.Uri.AbsolutePath == "/playlists")
                {
                    return Json("""
                        {"MediaContainer":{"Metadata":[
                          {"ratingKey":"10","title":"Short","playlistType":"video","smart":false},
                          {"ratingKey":"11","title":"Paged","playlistType":"video","smart":false},
                          {"ratingKey":"12","title":"Stuck","playlistType":"video","smart":false}
                        ]}}
                        """);
                }

                var start = StartOf(request.Uri.Query);
                if (request.Uri.AbsolutePath == "/playlists/10/items")
                    return Json(start == 0
                        ? """{"MediaContainer":{"Metadata":[{"ratingKey":"1","title":"One","type":"movie"}]}}"""
                        : """{"MediaContainer":{"Metadata":[]}}""");
                if (request.Uri.AbsolutePath == "/playlists/11/items")
                {
                    if (start == 0)
                        return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"2","title":"A","type":"movie"},{"ratingKey":"3","title":"B","type":"movie"}]}}""");
                    if (start == 2)
                        return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"4","title":"C","type":"movie"}]}}""");
                    return Json("""{"MediaContainer":{"Metadata":[]}}""");
                }

                if (request.Uri.AbsolutePath == "/playlists/12/items")
                    return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"8","title":"Same","type":"movie"},{"ratingKey":"9","title":"Again","type":"movie"}]}}""");
                return Json("""{"MediaContainer":{}}""");
            }
        };
        var reader = new PlexMigrationReader(
            new HandlerTransport(handler),
            new AllowAllAddressGuard(),
            new MigrationPreviewOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlexMigrationReader>.Instance);
        var catalog = await reader.ReadCatalogAsync(new Uri("http://100.64.8.2:32400/"), DestinationToken, true, 2, "Desktop", "Destination", null, CancellationToken.None, null);
        Assert.Equal(3, catalog.Playlists.Count);
        Assert.Single(catalog.Playlists.Single(playlist => playlist.RatingKey == "10").Items);
        Assert.Equal(3, catalog.Playlists.Single(playlist => playlist.RatingKey == "11").Items.Count);
        Assert.Equal(2, catalog.Playlists.Single(playlist => playlist.RatingKey == "12").Items.Count);
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath == "/playlists/10/items");
        var paged = handler.Requests.Where(request => request.Uri.AbsolutePath == "/playlists/11/items").ToList();
        Assert.Equal(2, paged.Count);
        Assert.Equal([0, 2], paged.Select(request => StartOf(request.Uri.Query)).ToArray());
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath == "/playlists/12/items"));
    }

    [Fact]
    public async Task A_second_preview_keeps_the_run_already_in_flight()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.AsyncResponder = async (request, token) =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal) && entered.TrySetResult())
                await release.Task.WaitAsync(token);
            return Library()(request);
        };

        var first = await fixture.Service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = await fixture.Service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(MigrationJobState.Running, fixture.Jobs.Latest("admin")!.State);
        release.SetResult();
        var done = await WaitForJob(fixture, first.Id, TimeSpan.FromSeconds(5));
        Assert.Equal(MigrationJobState.Completed, done.State);
    }

    [Fact]
    public async Task Playlist_copy_adds_only_matched_items_and_skips_smart_playlists()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = request =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/playlists")
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"77","title":"Friday","playlistType":"video"}]}}""");
            if (request.Uri.AbsolutePath == "/playlists" && request.Uri.IdnHost == "192.0.2.20")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[
                      {"ratingKey":"50","title":"Friday","playlistType":"video","smart":false},
                      {"ratingKey":"51","title":"Suggested","playlistType":"video","smart":true}
                    ]}}
                    """);
            }
            if (request.Uri.AbsolutePath == "/playlists")
                return Json("""{"MediaContainer":{"Metadata":[]}}""");
            if (request.Uri.AbsolutePath == "/playlists/50/items")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[
                      {"ratingKey":"10","title":"Arrival","type":"movie","Guid":[{"id":"imdb://tt2543164"}]},
                      {"ratingKey":"12","title":"Same Title","type":"movie"}
                    ]}}
                    """);
            }
            if (request.Uri.AbsolutePath == "/playlists/77/items")
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"20","title":"Arrival copy","type":"movie"}]}}""");
            if (request.Uri.AbsolutePath.StartsWith("/playlists/", StringComparison.Ordinal))
                return Json("""{"MediaContainer":{}}""");
            return Library()(request);
        };

        var plan = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Playlists]);
        Assert.Contains(plan.Rows, row => row.Action == "playlist-create" && row.TargetRatingKey == "20");
        Assert.Contains(plan.Rows, row => row.Kind == MigrationRowKind.UnmatchedSource && row.Title.Contains("Same Title", StringComparison.Ordinal));
        Assert.Contains(plan.Rows, row => row.Kind == MigrationRowKind.Skipped && row.Title == "Suggested");
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method == HttpMethod.Post);

        var keys = plan.Rows.Where(row => row.Action is "playlist-create" or "playlist-add").Select(row => row.Key).ToArray();
        var result = await fixture.Service.ApplyAsync(plan.Id, keys);
        Assert.True(result.Applied == 1, string.Join(" | ", result.Items.Select(item => item.Outcome + " " + item.Detail)));
        Assert.Equal("Verified", result.Items.Single().Outcome);
        Assert.Equal(0, result.Failed);
        Assert.Contains(fixture.Handler.Requests, request => request.Method == HttpMethod.Get && request.Uri.AbsolutePath == "/playlists/77/items");
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Contains(fixture.Logs.Logger.Messages, message => message.Contains("Migration write POST /playlists.", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logs.Logger.Messages, message => message.Contains(DestinationToken, StringComparison.Ordinal));
        var create = Assert.Single(fixture.Handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("uri=", create.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("metadata/20", Uri.UnescapeDataString(create.Uri.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("metadata/12", Uri.UnescapeDataString(create.Uri.Query), StringComparison.Ordinal);
        Assert.DoesNotContain(SourceToken, create.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.Query.Contains("51", StringComparison.Ordinal) && request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Existing_playlist_adds_a_missing_item_and_the_reread_verifies_it()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        var added = false;
        fixture.Handler.Responder = request =>
        {
            if (request.Method == HttpMethod.Put && request.Uri.AbsolutePath == "/playlists/70/items")
            {
                added = true;
                return Json("""{"MediaContainer":{"Metadata":[]}}""");
            }
            if (request.Uri.AbsolutePath == "/playlists" && request.Uri.IdnHost == "192.0.2.20")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[
                      {"ratingKey":"50","title":"Friday","playlistType":"video","smart":false}
                    ]}}
                    """);
            }
            if (request.Uri.AbsolutePath == "/playlists" && request.Uri.IdnHost != "192.0.2.20")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[
                      {"ratingKey":"70","title":"Friday","playlistType":"video","smart":false}
                    ]}}
                    """);
            }
            if (request.Uri.AbsolutePath == "/playlists/50/items")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[
                      {"ratingKey":"10","title":"Arrival","type":"movie","Guid":[{"id":"imdb://tt2543164"}]},
                      {"ratingKey":"14","title":"Second","type":"movie","Guid":[{"id":"imdb://tt2222222"}]}
                    ]}}
                    """);
            }
            if (request.Uri.AbsolutePath == "/playlists/70/items")
            {
                var extra = added ? ",{\"ratingKey\":\"25\",\"title\":\"Second copy\",\"type\":\"movie\"}" : "";
                return Json("{\"MediaContainer\":{\"Metadata\":[{\"ratingKey\":\"20\",\"title\":\"Arrival copy\",\"type\":\"movie\",\"Guid\":[{\"id\":\"imdb://tt2543164\"}]}" + extra + "]}}");
            }
            return Library()(request);
        };

        var plan = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Playlists]);
        var add = Assert.Single(plan.Rows, row => row.Action == "playlist-add");
        Assert.Equal("25", add.TargetRatingKey);
        Assert.DoesNotContain(plan.Rows, row => row.Action == "playlist-create");
        var result = await fixture.Service.ApplyAsync(plan.Id, [add.Key]);
        Assert.Equal("Verified", Assert.Single(result.Items).Outcome);
        var write = Assert.Single(fixture.Handler.Requests, request => request.Method == HttpMethod.Put);
        Assert.Equal("/playlists/70/items", write.Uri.AbsolutePath);
        Assert.Contains("metadata/25", Uri.UnescapeDataString(write.Uri.Query), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method == HttpMethod.Post || request.Method == HttpMethod.Delete);
        Assert.Contains(fixture.Logs.Logger.Messages, message => message.Contains("Migration write PUT /playlists/70/items.", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logs.Logger.Messages, message => message.Contains(DestinationToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applying_a_playlist_twice_adds_nothing_the_second_time()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        var members = new List<string>();
        string? created = null;
        fixture.Handler.Responder = request =>
        {
            var destinationHost = request.Uri.IdnHost != "192.0.2.20";
            if (destinationHost && request.Method == HttpMethod.Get && request.Uri.AbsolutePath == "/playlists")
            {
                return created is null
                    ? Json("""{"MediaContainer":{"Metadata":[]}}""")
                    : Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"77","title":"Friday","playlistType":"video","smart":false}]}}""");
            }
            if (!destinationHost && request.Method == HttpMethod.Get && request.Uri.AbsolutePath == "/playlists")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[{"ratingKey":"50","title":"Friday","playlistType":"video","smart":false}]}}
                    """);
            }
            if (request.Uri.AbsolutePath == "/playlists/50/items")
            {
                return Json("""
                    {"MediaContainer":{"Metadata":[
                      {"ratingKey":"10","title":"Arrival","type":"movie","Guid":[{"id":"imdb://tt2543164"}]},
                      {"ratingKey":"14","title":"Second","type":"movie","Guid":[{"id":"imdb://tt2222222"}]}
                    ]}}
                    """);
            }
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/playlists")
            {
                created = "77";
                var key = MetadataKey(request.Uri.Query);
                if (members.Contains(key))
                    return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
                members.Add(key);
                return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"77","title":"Friday","playlistType":"video"}]}}""");
            }
            if (request.Method == HttpMethod.Put && request.Uri.AbsolutePath == "/playlists/77/items")
            {
                var key = MetadataKey(request.Uri.Query);
                if (members.Contains(key))
                    return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
                members.Add(key);
                return Json("""{"MediaContainer":{}}""");
            }
            if (request.Uri.AbsolutePath == "/playlists/77/items")
            {
                var body = string.Join(',', members.Select(key => "{\"ratingKey\":\"" + key + "\",\"type\":\"movie\"}"));
                return Json("{\"MediaContainer\":{\"Metadata\":[" + body + "]}}");
            }
            return Library()(request);
        };

        var plan = await fixture.Service.PreviewAsync(source.Id, destination.Id, [MigrationCategory.Playlists]);
        var keys = plan.Rows.Where(row => row.Action is "playlist-create" or "playlist-add").Select(row => row.Key).ToArray();
        Assert.Equal(2, keys.Length);
        var first = await fixture.Service.ApplyAsync(plan.Id, keys);
        Assert.Equal(2, first.Applied);
        Assert.Equal(2, members.Count);
        Assert.Equal(members.Distinct(StringComparer.Ordinal).Count(), members.Count);
        var writes = fixture.Handler.Requests.Count(request => request.Method == HttpMethod.Post || request.Method == HttpMethod.Put);
        var second = await fixture.Service.ApplyAsync(plan.Id, keys);
        Assert.Equal(0, second.Applied);
        Assert.Equal(2, second.Skipped);
        Assert.All(second.Items, item => Assert.Equal("Skipped", item.Outcome));
        Assert.Equal(writes, fixture.Handler.Requests.Count(request => request.Method == HttpMethod.Post || request.Method == HttpMethod.Put));
        Assert.Equal(2, members.Count);
    }

    private static string MetadataKey(string query)
    {
        var text = Uri.UnescapeDataString(query);
        var marker = "/library/metadata/";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return "";
        var rest = text[(at + marker.Length)..];
        var end = rest.IndexOfAny(['&', '"', ' ']);
        return end < 0 ? rest : rest[..end];
    }

    [Fact]
    public void Playlist_rows_list_matched_items_and_the_library_can_make_them_match()
    {
        var arrival = new MigrationItem("10", "Arrival", "movie", 2016, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt2543164"]), null, null);
        var second = new MigrationItem("14", "Second", "movie", 2010, null, null, null, 0, 0, null, PlexGuids.Parse(["imdb://tt2222222"]), null, null);
        var dawn = new MigrationItem("13", "Dawn", "track", null, null, 1, "Max Richter", 0, 0, null, [], null, null, ParentTitle: "Sleep");
        var destArrival = arrival with { RatingKey = "20", Title = "Arrival copy" };
        var destSecond = second with { RatingKey = "25", Title = "Second copy" };
        var friday = new MigrationPlaylist("50", "Friday", "video", false, [arrival, second, dawn]);
        var road = new MigrationPlaylist("60", "Road", "video", false, [arrival, second]);
        var destFriday = new MigrationPlaylist("70", "Friday", "video", false, [destArrival]);
        var built = MigrationPlanBuilder.Build(
            new MigrationCatalog([arrival, second, dawn], [friday, road]),
            new MigrationCatalog([destArrival, destSecond], [destFriday]),
            new HashSet<MigrationCategory> { MigrationCategory.Playlists });
        var board = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Playlists });
        board.Expect("Source", "Playlists");
        board.Expect("Destination", "Playlists");
        board.IngestSource("Movies", [arrival, second, dawn], confirmed: true);
        board.IngestDestination("Movies", [destArrival, destSecond]);
        board.AddPlaylist("Destination", destFriday, confirmed: true);
        board.AddPlaylist("Source", friday, confirmed: true);
        board.AddPlaylist("Source", road, confirmed: true);
        board.FinishLibrary("Source", "Playlists", 2);
        board.FinishLibrary("Destination", "Playlists", 1);
        board.Stamp(built.Comparisons, built.Rows);

        var section = board.Libraries("all", null, null, new HashSet<string> { "Playlists" }, null).Single(item => item.Name == "Playlists");
        Assert.Contains(section.Keys, key => key.StartsWith("playlist|create|", StringComparison.Ordinal));
        Assert.Contains(section.Keys, key => key.StartsWith("playlist|add|", StringComparison.Ordinal));
        var fridayRow = section.Rows.Single(row => row.SourceText == "Friday");
        Assert.Equal("playlist mismatch", fridayRow.ResultText);
        Assert.Equal("Friday", fridayRow.DestinationText);
        Assert.Contains(fridayRow.Items, item => item.Title == "Arrival" && item.Status == "matched on destination ✓" && !item.HasAction);
        var added = fridayRow.Items.Single(item => item.Title == "Second");
        Assert.Equal("matched on destination ✓", added.Status);
        Assert.StartsWith("playlist|add|", added.ActionKey, StringComparison.Ordinal);
        Assert.EndsWith("|25", added.ActionKey, StringComparison.Ordinal);
        var missing = fridayRow.Items.Single(item => item.Title == "Dawn");
        Assert.Equal("Max Richter — Sleep", missing.Detail);
        Assert.Equal("not found ✗", missing.Status);
        Assert.False(missing.HasAction);
        var roadRow = section.Rows.Single(row => row.SourceText == "Road");
        Assert.Equal("not on destination", roadRow.DestinationText);
        Assert.Equal("playlist mismatch", roadRow.ResultText);
        Assert.Contains(roadRow.Keys, key => key.StartsWith("playlist|create|", StringComparison.Ordinal));
        Assert.All(roadRow.Items, item => Assert.False(item.HasAction));
        Assert.Equal(2, roadRow.Items.Count(item => item.Status == "matched on destination ✓"));
    }

    [Fact]
    public async Task Migration_startup_preserves_saved_tailscale_urls_tokens_and_slots()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.Migrate();
        var rows = await db.PlexServers.AsNoTracking().OrderBy(server => server.Slot).ToListAsync();
        Assert.Equal(SourceUrl, rows[0].BaseUrl);
        Assert.Equal(DestinationUrl, rows[1].BaseUrl);
        Assert.Equal(1, rows[0].Slot);
        Assert.Equal(2, rows[1].Slot);
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.Equal(SourceToken, protector.Unprotect(rows[0].ProtectedToken!));
        Assert.Equal(DestinationToken, protector.Unprotect(rows[1].ProtectedToken!));
        Assert.Equal(source.Id, rows[0].Id);
        Assert.Equal(destination.Id, rows[1].Id);
    }

    [Fact]
    public async Task Preview_reads_leaf_items_and_reports_progress()
    {
        var handler = new ScriptHandler
        {
            Responder = request =>
            {
                if (request.Uri.AbsolutePath == "/library/sections")
                {
                    return Json("""
                        {"MediaContainer":{"Directory":[
                          {"key":"1","type":"movie","title":"Films"},
                          {"key":"2","type":"show","title":"Shows"},
                          {"key":"3","type":"artist","title":"Music"}
                        ]}}
                        """);
                }

                if (request.Uri.Query.Contains("type=10", StringComparison.Ordinal))
                    return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"3","title":"Song","type":"track","index":1,"parentGuid":"plex://album/abcd1234"}]}}""");
                if (request.Uri.Query.Contains("type=4", StringComparison.Ordinal))
                    return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"2","title":"Pilot","type":"episode","parentIndex":1,"index":1,"grandparentGuid":"plex://show/abcd1234","viewCount":1}]}}""");
                if (HasType(request.Uri.Query, "1"))
                    return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"1","title":"Film","type":"movie","Guid":[{"id":"imdb://tt1000001"}]}]}}""");
                return Json("""{"MediaContainer":{"Metadata":[]}}""");
            }
        };
        var progress = new ListProgress();
        var reader = new PlexMigrationReader(
            new HandlerTransport(handler),
            new AllowAllAddressGuard(),
            new MigrationPreviewOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlexMigrationReader>.Instance);
        var catalog = await reader.ReadCatalogAsync(new Uri("http://100.64.8.2:32400/"), DestinationToken, false, "Desktop", "Destination", progress, CancellationToken.None);
        Assert.Equal(3, catalog.Items.Count);
        Assert.Contains(progress.Items, item => item.Library == "Films" && item.ItemsRead >= 1);
        Assert.Contains(handler.Requests, request => HasType(request.Uri.Query, "1"));
        Assert.Contains(handler.Requests, request => HasType(request.Uri.Query, "4"));
        Assert.Contains(handler.Requests, request => HasType(request.Uri.Query, "10"));
        Assert.DoesNotContain(handler.Requests, request => HasType(request.Uri.Query, "2"));
        Assert.DoesNotContain(handler.Requests, request => HasType(request.Uri.Query, "3"));
        Assert.DoesNotContain(handler.Requests, request => HasType(request.Uri.Query, "8"));
        Assert.DoesNotContain(handler.Requests, request => HasType(request.Uri.Query, "9"));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Cancel_stops_the_preview_job_without_a_write()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.AsyncResponder = async (request, token) =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal))
                return Library()(request);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json("""{"MediaContainer":{"Metadata":[]}}""");
        };

        var job = await fixture.Service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Service.CancelPreviewAsync(job.Id);
        var done = await WaitForJob(fixture, job.Id, TimeSpan.FromSeconds(3));
        Assert.Equal(MigrationJobState.Canceled, done.State);
        Assert.Contains("Nothing was written", done.Status, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timed_out_page_fails_the_job_without_a_write()
    {
        await using var fixture = new MigrationFixture();
        fixture.Options.RequestTimeout = TimeSpan.FromMilliseconds(200);
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.AsyncResponder = async (request, _) =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal))
                return Library()(request);
            await Task.Delay(TimeSpan.FromSeconds(5));
            return Json("""{"MediaContainer":{"Metadata":[]}}""");
        };

        var job = await fixture.Service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
        var done = await WaitForJob(fixture, job.Id, TimeSpan.FromSeconds(3));
        Assert.Equal(MigrationJobState.Failed, done.State);
        Assert.Contains("Nothing was written", done.Error, StringComparison.Ordinal);
        Assert.Contains("timeout", done.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_job_keeps_running_after_the_caller_scope_is_disposed()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.AsyncResponder = async (request, _) =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal))
                return Library()(request);
            entered.TrySetResult();
            await release.Task;
            return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"10","title":"Arrival","type":"movie","viewCount":1,"Guid":[{"id":"imdb://tt2543164"}]}]}}""");
        };

        string jobId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MigrationService>();
            var job = await service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
            jobId = job.Id;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var during = fixture.Jobs.Latest("admin");
        Assert.NotNull(during);
        Assert.Equal(MigrationJobState.Running, during.State);
        release.TrySetResult();
        var done = await WaitForJob(fixture, jobId, TimeSpan.FromSeconds(3));
        Assert.Equal(MigrationJobState.Completed, done.State);
        Assert.NotNull(done.Summary);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public void Fifteen_thousand_items_per_server_stay_under_the_plan_budget()
    {
        var source = new List<MigrationItem>(15_000);
        var destination = new List<MigrationItem>(15_000);
        for (var i = 0; i < 10_000; i++)
        {
            source.Add(new MigrationItem(i.ToString(), "Kept " + i, "movie", 2020, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt" + (1_000_000 + i)]), null, null));
            destination.Add(new MigrationItem((i + 100_000).ToString(), "Kept " + i, "movie", 2020, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt" + (1_000_000 + i)]), null, null));
        }

        for (var i = 0; i < 5_000; i++)
        {
            source.Add(new MigrationItem((20_000 + i).ToString(), "Only source " + i, "movie", 2020, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt" + (2_000_000 + i)]), null, null));
            destination.Add(new MigrationItem((30_000 + i).ToString(), "Only destination " + i, "movie", 2020, null, null, null, 0, 0, null, PlexGuids.Parse(["imdb://tt" + (3_000_000 + i)]), null, null));
        }

        var built = MigrationPlanBuilder.Build(
            new MigrationCatalog(source, []),
            new MigrationCatalog(destination, []),
            new HashSet<MigrationCategory> { MigrationCategory.Watched, MigrationCategory.Progress, MigrationCategory.Ratings });
        Assert.Equal(30_000, built.Summary.AlreadySame);
        Assert.Equal(5_000, built.Summary.UnmatchedSource);
        Assert.Equal(5_000, built.Summary.UnmatchedDestination);
        Assert.DoesNotContain(built.Rows, row => row.Detail == "Already the same.");
        Assert.Equal(10_000, built.Summary.UpToDateItems);
        var plan = new MigrationPlan("p", "fp", 1, 2, "NAS", "Desktop", built.Rows, built.Summary, built.Comparisons);
        Assert.True(plan.EstimatedBytes() < 64_000_000, plan.EstimatedBytes().ToString());
        var upToDate = plan.ComparisonPage("up-to-date", "movie", null, 0, 50);
        Assert.Equal(50, upToDate.Rows.Count);
        Assert.Equal(10_000, upToDate.Total);
        var missing = plan.ComparisonPage("missing", null, "Only source 1", 0, 50);
        Assert.Contains(missing.Rows, row => row.SourceTitle == "Only source 1");
        Assert.All(missing.Rows, row => Assert.Equal(ComparisonResult.Missing, row.Result));
        var onlyDest = plan.ComparisonPage("only-on-destination", null, null, 100, 50);
        Assert.Equal(50, onlyDest.Rows.Count);
        Assert.Equal(5_000, onlyDest.Total);
    }

    [Fact]
    public void Comparison_classifies_out_of_date_conflict_missing_and_up_to_date()
    {
        var shared = PlexGuids.Parse(["imdb://tt1000001"]);
        var source = new MigrationItem("10", "Arrival", "movie", 2016, null, null, null, 1, 45_000, 8, shared, null, null, null, null, null, 100_000);
        var destination = new MigrationItem("20", "Arrival", "movie", 2016, null, null, null, 0, 0, null, shared, null, null, null, null, null, 100_000);
        var conflictSource = new MigrationItem("11", "Shared", "movie", 2014, null, null, null, 0, 0, null, PlexGuids.Parse(["imdb://tt1000002"]), null, null);
        var conflictDest = new MigrationItem("21", "Shared", "movie", 2014, null, null, null, 1, 0, 2, PlexGuids.Parse(["imdb://tt1000002"]), null, null);
        var onlySource = new MigrationItem("12", "Same Title", "movie", 2001, null, null, null, 1, 0, null, [], null, null);
        var onlyDest = new MigrationItem("22", "Elsewhere", "movie", 1999, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt1000003"]), null, null);
        var sameSource = new MigrationItem("13", "Quiet", "episode", null, 1, 2, "Station", 1, 0, null, PlexGuids.Parse(["plex://episode/abcd1234"]), null, "1");
        var sameDest = new MigrationItem("23", "Quiet", "episode", null, 1, 2, "Station", 1, 0, null, PlexGuids.Parse(["plex://episode/abcd1234"]), null, "9");
        var built = MigrationPlanBuilder.Build(
            new MigrationCatalog([source, conflictSource, onlySource, sameSource], []),
            new MigrationCatalog([destination, conflictDest, onlyDest, sameDest], []),
            new HashSet<MigrationCategory> { MigrationCategory.Watched, MigrationCategory.Progress, MigrationCategory.Ratings });

        var outOfDate = Assert.Single(built.Comparisons, row => row.SourceTitle == "Arrival" && row.ParentKey == "");
        Assert.Equal(ComparisonResult.OutOfDate, outOfDate.Result);
        Assert.Equal("Arrival", outOfDate.DestinationTitle);
        Assert.Contains("watched on source, unwatched here", outOfDate.Difference, StringComparison.Ordinal);
        Assert.Contains("progress 45% vs 0%", outOfDate.Difference, StringComparison.Ordinal);
        Assert.Contains("rating 8 vs none", outOfDate.Difference, StringComparison.Ordinal);
        Assert.Contains("2016", outOfDate.SourceDetail, StringComparison.Ordinal);

        var conflict = Assert.Single(built.Comparisons, row => row.SourceTitle == "Shared");
        Assert.Equal(ComparisonResult.Conflict, conflict.Result);
        Assert.Contains("watched here, unwatched on source", conflict.Difference, StringComparison.Ordinal);

        var missing = Assert.Single(built.Comparisons, row => row.Result == ComparisonResult.Missing);
        Assert.Equal("Same Title", missing.SourceTitle);
        Assert.Equal("", missing.DestinationTitle);

        var only = Assert.Single(built.Comparisons, row => row.Result == ComparisonResult.OnlyOnDestination);
        Assert.Equal("", only.SourceTitle);
        Assert.Equal("Elsewhere", only.DestinationTitle);

        var current = Assert.Single(built.Comparisons, row => row.SourceTitle == "Quiet");
        Assert.Equal(ComparisonResult.UpToDate, current.Result);
        Assert.Contains("S01E02", current.SourceDetail, StringComparison.Ordinal);

        var page = new MigrationPlan("p", "fp", 1, 2, "A", "B", built.Rows, built.Summary, built.Comparisons)
            .ComparisonPage("out-of-date", "movie", "arr", 0, 50);
        var found = Assert.Single(page.Rows);
        Assert.Equal("Arrival", found.SourceTitle);
    }

    [Fact]
    public async Task A_reloaded_page_sees_the_completed_preview_and_cancel_leaves_it()
    {
        await using var fixture = new MigrationFixture();
        var (source, destination) = await fixture.SavePairAsync();
        fixture.Handler.Responder = Library();
        var started = await fixture.Service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
        var done = await WaitForJob(fixture, started.Id, TimeSpan.FromSeconds(5));
        Assert.Equal(MigrationJobState.Completed, done.State);
        Assert.NotNull(done.Summary);

        await using var scope = fixture.Provider.CreateAsyncScope();
        var reloaded = scope.ServiceProvider.GetRequiredService<MigrationService>();
        var latest = await reloaded.LatestPreviewAsync();
        Assert.NotNull(latest);
        Assert.Equal(started.Id, latest.Id);
        Assert.NotNull(latest.Summary);
        var page = await reloaded.PreviewPageAsync(latest.Id, "changes", 0);
        Assert.NotNull(page);
        Assert.True(page.Rows.Count <= MigrationPlan.PageSize);
        await reloaded.CancelPreviewAsync(latest.Id);
        var after = await reloaded.LatestPreviewAsync();
        Assert.Equal(MigrationJobState.Completed, after!.State);
        Assert.NotNull(after.Summary);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Library_progress_counts_each_library_separately()
    {
        var handler = new ScriptHandler
        {
            Responder = request =>
            {
                if (request.Uri.AbsolutePath == "/library/sections")
                {
                    return Json("""
                        {"MediaContainer":{"Directory":[
                          {"key":"1","type":"movie","title":"Films"},
                          {"key":"2","type":"movie","title":"Other Videos"},
                          {"key":"3","type":"show","title":"Shows"}
                        ]}}
                        """);
                }

                if (request.Uri.AbsolutePath.EndsWith("/1/all", StringComparison.Ordinal))
                    return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"1","title":"A","type":"movie"},{"ratingKey":"2","title":"B","type":"movie"}]}}""");
                if (request.Uri.AbsolutePath.EndsWith("/2/all", StringComparison.Ordinal))
                    return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"3","title":"C","type":"movie"}]}}""");
                if (request.Uri.AbsolutePath.EndsWith("/3/all", StringComparison.Ordinal))
                    return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"4","title":"Pilot","type":"episode","parentIndex":1,"index":1}]}}""");
                return Json("""{"MediaContainer":{"Metadata":[]}}""");
            }
        };
        var progress = new ListProgress();
        var reader = new PlexMigrationReader(
            new HandlerTransport(handler),
            new AllowAllAddressGuard(),
            new MigrationPreviewOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PlexMigrationReader>.Instance);
        await reader.ReadCatalogAsync(new Uri("http://100.64.8.2:32400/"), DestinationToken, false, "Desktop", "Destination", progress, CancellationToken.None);
        Assert.Equal(2, progress.Items.Last(item => item.Library == "Films" && item.State == "Done").ItemsRead);
        Assert.Equal(1, progress.Items.Last(item => item.Library == "Other Videos" && item.State == "Done").ItemsRead);
        Assert.Equal(1, progress.Items.Last(item => item.Library == "Shows" && item.State == "Done").ItemsRead);
        Assert.DoesNotContain(progress.Items, item => item.Library == "Other Videos" && item.ItemsRead > 1);
    }

    [Fact]
    public void Scan_row_moves_from_scanning_to_a_final_result()
    {
        var board = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched });
        board.Expect("Source", "Movies");
        board.Expect("Destination", "Movies");
        var source = new MigrationItem("1", "28 Days Later", "movie", null, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt0463854"]), null, null);
        board.IngestSource("Movies", [source], confirmed: false);
        var scanning = board.Page("all", null, null, 0, 50).Rows.Single(row => !row.Header);
        Assert.Equal("scanning 28 Days Later", scanning.SourceText);
        Assert.Equal("scanning…", scanning.DestinationText);
        Assert.Equal("scanning", scanning.ResultText);
        var header = board.Page("all", null, null, 0, 50).Rows.Single(row => row.Header);
        Assert.Equal("scanning… Movies", header.SourceText);
        Assert.Equal("scanning… Movies", header.DestinationText);
        Assert.Equal("results", header.ResultText);

        board.IngestSource("Movies", [source], confirmed: true);
        var confirmed = board.Page("all", null, null, 0, 50).Rows.Single(row => !row.Header);
        Assert.Equal("28 Days Later", confirmed.SourceText);
        Assert.Equal("scanning", confirmed.ResultText);

        var destination = new MigrationItem("9", "28 Days Later", "movie", null, null, null, null, 0, 0, null, PlexGuids.Parse(["imdb://tt0463854"]), null, null);
        board.IngestDestination("Movies", [destination]);
        board.FinishLibrary("Destination", "Movies", 1);
        board.FinishLibrary("Source", "Movies", 1);
        var final = board.Page("all", null, null, 0, 50).Rows.Single(row => !row.Header);
        Assert.Equal("28 Days Later", final.SourceText);
        Assert.Equal("28 Days Later", final.DestinationText);
        Assert.Equal("watched mismatch", final.ResultText);
        header = board.Page("all", null, null, 0, 50).Rows.Single(row => row.Header);
        Assert.Equal("Movies ✓ 1", header.SourceText);
        Assert.Equal("Movies ✓ 1", header.DestinationText);
        Assert.Equal("1 out of date", header.ResultText);
    }

    [Fact]
    public void Section_headers_summarize_each_library_on_its_own()
    {
        var board = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched });
        board.Expect("Source", "Movies");
        board.Expect("Destination", "Movies");
        board.Expect("Source", "TV episodes");
        board.Expect("Destination", "TV episodes");
        var matched = new MigrationItem("1", "Arrival", "movie", null, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt2543164"]), null, null);
        var missing = new MigrationItem("2", "Same Title", "movie", null, null, null, null, 1, 0, null, [], null, null);
        board.IngestSource("Movies", [matched, missing], confirmed: true);
        board.IngestDestination("Movies", [matched with { RatingKey = "20" }]);
        board.FinishLibrary("Source", "Movies", 2);
        board.FinishLibrary("Destination", "Movies", 1);
        var episode = new MigrationItem("4", "Pilot", "episode", null, 1, 1, "Station", 1, 0, null, [], null, null);
        board.IngestSource("TV episodes", [episode], confirmed: false);
        var page = board.Page("all", null, null, 0, 50);
        var movies = page.Rows.Single(row => row.Header && row.Section == "Movies");
        var episodes = page.Rows.Single(row => row.Header && row.Section == "TV episodes");
        Assert.Equal("Movies ✓ 2", movies.SourceText);
        Assert.Equal("Movies ✓ 1", movies.DestinationText);
        Assert.Equal("1 missing, 1 matching", movies.ResultText);
        var opened = board.Libraries("all", null, null, new HashSet<string> { "Movies" }, null).Single(section => section.Name == "Movies");
        Assert.Equal("Movies", opened.SourceLabel);
        Assert.Equal("Movies", opened.DestinationLabel);
        Assert.Equal("matching", opened.Summary);
        Assert.Equal("1 only on source", opened.SummaryNote);
        Assert.Equal("matching", opened.Rows.Single(row => row.SourceText.StartsWith("Arrival", StringComparison.Ordinal)).ResultText);
        Assert.Equal(LiveScanBoard.NotOnDestination, opened.Rows.Single(row => row.SourceText.StartsWith("Same Title", StringComparison.Ordinal)).ResultText);
        Assert.DoesNotContain("3", movies.ResultText, StringComparison.Ordinal);
        Assert.Equal("scanning… TV episodes", episodes.SourceText);
        Assert.Equal("results", episodes.ResultText);
        Assert.Equal("scanning Pilot", page.Rows.Single(row => row.Section == "TV episodes" && !row.Header).SourceText);
        Assert.Contains("episode S01E01 Station", page.Rows.Single(row => row.Section == "TV episodes" && !row.Header).SourceDetail, StringComparison.Ordinal);
        var hidden = board.Page("hide-only-on-destination", null, null, 0, 50);
        Assert.DoesNotContain(hidden.Rows, row => row.Result == ComparisonResult.OnlyOnDestination);
        var mismatches = board.Page("mismatches", null, null, 0, 50);
        Assert.DoesNotContain(mismatches.Rows, row => !row.Header && row.Result == ComparisonResult.Missing);
        Assert.DoesNotContain(mismatches.Rows, row => !row.Header && row.Result == ComparisonResult.UpToDate);
    }

    [Fact]
    public void Only_on_destination_names_the_empty_side_and_the_file()
    {
        var board = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched });
        board.Expect("Destination", "Other Videos");
        board.FinishLibrary("Source", "Other Videos", 0);
        var clip = new MigrationItem(
            "90",
            "2013-08-03 15.10.59",
            "movie",
            2013,
            null,
            null,
            null,
            1,
            0,
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            0,
            "Other Videos",
            "2013-08-03 15.10.59.mp4");
        board.IngestDestination("Other Videos", [clip]);
        board.FinishLibrary("Destination", "Other Videos", 1);
        var row = board.Page("only-on-destination", null, null, 0, 50).Rows.Single(item => !item.Header);
        Assert.Equal("not on source", row.SourceText);
        Assert.True(row.SourceAbsent);
        Assert.Equal("2013-08-03 15.10.59 (2013)", row.DestinationText);
        Assert.False(row.DestinationAbsent);
        Assert.Contains("Other Videos", row.DestinationDetail, StringComparison.Ordinal);
        Assert.Contains("other video", row.DestinationDetail, StringComparison.Ordinal);
        Assert.Contains("2013-08-03 15.10.59.mp4", row.DestinationDetail, StringComparison.Ordinal);
        Assert.True(row.NoAction);
        Assert.Equal("Other Videos", board.Page("only-on-destination", null, null, 0, 50).Rows[0].Section);
        Assert.True(board.Page("only-on-destination", null, null, 0, 50).Rows[0].Header);

        var source = new MigrationItem("2", "Same Title", "movie", null, null, null, null, 1, 0, null, [], null, null, null, null, null, 0, "Films", "same.mkv");
        board.Expect("Source", "Movies");
        board.Expect("Destination", "Movies");
        board.IngestSource("Movies", [source], confirmed: true);
        board.FinishLibrary("Source", "Movies", 1);
        board.FinishLibrary("Destination", "Movies", 0);
        var missing = board.Page("missing", null, null, 0, 50).Rows.Single(item => !item.Header && item.Section == "Movies");
        Assert.Equal("Same Title", missing.SourceText);
        Assert.Contains("Films", missing.SourceDetail, StringComparison.Ordinal);
        Assert.Contains("movie", missing.SourceDetail, StringComparison.Ordinal);
        Assert.Contains("same.mkv", missing.SourceDetail, StringComparison.Ordinal);
        Assert.True(missing.NoAction);
        Assert.Equal("not on destination", missing.DestinationText);
        Assert.True(missing.DestinationAbsent);
    }

    [Fact]
    public void Library_status_and_item_results_use_short_phrases()
    {
        var categories = new HashSet<MigrationCategory> { MigrationCategory.Watched, MigrationCategory.Progress, MigrationCategory.Ratings };
        var progress = new LiveScanBoard(categories);
        progress.Expect("Source", "Movies");
        progress.Expect("Destination", "Movies");
        var arrival = new MigrationItem("1", "Arrival", "movie", 2016, null, null, null, 1, 45_000, null, PlexGuids.Parse(["imdb://tt2543164"]), null, null);
        progress.IngestSource("Movies", [arrival], confirmed: true);
        progress.IngestDestination("Movies", [arrival with { RatingKey = "11", ViewOffset = 0 }]);
        progress.FinishLibrary("Source", "Movies", 1);
        progress.FinishLibrary("Destination", "Movies", 1);
        var progressSection = progress.Libraries("all", null, null, new HashSet<string> { "Movies" }, null).Single();
        Assert.Equal("Library exists on both · mismatches", progressSection.Summary);
        Assert.Equal("watch progress mismatch", progressSection.Rows.Single().ResultText);

        var both = new LiveScanBoard(categories);
        both.Expect("Source", "Movies");
        both.Expect("Destination", "Movies");
        var shared = new MigrationItem("2", "Shared", "movie", 2020, null, null, null, 1, 45_000, 8, PlexGuids.Parse(["imdb://tt1234567"]), null, null);
        both.IngestSource("Movies", [shared], confirmed: true);
        both.IngestDestination("Movies", [shared with { RatingKey = "21", ViewCount = 0, ViewOffset = 0, UserRating = null }]);
        both.FinishLibrary("Source", "Movies", 1);
        both.FinishLibrary("Destination", "Movies", 1);
        Assert.Equal("watched + progress + rating mismatch", both.Libraries("all", null, null, new HashSet<string> { "Movies" }, null).Single().Rows.Single().ResultText);

        var same = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched });
        same.Expect("Source", "Movies");
        same.Expect("Destination", "Movies");
        var quiet = new MigrationItem("3", "Quiet", "movie", 2020, null, null, null, 1, 0, null, PlexGuids.Parse(["imdb://tt0000001"]), null, null);
        same.IngestSource("Movies", [quiet], confirmed: true);
        same.IngestDestination("Movies", [quiet with { RatingKey = "31" }]);
        same.FinishLibrary("Source", "Movies", 1);
        same.FinishLibrary("Destination", "Movies", 1);
        var matched = same.Libraries("all", null, null, null, null).Single();
        Assert.Equal("matching", matched.Summary);
        Assert.Empty(matched.Rows);

        var only = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched });
        only.Expect("Destination", "Other Videos");
        only.FinishLibrary("Source", "Other Videos", 0);
        only.IngestDestination("Other Videos", [new MigrationItem("90", "2013-08-03 15.10.59", "movie", null, null, null, null, 0, 0, null, [], null, null)]);
        only.FinishLibrary("Destination", "Other Videos", 1);
        var destOnly = only.Libraries("all", null, null, null, null).Single();
        Assert.Equal("not on source", destOnly.SourceLabel);
        Assert.Equal("Other Videos", destOnly.DestinationLabel);
        Assert.Equal("only on destination", destOnly.Summary);
        Assert.Empty(destOnly.Rows);
    }

    [Fact]
    public void Each_library_pages_on_its_own_and_the_one_being_scanned_is_open()
    {
        var board = new LiveScanBoard(new HashSet<MigrationCategory> { MigrationCategory.Watched });
        board.Expect("Source", "Movies");
        board.Expect("Source", "TV episodes");
        var movies = Enumerable.Range(1, 3).Select(index => new MigrationItem(index.ToString(), "Film " + index, "movie", null, null, null, null, 1, 0, null, [], null, null)).ToList();
        var episode = new MigrationItem("9", "Pilot", "episode", null, 1, 1, "Station", 1, 0, null, [], null, null);
        board.IngestSource("Movies", movies, confirmed: true);
        board.IngestSource("TV episodes", [episode], confirmed: true);
        board.ApplyProgress(new MigrationReadProgress("Source", "Desktop", "TV episodes", "Shows", "Reading", 1, 1));
        var closed = board.Libraries("all", null, null, null, null);
        Assert.Empty(closed.Single(section => section.Name == "Movies").Rows);
        Assert.Empty(closed.Single(section => section.Name == "TV episodes").Rows);
        Assert.Equal("scanning…", closed.Single(section => section.Name == "TV episodes").Summary);
        Assert.True(closed.Single(section => section.Name == "TV episodes").Scanning);
        var moviesPage = board.Libraries("all", null, null, new HashSet<string> { "Movies" }, null).Single(section => section.Name == "Movies");
        Assert.Equal(3, moviesPage.Total);
        Assert.Equal(3, moviesPage.Rows.Count);
        Assert.DoesNotContain(moviesPage.Rows, row => row.Section == "TV episodes");
        var tail = board.Libraries("all", null, new Dictionary<string, int> { ["Movies"] = 2 }, new HashSet<string> { "Movies" }, null).Single(section => section.Name == "Movies");
        Assert.Equal("Film 3", tail.Rows[0].SourceText);
    }

    [Fact]
    public async Task Source_pages_are_classified_before_the_preview_finishes()
    {
        await using var fixture = new MigrationFixture();
        fixture.Options.PageSize = 2;
        var (source, destination) = await fixture.SavePairAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.AsyncResponder = async (request, _) =>
        {
            if (!request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal))
                return SectionsAndIdentity(request);
            var start = StartOf(request.Uri.Query);
            if (request.Uri.IdnHost == "100.64.8.2" && start == 0)
            {
                entered.TrySetResult();
                await release.Task;
            }

            if (start >= 4)
                return Json("""{"MediaContainer":{"Metadata":[]}}""");

            var prefix = request.Uri.IdnHost == "192.0.2.20" ? "s" : "d";
            var first = start + 1;
            var second = start + 2;
            return Json("{\"MediaContainer\":{\"Metadata\":["
                + MoviePage(prefix + first, "Film " + first, "tt" + (100000 + first))
                + ","
                + MoviePage(prefix + second, "Film " + second, "tt" + (100000 + second))
                + "]}}");
        };

        string jobId;
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MigrationService>();
            var job = await service.StartPreviewAsync(source.Id, destination.Id, [MigrationCategory.Watched]);
            jobId = job.Id;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        var during = fixture.Jobs.Latest("admin");
        Assert.NotNull(during);
        Assert.Equal(MigrationJobState.Running, during.State);
        ScanPage? live = null;
        for (var attempt = 0; attempt < 20 && live?.Rows.Any(row => !row.Header && row.ResultText == "scanning") != true; attempt++)
        {
            live = fixture.Jobs.Page("admin", jobId, "all", 0, 50);
            await Task.Delay(25);
        }

        Assert.NotNull(live);
        Assert.Contains(live.Rows, row => row.Header && row.Section == "Movies" && row.DestinationText.StartsWith("scanning…", StringComparison.Ordinal) && row.ResultText == "results");
        Assert.Contains(live.Rows, row => !row.Header && row.SourceText.StartsWith("Film ", StringComparison.Ordinal) && row.DestinationText == "scanning…" && row.ResultText == "scanning");
        Assert.True(live.Rows.Count <= MigrationPlan.PageSize);
        release.TrySetResult();
        var done = await WaitForJob(fixture, jobId, TimeSpan.FromSeconds(4));
        Assert.Equal(MigrationJobState.Completed, done.State);
        var finished = fixture.Jobs.Page("admin", jobId, "all", 0, 50);
        Assert.NotNull(finished);
        Assert.Contains(finished.Rows, row => row.Header && row.Section == "Movies" && row.SourceText == "Movies ✓ 4" && row.ResultText == "4 matching");
        Assert.Contains(finished.Rows, row => !row.Header && row.ResultText == "matching" && row.DestinationText.StartsWith("Film ", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.StartsWith("/:/", StringComparison.Ordinal));
    }

    private static int StartOf(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            const string marker = "X-Plex-Container-Start=";
            if (part.StartsWith(marker, StringComparison.Ordinal) && int.TryParse(part[marker.Length..], out var start))
                return start;
        }

        return 0;
    }

    private static HttpResponseMessage SectionsAndIdentity(CapturedRequest request)
    {
        if (request.Uri.AbsolutePath is "/identity" or "/")
        {
            var machine = request.Uri.IdnHost == "192.0.2.20"
                ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            return Json("{\"MediaContainer\":{\"machineIdentifier\":\"" + machine + "\",\"version\":\"1.0\"}}");
        }

        if (request.Uri.AbsolutePath == "/library/sections")
            return Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Films"}]}}""");
        return Json("""{"MediaContainer":{}}""");
    }

    private static string MoviePage(string key, string title, string imdb)
        => "{\"ratingKey\":\"" + key + "\",\"title\":\"" + title + "\",\"type\":\"movie\",\"viewCount\":1,\"Guid\":[{\"id\":\"imdb://" + imdb + "\"}]}";

    private static bool HasType(string query, string type)
        => query.Contains("type=" + type + "&", StringComparison.Ordinal) || query.EndsWith("type=" + type, StringComparison.Ordinal);

    private static async Task<MigrationJobSnapshot> WaitForJob(MigrationFixture fixture, string jobId, TimeSpan limit, string? plexUserKey = null)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            var job = fixture.Jobs.Latest("admin", plexUserKey);
            if (job is not null && job.Id == jobId && job.State != MigrationJobState.Running)
                return job;
            await Task.Delay(50);
        }

        throw new TimeoutException("The preview job did not finish.");
    }

    private sealed class ListProgress : IProgress<MigrationReadProgress>
    {
        public List<MigrationReadProgress> Items { get; } = [];
        public void Report(MigrationReadProgress value) => Items.Add(value);
    }

    private static Func<CapturedRequest, HttpResponseMessage> Library()
        => request =>
        {
            if (request.Uri.AbsolutePath is "/identity" or "/")
            {
                var machine = request.Uri.IdnHost == "192.0.2.20"
                    ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                return Json("{\"MediaContainer\":{\"machineIdentifier\":\"" + machine + "\",\"version\":\"1.0\"}}");
            }

            if (request.Uri.AbsolutePath == "/library/sections")
                return Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Films"}]}}""");
            if (request.Uri.AbsolutePath.EndsWith("/all", StringComparison.Ordinal))
                return Json(Items(request.Uri.IdnHost, arrivalWatched: true));
            return Json("""{"MediaContainer":{}}""");
        };

    private static string MetadataItem(string host, string key, bool arrivalWatched)
    {
        var all = Items(host, arrivalWatched);
        var marker = "\"ratingKey\":\"" + key + "\"";
        var at = all.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return """{"MediaContainer":{"Metadata":[]}}""";
        var start = all.LastIndexOf('{', at);
        var end = at;
        var depth = 0;
        for (var i = start; i < all.Length; i++)
        {
            if (all[i] == '{')
                depth++;
            else if (all[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    break;
                }
            }
        }

        return "{\"MediaContainer\":{\"Metadata\":[" + all[start..(end + 1)] + "]}}";
    }

    private static string Items(string host, bool arrivalWatched)
    {
        if (host == "192.0.2.20")
        {
            var watched = arrivalWatched ? 1 : 0;
            return "{\"MediaContainer\":{\"Metadata\":[" + string.Join(',',
                MovieJson("10", "Arrival", "tt2543164", watched, 9, 0),
                MovieJson("14", "Second", "tt2222222", 1, null, 0),
                MovieJson("15", "Third", "tt3333333", 1, null, 0),
                MovieJson("11", "Shared", "tt1234567", 0, 8, 5_000),
                MovieJson("12", "Same Title", null, 1, null, 0)) + "]}}";
        }

        return "{\"MediaContainer\":{\"Metadata\":[" + string.Join(',',
            MovieJson("20", "Arrival copy", "tt2543164", 0, null, 0),
            MovieJson("25", "Second copy", "tt2222222", 0, null, 0),
            MovieJson("26", "Third copy", "tt3333333", 0, null, 0),
            MovieJson("21", "Shared", "tt1234567", 1, 2, 90_000),
            MovieJson("22", "Same Title", null, 1, null, 0),
            MovieJson("24", "Destination only", "tt8888888", 1, null, 0)) + "]}}";
    }

    private static string MovieJson(string key, string title, string? imdb, int watched, int? rating, long offset)
    {
        var guid = imdb is null ? "" : ",\"Guid\":[{\"id\":\"imdb://" + imdb + "\"}]";
        var stars = rating is null ? "" : ",\"userRating\":" + rating.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "{\"ratingKey\":\"" + key + "\",\"title\":\"" + title + "\",\"type\":\"movie\",\"viewCount\":" + watched + ",\"viewOffset\":" + offset + stars + guid
            + ",\"Media\":[{\"Part\":[{\"file\":\"/media/" + title + ".mkv\"}]}]}";
    }

    private static HttpResponseMessage Json(string body) => HttpJson.Json(body);
}

public sealed class MigrationFixture : IAsyncDisposable
{
    public ScriptHandler Handler { get; } = new();
    public ScriptHandler AuthHandler { get; } = new();
    public PlexProbeLimits Limits { get; } = new();
    public MigrationPreviewOptions Options { get; } = new();
    public MigrationPreviewJobs Jobs => Provider.GetRequiredService<MigrationPreviewJobs>();
    public ServiceProvider Provider { get; }
    public MigrationService Service { get; }
    public SpyLoggerProvider Logs { get; } = new();
    private readonly Gate _gate = new();
    private readonly CircuitScope _circuit = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexbridge-migrate", Guid.NewGuid().ToString("n"));
    public bool Admin
    {
        init => _gate.Allow = value;
    }

    public MigrationFixture(bool circuitOnly = false)
    {
        Directory.CreateDirectory(_root);
        var keys = Path.Combine(_root, "keys");
        Directory.CreateDirectory(keys);
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("PlexBridgeMigrationTests");
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_root, "plexbridge.db")}"));
        services.AddSingleton<IPlexTransport>(new HandlerTransport(Handler));
        services.AddSingleton<IPlexWriteTransport>(sp => new ForwardingPlexWriteTransport(sp.GetRequiredService<IPlexTransport>()));
        services.AddSingleton<IPlexAddressGuard, AllowAllAddressGuard>();
        services.AddSingleton(Limits);
        services.AddSingleton(Options);
        services.AddSingleton<PlexReadClient>();
        services.AddSingleton<PlexMigrationReader>();
        services.AddSingleton<PlexWriteClient>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPlexAuthTransport>(new AuthTransport(AuthHandler));
        services.AddSingleton<PlexAuthClient>();
        services.AddSingleton<PlexPinCoordinator>();
        services.AddSingleton<MigrationUserTokens>();
        services.AddScoped<MigrationPlexUsers>();
        services.AddScoped<PlexServerService>();
        if (circuitOnly)
        {
            services.AddSingleton(_circuit);
            services.AddScoped<IAdminGate>(sp => new CircuitOnlyGate(sp, sp.GetRequiredService<CircuitScope>()));
        }
        else
            services.AddSingleton<IAdminGate>(_gate);
        services.AddSingleton<MigrationPreviewJobs>();
        services.AddScoped<MigrationService>();
        Provider = services.BuildServiceProvider();
        using var scope = Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();
        Service = Provider.CreateScope().ServiceProvider.GetRequiredService<MigrationService>();
    }

    public async Task<(PlexServerSummary Source, PlexServerSummary Destination)> SavePairAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        var servers = scope.ServiceProvider.GetRequiredService<PlexServerService>();
        var source = await servers.SaveAsync(null, "NAS", MigrationFlowTestsSource.SourceUrl, MigrationFlowTestsSource.SourceToken, slot: 1);
        var destination = await servers.SaveAsync(null, "Desktop", MigrationFlowTestsSource.DestinationUrl, MigrationFlowTestsSource.DestinationToken, slot: 2);
        _saved = (source, destination);
        return (source, destination);
    }

    private (PlexServerSummary Source, PlexServerSummary Destination) _saved;

    public async Task AssertPreservedAsync(int sourceId, int destinationId)
    {
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var source = await db.PlexServers.AsNoTracking().SingleAsync(server => server.Id == sourceId);
        var destination = await db.PlexServers.AsNoTracking().SingleAsync(server => server.Id == destinationId);
        Assert.Equal(MigrationFlowTestsSource.SourceUrl, source.BaseUrl);
        Assert.Equal(MigrationFlowTestsSource.DestinationUrl, destination.BaseUrl);
        Assert.Equal(1, source.Slot);
        Assert.Equal(2, destination.Slot);
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.Equal(MigrationFlowTestsSource.SourceToken, protector.Unprotect(source.ProtectedToken!));
        Assert.Equal(MigrationFlowTestsSource.DestinationToken, protector.Unprotect(destination.ProtectedToken!));
    }

    public void UseCircuit(IServiceProvider provider) => _circuit.Provider = provider;

    public void AssertNoSecret(string secret)
    {
        Assert.DoesNotContain(Logs.Logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class Gate : IAdminGate
    {
        public bool Allow { get; set; } = true;
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken)
            => Allow ? Task.FromResult("admin") : throw new PlexBridgeException("Only the admin can connect a Plex account.");
    }

    private sealed class CircuitScope
    {
        public IServiceProvider? Provider { get; set; }
    }

    private sealed class CircuitOnlyGate(IServiceProvider scope, CircuitScope circuit) : IAdminGate
    {
        public Task<string> RequireUserIdAsync(CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(scope, circuit.Provider))
                throw new InvalidOperationException("GetAuthenticationStateAsync was called off the circuit.");
            return Task.FromResult("admin");
        }
    }
}

file static class MigrationFlowTestsSource
{
    public const string SourceToken = "source-token-do-not-leak-11";
    public const string DestinationToken = "dest-token-do-not-leak-22";
    public const string SourceUrl = "http://192.0.2.20:32400";
    public const string DestinationUrl = "http://100.64.8.2:32400";
}
