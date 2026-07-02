# Game Helper

Watches a game from four angles and dispatches structured events. Generic infrastructure lives in
**`GameHelper.Core`**; each supported game is a thin plug-in on top — this repo ships a Call of
Duty: Warzone profile in **`WarzoneHelper.Game`**. A new game = one `IGameProfile` implementation.

**Architecture (agent + UI):** a standalone **`GameHelper.Console.exe`** runs in the background as
an **elevated scheduled task** — it does all the work, including the ETW UDP game-server detection
that *requires admin*, and hosts a **WebSocket server** on `ws://127.0.0.1:17999`. The **Overwolf
app is a pure WebSocket client** with no native code: it connects to the agent, renders the event
stream, and relays Overwolf GEP hints back over the same socket. This sidesteps the fact that
Overwolf itself runs unelevated (so an in-process plugin could never start the ETW trace).

> A native Overwolf plugin (`GameHelper.Plugin`) that hosts Core in-process still exists in the
> repo as an optional/legacy path, but the app no longer loads it.

## What it does

| Subsystem | Source | Events |
|-----------|--------|--------|
| **Log / cache watch** | `FileSystemWatcher` over Warzone log & cache dirs, with line-tailing | `LOG_FILE_CHANGED`, `CACHE_CHANGED` |
| **Network** | Per-process TCP table (IP Helper API) + **ETW** UDP packets for `cod.exe`, classified into game servers vs. backend services, enriched with local **MaxMind** geo + ICMP ping + a **VPN/proxy heuristic** | `GAME_SERVER_CONNECTED/DISCONNECTED`, `SERVICE_CONNECTED/DISCONNECTED`, `GAME_PROCESS_STARTED/STOPPED` |
| **Status API** | Polls Activision's public status endpoint (same one the `codstatus` Discord cog uses) and diffs it | `GAME_STATUS_CHANGED` |
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
    GameHelper.Core/         # generic infra: monitors, geo, net, OCR framework, event bus, orchestrator, IGameProfile
    WarzoneHelper.Game/      # Warzone plug-in: analyzer, parsers, roster, OCR fields, WarzoneProfile/Config
    GameHelper.Console/      # standalone EXE host -> ConsoleRunner.Run(new WarzoneProfile(), args)
    GameHelper.Plugin/       # Overwolf extra-object bridge (overwolf.plugins.gamehelper.GameHelperPlugin)
    GameHelper.RegionEditor/ # WPF overlay for tuning the Warzone screen regions
  app/                       # the Overwolf WebApp (manifest + windows + js), plugin staged into app/plugins
  build.ps1
```

## Build

Requires the .NET SDK (builds the `net48` / x64 targets via `Microsoft.NETFramework.ReferenceAssemblies`).

```powershell
./build.ps1
```

This builds everything and copies `GameHelper.Plugin.dll` + its dependencies (Newtonsoft,
MaxMind.Db, TraceEvent, Tesseract + native libs) into `app/plugins`, unblocking them so Overwolf
will load them.

## Running it

### 1. Start the agent (elevated, background)

The agent must run **elevated** for ETW UDP detection. Register it once as a scheduled task with
highest privileges, then it starts prompt-free (a desktop shortcut / `Start-ScheduledTask` triggers it):

```powershell
$exe = "$PWD\src\GameHelper.Console\bin\Release\net48\GameHelper.Console.exe"
$action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName "GameHelper-Console" -Action $action -Principal $principal -Force  # once, elevated
Start-ScheduledTask   -TaskName "GameHelper-Console"
```

The agent hosts the WebSocket server on `ws://127.0.0.1:17999` and writes a durable event log to
`%TEMP%\GameHelper\events_{unixtime}.ndjson` (see below). Without elevation everything still
works except UDP game-server IPs.

### 2. Load the Overwolf app (UI only)

Overwolf → **Settings → Support → Development → Load unpacked extension** → pick `wz-helper/app`.
Launch **Game Helper**: the background window connects to the agent over WebSocket, opens the
desktop dashboard, and relays GEP hints back. If the agent isn't running the header shows
"agent offline" and the app auto-reconnects every few seconds. **Overwolf does not need to be
elevated** — the agent already is.

### WebSocket protocol

Text frames, one JSON object each. Configure via `EnableWebSocket` / `WebSocketHost` / `WebSocketPort`.

| Direction | Message |
|-----------|---------|
| agent → client | the `HelperEvent` JSON, verbatim |
| client → agent | `{"type":"hello"}` → agent replies with a backlog of recent events |
| client → agent | `{"type":"gep","name":"match_start","data":"..."}` → relayed into the stream as a hint |

## Run standalone (no Overwolf)

```powershell
# elevated for ETW UDP detection
./src/GameHelper.Console/bin/Release/net48/GameHelper.Console.exe
./GameHelper.Console.exe --write-default-config config.json   # emit a full config to edit
./GameHelper.Console.exe --config config.json                 # one JSON event per line on stdout
```

The console prints one JSON event object per line to **stdout** and logs to **stderr**, so it pipes
cleanly into other tools. It **also** appends events to a durable log file by default (important
when run headless as a scheduled task, where no console is attached):

- Events → `%TEMP%\GameHelper\events_{unixtime}.ndjson` (newline-delimited JSON; a fresh file per run)
- Diagnostics → sibling `events_{unixtime}.log`
- Files rotate to `…_{unixtime}.1.ndjson` past `LogRotateMB` (default 20 MB).
- Path supports the `{unixtime}` and `{pid}` tokens.

Override with `--logfile <path>`, disable with `--no-logfile`; or set `EventLogFile` / `DiagLogFile`
/ `LogRotateMB` in config. `--quiet` silences stderr `[log]` lines but keeps writing them to the file.
The agent GDI-captures the game window itself (`SelfCapture=true`). The same logic is reachable as
`GameHelper.Core.ConsoleRunner.Run(IGameProfile, string[])` for a generic managed DLL runner.

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
