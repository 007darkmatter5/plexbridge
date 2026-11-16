using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlexBridge.Lyrics;

public enum LyricsChoiceKind
{
    Match,
    Instrumental,
    None
}

public sealed record LyricsChoice(LyricsChoiceKind Kind, LrclibRecord? Record);

public sealed record LrclibRecord(
    string TrackName,
    string ArtistName,
    string AlbumName,
    int DurationSeconds,
    bool Instrumental,
    string? PlainLyrics,
    string? SyncedLyrics);

/// <summary>
/// Picks an LRCLIB search hit. Title and artist have to match. Duration has to be within 10 seconds
/// when both sides know it. Synced lyrics win a close call. Instrumentals are not chosen.
/// </summary>
public static partial class LyricsMatching
{
    public const int DurationLimitSeconds = 10;

    public static LyricsChoice Choose(LyricsQuery query, IReadOnlyList<LrclibRecord> candidates)
    {
        LrclibRecord? best = null;
        var bestScore = int.MinValue;
        var bestDurationGap = int.MaxValue;
        var sawInstrumental = false;
        foreach (var candidate in candidates)
        {
            if (!NamesMatch(query, candidate, RiskyArtist(query.Artist)))
                continue;
            if (!DurationFits(query.DurationSeconds, candidate.DurationSeconds, RiskyArtist(query.Artist), out var gap))
                continue;
            if (candidate.Instrumental || (!HasSynced(candidate) && !HasPlain(candidate)))
            {
                if (candidate.Instrumental)
                    sawInstrumental = true;
                continue;
            }

            var score = Score(query, candidate, gap);
            var better = score > bestScore || (score == bestScore && gap < bestDurationGap);
            if (!better)
                continue;
            best = candidate;
            bestScore = score;
            bestDurationGap = gap;
        }

        if (best is not null)
            return new LyricsChoice(LyricsChoiceKind.Match, best);
        return sawInstrumental
            ? new LyricsChoice(LyricsChoiceKind.Instrumental, null)
            : new LyricsChoice(LyricsChoiceKind.None, null);
    }

    public static bool HasSynced(LrclibRecord record)
        => LyricsSidecar.HasTimestamps(record.SyncedLyrics);

    public static bool HasPlain(LrclibRecord record)
        => !string.IsNullOrWhiteSpace(record.PlainLyrics);

    private static int Score(LyricsQuery query, LrclibRecord candidate, int durationGap)
    {
        var score = Fold(query.Title) == Fold(candidate.TrackName) ? 50 : 46;
        score += 30;
        var album = AlbumCore(query.Album);
        var other = AlbumCore(candidate.AlbumName);
        if (album.Length > 0 && album == other)
            score += 10;
        else if (album.Length >= 4 && other.Length >= 4 && (album.Contains(other, StringComparison.Ordinal) || other.Contains(album, StringComparison.Ordinal)))
            score += 6;
        score += durationGap switch
        {
            <= 2 => 15,
            <= 5 => 8,
            _ => 2
        };
        if (HasSynced(candidate))
            score += 12;
        return score;
    }

    private static bool NamesMatch(LyricsQuery query, LrclibRecord candidate, bool requireAlbum)
    {
        if (!SameTitle(query.Title, candidate.TrackName) || !SameArtist(query.Artist, candidate.ArtistName))
            return false;
        if (!requireAlbum)
            return true;
        var album = AlbumCore(query.Album);
        return album.Length > 0 && album == AlbumCore(candidate.AlbumName);
    }

    private static bool DurationFits(int wanted, int candidate, bool strict, out int gap)
    {
        if (wanted <= 0 || candidate <= 0)
        {
            gap = strict ? int.MaxValue : 6;
            return !strict;
        }

        gap = Math.Abs(wanted - candidate);
        var limit = strict ? 5 : DurationLimitSeconds;
        return gap <= limit;
    }

    private static bool RiskyArtist(string artist)
    {
        var folded = Fold(artist);
        return folded is "various artists" or "various artist" or "various" or "va" or "soundtrack";
    }

    private static bool SameTitle(string? left, string? right)
    {
        var foldedLeft = Fold(left);
        var foldedRight = Fold(right);
        if (foldedLeft.Length > 0 && foldedLeft == foldedRight)
            return true;
        var coreLeft = Core(left);
        var coreRight = Core(right);
        return coreLeft.Length >= 2 && coreLeft == coreRight;
    }

    private static bool SameArtist(string? left, string? right)
    {
        var leftParts = ArtistParts(left);
        var rightParts = ArtistParts(right);
        if (leftParts.Count == 0 || rightParts.Count == 0)
            return false;
        return StripArticle(leftParts[0]) == StripArticle(rightParts[0]) && StripArticle(leftParts[0]).Length >= 2;
    }

    private static List<string> ArtistParts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        var prepared = value.Replace("&", " and ", StringComparison.Ordinal);
        return ArtistSplit().Split(prepared)
            .Select(Fold)
            .Where(part => part.Length > 0)
            .ToList();
    }

    private static string StripArticle(string folded)
        => folded.StartsWith("the ", StringComparison.Ordinal) ? folded[4..] : folded;

    private static string Core(string? value)
    {
        var text = value?.Trim() ?? "";
        while (true)
        {
            var next = TrailingBracket().Replace(text, "").Trim();
            if (next == text)
                break;
            text = next;
        }

        return Fold(text);
    }

    private static string AlbumCore(string? album)
    {
        var folded = Core(album);
        foreach (var noise in new[] { " deluxe edition", " deluxe", " remastered", " remaster", " expanded edition", " bonus track version" })
            folded = folded.Replace(noise, " ", StringComparison.Ordinal);
        return string.Join(' ', folded.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"\s*(?:,|;|\bfeaturing\b|\bfeat\.?\b|\bft\.?\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistSplit();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$")]
    private static partial Regex TrailingBracket();
}
