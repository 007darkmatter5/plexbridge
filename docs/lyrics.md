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

Plex returns the path it sees, such as `/mnt/user/media/music/Artist/Album/01.flac` or `M:\Music\Artist\Album\01.flac`. The container has to see that file at a path of its own.

Library paths is the settings page for that mount. The usual folder is `/libraries`: the parent folder that contains every Plex library, mounted read-write. `/music` still works when that is the folder you already mounted. PlexBridge reads every movie, show, and music library from `GET /library/sections` (the `Location` paths) and maps each one. Lyrics uses the same paths.

A library can list more than one folder. For each folder, PlexBridge takes the trailing parts of the Plex path and looks for the longest one that exists under the mount. The match is case-sensitive first, then case-insensitive. It then checks a few real files.

- `/mnt/user/media/music` with the media share mounted at `/libraries` becomes `/libraries/music`.
- `M:\Music` becomes `/libraries/music` when that folder is there, even if the on-disk name is `music`.
- `D:\Media\Music` becomes `/libraries/Media/Music` when that longer path is the one that holds the files.
- A folder mounted directly, such as `/mnt/user/media/music` mounted at `/music`, stays `/music` when the files sit in the mount itself.

It reads a few real items (up to five per folder, and at most three pages) and checks that the files exist. Each library is shown as **OK** or **Not found**. A check that finds nothing does not erase prefixes that were already saved. Checking paths only reads the mount. It does not write lyrics.

When a file is translated, the longest prefix that matches on a folder boundary wins. `M:\Music` and `m:\music` match. Linux prefixes are case-sensitive. The mapped path has to stay under the container prefix. Prefixes such as `/config`, `/app`, and `/` are rejected so a mapping cannot point at the database or the app.

**Advanced** is collapsed on Library paths and on the Lyrics page. Use it only to type prefixes by hand when the library mapping is not the right one. Those prefixes replace the library mapping. Prefixes saved by an older version stay in use until a library check succeeds. The music library dropdown shows the library name.

The scan says **Path not reachable** when no prefix matches or the audio file is not at the mapped path. It says **Path not writable** when the file is there and the folder cannot be written. Those rows are not selected. Writing the lyrics files is the only change, and it waits until you confirm.

On Unraid, add the optional **Libraries** path in the template. Point it at the folder that contains all your Plex media libraries. It mounts that share read-write at `/libraries`. The container does not change ownership of that share. Plex has to be able to read the new files. Running both containers as `PUID` 99 and `PGID` 100 is the usual way.

The Docker image only prepares `/config`. The libraries mount is optional and is not required for migration or the watchdog.

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

The setting is stored in `LyricsSettings`. The shared path maps live in `LibraryMounts`. Both tables are added by EF Core migrations on startup. An older database keeps its saved prefixes, and those prefixes are copied into `LibraryMounts` without rewriting servers, encrypted tokens, slots, or the keys folder.
