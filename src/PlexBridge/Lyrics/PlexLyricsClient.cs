using PlexBridge.Plex;

namespace PlexBridge.Lyrics;

/// <summary>
/// Reads music from one Plex server and refreshes a single album or track so a new sidecar is picked up.
/// Plex's local-lyrics article says a .lrc or .txt beside the audio is found when that album's metadata is refreshed.
/// A track refresh is used only when the album refresh does not add a lyrics stream. This client does not scan a library.
/// </summary>
public sealed class PlexLyricsClient(IPlexTransport reads, IPlexWriteTransport writes, IPlexAddressGuard addresses, ILogger<PlexLyricsClient> logger)
{
    public const int PageSize = 100;

    public async Task<IReadOnlyList<PlexCatalogItem>> ListAsync(
        Uri baseUri,
        string token,
        string sectionKey,
        string plexType,
        string? artistKey,
        string? albumKey,
        bool newestFirst,
        long? addedAfterUnix,
        Action<int, int?>? progress,
        CancellationToken cancellationToken)
    {
        if (sectionKey.Length is 0 or > 10 || !sectionKey.All(char.IsDigit))
            throw new PlexRequestException("The library id is not valid.");
        if (plexType is not ("8" or "9" or "10"))
            throw new PlexRequestException("That library list is not part of a lyrics lookup.");
        if (!IsOptionalKey(artistKey) || !IsOptionalKey(albumKey))
            throw new PlexRequestException("The artist or album id is not valid.");

        var expected = plexType switch
        {
            "8" => "artist",
            "9" => "album",
            _ => "track"
        };
        var items = new List<PlexCatalogItem>();
        var start = 0;
        int? total = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = $"/library/sections/{sectionKey}/all?type={plexType}&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}";
            if (!string.IsNullOrEmpty(artistKey))
                relative += "&artist.id=" + artistKey;
            if (!string.IsNullOrEmpty(albumKey))
                relative += "&album.id=" + albumKey;
            if (newestFirst)
                relative += "&sort=addedAt:desc";

            var page = PlexTrackParser.Parse(await GetAsync(baseUri, token, relative, cancellationToken));
            total ??= page.Total;
            var fresh = page.Items.Where(item => item.Type.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
            var stop = false;
            if (addedAfterUnix is long cutoff && newestFirst && fresh.Count > 0 && fresh.All(item => item.AddedAtUnix is long added && added < cutoff))
            {
                var ordered = true;
                for (var index = 1; index < fresh.Count; index++)
                {
                    if (fresh[index].AddedAtUnix > fresh[index - 1].AddedAtUnix)
                        ordered = false;
                }

                if (ordered)
                    stop = true;
            }

            if (addedAfterUnix is long keep)
                fresh = fresh.Where(item => item.AddedAtUnix is long added && added >= keep).ToList();
            items.AddRange(fresh);
            progress?.Invoke(items.Count, total);
            if (stop || page.Items.Count == 0 || page.Items.Count < PageSize)
                break;
            start += page.Items.Count;
            if (total is int known && start >= known)
                break;
            if (items.Count > 100_000)
                throw new PlexRequestException("That library is larger than this lyrics scan will read.");
        }

        return items;
    }

    public async Task<PlexCatalogItem?> GetTrackAsync(Uri baseUri, string token, string ratingKey, CancellationToken cancellationToken)
    {
        if (ratingKey.Length is 0 or > 12 || !ratingKey.All(char.IsDigit))
            throw new PlexRequestException("The track id is not valid.");
        var page = PlexTrackParser.Parse(await GetAsync(baseUri, token, "/library/metadata/" + ratingKey, cancellationToken));
        return page.Items.FirstOrDefault();
    }

    public Task RefreshAsync(Uri baseUri, string token, string ratingKey, CancellationToken cancellationToken)
    {
        if (ratingKey.Length is 0 or > 12 || !ratingKey.All(char.IsDigit))
            throw new PlexRequestException("The item id is not valid.");
        logger.LogInformation("Lyrics metadata refresh for item {RatingKey}.", ratingKey);
        return PlexDeadline.Run(PlexHttp.RequestTimeout, cancellationToken, probe => PlexExchange.SendAsync(
            writes,
            addresses,
            HttpMethod.Put,
            baseUri,
            token,
            "/library/metadata/" + ratingKey + "/refresh",
            PlexLyricsPolicy.EnsureWrite,
            followSameHostRedirect: false,
            probe));
    }

    private static bool IsOptionalKey(string? value)
        => string.IsNullOrEmpty(value) || (value.Length <= 12 && value.All(char.IsDigit));

    private Task<string> GetAsync(Uri baseUri, string token, string relative, CancellationToken cancellationToken)
        => PlexDeadline.Run(PlexHttp.RequestTimeout, cancellationToken, probe => PlexExchange.SendAsync(
            reads,
            addresses,
            HttpMethod.Get,
            baseUri,
            token,
            relative,
            PlexLyricsPolicy.EnsureRead,
            followSameHostRedirect: true,
            probe));
}
