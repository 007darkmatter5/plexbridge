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

## What this version does not do

- No matching across servers. A later milestone can match on IMDb, TMDB, or TVDB. A Plex `ratingKey` or a filesystem path is not an identity across servers.
- No two-way sync, no original watch dates, and no copy of collections or server settings. A confirmed migration can mark items watched, set a stop time, set a rating, and add matched items to a regular playlist.
- No password sign-in to Plex, no library refresh/scan, no metadata refresh, and no playback calls. The only Plex account change is creating the PIN used to authorize this app.
- TLS certificate validation is left on. There is no switch to skip it.

## Tokens

Use **Plex account** to authorize on Plex's site, or paste an existing X-Plex-Token on the dashboard. PlexBridge does not ask for the Plex account password. Plex's own note on where a token appears is [Finding an authentication token](https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/). The PIN flow Plex documents for apps is [Authenticating with Plex](https://forums.plex.tv/t/authenticating-with-plex/609370).

The token is encrypted with ASP.NET Core Data Protection before it is written to SQLite. The keys live in the `keys` folder, separate from the database. The token is not shown again, is not written to logs, and is sent only as the `X-Plex-Token` header to the server you configured. Leave the token field blank when you edit a server if you want to keep the saved one.

If the keys folder is lost, saved tokens cannot be decrypted. Enter them again. If the admin password is lost, stop PlexBridge and delete `plexbridge.db` (the encrypted tokens go with it; `keys` can stay), then create the admin account and enter the tokens again.

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
- `PUID` 99, `PGID` 100, plus `TZ`

If the package is still private, Unraid cannot pull it. GitHub → your profile → Packages → plexbridge → Package settings → Change visibility → Public.

## Release workflow

`.github/workflows/release.yml` runs the tests, smoke-tests the image, then publishes only when `github.repository` is `007darkmatter5/plexbridge`.

| When | Image tags |
| --- | --- |
| Push to `main` | `latest` and `sha-<12-character commit>` |
| Tag `v*` (for example `v0.1.0`) | that semver (`0.1.0`) |

## How it's built

- `src/PlexBridge/Plex` — URL checks, address checks, the read-only request allowlist, JSON parsing, and the HTTP client
- `src/PlexBridge/Services/PlexServerService.cs` — the only place that stores a token, and only as ciphertext
- `src/PlexBridge/Data` — Identity and the two server tables, with EF Core migrations
- `src/PlexBridge/Components` — MudBlazor UI. Account pages are ordinary form posts. Everything else requires a signed-in admin
- `tests/PlexBridge.Tests` — policy, parsing, encryption, and the anonymous HTTP surface

Allowed Plex calls are `GET /identity`, `GET /`, `GET /library/sections`, `GET /library/sections/{id}`, and `GET /library/sections/{id}/all` with paging. Anything else, including refresh, scrobble, rate, progress, and playback, is rejected before a socket is opened.

Add a migration with:

```bash
dotnet ef migrations add <Name> --project src/PlexBridge --output-dir Data/Migrations
```

## License

[MIT](LICENSE)
