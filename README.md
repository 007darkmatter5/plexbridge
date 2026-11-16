# PlexBridge

One-way companion for two Plex Media Servers. Preview watched status, stop time, ratings, and regular playlists, then apply the changes you select. Nothing is written until you confirm.

Blazor Server (.NET 10) + MudBlazor + SQLite. One admin account. One container.

## What this version does

- First account becomes the admin. Sign-ups close after that.
- Connect the Plex account with Plex's PIN link. The admin opens `https://app.plex.tv/auth`. PlexBridge polls `https://plex.tv/api/v2/pins` on the server. It does not ask for the Plex password, and it does not put the account token in a browser URL.
- Owned servers come from `GET https://plex.tv/api/v2/resources`. That list is optional. Every address Plex returns stays visible, and each check ends as verified, failed, or canceled. A row does not stay on Testing. Saving one of those addresses is a separate step and does not run on its own.
- Two server slots. Each one stores a display name, a server root URL, and an encrypted token. Startup and account discovery leave a saved URL, its encrypted token, and its slot as they are. A hostname without a scheme gets `https://` only when you type a new address. A missing port becomes `32400`, the Plex Media Server port. An address typed as `https://host` with no port used to stay on 443, so it never reached a normal server. Typing `:443` keeps 443 for a reverse proxy. An address that already includes its port, including a Tailscale `http://100.x.x.x:32400`, is stored exactly.
- Test connection reads server identity only.
- Refresh inventory reads library sections and item counts. Titles in a preview are read live and are not stored.
- Migrate copies watched status, stop time, ratings, and regular playlists from one saved server to the other. A Plex user picker lists the Plex Home users for the connected admin account, including the admin. A PIN-protected user asks for the PIN in a dialog; the PIN is not saved or logged. That user's own server tokens are used for the preview and the apply. The page says who the migration is for, and each Plex user keeps a separate plan. Preview is one three-column table, Source, Destination, and Result. Each library is a header row in that table, and that row is the collapsible toggle. Libraries start collapsed, including while a scan is running and after it finishes. A collapsed header result is one status: scanning…, matching, missing on destination, only on destination, or Library exists on both · mismatches. Matching can add a muted note such as · 5 skipped, · 3 only on destination, or · 2 only on source. A row that is not on the destination says not on destination (file not present, nothing to sync) and stays out of Mismatches. A matched row says how, such as matched by tvdb or matched by season/episode. A missing row can open Why not matched? to show its ids and the closest episodes of that show. Leaf reads request includeGuids=1. A header says mismatches only when a row in that library still has Make match. Opening a library shows its items, indented, with a short result such as watch progress mismatch, rating mismatch, watched + progress mismatch, matching, missing on destination, or only on destination. Each library is paged on its own. A side with no item says not on source or not on destination. Each item shows its title, library, type, and file name. The file name is display only and is never used to match. A row that differs opens onto watched status, viewing progress, rating, and playlist membership, and Make match adds only that change. Make all match for one library sits on that header row. A playlist row opens onto its items: title, artist and album, and matched on destination or not found. Make match creates that playlist and adds the matched items in source order. An existing playlist can add one matched item at a time and does not remove items that are only on the destination. A row that is only on the destination, or a library item that is missing there, says no action (one-way). Nothing is selected until you choose it. Show all or Mismatches only is remembered for the signed-in admin. The Mismatches filter uses that same rule: a row counts only when it still has Make match. Skipped, one-way, and destination-only rows stay out of that filter and do not make the header say mismatches. A skipped playlist says why, for example skipped · smart playlist (Plex builds it automatically). A result filter and title search work during the run. It can be canceled, and reopening the page shows the rows already found. It reads movies, episodes, and tracks, not every season, show, artist, or album. Apply runs in the background. It re-reads only the selected items, writes those changes, then re-reads the same destination items, or the destination playlist, and marks each one verified or not reflected. It writes only after the admin confirms that plan. A log file is written next to the data folder (`logs/plexbridge.log`) without tokens. Matches use IMDb, TMDB, TVDB, MusicBrainz, and Plex content ids (`plex://movie`, `plex://show`, `plex://episode`, and the other content prefixes). A file path or a ratingKey is not a match. Episodes match through the show id plus season and episode number when the episode itself has no id. Source values overwrite destination values. Plex cannot restore the original watch date. Smart playlists, collections, and server settings are not applied.
- Clear errors for timeouts, connection failures, rejected tokens, and bad URLs. Upstream response bodies are not shown. The migration audit stores counts and titles, not tokens or server URLs.
- Watchdog watches the slots you enable for streams that lock up, records each stall, and groups repeats into suggestions. Recovery is report-only until you change that. See [Watchdog](#watchdog).
- Lyrics looks up a music library on LRCLIB and can save a sidecar next to each track after you confirm. Synced lyrics are a `.lrc` file. Plain lyrics are a `.txt` file. An existing lyrics file, or a lyrics stream Plex already has, is left alone. See [Lyrics](#lyrics).

## What this version does not do

- No matching across servers. A later milestone can match on IMDb, TMDB, or TVDB. A Plex `ratingKey` or a filesystem path is not an identity across servers.
- No two-way sync, no original watch dates, and no copy of collections or server settings. A confirmed migration can mark items watched, set a stop time, set a rating, and add matched items to a regular playlist.
- No password sign-in to Plex, and no library scan. The lyrics page refreshes one album after it writes sidecars, and refreshes a track only when that album refresh does not add a lyrics stream. The watchdog can end a stuck playback session only when that server is set to Auto, or when you press Do it now. It does not change quality, subtitles, libraries, or server settings. The only Plex account change is creating the PIN used to authorize this app.
- TLS certificate validation is left on. There is no switch to skip it.

## Tokens

Use **Plex account** to authorize on Plex's site, or paste an existing X-Plex-Token on the dashboard. PlexBridge does not ask for the Plex account password. Plex's own note on where a token appears is [Finding an authentication token](https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/). The PIN flow Plex documents for apps is [Authenticating with Plex](https://forums.plex.tv/t/authenticating-with-plex/609370).

The token is encrypted with ASP.NET Core Data Protection before it is written to SQLite. The keys live in the `keys` folder, separate from the database. The token is not shown again, is not written to logs, and is sent only as the `X-Plex-Token` header to the server you configured. Leave the token field blank when you edit a server if you want to keep the saved one.

If the keys folder is lost, saved tokens cannot be decrypted. Enter them again. If the admin password is lost, stop PlexBridge and delete `plexbridge.db` (the encrypted tokens go with it; `keys` can stay), then create the admin account and enter the tokens again.

## Watchdog

The Watchdog page watches the server slots you turn on. It stays off until you enable a slot. A desktop Plex Media Server — Windows, an RTX 3080 Ti for hardware transcode, media on NAS shares — is the usual target, and in a two-server install that is slot 2. Any saved slot can be watched.

It polls `GET /status/sessions` (5 seconds unless you change it). If notifications are on, it also opens `/:/websockets/notifications`. A playing or transcode event wakes the next poll. The token is sent only as the `X-Plex-Token` header. If the socket cannot be opened, polling continues and the page says so.

Each poll keeps the play state, how far the view offset moved, and the transcode session: speed, whether it is throttled, progress, video and audio decision, hardware requested, full hardware pipeline, container, codecs, and resolution. Subtitle burn-in is recorded on its own.

A stall is recorded when one of these lasts longer than its threshold. Paused time does not count.

| Rule | Default | What it means |
| --- | --- | --- |
| View offset frozen | 20 seconds | The offset does not move while the player says it is playing. Time spent buffering counts toward this window, but a buffer is reported as long buffering instead. |
| Transcode below 1× | 20 seconds | Speed stays under 1.0 while the transcode is not throttled and its progress is not ahead of playback. A finished transcode (about 97% or more) is not treated as slow. |
| Transcode disappeared | 15 seconds | A transcode session was there and is gone, the play session is still playing or buffering, and the offset is not moving. If the offset keeps moving, that is a handoff, not a stall. |
| Long buffering | 30 seconds | The player stays on buffering. A shorter buffer that clears is not an incident. |

A pause is not a stall. A throttled transcode that is ahead of the viewer is not a stall.

Each incident stores the time, server, account title, player product, platform and device, title and GUIDs, file path and part, container, video and audio codecs, resolution, bitrate, direct play / direct stream / transcode, hardware and full hardware pipeline, subtitle burn-in, which rule fired, a short timeline of the samples around the stall, and how it ended: recovered on its own, the user stopped, still stuck, or ended by the watchdog. Tokens are not stored and are not written to the log.

Recovery is built and does nothing by itself until you opt in. Each watched server has a mode:

| Mode | Behavior |
| --- | --- |
| Off | Record the incident only. |
| Report-only | The default. The incident shows the action that would have been taken. **Do it now** asks you to confirm before anything is sent. |
| Auto | PlexBridge performs that action when the incident opens. |

The action tells the client why playback ended, with `GET /status/sessions/terminate?sessionId=…&reason=…`, so it can start again. If a transcode session is still running, PlexBridge first calls `GET /video/:/transcode/universal/stop?session=…`. This version does not change Plex quality, subtitles, libraries, or server settings.

Patterns group the incidents by file, client, codec and resolution, subtitle burn-in, transcode versus direct play, and hardware versus software. When the same situation repeats, the page says so in plain language. A file that stalls while burning PGS subtitles gets a suggestion to make an Optimized Version or use SRT. A client that stalls on HEVC 4K transcodes gets a suggestion to check its quality setting. Those lines are advice only.

Log tailing is optional. Set a folder that contains `Plex Media Server.log` and the `Plex Transcoder*.log` files. A mapped copy of `%LOCALAPPDATA%\Plex Media Server\Logs` is the usual Windows path. Error and warning lines near the incident are attached to it. If the folder is blank, missing, or unreadable, the incident is still recorded. The Docker install on Unraid does not have this path unless you mount it and type that path on the page. The container `TZ` should match the Plex server, because those log timestamps are local.

The new tables are an EF Core migration applied on startup, the same way as the server and migration tables. Saved servers, encrypted tokens, and the keys folder are left as they are.

## Lyrics

The Lyrics page reads one music library on a saved server, or one artist or album inside it. It asks [LRCLIB](https://lrclib.net) for a match on artist, title, album, and duration, and prefers time-synced lyrics. Nothing is written until you choose the rows and confirm. The background write then asks Plex to refresh those albums so Plex and Plexamp can show the lyrics without a library scan.

Plex's own note is [Adding Local Lyrics and Artwork for Music files](https://support.plex.tv/articles/215916117-adding-local-lyrics/). For the default Plex Music agent, a sidecar has to sit in the same folder as the audio and use the same file name:

| Lyrics | File | Example |
| --- | --- | --- |
| Timed | `.lrc` | `01-Come With Me Tonight.flac` and `01-Come With Me Tonight.lrc` |
| Plain | `.txt` | `02-Medicine.mp3` and `02-Medicine.txt` |

Both files are UTF-8. Plex staff say to refresh the album's metadata after the files are in place. PlexBridge does that with `PUT /library/metadata/{albumRatingKey}/refresh`, then reads the track again. A lyrics stream is `streamType` 4. When the provider is `com.plexapp.agents.localmedia`, the stream is the sidecar. If that stream is still missing, PlexBridge refreshes that track the same way and reads it once more. It does not start a library scan.

Plex reports the path it sees, which is not the path inside this container. Each server has its own list of prefixes, for example `/mnt/user/media/music` to `/music`, or `D:\Music` to `/music`. The longest matching prefix is used. The page says when a file is not at the mapped path or the folder is not writable. Details, the LRCLIB matching rules, and the optional new-track check are in [docs/lyrics.md](docs/lyrics.md).

The check for newly added tracks is off until you turn it on. Turning it on does not walk the library you already have. It also never replaces a `.lrc`, a `.txt`, or a lyrics stream that is already there.

## Running from source

Requires the .NET 10 SDK (this tree was built with SDK 10.0.403 / runtime 10.0.14).

```bash
dotnet run --project src/PlexBridge
```

Open `http://127.0.0.1:43123`, create the admin account, then add a server. The database is created on startup at `src/PlexBridge/Data/plexbridge.db`. Keys are created in `src/PlexBridge/keys`.

```bash
dotnet test
```

Tests use fake HTTP responses and a temporary database. They do not contact a Plex server.

## Container

The image listens on port 8080 and keeps everything under `/config`.

| Path | Contents |
| --- | --- |
| `/config/plexbridge.db` | SQLite database |
| `/config/keys` | Data Protection keys for Plex tokens and login cookies. Back these up with the database. |
| `/config/logs/plexbridge.log` | Log file. Tokens are not written here. |

An empty `/config` is a valid first start. The container applies the EF Core migrations, creates the keys, and then the register page is the first screen. The first account is the admin. Sign-ups close after that.

The image default user is `1654:1654`. Set `PUID` and `PGID` to run as someone else. The entrypoint starts as root only long enough to give `/config` to that user, then drops privileges. It refuses to run as root.

```bash
docker build --build-arg VERSION=0.1.0-local -t plexbridge .
docker run -d --name plexbridge -p 8080:8080 \
  -e PUID=99 -e PGID=100 -e TZ=Etc/UTC \
  -v /srv/plexbridge:/config \
  plexbridge
```

Open `http://127.0.0.1:8080` and create the admin account.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__DefaultConnection` | `Data Source=/config/plexbridge.db` | SQLite file. |
| `DataProtection__KeysPath` | `/config/keys` | Key ring. |
| `PUID` / `PGID` | `1654` | User and group PlexBridge runs as. Unraid uses 99/100. |
| `TZ` | `Etc/UTC` | Container timezone. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | unset | Set to `true` behind a reverse proxy that terminates HTTPS. The proxy must allow WebSockets. |

`GET /healthz` returns `Healthy` or `Unhealthy`.

## Unraid

Community Apps template: [templates/plexbridge.xml](https://github.com/007darkmatter5/unraid-templates/blob/main/templates/plexbridge.xml) in [007darkmatter5/unraid-templates](https://github.com/007darkmatter5/unraid-templates). A compose example is [`deploy/unraid/docker-compose.yml`](deploy/unraid/docker-compose.yml).

- Image `ghcr.io/007darkmatter5/plexbridge:latest`
- Network bridge, WebUI `http://[IP]:[PORT:8080]/`, host port 8080
- `/mnt/user/appdata/plexbridge` mounted at `/config`
- Optional music folder mounted read-write at `/music`, used only when you download lyrics
- `PUID` 99, `PGID` 100, plus `TZ`

If the package is still private, Unraid cannot pull it. GitHub → your profile → Packages → plexbridge → Package settings → Change visibility → Public.

## Release workflow

`.github/workflows/release.yml` runs the tests, smoke-tests the image, then publishes only when `github.repository` is `007darkmatter5/plexbridge`.

| When | Image tags |
| --- | --- |
| Push to `main` | `latest` and `sha-<12-character commit>` |
| Tag `v*` (for example `v0.1.0`) | that semver (`0.1.0`) |

## How it's built

- `src/PlexBridge/Plex` — URL checks, address checks, the read allowlists, JSON parsing, and the HTTP client
- `src/PlexBridge/Lyrics` — LRCLIB matching, sidecar files, path mapping, and the album refresh
- `src/PlexBridge/Watchdog` — stall detection, session parsing, and the watchdog allowlist
- `src/PlexBridge/Services/PlexServerService.cs` — the only place that stores a token, and only as ciphertext
- `src/PlexBridge/Data` — Identity, the two server tables, the watchdog tables, and the lyrics settings, with EF Core migrations
- `src/PlexBridge/Components` — MudBlazor UI. Account pages are ordinary form posts. Everything else requires a signed-in admin
- `tests/PlexBridge.Tests` — policy, parsing, stall detection, encryption, and the anonymous HTTP surface

Each Plex call is checked against an allowlist before a socket is opened. Inventory reads are `GET /identity`, `GET /`, and the library section reads. Migration adds the preview reads and the watched, progress, rating, and playlist writes. The watchdog adds `GET /status/sessions`, and, only for a recovery action, terminate and transcode stop. Lyrics adds music-library reads and `PUT /library/metadata/{ratingKey}/refresh` for one album or one track. A token in the query string is rejected. A library scan and server-setting changes are not sent.

Add a migration with:

```bash
dotnet ef migrations add <Name> --project src/PlexBridge --output-dir Data/Migrations
```

## License

[MIT](LICENSE)
