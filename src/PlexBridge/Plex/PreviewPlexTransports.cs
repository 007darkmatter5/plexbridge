#if DEBUG
using System.Net;
using System.Text;

namespace PlexBridge.Plex;

/// <summary>Development-only stand-in so the connection list can be screenshotted. Release builds omit this.</summary>
public sealed class PreviewPlexAuthTransport : IPlexAuthTransport
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Post && path == "/api/v2/pins")
            return Task.FromResult(Json("""{"id":1,"code":"preview","authToken":null,"expiresIn":1800}"""));
        if (path.StartsWith("/api/v2/pins/", StringComparison.Ordinal))
            return Task.FromResult(Json("""{"id":1,"authToken":"preview-account-token"}"""));
        if (path == "/api/v2/user")
            return Task.FromResult(Json("""{"id":9,"uuid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","username":"Preview household","title":"Preview household","admin":true}"""));
        if (path == "/api/v2/home/users")
        {
            return Task.FromResult(Json("""
                {"users":[
                  {"uuid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","title":"Preview household","admin":true,"protected":false},
                  {"uuid":"bbbbbbbb-cccc-dddd-eeee-ffffffffffff","title":"Kid","admin":false,"protected":true}
                ]}
                """));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/switch", StringComparison.Ordinal))
            return Task.FromResult(Json("""{"authToken":"preview-user-token-ok"}"""));
        if (path == "/api/v2/resources")
        {
            return Task.FromResult(Json("""
                [{
                  "name":"Sample source",
                  "productVersion":"1.43.0",
                  "provides":"server",
                  "owned":true,
                  "clientIdentifier":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "accessToken":"preview-source-user-token",
                  "connections":[{"protocol":"http","uri":"http://100.64.1.8:32400","local":true,"relay":false}]
                },{
                  "name":"Sample destination",
                  "productVersion":"1.43.0",
                  "provides":"server",
                  "owned":true,
                  "clientIdentifier":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "accessToken":"preview-dest-user-token",
                  "connections":[{"protocol":"http","uri":"http://100.64.1.9:32400","local":true,"relay":false}]
                },{
                  "name":"Preview shelf",
                  "productVersion":"1.0-preview",
                  "provides":"server",
                  "clientIdentifier":"preview-machine",
                  "owned":true,
                  "accessToken":"preview-server-token",
                  "connections":[
                    {"protocol":"https","uri":"https://172-18-0-1.preview.plex.direct:32400","local":true,"relay":false},
                    {"protocol":"https","uri":"https://172-22-0-1.preview.plex.direct:32400","local":true,"relay":false},
                    {"protocol":"https","uri":"https://172-26-0-1.preview.plex.direct:32400","local":true,"relay":false},
                    {"protocol":"https","uri":"https://172-19-0-1.preview.plex.direct:32400","local":true,"relay":false},
                    {"protocol":"https","uri":"https://172-17-0-1.preview.plex.direct:32400","local":true,"relay":false},
                    {"protocol":"https","uri":"https://10-20-51-142.preview.plex.direct:32400","local":true,"relay":false},
                    {"protocol":"https","uri":"https://10-20-51-141.preview.plex.direct:32400","local":true,"relay":false},
                    {"protocol":"https","uri":"https://192-0-2-10.preview.plex.direct:32401","local":false,"relay":false},
                    {"protocol":"https","uri":"https://198-51-100-5.preview.plex.direct:8443","local":false,"relay":false}
                  ]
                }]
                """));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class PreviewPlexMediaTransport : IPlexTransport
{
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.IdnHost ?? "";
        if (host is "100.64.1.8" or "100.64.1.9")
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("PlexBridge__PreviewPaceMs"), out var pace) && pace > 0
                && (request.RequestUri?.AbsolutePath.Contains("/all", StringComparison.Ordinal) ?? false))
                await Task.Delay(pace, cancellationToken);
            return SampleMigration(host, request);
        }

        if (host.StartsWith("198-51-100-", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        if (host.StartsWith("10-20-51-", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"MediaContainer":{"machineIdentifier":"preview-machine","version":"1.0-preview","friendlyName":"Preview shelf"}}""", Encoding.UTF8, "application/json")
            };
        if (host.StartsWith("172-", StringComparison.Ordinal))
            throw new TaskCanceledException();
        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
    }

    private static HttpResponseMessage SampleMigration(string host, HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var query = request.RequestUri?.Query ?? "";
        var source = host == "100.64.1.8";
        if (request.Method == HttpMethod.Post && path == "/playlists")
            return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"77","title":"Friday","playlistType":"video"}]}}""");
        if (request.Method != HttpMethod.Get || path.StartsWith("/:/", StringComparison.Ordinal))
            return Json("""{"MediaContainer":{}}""");
        if (path is "/identity" or "/")
        {
            var machine = source
                ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            return Json("{\"MediaContainer\":{\"machineIdentifier\":\"" + machine + "\",\"version\":\"1.43.0\",\"friendlyName\":\"" + (source ? "Sample source" : "Sample destination") + "\"}}");
        }

        if (path == "/library/sections")
            return Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie","title":"Films"},{"key":"2","type":"show","title":"Shows"},{"key":"3","type":"movie","title":"Other Videos"}]}}""");
        if (path == "/library/sections/1/all")
            return Json(source ? SourceMovies : DestinationMovies);
        if (path == "/library/sections/3/all")
            return Json(source ? """{"MediaContainer":{"Metadata":[]}}""" : DestinationHomeVideos);
        if (path == "/library/sections/2/all" && query.Contains("type=2", StringComparison.Ordinal))
            return Json(source ? SourceShows : DestinationShows);
        if (path == "/library/sections/2/all" && query.Contains("type=4", StringComparison.Ordinal))
            return Json(source ? SourceEpisodes : DestinationEpisodes);
        if (path == "/playlists" && source)
            return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"50","title":"Friday","playlistType":"video","smart":false},{"ratingKey":"51","title":"Suggested","playlistType":"video","smart":true}]}}""");
        if (path == "/playlists/50/items")
            return Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"10","title":"Arrival","type":"movie","Guid":[{"id":"imdb://tt2543164"}]},{"ratingKey":"13","title":"Dawn","type":"track","grandparentTitle":"Max Richter","parentTitle":"Sleep"},{"ratingKey":"12","title":"Same Title","type":"movie"}]}}""");
        return Json("""{"MediaContainer":{"Metadata":[]}}""");
    }

    private const string SourceMovies = """
        {"MediaContainer":{"Metadata":[
          {"ratingKey":"10","title":"Arrival","year":2016,"type":"movie","viewCount":1,"viewOffset":0,"userRating":9,"Guid":[{"id":"imdb://tt2543164"}]},
          {"ratingKey":"11","title":"Shared Story","year":2014,"type":"movie","viewCount":0,"viewOffset":5000,"userRating":8,"Guid":[{"id":"imdb://tt1234567"}]},
          {"ratingKey":"12","title":"Same Title","year":2001,"type":"movie","viewCount":1,"Media":[{"Part":[{"file":"/media/same.mkv"}]}]}
        ]}}
        """;

    private const string DestinationMovies = """
        {"MediaContainer":{"Metadata":[
          {"ratingKey":"20","title":"Arrival","year":2016,"type":"movie","viewCount":0,"viewOffset":0,"Guid":[{"id":"imdb://tt2543164"}]},
          {"ratingKey":"21","title":"Shared Story","year":2014,"type":"movie","viewCount":1,"viewOffset":90000,"userRating":2,"Guid":[{"id":"imdb://tt1234567"}]},
          {"ratingKey":"22","title":"Same Title","year":2001,"type":"movie","viewCount":1,"Media":[{"Part":[{"file":"/media/other-path.mkv"}]}]},
          {"ratingKey":"24","title":"Destination only","year":1999,"type":"movie","viewCount":1,"Guid":[{"id":"imdb://tt8888888"}],"Media":[{"Part":[{"file":"/media/destination-only.mkv"}]}]}
        ]}}
        """;

    private const string DestinationHomeVideos = """
        {"MediaContainer":{"Metadata":[
          {"ratingKey":"90","title":"2013-08-03 15.10.59","year":2013,"type":"movie","viewCount":1,"Media":[{"Part":[{"file":"/volume1/video/2013-08-03 15.10.59.mp4"}]}]}
        ]}}
        """;

    private const string SourceShows = """
        {"MediaContainer":{"Metadata":[{"ratingKey":"30","title":"Station","year":2016,"type":"show","Guid":[{"id":"tvdb://100"}]}]}}
        """;

    private const string DestinationShows = """
        {"MediaContainer":{"Metadata":[{"ratingKey":"40","title":"Station","year":2016,"type":"show","Guid":[{"id":"tvdb://100"}]}]}}
        """;

    private const string SourceEpisodes = """
        {"MediaContainer":{"Metadata":[{"ratingKey":"31","title":"Pilot","type":"episode","parentIndex":1,"index":2,"grandparentTitle":"Station","grandparentRatingKey":"30","viewCount":1,"viewOffset":0}]}}
        """;

    private const string DestinationEpisodes = """
        {"MediaContainer":{"Metadata":[{"ratingKey":"41","title":"Different episode title","type":"episode","parentIndex":1,"index":2,"grandparentTitle":"Station","grandparentRatingKey":"40","viewCount":0,"viewOffset":0}]}}
        """;

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
#endif
