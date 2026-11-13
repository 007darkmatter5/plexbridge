namespace PlexBridge.Plex;

/// <summary>A cross-server content id. A Plex ratingKey and a file path are not content ids.</summary>
public sealed record PlexContentId(string Provider, string Value)
{
    public string Key => Provider + ":" + Value;
}

/// <summary>
/// Reads IMDb, TMDB, TVDB, MusicBrainz, and Plex content ids (plex://movie, plex://show, and the other content prefixes).
/// Legacy agent strings such as com.plexapp.agents.imdb://tt0133093?lang=en count as the same id.
/// local://, file paths, and rating keys are ignored.
/// </summary>
public static class PlexGuids
{
    private static readonly string[] Providers =
    [
        "imdb", "tmdb", "tvdb", "musicbrainz",
        "plex-movie", "plex-show", "plex-season", "plex-episode",
        "plex-artist", "plex-album", "plex-track"
    ];

    public static IReadOnlyList<PlexContentId> Parse(IEnumerable<string?> rawIds)
    {
        var found = new List<PlexContentId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawIds)
        {
            if (TryParseOne(raw, out var id) && seen.Add(id.Key))
                found.Add(id);
        }

        return found;
    }

    public static bool TryParseOne(string? raw, out PlexContentId id)
    {
        id = null!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        var query = text.IndexOf('?');
        if (query >= 0)
            text = text[..query];
        text = text.Trim().TrimEnd('/');
        if (text.Length == 0 || text.Contains(":///", StringComparison.Ordinal))
            return false;

        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("file:", StringComparison.Ordinal) || lower.Contains("file://", StringComparison.Ordinal))
            return false;

        if (TryPrefixed(lower, text, "imdb://", "imdb", IsImdb, out id))
            return true;
        if (TryPrefixed(lower, text, "themoviedb://", "tmdb", IsDigits, out id) || TryPrefixed(lower, text, "tmdb://", "tmdb", IsDigits, out id))
            return true;
        if (TryPrefixed(lower, text, "thetvdb://", "tvdb", IsDigits, out id) || TryPrefixed(lower, text, "tvdb://", "tvdb", IsDigits, out id))
            return true;
        if (TryLegacyEpisode(lower, text, "thetvdb://", "tvdb", out id) || TryLegacyEpisode(lower, text, "tvdb://", "tvdb", out id))
            return true;
        if (TryLegacyEpisode(lower, text, "themoviedb://", "tmdb", out id) || TryLegacyEpisode(lower, text, "tmdb://", "tmdb", out id))
            return true;
        if (TryPrefixed(lower, text, "musicbrainz://", "musicbrainz", IsMusicBrainz, out id) || TryPrefixed(lower, text, "mbid://", "musicbrainz", IsMusicBrainz, out id))
            return true;
        if (TryPlex(lower, out id))
            return true;

        return false;
    }

    public static bool IsContentProvider(string provider)
        => Providers.Contains(provider, StringComparer.Ordinal);

    private static bool TryPrefixed(string lower, string original, string marker, string provider, Func<string, bool> valid, out PlexContentId id)
    {
        id = null!;
        var index = lower.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var value = original[(index + marker.Length)..].Trim();
        if (!valid(value))
            return false;
        id = new PlexContentId(provider, provider == "imdb" ? value.ToLowerInvariant() : value);
        return true;
    }

    /// <summary>
    /// A legacy episode agent id is show/season/episode, such as thetvdb://81189/1/50.
    /// The show id is the first segment. The whole path stays the episode id, so it does not match every episode of the show.
    /// </summary>
    public static bool TryLegacyParts(PlexContentId id, out PlexContentId showId, out int season, out int episode)
    {
        showId = null!;
        season = 0;
        episode = 0;
        if (id.Provider is not ("tvdb" or "tmdb"))
            return false;
        var parts = id.Value.Split('/');
        if (parts.Length != 3 || !parts.All(IsDigits))
            return false;
        if (!int.TryParse(parts[1], out season) || !int.TryParse(parts[2], out episode))
            return false;
        showId = new PlexContentId(id.Provider, parts[0]);
        return true;
    }

    private static bool TryLegacyEpisode(string lower, string original, string marker, string provider, out PlexContentId id)
    {
        id = null!;
        var index = lower.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var value = original[(index + marker.Length)..].Trim();
        var parts = value.Split('/');
        if (parts.Length != 3 || !parts.All(IsDigits))
            return false;
        id = new PlexContentId(provider, value);
        return true;
    }

    private static bool TryPlex(string lower, out PlexContentId id)
    {
        id = null!;
        const string marker = "plex://";
        var index = lower.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return false;
        var rest = lower[(index + marker.Length)..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            return false;
        var kind = rest[..slash];
        var value = rest[(slash + 1)..];
        if (value.Contains('/') || !IsPlexId(value))
            return false;
        var provider = kind switch
        {
            "movie" => "plex-movie",
            "show" => "plex-show",
            "season" => "plex-season",
            "episode" => "plex-episode",
            "artist" => "plex-artist",
            "album" => "plex-album",
            "track" => "plex-track",
            _ => null
        };
        if (provider is null)
            return false;
        id = new PlexContentId(provider, value);
        return true;
    }

    private static bool IsImdb(string value)
        => value.Length is > 2 and <= 16
           && value.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
           && value[2..].All(char.IsDigit);

    private static bool IsDigits(string value)
        => value.Length is > 0 and <= 16 && value.All(char.IsDigit);

    private static bool IsPlexId(string value)
        => value.Length is >= 4 and <= 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static bool IsMusicBrainz(string value)
    {
        if (value.Length is < 32 or > 36)
            return false;
        foreach (var character in value)
        {
            if (character is not '-' && !char.IsAsciiHexDigit(character))
                return false;
        }

        return true;
    }
}
