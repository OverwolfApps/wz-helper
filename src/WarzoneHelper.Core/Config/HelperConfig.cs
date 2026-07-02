using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace WarzoneHelper.Core.Config
{
    /// <summary>
    /// Runtime configuration. Loaded from JSON (config.json next to the DLL by default),
    /// with environment-variable expansion on all paths. Every value has a working default
    /// so the helper runs with an empty/missing config file.
    /// </summary>
    public sealed class HelperConfig
    {
        // --- Process ---
        public string[] GameProcessNames { get; set; } = { "cod", "cod.exe" };

        // --- Log / cache watching ---
        /// <summary>Directories watched recursively for Warzone log/cache changes.</summary>
        public string[] WatchPaths { get; set; } =
        {
            @"%USERPROFILE%\Documents\Call of Duty",
            @"%LOCALAPPDATA%\Activision",
            @"%PROGRAMDATA%\Battle.net\Logs"
        };
        public string[] WatchFilters { get; set; } = { "*.log", "*.txt", "*.json", "*.ndjson" };
        /// <summary>Debounce for noisy filesystem events, milliseconds.</summary>
        public int WatchDebounceMs { get; set; } = 400;

        // --- Network ---
        public int NetworkPollMs { get; set; } = 1000;
        /// <summary>UDP ports treated as candidate gameplay traffic (CoD PC game ports).</summary>
        public int[] GameUdpPorts { get; set; } =
        {
            3074, 3075, 3076, 3077, 3078, 3079,
            3478, 4379, 4380
        };
        public int[] GameUdpPortRangeStart { get; set; } = { 27000 };
        public int[] GameUdpPortRangeEnd { get; set; } = { 27031 };
        /// <summary>A UDP peer must persist this many polls before we call it a game server.</summary>
        public int GameServerConfirmPolls { get; set; } = 3;
        /// <summary>Drop a game server after this many polls with no packets/absence.</summary>
        public int GameServerDropPolls { get; set; } = 4;
        public bool ResolvePing { get; set; } = true;
        public int PingTimeoutMs { get; set; } = 800;

        // --- Game-server classification filters (tune once real match data is captured) ---
        /// <summary>Only promote a UDP peer to a game server while we believe we're in a match.
        /// Off by default so lobby/matchmaking candidates are still recorded for analysis.</summary>
        public bool GameServerRequireInMatch { get; set; } = false;
        /// <summary>Minimum sustained UDP throughput (bytes/sec, both directions) to qualify as a
        /// game server. Applies even to game-port peers, so near-idle Demonware endpoints on port
        /// 3074 (~4 B/s) are excluded while the real match server (~7 KB/s) qualifies. 0 = disabled.</summary>
        public int GameServerMinBytesPerSec { get; set; } = 1000;
        /// <summary>Treat ANY UDP peer above this throughput (bytes/sec) as a game-server candidate,
        /// even on a non-standard port. The real match server pushes a continuous high-rate stream,
        /// whereas Demonware/backend endpoints on game ports are near-idle. 0 = port-only detection.</summary>
        public int GameServerTrafficBytesPerSec { get; set; } = 3000;

        // --- VPN / proxy heuristics for game servers ---
        /// <summary>Ping at/above this (ms) flags a server as likely VPN/proxied routing.</summary>
        public int VpnPingThresholdMs { get; set; } = 130;
        /// <summary>Great-circle distance (km) from home at/above which a server looks suspiciously far.</summary>
        public double VpnDistanceKmThreshold { get; set; } = 4000;
        /// <summary>Home location for distance checks. If null and AutoResolveHome, resolved from public IP.</summary>
        public double? HomeLatitude { get; set; }
        public double? HomeLongitude { get; set; }
        public bool AutoResolveHome { get; set; } = true;
        /// <summary>Service returning the caller's public IP as plain text.</summary>
        public string PublicIpUrl { get; set; } = "https://api.ipify.org";

        // --- GeoIP ---
        public string GeoDbDir { get; set; } = "%LOCALAPPDATA%\\WarzoneHelper\\geoip";
        public bool AutoDownloadGeoDb { get; set; } = true;

        // --- Activision status API ---
        public bool EnableStatusApi { get; set; } = true;
        public int StatusPollMs { get; set; } = 60000;
        public string StatusApiUrl { get; set; } =
            "https://prod-psapi.infra-ext.activision.com/open/api/apexrest/oshp/landingpage";
        /// <summary>Only status entries whose gameTitle contains one of these (lowercased) are
        /// counted/logged — the API returns all Activision games (Crash, Skylanders, ...).</summary>
        public string[] StatusGameTitles { get; set; } =
            { "warzone", "black ops", "modern warfare", "call of duty" };

        // --- Screen CV ---
        public bool EnableScreen { get; set; } = true;
        public int ScreenPollMs { get; set; } = 500;
        /// <summary>
        /// GDI screen capture of the game window (works for DX12/borderless where Overwolf's
        /// in-memory screenshot doesn't). Regions covered by our own overlay windows are skipped so
        /// we never OCR ourselves (see CaptureExcludeTitles). Push-frame mode (false) is only for
        /// DX9/DX11 games where the Overwolf app can supply frames.
        /// </summary>
        public bool SelfCapture { get; set; } = true;
        /// <summary>Window titles of our own overlays; regions they cover are excluded from OCR.</summary>
        public string[] CaptureExcludeTitles { get; set; } = { "Warzone Helper", "Players" };
        public string TesseractDataDir { get; set; } = "%LOCALAPPDATA%\\WarzoneHelper\\tessdata";
        /// <summary>Grayscale OCR crops by max(R,G,B) (brightness) instead of luminance, so bright
        /// colored/animated HUD text (e.g. the rainbow level 1000) reads reliably.</summary>
        public bool OcrGrayscaleByValue { get; set; } = true;
        /// <summary>Times a value/name must be read the same way to become "true" (OCR hysteresis).</summary>
        public int ConfidenceEstablish { get; set; } = 3;
        /// <summary>Times a DIFFERENT value must be read in a row to overturn an established one.</summary>
        public int ConfidenceOverturn { get; set; } = 4;
        /// <summary>Grace window (seconds): reconnecting to the same game server within this keeps
        /// the same match session (roster preserved), absorbing the double connect/disconnect.</summary>
        public int MatchSessionGraceSec { get; set; } = 25;
        /// <summary>Keep disconnected players in the roster this long (seconds) before removing.</summary>
        public int PlayerRetainSec { get; set; } = 120;
        /// <summary>Your own player name (letters compared loosely) so the roster marks you as self.</summary>
        public string PlayerSelfName { get; set; } = "";
        /// <summary>Persistent multi-session player cache (minified JSON).</summary>
        public string PlayerCacheFile { get; set; } = "%APPDATA%\\WarzoneHelper\\players.json";
        /// <summary>Name-similarity (0..1) at/above which a new read is linked to an existing cached
        /// player instead of creating a new one (kills OCR-variant duplicates / made-up players).</summary>
        public double PlayerFuzzyThreshold { get; set; } = 0.84;
        /// <summary>Screen regions (normalized 0..1) for the analyzer. See ScreenRegions.</summary>
        public ScreenRegions Regions { get; set; } = new ScreenRegions();

        // --- Feature toggles ---
        public bool EnableNetwork { get; set; } = true;
        public bool EnableLogWatch { get; set; } = true;

        // --- WebSocket server (agent -> Overwolf app UI) ---
        public bool EnableWebSocket { get; set; } = true;
        public string WebSocketHost { get; set; } = "127.0.0.1";
        public int WebSocketPort { get; set; } = 17999;

        // --- Standalone event logging (console/scheduled-task host) ---
        /// <summary>Events are appended here as newline-delimited JSON. Empty/null disables file logging.
        /// Supports the tokens {unixtime} (seconds since epoch at startup) and {pid}.</summary>
        public string EventLogFile { get; set; } = "%TEMP%\\WarzoneHelper\\events_{unixtime}.ndjson";
        /// <summary>Diagnostic [log] lines file. Defaults to EventLogFile with a .log extension when null.</summary>
        public string DiagLogFile { get; set; } = null;
        /// <summary>Rotate a log file once it exceeds this size (MB). 0 = never rotate.</summary>
        public int LogRotateMB { get; set; } = 20;

        public static string Expand(string path)
        {
            return string.IsNullOrEmpty(path) ? path : Environment.ExpandEnvironmentVariables(path);
        }

        /// <summary>Expand env vars plus the {unixtime}/{pid} tokens (used for per-run log file names).</summary>
        public static string ExpandTokens(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var expanded = Environment.ExpandEnvironmentVariables(path);
            expanded = expanded.Replace("{unixtime}", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            expanded = expanded.Replace("{pid}", System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            return expanded;
        }

        public IEnumerable<string> ExpandedWatchPaths()
        {
            foreach (var p in WatchPaths ?? Array.Empty<string>())
                yield return Expand(p);
        }

        public string ExpandedGeoDbDir() => Expand(GeoDbDir);
        public string ExpandedTessDir() => Expand(TesseractDataDir);

        /// <summary>Default settings file: %APPDATA%\WarzoneHelper\settings.jsonc</summary>
        public static string DefaultConfigPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarzoneHelper", "settings.jsonc");

        /// <summary>Load the JSONC config (comments allowed), writing a commented default if missing.</summary>
        public static HelperConfig LoadOrCreate(string path, Action<string> log = null)
        {
            try
            {
                if (File.Exists(path))
                {
                    // Newtonsoft ignores // and /* */ comments, so .jsonc parses directly.
                    var cfg = JsonConvert.DeserializeObject<HelperConfig>(File.ReadAllText(path));
                    if (cfg != null) { log?.Invoke($"[config] loaded {path}"); return cfg; }
                }
                else
                {
                    var def = new HelperConfig();
                    def.SaveJsonc(path);
                    log?.Invoke($"[config] wrote default {path}");
                    return def;
                }
            }
            catch (Exception ex) { log?.Invoke($"[config] error ({ex.Message}); using defaults"); }
            return new HelperConfig();
        }

        /// <summary>Write as JSONC with a header comment (all tunables incl. region coordinates).</summary>
        public void SaveJsonc(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            const string header =
                "// Warzone Helper settings (JSONC — // and /* */ comments are allowed).\n" +
                "// Delete this file to regenerate defaults. Screen region coordinates under \"Regions\"\n" +
                "// are normalized fractions [x, y, width, height] of the game frame.\n";
            File.WriteAllText(path, header + JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public static HelperConfig Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var cfg = JsonConvert.DeserializeObject<HelperConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { /* fall through to defaults */ }
            return new HelperConfig();
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    /// <summary>
    /// Normalized screen regions (x, y, w, h in 0..1 of the game frame) that the analyzer
    /// samples. Defaults target a 16:9 Warzone HUD; tune per resolution from your Screenshots/.
    /// </summary>
    /// <summary>
    /// A screen region anchored to an edge/corner so it stays correct across aspect ratios.
    /// Anchor is any combination of top/bottom/left/right (or "center"); X/Y are the offset (as
    /// fractions of the frame) FROM the anchored edge(s) — from center when an axis isn't anchored —
    /// and W/H are the size fractions. Deserializable from JSONC.
    /// </summary>
    public sealed class Region
    {
        public string Anchor { get; set; } = "topleft";
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public Region() { }
        public Region(string anchor, double x, double y, double w, double h)
        { Anchor = anchor; X = x; Y = y; W = w; H = h; }
    }

    // Defaults measured from 3440x1440 (21:9) reference frames in .references/Screenshots and
    // expressed with edge anchors so they hold on other aspect ratios (16:9, 16:10, ...).
    public sealed class ScreenRegions
    {
        // Deploy countdown ("Deployment will begin in: NN"), upper-center.
        public Region DeployBanner { get; set; } = new Region("top", 0.0, 0.08, 0.16, 0.18);
        // Death / "You were killed by" banner, upper-center.
        public Region DeathBanner { get; set; } = new Region("top", 0.0, 0.10, 0.40, 0.14);
        // Health/armor bar (shown when damaged), low-center.
        public Region Health { get; set; } = new Region("bottom", 0.0, 0.08, 0.16, 0.02);
        // Lobby/session ID (~19 digits), very bottom-left corner (in-match).
        public Region LobbyId { get; set; } = new Region("bottomleft", 0.0, 0.004, 0.11, 0.024);
        // In-game chat: upper-right stack of "[CHANNEL] name / message" lines.
        public Region Chat { get; set; } = new Region("topright", 0.0, 0.14, 0.23, 0.28);
        // Lobby player panel, top-right (small = your PARTY, large = full MATCH/lobby list).
        public Region Party { get; set; } = new Region("topright", 0.13, 0.12, 0.21, 0.36);
        // In-match squad panel, bottom-left (teammates + cash).
        public Region InGameSquad { get; set; } = new Region("bottomleft", 0.0, 0.025, 0.13, 0.285);
        // "SPECTATING: name#id" panel, bottom-center (when dead/spectating).
        public Region Spectating { get; set; } = new Region("bottom", 0.0, 0.17, 0.26, 0.10);
        // Perf/telemetry overlay strip along the very top (FPS / LATENCY / GAME LATENCY / etc.).
        public Region TopBar { get; set; } = new Region("topleft", 0.06, 0.0, 0.70, 0.032);
        // Killfeed + event log, left-middle ("<killer> [icon] <victim>" and "<player> Disconnected").
        public Region Feed { get; set; } = new Region("left", 0.0, 0.015, 0.18, 0.17);
        // "YOUR PARTY CODE" value (big centered code, e.g. LLJGJ) on the party-code menu.
        public Region PartyCode { get; set; } = new Region("center", 0.0, -0.1325, 0.12, 0.075);
        // Inspect-player detail panel (right side): name#id, platform, level, rank, input.
        public Region Inspect { get; set; } = new Region("topright", 0.02, 0.10, 0.32, 0.76);
    }
}
