# WeTrakr Scrobbler for Jellyfin

Dedicated Jellyfin plugin that scrobbles your playback activity to [WeTrakr](https://wetrakr.com).

Sends `PlaybackStart`, `PlaybackProgress` (periodic), `PlaybackPause`, `PlaybackUnpause`, and `PlaybackStop` events with provider IDs (IMDb, TMDB, TVDB) for both movies and episodes.

## Install

1. Open Jellyfin → **Dashboard → Plugins → Repositories → Add Repository**.
2. Name: `WeTrakr`. URL: `https://wetrakr.github.io/wetrakr-jellyfin/manifest.json`.
3. Go to **Catalog**, find **WeTrakr Scrobbler**, install. Restart Jellyfin when prompted.
4. Open **Dashboard → My Plugins → WeTrakr Scrobbler**, click **Connect**.
5. A short code is shown. Open `https://wetrakr.com/activate?platform=jellyfin`, paste the code, confirm.
6. The plugin page flips to **Connected**. Done.

## Uninstall

Dashboard → My Plugins → WeTrakr Scrobbler → Uninstall → restart.

## Development

Requires .NET 8 SDK.

```bash
# Build
dotnet build

# Publish plugin DLL
dotnet publish Jellyfin.Plugin.WeTrakr/Jellyfin.Plugin.WeTrakr.csproj -c Release -f net8.0 -o publish

# Sideload for local testing (Docker)
docker run -d --name jf -p 8096:8096 \
  -v $(pwd)/publish:/config/plugins/WeTrakr_0.1.0 \
  jellyfin/jellyfin:10.9.11
```

Browse to `http://localhost:8096`, run through the first-time setup, then Dashboard → My Plugins → WeTrakr Scrobbler.

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
