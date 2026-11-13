using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PlexBridge.Plex;

/// <summary>Sends the migration writes. It does not scan libraries, play media, or delete a server.</summary>
public sealed class PlexWriteClient(IPlexWriteTransport transport, IPlexAddressGuard addresses, MigrationPreviewOptions options, ILogger<PlexWriteClient> logger)
{
    public Task MarkWatchedAsync(Uri baseUri, string token, string ratingKey, bool watched, CancellationToken cancellationToken)
    {
        var path = watched ? "/:/scrobble" : "/:/unscrobble";
        var relative = $"{path}?key={ratingKey}&identifier=com.plexapp.plugins.library";
        return SendAsync(HttpMethod.Get, baseUri, token, relative, cancellationToken);
    }

    public Task SetProgressAsync(Uri baseUri, string token, string ratingKey, long offsetMs, CancellationToken cancellationToken)
    {
        if (offsetMs < 0)
            offsetMs = 0;
        if (offsetMs > 172_800_000)
            throw new PlexRequestException("That stop time is outside the range this migration will write.");
        var relative = $"/:/progress?key={ratingKey}&identifier=com.plexapp.plugins.library&time={offsetMs.ToString(CultureInfo.InvariantCulture)}&state=stopped";
        return SendAsync(HttpMethod.Get, baseUri, token, relative, cancellationToken);
    }

    public Task SetRatingAsync(Uri baseUri, string token, string ratingKey, decimal? rating, CancellationToken cancellationToken)
    {
        var value = rating is null ? -1m : decimal.Round(rating.Value, 1, MidpointRounding.AwayFromZero);
        if (value is < -1 or > 10)
            throw new PlexRequestException("That rating is outside the range this migration will write.");
        var relative = $"/:/rate?key={ratingKey}&identifier=com.plexapp.plugins.library&rating={value.ToString("0.0", CultureInfo.InvariantCulture)}";
        return SendAsync(HttpMethod.Put, baseUri, token, relative, cancellationToken);
    }

    public async Task<string> CreatePlaylistAsync(Uri baseUri, string token, string machineId, string playlistType, string title, string firstRatingKey, CancellationToken cancellationToken)
    {
        var relative = "/playlists?type=" + Uri.EscapeDataString(playlistType)
            + "&title=" + Uri.EscapeDataString(title)
            + "&smart=0&uri=" + Uri.EscapeDataString(PlexWritePolicy.ItemUriFor(machineId, firstRatingKey));
        var body = await SendAsync(HttpMethod.Post, baseUri, token, relative, cancellationToken);
        var created = PlexJsonParser.ParseMigrationPage(body, playlist: true).Playlists.FirstOrDefault();
        var ratingKey = created?.RatingKey;
        if (string.IsNullOrEmpty(ratingKey))
            ratingKey = PlexJsonParser.ParseMigrationItems(body).FirstOrDefault()?.RatingKey;
        if (string.IsNullOrEmpty(ratingKey) || !ratingKey.All(char.IsDigit))
            throw new PlexRequestException("The destination did not return the new playlist.");
        return ratingKey;
    }

    public Task AddPlaylistItemAsync(Uri baseUri, string token, string playlistKey, string machineId, string ratingKey, CancellationToken cancellationToken)
    {
        var relative = $"/playlists/{playlistKey}/items?uri=" + Uri.EscapeDataString(PlexWritePolicy.ItemUriFor(machineId, ratingKey));
        return SendAsync(HttpMethod.Put, baseUri, token, relative, cancellationToken);
    }

    private Task<string> SendAsync(HttpMethod method, Uri baseUri, string token, string relative, CancellationToken cancellationToken)
    {
        var path = relative.Split('?')[0];
        logger.LogInformation("Migration write {Method} {Path}.", method.Method, path);
        return PlexDeadline.Run(options.RequestTimeout, cancellationToken, probe => PlexExchange.SendAsync(
            transport,
            addresses,
            method,
            baseUri,
            token,
            relative,
            PlexWritePolicy.EnsureAllowed,
            followSameHostRedirect: false,
            probe));
    }
}
