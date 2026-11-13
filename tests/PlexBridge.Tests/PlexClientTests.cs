using System.Net;
using PlexBridge.Plex;
using PlexBridge.Security;

namespace PlexBridge.Tests;

public class PlexJsonParserTests
{
    [Fact]
    public void Parses_identity_sections_and_single_directory_object()
    {
        var identity = PlexJsonParser.ParseIdentity("""
            {"MediaContainer":{"machineIdentifier":"abc","version":"1.2.3"}}
            """);
        Assert.Equal("abc", identity.MachineIdentifier);
        Assert.Equal("1.2.3", identity.Version);

        var sections = PlexJsonParser.ParseSections("""
            {"MediaContainer":{"size":1,"Directory":{"key":"4","type":"movie","title":"Films","Location":{"path":"/media/Films"}}}}
            """);
        var section = Assert.Single(sections);
        Assert.Equal("4", section.Key);
        Assert.Equal("Films", section.Title);
        Assert.Equal("/media/Films", Assert.Single(section.Locations));
    }

    [Fact]
    public void Parses_paginated_items_and_external_ids()
    {
        var page = PlexJsonParser.ParseItems("""
            {
              "MediaContainer": {
                "size": 1,
                "totalSize": 635,
                "offset": 25,
                "Metadata": [
                  {
                    "ratingKey": "99",
                    "title": "Example",
                    "year": 2020,
                    "type": "movie",
                    "Guid": [
                      {"id": "imdb://tt0133093"},
                      {"id": "tmdb://603"},
                      {"id": "plex://movie/5d776"},
                      {"id": "com.plexapp.agents.thetvdb://81189"}
                    ]
                  }
                ]
              }
            }
            """);
        Assert.Equal(635, page.Total);
        Assert.Equal(25, page.Offset);
        var item = Assert.Single(page.Items);
        Assert.Equal("tt0133093", item.ImdbId);
        Assert.Equal("603", item.TmdbId);
        Assert.Equal("81189", item.TvdbId);
        Assert.Equal("99", item.RatingKey);
    }

    [Fact]
    public void Invalid_payload_does_not_echo_the_body()
    {
        const string secret = "plex-token-in-body-should-not-leak";
        var ex = Assert.Throws<PlexRequestException>(() => PlexJsonParser.ParseIdentity("{\"error\":\"" + secret + "\"}"));
        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain(secret, ex.ToString());
    }
}

public class PlexReadClientTests
{
    private const string Token = "unit-token-value-123456";

    [Fact]
    public async Task Sends_token_as_a_header_and_pages_until_the_library_is_read()
    {
        var handler = new ScriptHandler
        {
            Responder = request =>
            {
                var start = QueryValue(request.Uri, "X-Plex-Container-Start");
                if (start == 0)
                {
                    return HttpJson.Json("""
                        {"MediaContainer":{"size":2,"totalSize":3,"offset":0,"Metadata":[
                          {"ratingKey":"1","title":"One","type":"movie"},
                          {"ratingKey":"2","title":"Two","type":"movie"}
                        ]}}
                        """);
                }

                return HttpJson.Json("""
                    {"MediaContainer":{"size":1,"totalSize":3,"offset":2,"Metadata":[
                      {"ratingKey":"3","title":"Three","type":"movie"}
                    ]}}
                    """);
            }
        };
        var client = new PlexReadClient(new HandlerTransport(handler), new AllowAllAddressGuard());
        var items = await client.ListAllItemsAsync(new Uri("http://plex.test:32400/"), Token, "7", 2, CancellationToken.None);

        Assert.Equal(["One", "Two", "Three"], items.Select(item => item.Title).ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Token, request.Token);
            Assert.DoesNotContain("X-Plex-Token", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Token, request.Uri.ToString());
            Assert.Contains("/library/sections/7/all", request.Uri.AbsolutePath);
        });
        Assert.Contains("X-Plex-Container-Start=2", handler.Requests[1].Uri.Query);
    }

    private static int QueryValue(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var pair = query.Single(part => part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return int.Parse(pair[(name.Length + 1)..]);
    }

    [Fact]
    public async Task Does_not_call_the_network_for_a_forbidden_path()
    {
        var handler = new ScriptHandler();
        var client = new PlexReadClient(new HandlerTransport(handler), new AllowAllAddressGuard());
        var ex = await Assert.ThrowsAsync<PlexRequestException>(() =>
            client.ReadForTestsAsync(new Uri("http://plex.test:32400/"), Token, "/library/sections/1/refresh", CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task Upstream_error_body_is_not_returned()
    {
        var handler = new ScriptHandler
        {
            Responder = _ => HttpJson.Json("{\"message\":\"" + Token + "\"}", HttpStatusCode.InternalServerError)
        };
        var client = new PlexReadClient(new HandlerTransport(handler), new AllowAllAddressGuard());
        var ex = await Assert.ThrowsAsync<PlexRequestException>(() =>
            client.ReadForTestsAsync(new Uri("http://plex.test:32400/"), Token, "/identity", CancellationToken.None));
        Assert.Equal("The server returned an error.", ex.Message);
        Assert.DoesNotContain(Token, ex.ToString());
    }

    [Fact]
    public async Task Timeout_and_cancellation_are_distinct_and_safe()
    {
        var handler = new ScriptHandler { Throw = new TaskCanceledException() };
        var client = new PlexReadClient(new HandlerTransport(handler), new AllowAllAddressGuard());
        var timeout = await Assert.ThrowsAsync<PlexRequestException>(() =>
            client.ReadForTestsAsync(new Uri("http://plex.test:32400/"), Token, "/identity", CancellationToken.None));
        Assert.Contains("timeout", timeout.Message, StringComparison.OrdinalIgnoreCase);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = await Assert.ThrowsAsync<PlexRequestException>(() =>
            client.ReadForTestsAsync(new Uri("http://plex.test:32400/"), Token, "/identity", cts.Token));
        Assert.Contains("canceled", canceled.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, timeout.Message + canceled.Message);
    }

    [Fact]
    public async Task Refuses_a_redirect_to_another_host()
    {
        var handler = new ScriptHandler
        {
            Responder = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://evil.example/identity?X-Plex-Token=" + Token);
                return response;
            }
        };
        var client = new PlexReadClient(new HandlerTransport(handler), new AllowAllAddressGuard());
        var ex = await Assert.ThrowsAsync<PlexRequestException>(() =>
            client.ReadForTestsAsync(new Uri("http://plex.test:32400/"), Token, "/identity", CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("evil.example", ex.Message);
    }

    [Fact]
    public void Redactor_strips_a_token_that_reached_a_message()
    {
        var text = SecretRedactor.ForDisplay("failed with " + Token + " and X-Plex-Token", Token);
        Assert.DoesNotContain(Token, text);
        Assert.DoesNotContain("X-Plex-Token", text, StringComparison.OrdinalIgnoreCase);
    }
}
