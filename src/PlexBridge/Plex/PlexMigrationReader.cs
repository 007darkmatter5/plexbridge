using Microsoft.Extensions.Logging;

namespace PlexBridge.Plex;

/// <summary>
/// Reads the leaf items a migration can match: movies, episodes, and tracks.
/// Show and album identity comes from grandparentGuid and parentGuid on those leaves.
/// Seasons, shows, artists, and albums are not scanned. It never writes.
/// </summary>
public sealed class PlexMigrationReader(
    IPlexTransport transport,
    IPlexAddressGuard addresses,
    MigrationPreviewOptions options,
    ILogger<PlexMigrationReader> logger)
{
    public Task<MigrationCatalog> ReadCatalogAsync(
        Uri baseUri,
        string token,
        bool includePlaylists,
        string serverName,
        string role,
        IProgress<MigrationReadProgress>? progress,
        CancellationToken cancellationToken)
        => ReadCatalogAsync(baseUri, token, includePlaylists, options.PageSize, serverName, role, progress, cancellationToken, null);

    public Task<MigrationCatalog> ReadCatalogAsync(
        Uri baseUri,
        string token,
        bool includePlaylists,
        string serverName,
        string role,
        IProgress<MigrationReadProgress>? progress,
        CancellationToken cancellationToken,
        Action<MigrationPageBatch>? onPage)
        => ReadCatalogAsync(baseUri, token, includePlaylists, options.PageSize, serverName, role, progress, cancellationToken, onPage);

    public Task<MigrationCatalog> ReadCatalogAsync(
        Uri baseUri,
        string token,
        bool includePlaylists,
        string serverName,
        string role,
        IProgress<MigrationReadProgress>? progress,
        CancellationToken cancellationToken,
        Action<MigrationPageBatch>? onPage,
        Action? onLibrariesFinished,
        Action<MigrationPlaylist>? onPlaylist)
        => ReadCatalogAsync(baseUri, token, includePlaylists, options.PageSize, serverName, role, progress, cancellationToken, onPage, onLibrariesFinished, onPlaylist);

    public async Task<MigrationCatalog> ReadCatalogAsync(
        Uri baseUri,
        string token,
        bool includePlaylists,
        int pageSize,
        string serverName,
        string role,
        IProgress<MigrationReadProgress>? progress,
        CancellationToken cancellationToken,
        Action<MigrationPageBatch>? onPage,
        Action? onLibrariesFinished = null,
        Action<MigrationPlaylist>? onPlaylist = null)
    {
        var sectionsJson = await GetAsync(baseUri, token, "/library/sections", cancellationToken);
        var sections = PlexJsonParser.ParseSections(sectionsJson);
        var items = new Dictionary<string, MigrationItem>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (section.Type.Equals("photo", StringComparison.OrdinalIgnoreCase))
                continue;
            progress?.Report(new MigrationReadProgress(role, serverName, PhaseOf(section), section.Title, "Waiting", 0, 0));
        }

        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (section.Type.Equals("photo", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (section.Type.Equals("show", StringComparison.OrdinalIgnoreCase))
                    await ListAsync(baseUri, token, section, "4", pageSize, items, serverName, role, progress, onPage, cancellationToken);
                else if (section.Type.Equals("artist", StringComparison.OrdinalIgnoreCase))
                    await ListAsync(baseUri, token, section, "10", pageSize, items, serverName, role, progress, onPage, cancellationToken);
                else if (section.Type.Equals("movie", StringComparison.OrdinalIgnoreCase))
                    await ListAsync(baseUri, token, section, "1", pageSize, items, serverName, role, progress, onPage, cancellationToken);
                else
                    await ListAsync(baseUri, token, section, null, pageSize, items, serverName, role, progress, onPage, cancellationToken);
            }
            catch (PlexRequestException exception)
            {
                throw new PlexRequestException($"{role} · {serverName} · {section.Title}: {exception.Message}");
            }
        }

        onLibrariesFinished?.Invoke();
        var playlists = includePlaylists
            ? await ReadPlaylistsAsync(baseUri, token, pageSize, serverName, role, progress, onPlaylist, cancellationToken)
            : [];
        logger.LogInformation(
            "Finished reading {Role} server {ServerIdName}. {Count} leaf items.",
            role,
            serverName,
            items.Count);
        return new MigrationCatalog(items.Values.ToList(), playlists);
    }

    private async Task ListAsync(
        Uri baseUri,
        string token,
        PlexSectionInfo section,
        string? type,
        int pageSize,
        Dictionary<string, MigrationItem> items,
        string serverName,
        string role,
        IProgress<MigrationReadProgress>? progress,
        Action<MigrationPageBatch>? onPage,
        CancellationToken cancellationToken)
    {
        if (section.Key.Length is 0 or > 10 || !section.Key.All(char.IsDigit))
            throw new PlexRequestException("The library id is not valid.");
        pageSize = Math.Clamp(pageSize, 1, 100);
        var start = 0;
        var pageNumber = 0;
        var sectionCount = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageNumber++;
                var typeQuery = type is null ? "" : $"&type={type}";
                var relative = $"/library/sections/{section.Key}/all?includeGuids=1&X-Plex-Container-Start={start}&X-Plex-Container-Size={pageSize}{typeQuery}";
                var page = PlexJsonParser.ParseMigrationPage(await GetAsync(baseUri, token, relative, cancellationToken), playlist: false);
                var fresh = new List<MigrationItem>();
                foreach (var item in page.Items)
                {
                    var stored = item.Library is null ? item with { Library = section.Title } : item;
                    if (items.TryAdd(stored.RatingKey, stored))
                        fresh.Add(stored);
                }

                sectionCount += fresh.Count;
                progress?.Report(new MigrationReadProgress(role, serverName, PhaseOf(section), section.Title, "Reading", pageNumber, sectionCount));
                onPage?.Invoke(new MigrationPageBatch(section, pageNumber, sectionCount, fresh));
                logger.LogInformation(
                    "Migration read {Role} {ServerName} library {Library} page {Page}. {Count} items in this library.",
                    role,
                    serverName,
                    section.Title,
                    pageNumber,
                    sectionCount);
                if (fresh.Count == 0 || page.Items.Count < pageSize)
                    break;
                start += page.Items.Count;
                if (items.Count > 100_000)
                    throw new PlexRequestException("That library is larger than this migration will read.");
            }
        }
        catch (PlexRequestException)
        {
            progress?.Report(new MigrationReadProgress(role, serverName, PhaseOf(section), section.Title, "Failed", pageNumber, sectionCount));
            throw;
        }

        progress?.Report(new MigrationReadProgress(role, serverName, PhaseOf(section), section.Title, "Done", pageNumber, sectionCount));
    }

    internal static string PhaseOf(PlexSectionInfo section)
    {
        if (section.Type.Equals("show", StringComparison.OrdinalIgnoreCase))
            return "TV episodes";
        if (section.Type.Equals("artist", StringComparison.OrdinalIgnoreCase))
            return "Music tracks";
        if (section.Type.Equals("movie", StringComparison.OrdinalIgnoreCase))
            return section.Title.Contains("other", StringComparison.OrdinalIgnoreCase) ? "Other Videos" : "Movies";
        return section.Title;
    }

    private async Task<IReadOnlyList<MigrationPlaylist>> ReadPlaylistsAsync(
        Uri baseUri,
        string token,
        int pageSize,
        string serverName,
        string role,
        IProgress<MigrationReadProgress>? progress,
        Action<MigrationPlaylist>? onPlaylist,
        CancellationToken cancellationToken)
    {
        var catalog = PlexJsonParser.ParseMigrationPage(await GetAsync(baseUri, token, "/playlists", cancellationToken), playlist: true);
        progress?.Report(new MigrationReadProgress(role, serverName, "Playlists", "Playlists", "Waiting", 0, 0));
        var lists = new List<MigrationPlaylist>();
        pageSize = Math.Clamp(pageSize, 1, 100);
        var index = 0;
        foreach (var playlist in catalog.Playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (playlist.RatingKey.Length is 0 or > 12 || !playlist.RatingKey.All(char.IsDigit))
                continue;
            var members = new List<MigrationItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var start = 0;
            while (members.Count <= 20_000)
            {
                var relative = $"/playlists/{playlist.RatingKey}/items?X-Plex-Container-Start={start.ToString(System.Globalization.CultureInfo.InvariantCulture)}&X-Plex-Container-Size={pageSize}";
                var page = PlexJsonParser.ParseMigrationPage(await GetAsync(baseUri, token, relative, cancellationToken), playlist: false);
                var fresh = 0;
                foreach (var item in page.Items)
                {
                    if (seen.Add(item.RatingKey))
                    {
                        members.Add(item);
                        fresh++;
                    }
                }

                if (page.Items.Count < pageSize || fresh == 0)
                    break;
                start += page.Items.Count;
            }

            var stored = playlist with { Items = members };
            lists.Add(stored);
            onPlaylist?.Invoke(stored);
            index++;
            progress?.Report(new MigrationReadProgress(role, serverName, "Playlists", "Playlists", "Reading", index, lists.Count));
        }

        progress?.Report(new MigrationReadProgress(role, serverName, "Playlists", "Playlists", "Done", index, lists.Count));
        return lists;
    }

    public async Task<IReadOnlyList<MigrationPlaylist>> ListPlaylistsAsync(Uri baseUri, string token, CancellationToken cancellationToken)
    {
        var json = await GetAsync(baseUri, token, "/playlists", cancellationToken);
        return PlexJsonParser.ParseMigrationPage(json, playlist: true).Playlists;
    }

    public async Task<IReadOnlyList<MigrationItem>> ReadPlaylistItemsAsync(Uri baseUri, string token, string playlistKey, CancellationToken cancellationToken)
    {
        if (playlistKey.Length is 0 or > 12 || !playlistKey.All(char.IsDigit))
            throw new PlexRequestException("The playlist id is not valid.");
        var items = new List<MigrationItem>();
        var start = 0;
        const int pageSize = 100;
        while (items.Count < 20_000)
        {
            var relative = $"/playlists/{playlistKey}/items?X-Plex-Container-Start={start.ToString(System.Globalization.CultureInfo.InvariantCulture)}&X-Plex-Container-Size={pageSize}";
            var page = PlexJsonParser.ParseMigrationItems(await GetAsync(baseUri, token, relative, cancellationToken));
            items.AddRange(page);
            if (page.Count < pageSize)
                break;
            start += page.Count;
        }

        return items;
    }

    public async Task<MigrationItem?> ReadItemAsync(Uri baseUri, string token, string ratingKey, CancellationToken cancellationToken)
    {
        if (ratingKey.Length is 0 or > 12 || !ratingKey.All(char.IsDigit))
            throw new PlexRequestException("The item id is not valid.");
        var json = await GetAsync(baseUri, token, "/library/metadata/" + ratingKey, cancellationToken);
        return PlexJsonParser.ParseMigrationItems(json).FirstOrDefault();
    }

    private Task<string> GetAsync(Uri baseUri, string token, string relative, CancellationToken cancellationToken)
        => PlexDeadline.Run(options.RequestTimeout, cancellationToken, probe => PlexExchange.SendAsync(
            transport,
            addresses,
            HttpMethod.Get,
            baseUri,
            token,
            relative,
            PlexMigrationReadPolicy.EnsureAllowed,
            followSameHostRedirect: true,
            probe));
}
