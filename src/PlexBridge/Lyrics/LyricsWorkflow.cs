namespace PlexBridge.Lyrics;

/// <summary>Scans a music library, writes the sidecars you confirmed, and asks Plex to refresh those albums.</summary>
public sealed class LyricsWorkflow(PlexLyricsClient plex, LrclibClient lrclib, LyricsRefreshTiming timing, ILogger<LyricsWorkflow> logger)
{
    public async Task<List<LyricsTrackRow>> ScanAsync(
        Uri baseUri,
        string token,
        string sectionKey,
        string? artistKey,
        string? albumKey,
        IReadOnlyList<LyricsPathMap> maps,
        long? addedAfterUnix,
        bool newestFirst,
        IProgress<LyricsProgress>? progress,
        Func<string, bool>? canWrite,
        CancellationToken cancellationToken)
    {
        progress?.Report(new LyricsProgress("Reading tracks", 0, 0));
        var tracks = await plex.ListAsync(
            baseUri,
            token,
            sectionKey,
            "10",
            artistKey,
            albumKey,
            newestFirst,
            addedAfterUnix,
            (done, total) => progress?.Report(new LyricsProgress("Reading tracks", done, total ?? 0)),
            cancellationToken);

        var rows = new List<LyricsTrackRow>(tracks.Count);
        var lookups = new List<LyricsTrackRow>();
        var writeCheck = canWrite ?? DirectoryWrite.CanWrite;
        foreach (var track in tracks)
        {
            var row = new LyricsTrackRow
            {
                RatingKey = track.RatingKey,
                AlbumKey = track.AlbumKey,
                Artist = track.Artist,
                Album = track.Album,
                Title = track.Title,
                FileName = LyricsPaths.FileNameOf(track.FilePath),
                PlexPath = track.FilePath,
                DurationSeconds = track.DurationSeconds,
                AddedAtUnix = track.AddedAtUnix
            };
            Classify(row, track, maps, writeCheck, lookups);
            rows.Add(row);
        }

        var finished = 0;
        await Parallel.ForEachAsync(lookups, new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = cancellationToken
        }, async (row, token) =>
        {
            var lookup = await lrclib.FindAsync(new LyricsQuery(row.Title, row.Artist, row.Album, row.DurationSeconds), token);
            lock (lookups)
            {
                ApplyLookup(row, lookup);
                finished++;
                progress?.Report(new LyricsProgress("Looking up lyrics", finished, lookups.Count));
            }
        });

        logger.LogInformation(
            "Lyrics scan read {Tracks} tracks. {Ready} can be written.",
            rows.Count,
            rows.Count(row => row.CanSelect));
        progress?.Report(new LyricsProgress("Scan finished", rows.Count, rows.Count));
        return rows;
    }

    public async Task PublishAsync(
        Uri baseUri,
        string token,
        IReadOnlyList<LyricsTrackRow> selected,
        IProgress<LyricsProgress>? progress,
        CancellationToken cancellationToken)
    {
        var written = new List<LyricsTrackRow>();
        var index = 0;
        foreach (var row in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new LyricsProgress("Writing lyrics", index, selected.Count));
            if (!row.CanSelect || string.IsNullOrWhiteSpace(row.ContainerPath) || string.IsNullOrWhiteSpace(row.LyricsBody))
            {
                row.ApplyResult = "Skipped.";
                continue;
            }

            if (!File.Exists(row.ContainerPath))
            {
                row.ApplyResult = "Skipped. The audio file is not reachable.";
                row.Status = LyricsStatus.NotReachable;
                row.CanSelect = false;
                row.Selected = false;
                continue;
            }

            var contents = row.Synced
                ? LyricsSidecar.FormatLrc(row.Artist, row.Album, row.Title, row.DurationSeconds, row.LyricsBody)
                : LyricsSidecar.FormatPlain(row.LyricsBody);
            if (!LyricsSidecar.TryWrite(row.ContainerPath, row.Synced, contents, out var writtenPath, out var error))
            {
                row.ApplyResult = error;
                if (error.Contains("already", StringComparison.OrdinalIgnoreCase))
                {
                    row.Status = LyricsStatus.AlreadyHas;
                    row.CanSelect = false;
                    row.Selected = false;
                }

                continue;
            }

            row.ApplyResult = "Wrote " + Path.GetFileName(writtenPath) + ".";
            written.Add(row);
        }

        var groups = written.GroupBy(row => string.IsNullOrEmpty(row.AlbumKey) ? "track:" + row.RatingKey : "album:" + row.AlbumKey).ToList();
        var groupIndex = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groupIndex++;
            var rows = group.ToList();
            var albumRefresh = group.Key.StartsWith("album:", StringComparison.Ordinal);
            var refreshKey = albumRefresh ? rows[0].AlbumKey : rows[0].RatingKey;
            progress?.Report(new LyricsProgress("Refreshing Plex", groupIndex, groups.Count));
            try
            {
                await plex.RefreshAsync(baseUri, token, refreshKey, cancellationToken);
            }
            catch (Plex.PlexRequestException exception)
            {
                foreach (var row in rows)
                    row.ApplyResult += " The metadata refresh did not complete. " + exception.Message;
                continue;
            }

            await timing.Delay(timing.FirstWait, cancellationToken);
            await RecheckAsync(baseUri, token, rows, cancellationToken);
            var missing = rows.Where(row => row.PlexShowsLyrics != true).ToList();
            if (missing.Count == 0)
            {
                Describe(rows);
                continue;
            }

            if (albumRefresh)
            {
                foreach (var row in missing)
                {
                    try
                    {
                        await plex.RefreshAsync(baseUri, token, row.RatingKey, cancellationToken);
                    }
                    catch (Plex.PlexRequestException exception)
                    {
                        row.ApplyResult += " The track refresh did not complete. " + exception.Message;
                    }
                }
            }

            await timing.Delay(timing.SecondWait, cancellationToken);
            await RecheckAsync(baseUri, token, missing, cancellationToken);
            Describe(rows);
        }
    }

    public async Task<string> RefreshAlbumAsync(
        Uri baseUri,
        string token,
        string albumKey,
        IReadOnlyList<string> trackKeys,
        CancellationToken cancellationToken)
    {
        await plex.RefreshAsync(baseUri, token, albumKey, cancellationToken);
        await timing.Delay(timing.FirstWait, cancellationToken);
        var shown = 0;
        var local = 0;
        var checkedTracks = 0;
        foreach (var trackKey in trackKeys.Where(key => key.All(char.IsDigit)).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlexCatalogItem? track;
            try
            {
                track = await plex.GetTrackAsync(baseUri, token, trackKey, cancellationToken);
            }
            catch (Plex.PlexRequestException)
            {
                continue;
            }

            checkedTracks++;
            if (track?.HasLyricsStream == true)
                shown++;
            if (track?.HasLocalLyrics == true)
                local++;
        }

        if (checkedTracks == 0)
            return "Album refresh sent.";
        return shown + " of " + checkedTracks + " tracks show a lyrics stream. " + local + " of those are a local sidecar.";
    }

    private static void Classify(LyricsTrackRow row, PlexCatalogItem track, IReadOnlyList<LyricsPathMap> maps, Func<string, bool> canWrite, List<LyricsTrackRow> lookups)
    {
        if (string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(row.Artist))
        {
            row.Status = LyricsStatus.NotFound;
            row.Detail = "The track has no artist or title to search with.";
            return;
        }

        if (track.HasLyricsStream)
        {
            row.Status = LyricsStatus.AlreadyHas;
            row.Detail = track.HasLocalLyrics
                ? "Plex already has a local lyrics stream."
                : "Plex already has a lyrics stream.";
            return;
        }

        if (!LyricsPaths.TryMap(track.FilePath, maps, out var containerPath, out var mapError))
        {
            row.Status = LyricsStatus.NotReachable;
            row.Detail = mapError;
            return;
        }

        row.ContainerPath = containerPath;
        if (string.IsNullOrEmpty(row.FileName))
            row.FileName = LyricsPaths.FileNameOf(containerPath);
        if (!File.Exists(containerPath))
        {
            row.Status = LyricsStatus.NotReachable;
            row.Detail = "The audio file is not at the mapped path.";
            return;
        }

        if (!LyricsSidecar.IsAudioFile(containerPath))
        {
            row.Status = LyricsStatus.NotReachable;
            row.Detail = "The mapped file is not an audio track.";
            return;
        }

        if (LyricsSidecar.HasExisting(containerPath))
        {
            row.Status = LyricsStatus.AlreadyHas;
            row.Detail = "A .lrc or .txt file is already next to this track.";
            return;
        }

        var directory = Path.GetDirectoryName(containerPath);
        if (string.IsNullOrEmpty(directory) || !canWrite(directory))
        {
            row.Status = LyricsStatus.NotWritable;
            row.Detail = "The folder is not writable.";
            return;
        }

        lookups.Add(row);
    }

    private static void ApplyLookup(LyricsTrackRow row, LyricsLookup lookup)
    {
        row.Detail = lookup.Detail;
        switch (lookup.Kind)
        {
            case LyricsLookupKind.Synced:
                row.Status = LyricsStatus.Synced;
                row.Synced = true;
                row.LyricsBody = lookup.Body;
                row.CanSelect = true;
                row.Selected = true;
                break;
            case LyricsLookupKind.Plain:
                row.Status = LyricsStatus.Plain;
                row.Synced = false;
                row.LyricsBody = lookup.Body;
                row.CanSelect = true;
                row.Selected = true;
                break;
            case LyricsLookupKind.Instrumental:
                row.Status = LyricsStatus.Instrumental;
                break;
            case LyricsLookupKind.Failed:
                row.Status = LyricsStatus.LookupFailed;
                break;
            default:
                row.Status = LyricsStatus.NotFound;
                break;
        }
    }

    private async Task RecheckAsync(Uri baseUri, string token, IReadOnlyList<LyricsTrackRow> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            PlexCatalogItem? track;
            try
            {
                track = await plex.GetTrackAsync(baseUri, token, row.RatingKey, cancellationToken);
            }
            catch (Plex.PlexRequestException)
            {
                continue;
            }

            row.PlexShowsLyrics = track?.HasLyricsStream == true;
            row.PlexShowsLocalLyrics = track?.HasLocalLyrics == true;
        }
    }

    private static void Describe(IReadOnlyList<LyricsTrackRow> rows)
    {
        foreach (var row in rows)
        {
            var wrote = row.ApplyResult ?? "Wrote the lyrics file.";
            var sentence = wrote.TrimEnd();
            if (!sentence.EndsWith('.'))
                sentence += ".";
            if (row.PlexShowsLocalLyrics == true)
                row.ApplyResult = sentence + " Plex shows a local lyrics stream.";
            else if (row.PlexShowsLyrics == true)
                row.ApplyResult = sentence + " Plex shows a lyrics stream.";
            else if (row.PlexShowsLyrics == false)
                row.ApplyResult = sentence + " The album was refreshed. A lyrics stream is not on the track yet.";
            else
                row.ApplyResult = sentence + " The track was not re-read after the refresh.";
        }
    }
}
