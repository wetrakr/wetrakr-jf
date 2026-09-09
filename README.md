# WeTrakr for Jellyfin

Dedicated Jellyfin plugin that scrobbles your playback activity to [WeTrakr](https://wetrakr.com).

Sends `PlaybackStart`, `PlaybackProgress` (periodic), `PlaybackPause`, `PlaybackUnpause`, and `PlaybackStop` events with provider IDs (IMDb, TMDB, TVDB) for both movies and episodes.

## Install

1. Open Jellyfin → **Dashboard → Plugins → Repositories → Add Repository**.
2. Name: `WeTrakr`. URL — pick the one matching your server:

   | Jellyfin server | Repository URL |
   |-----------------|----------------|
   | **12.x** and **10.11.x** | `https://wetrakr.github.io/wetrakr-jf/manifest.json` |
   | **10.10.x** (LTS)        | `https://wetrakr.github.io/wetrakr-jf/lts/manifest.json` |

3. Go to **Catalog**, find **WeTrakr**, install. Restart Jellyfin when prompted.
4. Open **Dashboard → My Plugins → WeTrakr**, click **Connect**.
5. A short code is shown. Open `https://wetrakr.com/activate?platform=jellyfin`, paste the code, confirm.
6. The plugin page flips to **Connected**. Done.

> If you upgrade your Jellyfin server from 10.10 to 10.11, remove the LTS
> repository, add the Stable one, then update the plugin.

## Jellyfin 12

Jellyfin 12 runs on .NET 10, so the plugin ships a separate binary for it.
Both lines are served from the same repository URL: a 10.11 server is offered
the net9 build and a 12 server the net10 one, because Jellyfin only lists the
versions whose `targetAbi` its own version covers. Nothing to re-point after
upgrading the server — just update the plugin from the catalog.

Jellyfin's own 12.0 release notes recommend removing third-party plugins before
migrating and re-adding them once the database migration finishes. WeTrakr
stores nothing in the Jellyfin database (its state is the plugin XML config),
so a removal and re-install only costs you the pairing, which you redo from the
plugin page in a few seconds.

## Uninstall

Dashboard → My Plugins → WeTrakr → Uninstall → restart.

## Development

One target framework per Jellyfin line, same source for all three:

| Target | Jellyfin | `Jellyfin.Controller` | `targetAbi` |
|--------|----------|-----------------------|-------------|
| `net8.0`  | 10.10.x | 10.10.7 | 10.10.7.0 |
| `net9.0`  | 10.11.x | 10.11.8 | 10.11.8.0 |
| `net10.0` | 12.x    | 12.0.0  | 12.0.0.0  |

Requires the matching SDK for whichever target you build (the .NET 10 SDK can
build all three).

```bash
# Build
dotnet build

# Publish plugin DLL (pick the framework for your test server)
dotnet publish Jellyfin.Plugin.WeTrakr/Jellyfin.Plugin.WeTrakr.csproj -c Release -f net10.0 -o publish

# Sideload for local testing (Docker)
docker run -d --name jf -p 8096:8096 \
  -v $(pwd)/publish:/config/plugins/WeTrakr_0.1.0 \
  jellyfin/jellyfin:12.0
```

Browse to `http://localhost:8096`, run through the first-time setup, then Dashboard → My Plugins → WeTrakr.

### Point the plugin at a non-production API

Edit `ApiBaseUrl` in the plugin's configuration XML on disk, or set it from the plugin's config page when the override field lands.

## How it works

- Subscribes to `ISessionManager.PlaybackStart / PlaybackProgress / PlaybackStopped`.
- Derives pause/unpause events from `IsPaused` transitions on progress events (Jellyfin does not fire dedicated pause events).
- Posts a JSON body to `{ApiBaseUrl}/webhooks/jellyfin/{WebhookToken}` — the WeTrakr API already handles this endpoint.
- `WebhookToken` is obtained via the standard WeTrakr device-code OAuth flow (`/oauth/device/code?platform=jellyfin` + `/oauth/device/token`).

## Roadmap

- v2: `ItemMarkedPlayed` (manual watched toggle in Jellyfin).
- v3: `UserDataSaved` (ratings and favorites).
- Optional QR code on the config page during pairing.

## License

MIT. Jellyfin logo is © Jellyfin Project, used under MIT.
