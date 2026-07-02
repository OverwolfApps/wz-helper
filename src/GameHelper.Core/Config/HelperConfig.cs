using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace GameHelper.Core.Config
{
    /// <summary>
    /// Generic runtime configuration shared by every game helper. Loaded from a per-game JSONC file
    /// (%APPDATA%\GameHelper\{game}\settings.jsonc), with environment-variable expansion on all
    /// paths. Game-specific defaults and extra fields live in a derived config (e.g. WarzoneConfig)
    /// supplied by the game's IGameProfile — the base here keeps neutral defaults so a new game only
    /// overrides what it needs.
    /// </summary>
    public class HelperConfig
    {
        // --- Process --- (game supplies its process names)
        public string[] GameProcessNames { get; set; } = Array.Empty<string>();

        // --- Log / cache watching --- (game supplies the directories it cares about)
        public string[] WatchPaths { get; set; } = Array.Empty<string>();
        public string[] WatchFilters { get; set; } = { "*.log", "*.txt", "*.json", "*.ndjson" };
        /// <summary>Debounce for noisy filesystem events, milliseconds.</summary>
        public int WatchDebounceMs { get; set; } = 400;
        /// <summary>Optional regexes tried against each appended log line; the FIRST that matches
        /// contributes its NAMED capture groups (e.g. (?&lt;timestamp&gt;...), (?&lt;level&gt;...),
        /// (?&lt;message&gt;...)) as fields on the LOG_LINE_ADDED event, so consumers get the line
        /// pre-parsed. Empty = raw lines only.</summary>
        public string[] LogLinePatterns { get; set; } = Array.Empty<string>();

        // --- Network ---
        public int NetworkPollMs { get; set; } = 1000;
        /// <summary>UDP ports treated as candidate gameplay traffic (game supplies its ports).</summary>
        public int[] GameUdpPorts { get; set; } = Array.Empty<int>();
        public int[] GameUdpPortRangeStart { get; set; } = Array.Empty<int>();
        public int[] GameUdpPortRangeEnd { get; set; } = Array.Empty<int>();
        /// <summary>A UDP peer must persist this many polls before we call it a game server.</summary>
        public int GameServerConfirmPolls { get; set; } = 3;
        /// <summary>Drop a game server after this many polls with no packets/absence.</summary>
        public int GameServerDropPolls { get; set; } = 4;
        public bool ResolvePing { get; set; } = true;
        public int PingTimeoutMs { get; set; } = 800;

        // --- Game-server classification filters (tune once real match data is captured) ---
        /// <summary>Only promote a UDP peer to a game server while we believe we're in a match.</summary>
        public bool GameServerRequireInMatch { get; set; } = false;
        /// <summary>Minimum sustained UDP throughput (bytes/sec, both directions) to qualify as a
        /// game server, excluding near-idle backend endpoints on game ports. 0 = disabled.</summary>
        public int GameServerMinBytesPerSec { get; set; } = 1000;
        /// <summary>Treat ANY UDP peer above this throughput (bytes/sec) as a game-server candidate,
        /// even on a non-standard port. 0 = port-only detection.</summary>
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

        // --- GeoIP (shared across games) ---
        public string GeoDbDir { get; set; } = "%LOCALAPPDATA%\\GameHelper\\geoip";
        public bool AutoDownloadGeoDb { get; set; } = true;

        // --- Status API (game supplies the endpoint + title filter) ---
        public bool EnableStatusApi { get; set; } = true;
        public int StatusPollMs { get; set; } = 60000;
        public string StatusApiUrl { get; set; } = "";
        /// <summary>Only status entries whose gameTitle contains one of these (lowercased) are kept.</summary>
        public string[] StatusGameTitles { get; set; } = Array.Empty<string>();

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
        public string[] CaptureExcludeTitles { get; set; } = { "Game Helper", "Players" };
        public string TesseractDataDir { get; set; } = "%LOCALAPPDATA%\\GameHelper\\tessdata";
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
        public string EventLogFile { get; set; } = "%TEMP%\\GameHelper\\events_{unixtime}.ndjson";
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

        /// <summary>Per-game data directory: %APPDATA%\GameHelper\{game}.</summary>
        public static string GameDataDir(string game) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameHelper", string.IsNullOrEmpty(game) ? "default" : game);

        /// <summary>Default settings file: %APPDATA%\GameHelper\{game}\settings.jsonc</summary>
        public static string DefaultConfigPath(string game) =>
            Path.Combine(GameDataDir(game), "settings.jsonc");

        /// <summary>Load the JSONC config (comments allowed), writing a commented default if missing.
        /// <paramref name="factory"/> supplies a fresh game-typed default so the concrete (derived)
        /// config type round-trips.</summary>
        public static HelperConfig LoadOrCreate(string path, Func<HelperConfig> factory, Action<string> log = null)
        {
            var def = factory != null ? factory() : new HelperConfig();
            try
            {
                if (File.Exists(path))
                {
                    // Newtonsoft ignores // and /* */ comments, so .jsonc parses directly.
                    var cfg = (HelperConfig)JsonConvert.DeserializeObject(File.ReadAllText(path), def.GetType());
                    if (cfg != null) { log?.Invoke($"[config] loaded {path}"); return cfg; }
                }
                else
                {
                    def.SaveJsonc(path);
                    log?.Invoke($"[config] wrote default {path}");
                    return def;
                }
            }
            catch (Exception ex) { log?.Invoke($"[config] error ({ex.Message}); using defaults"); }
            return def;
        }

        /// <summary>Write as JSONC with a header comment (all tunables incl. any region coordinates).</summary>
        public void SaveJsonc(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            const string header =
                "// Game Helper settings (JSONC — // and /* */ comments are allowed).\n" +
                "// Delete this file to regenerate defaults. Any screen region coordinates under\n" +
                "// \"Regions\" are normalized fractions [x, y, width, height] of the game frame.\n";
            File.WriteAllText(path, header + JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public static HelperConfig Load(string path, Func<HelperConfig> factory)
        {
            var def = factory != null ? factory() : new HelperConfig();
            try
            {
                if (File.Exists(path))
                {
                    var cfg = (HelperConfig)JsonConvert.DeserializeObject(File.ReadAllText(path), def.GetType());
                    if (cfg != null) return cfg;
                }
            }
            catch { /* fall through to defaults */ }
            return def;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
