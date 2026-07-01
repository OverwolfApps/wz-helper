# Warzone Helper

An Overwolf app + native .NET plugin that watches Call of Duty: Warzone from four angles and
dispatches structured events. Everything lives in a shared **`WarzoneHelper.Core`** library; the
Overwolf plugin and a standalone console runner are thin hosts over it.

## What it does

| Subsystem | Source | Events |
|-----------|--------|--------|
| **Log / cache watch** | `FileSystemWatcher` over Warzone log & cache dirs, with line-tailing | `LOG_FILE_CHANGED`, `CACHE_CHANGED` |
| **Network** | Per-process TCP table (IP Helper API) + **ETW** UDP packets for `cod.exe`, classified into game servers vs. backend services, enriched with local **MaxMind** geo + ICMP ping + a **VPN/proxy heuristic** | `GAME_SERVER_CONNECTED/DISCONNECTED`, `SERVICE_CONNECTED/DISCONNECTED`, `COD_PROCESS_STARTED/STOPPED` |
| **Status API** | Polls Activision's public status endpoint (same one the `codstatus` Discord cog uses) and diffs it | `COD_STATUS_CHANGED` |
| **Screen CV** | 1 fps frame capture → HUD region analysis (health fill, death/deploy banners) + Tesseract OCR for the lobby ID and the in-game **chat** (right-middle region) | `HEALTH_CHANGED`, `PLAYER_DEAD`, `DEPLOYED`, `LOBBY_ID_CHANGED`, `CHAT_MESSAGE` |
| **GEP (hints only)** | Overwolf Game Events for game **27860** | `MATCH_STARTED`, `MATCH_ENDED`, `SCENE_CHANGED`, `MODE_CHANGED` |

> **GEP is treated as an unreliable hint, never a dependency.** Our own network + CV events fire
> regardless of whether Overwolf's game events do. GEP events are tagged `"source":"gep"` so
> consumers can weight them accordingly.

### Which connection is the *actual* game server?

Warzone gameplay runs over **UDP** (CoD PC ports: 3074-3079, 3478, 4379-4380, 27000-27031). The
Windows UDP owner table does **not** expose remote peers (TCPView shows `*`), so the real
server IP is learned from an **ETW kernel network trace** of `cod.exe`'s UDP packets. A peer is
only reported as `GAME_SERVER_CONNECTED` once it persists across several polls with sustained
bidirectional traffic on a game port — matchmaking probes and one-shot packets are filtered out.
All the TCP endpoints (Demonware / Akamai / AWS / CDN) are reported as `SERVICE_*` instead.

**ETW requires elevation.** Run the console host (or Overwolf) **as administrator** to enable UDP
game-server detection. Without admin it degrades gracefully: TCP services, logs, status and CV
still work; only UDP game-server IPs are unavailable.

## Layout

```
wz-helper/
  src/
    WarzoneHelper.Core/      # all logic: monitors, geo, net, screen, event bus, orchestrator
    WarzoneHelper.Plugin/    # Overwolf extra-object bridge (overwolf.plugins.warzonehelper.WarzoneHelperPlugin)
    WarzoneHelper.Console/   # standalone EXE host -> ConsoleRunner.Run() in Core
  app/                       # the Overwolf WebApp (manifest + windows + js), plugin staged into app/plugins
  build.ps1
```

## Build

Requires the .NET SDK (builds the `net48` / x64 targets via `Microsoft.NETFramework.ReferenceAssemblies`).

```powershell
./build.ps1
```

This builds everything and copies `WarzoneHelper.Plugin.dll` + its dependencies (Newtonsoft,
MaxMind.Db, TraceEvent, Tesseract + native libs) into `app/plugins`, unblocking them so Overwolf
will load them.

## Run in Overwolf

1. Overwolf → **Settings → Support → Development → Load unpacked extension** → pick `wz-helper/app`.
2. Launch **Warzone Helper**. The background window loads the plugin, starts all monitors, wires
   GEP, and opens the desktop dashboard. In fullscreen the app pushes Overwolf in-memory
   screenshots (`overwolf.media.getScreenshotUrl`) into the plugin for CV — this works in
   exclusive fullscreen where a raw GDI grab would be black.
3. For UDP game-server detection, run Overwolf as administrator.

Plugin config is `app/plugins/config.json` (loaded from beside the DLL). It ships with
`SelfCapture=false` because the app supplies frames.

## Run standalone (no Overwolf)

```powershell
# elevated for ETW UDP detection
./src/WarzoneHelper.Console/bin/Release/net48/WarzoneHelper.Console.exe
./WarzoneHelper.Console.exe --write-default-config config.json   # emit a full config to edit
./WarzoneHelper.Console.exe --config config.json                 # one JSON event per line on stdout
```

The console prints one JSON event object per line to **stdout** and logs to **stderr**, so it pipes
cleanly into other tools. `SelfCapture` defaults to `true` here (the plugin GDI-captures the game
window itself). The same logic is reachable as `WarzoneHelper.Core.ConsoleRunner.Run(string[])` for
a generic managed DLL runner.

## Event shape

```json
{ "name": "GAME_SERVER_CONNECTED", "source": "network", "timestamp": "2026-07-01T05:36:00Z",
  "data": { "ip": "185.34.106.103", "port": 3074, "protocol": "UDP", "isGameServer": true,
            "pingMs": 23, "distanceKm": 480, "isLikelyVPN": false, "vpnReason": null,
            "countryIso": "DE", "countryName": "Germany", "city": "Frankfurt",
            "latitude": 50.1, "longitude": 8.6, "asn": 57976, "asnOrg": "Blizzard Entertainment" } }
```

### VPN / proxy heuristic

Game-server events carry **`isLikelyVPN`**. It's set when the server's `pingMs` is at/above
`VpnPingThresholdMs` (default 130 ms) **or** its great-circle `distanceKm` from your location is
at/above `VpnDistanceKmThreshold` (default 4000 km); `vpnReason` says which (`"ping"`,
`"distance"`, or `"ping+distance"`). Your home location comes from `HomeLatitude`/`HomeLongitude`
if set, otherwise it's resolved once from your public IP against the local GeoLite2 City db
(`AutoResolveHome`). Distance-based flagging is skipped if home can't be resolved.

## Configuration highlights (`config.json`)

- `GameProcessNames` – process names to track (default `cod`).
- `WatchPaths` / `WatchFilters` – log/cache dirs and file globs (env vars expanded).
- `GameUdpPorts` / `GameUdpPortRange*` – ports treated as gameplay traffic.
- `GameServerConfirmPolls` / `GameServerDropPolls` – hysteresis for server connect/disconnect.
- `VpnPingThresholdMs` / `VpnDistanceKmThreshold` / `HomeLatitude` / `HomeLongitude` /
  `AutoResolveHome` / `PublicIpUrl` – the `isLikelyVPN` heuristic (see above).
- `Regions` – normalized (0..1) HUD regions for the CV analyzer, including `Chat` (the in-game chat
  region, right-middle skewed toward the top). **Tune these against your own captures in
  `.references/Screenshots/`** for your resolution/HUD scale.
- `GeoDbDir` / `AutoDownloadGeoDb` – MaxMind GeoLite2 dir; auto-downloads Country/City/ASN mmdb
  from the community mirror (same approach as the `universal-lookup` project) when missing.
- `TesseractDataDir` – tessdata dir; `eng.traineddata` auto-downloads on first use.

## Tuning the CV

The health/death/deploy heuristics and lobby-ID region in `HelperConfig.ScreenRegions` are seeded
for 16:9 at native resolution. Capture reference frames (the app can push them, or use the console
in `SelfCapture` mode), overlay the region rectangles, and adjust the normalized coordinates +
thresholds in `WarzoneScreenAnalyzer` until events land cleanly.
