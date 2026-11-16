# Lyrics

PlexBridge can find lyrics for music tracks on one saved Plex server and write them next to the audio. The Lyrics page previews every track first. Nothing is written until you confirm the rows. An existing lyrics file is never replaced.

## What Plex reads

Plex documents this for libraries that use the default Plex Music agent: [Adding Local Lyrics and Artwork for Music files](https://support.plex.tv/articles/215916117-adding-local-lyrics/).

A sidecar has to be in the same directory as the track and use the same file name, with only the extension changed. The text is UTF-8.

| Kind | Extension | What PlexBridge writes |
| --- | --- | --- |
| Timed | `.lrc` | LRCLIB `syncedLyrics`, plus `[ar:]`, `[ti:]`, `[al:]`, and `[length:]` when those tags are not already in the text |
| Plain | `.txt` | LRCLIB `plainLyrics` when no synced lyrics exist |

Plex's article says lyrics embedded in the audio tags are not read by that agent. PlexBridge still will not write a sidecar when the track's Plex metadata already has a lyrics stream, so a LyricFind stream or a local stream you already have is left alone.

In the track XML or JSON, a lyrics stream has `streamType` 4. A sidecar Plex has picked up looks like this:

```xml
<Stream streamType="4" codec="lrc" format="lrc" provider="com.plexapp.agents.localmedia" />
```

`com.plexapp.agents.lyricfind` is Plex's own online lyrics provider. PlexBridge treats any `streamType` 4 as "already has lyrics" and does not write over it.

A `.lrc` or `.txt` that is already next to the audio is also left alone, even when Plex has not indexed it yet. PlexBridge will not add the other extension beside it.

Instrumental records from LRCLIB are skipped. No empty file is written.

## Path mapping

Plex returns the path it sees, such as `/mnt/user/media/music/Artist/Album/01.flac` or `D:\Music\Artist\Album\01.flac`. The container has to see that file at a path of its own. On the Lyrics page, each server stores one or more pairs:

| Plex path prefix | Container path |
| --- | --- |
| `/mnt/user/media/music` | `/music` |
| `D:\Music` | `/music` |

The longest prefix that matches on a folder boundary wins. `D:\Music` and `d:\music` match. Linux prefixes are case-sensitive. The mapped path has to stay under the container prefix. Prefixes such as `/config`, `/app`, and `/` are rejected so a mapping cannot point at the database or the app.

The scan says **Path not reachable** when no prefix matches or the audio file is not at the mapped path. It says **Path not writable** when the file is there and the folder cannot be written. Those rows are not selected.

On Unraid, add the optional **Music** path in the template. It mounts the share read-write at `/music`. The container does not change ownership of that share. Plex has to be able to read the new files. Running both containers as `PUID` 99 and `PGID` 100 is the usual way.

The Docker image only prepares `/config`. The music mount is optional and is not required for migration or the watchdog.

## How a match is chosen

LRCLIB's public API needs no key. Each request sends:

```text
PlexBridge/<version> (https://github.com/007darkmatter5/plexbridge)
```

The version is the assembly version. The Plex token is not sent to LRCLIB and is not written to the log.

1. `GET /api/get` with track title, artist, album, and duration in seconds. LRCLIB treats duration as a match within about two seconds. Plex's millisecond duration is rounded to the nearest second.
2. A 404 falls back to `GET /api/search` with title and artist. Album is scored here, not required, except for Various Artists.
3. A hit has to match the title and the primary artist. A trailing `(Remastered)` or a `feat.` credit does not by itself reject a match. When both durations are known, a gap larger than 10 seconds is rejected. Various Artists also has to match the album and stay within 5 seconds.
4. Synced lyrics are preferred when the duration is close. Otherwise the plain text is saved as `.txt`.

Calls are limited to two at a time, with a short pause between them. A 429 is retried once.

## Making Plex show them

Writing the file is not enough. Plex staff say to refresh the album's metadata after the sidecar is in place. PlexBridge does only that, not a library scan:

1. `PUT /library/metadata/{albumRatingKey}/refresh`
2. Wait, then `GET /library/metadata/{trackRatingKey}` and look for a lyrics stream.
3. If the stream is still missing, `PUT /library/metadata/{trackRatingKey}/refresh`, wait, and read the track again.

The row then says whether Plex shows a local lyrics stream, some other lyrics stream, or no lyrics stream yet. A **Refresh album** button on the result list sends the same album refresh again without writing files.

The allowlist accepts that one refresh path and the music reads (`type` 8, 9, or 10). It rejects a section refresh, a library scan, and a token in the query string.

## Newly added tracks

**Check newly added tracks** is off until you save it. The interval is 1 to 168 hours, and the default is 12. Turning it on sets the cursor to that moment, so the library you already have is not backfilled. Later runs read tracks Plex added after the cursor, write only rows that have no lyrics yet, and refresh those albums.

If a lookup fails, or a new track's file is not reachable or not writable, the cursor stays put and the next run tries that window again. Tracks that were written are skipped because the sidecar is now there. Use the Lyrics page when you want to preview the rest of the library.

The setting is stored in `LyricsSettings`. That table is added by an EF Core migration on startup. Saved servers, encrypted tokens, slots, and the keys folder are not rewritten.
