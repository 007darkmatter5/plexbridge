using PlexBridge.Lyrics;
using PlexBridge.Plex;

namespace PlexBridge.Libraries;

/// <summary>
/// Reads a few movie, episode, or track paths so a library folder can be checked.
/// The token is sent only as the X-Plex-Token header and is not written to the log.
/// </summary>
public sealed class LibraryFileClient(IPlexTransport reads, IPlexAddressGuard addresses, ILogger<LibraryFileClient> logger)
{
    public const int PageSize = 100;

    public int ItemPageSize { get; set; } = PageSize;

    public async Task<PlexCatalogPage> ReadPageAsync(Uri baseUri, string token, string sectionKey, string plexType, int start, CancellationToken cancellationToken)
    {
        if (sectionKey.Length is 0 or > 10 || !sectionKey.All(char.IsDigit))
            throw new PlexRequestException("The library id is not valid.");
        if (plexType is not ("1" or "4" or "10"))
            throw new PlexRequestException("That library is not a movie, show, or music library.");
        if (start < 0)
            start = 0;
        var size = Math.Clamp(ItemPageSize, 1, 100);
        logger.LogInformation("Reading library {SectionKey} to check paths.", sectionKey);
        var relative = $"/library/sections/{sectionKey}/all?type={plexType}&X-Plex-Container-Start={start}&X-Plex-Container-Size={size}";
        var json = await PlexDeadline.Run(PlexHttp.RequestTimeout, cancellationToken, probe => PlexExchange.SendAsync(
            reads,
            addresses,
            HttpMethod.Get,
            baseUri,
            token,
            relative,
            LibraryPathPolicy.Ensure,
            followSameHostRedirect: true,
            probe));
        return PlexTrackParser.Parse(json);
    }

    public static string? LeafType(string? sectionType)
        => (sectionType ?? "").ToLowerInvariant() switch
        {
            "movie" => "1",
            "show" => "4",
            "artist" => "10",
            _ => null
        };
}
