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

        // --- Screen CV ---
        public bool EnableScreen { get; set; } = true;
        public int ScreenPollMs { get; set; } = 1000;
        /// <summary>
        /// When true, the plugin captures frames itself (standalone/console).
        /// When false, it waits for frames pushed in from Overwolf's in-memory screenshot API.
        /// </summary>
        public bool SelfCapture { get; set; } = true;
        public string TesseractDataDir { get; set; } = "%LOCALAPPDATA%\\WarzoneHelper\\tessdata";
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
    // Defaults measured from 3440x1440 (21:9) reference frames in .references/Screenshots.
    // All values are normalized (0..1) fractions of the game frame, so they scale across resolutions
    // of the same aspect ratio; other aspect ratios may need a nudge.
    public sealed class ScreenRegions
    {
        // Deploy countdown ("Deployment will begin in: NN"), upper-center.
        public double[] DeployBanner { get; set; } = { 0.42, 0.08, 0.16, 0.18 };
        // Death / "You were killed by" banner, upper-center.
        public double[] DeathBanner { get; set; } = { 0.30, 0.10, 0.40, 0.14 };
        // Health/armor bar (shown when damaged), low-center.
        public double[] Health { get; set; } = { 0.42, 0.90, 0.16, 0.02 };
        // Lobby/session ID (~19 digits), very bottom-left corner (in-match).
        public double[] LobbyId { get; set; } = { 0.0, 0.972, 0.11, 0.024 };
        // In-game chat: upper-right stack of "[CHANNEL] name / message" lines.
        public double[] Chat { get; set; } = { 0.77, 0.14, 0.23, 0.28 };
        // Lobby player panel, top-right (small = your PARTY, large = full MATCH/lobby list).
        public double[] Party { get; set; } = { 0.66, 0.12, 0.21, 0.36 };
        // In-match squad panel, bottom-left (teammates + cash).
        public double[] InGameSquad { get; set; } = { 0.0, 0.69, 0.13, 0.285 };
        // "SPECTATING: name#id" panel, bottom-center (when dead/spectating).
        public double[] Spectating { get; set; } = { 0.37, 0.73, 0.26, 0.10 };
        // Perf/telemetry overlay strip along the very top (FPS / LATENCY / GAME LATENCY / etc.).
        // Its exact position shifts, so this spans a wide top band and the parser extracts pairs.
        public double[] TopBar { get; set; } = { 0.06, 0.0, 0.70, 0.032 };
    }
}
