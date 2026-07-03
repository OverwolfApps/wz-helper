using GameHelper.Core.Config;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// Warzone-specific configuration: fills in the generic <see cref="HelperConfig"/> hooks with
    /// Call of Duty defaults and adds the Warzone-only fields (screen regions, self name, player
    /// cache). Serialized as the whole per-game settings file, so every generic knob is tunable
    /// per game too.
    /// </summary>
    public sealed class WarzoneConfig : HelperConfig
    {
        public WarzoneConfig()
        {
            // Process
            GameProcessNames = new[] { "cod", "cod.exe" };

            // Log / cache directories
            WatchPaths = new[]
            {
                @"%USERPROFILE%\Documents\Call of Duty",
                @"%LOCALAPPDATA%\Activision",
                @"%PROGRAMDATA%\Battle.net\Logs"
            };

            // CoD PC game UDP ports
            GameUdpPorts = new[] { 3074, 3075, 3076, 3077, 3078, 3079, 3478, 4379, 4380 };
            GameUdpPortRangeStart = new[] { 27000 };
            GameUdpPortRangeEnd = new[] { 27031 };

            // Activision status API + CoD title filter
            StatusApiUrl = "https://prod-psapi.infra-ext.activision.com/open/api/apexrest/oshp/landingpage";
            StatusGameTitles = new[] { "warzone", "black ops", "modern warfare", "call of duty" };
        }

        /// <summary>Your own player name (letters compared loosely) so the roster marks you as self.</summary>
        public string PlayerSelfName { get; set; } = "";
        /// <summary>Persistent multi-session player cache (minified JSON).</summary>
        public string PlayerCacheFile { get; set; } = "%APPDATA%\\GameHelper\\warzone\\players.json";
        /// <summary>Name-similarity (0..1) at/above which a new read is linked to an existing cached
        /// player instead of creating a new one (kills OCR-variant duplicates / made-up players).</summary>
        public double PlayerFuzzyThreshold { get; set; } = 0.84;
        /// <summary>Screen regions (normalized 0..1) for the analyzer. See <see cref="ScreenRegions"/>.</summary>
        public ScreenRegions Regions { get; set; } = new ScreenRegions();
    }

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
        // Build/version watermark ("12.11.27503415[...]") — corner text; tune with the region editor.
        public Region Version { get; set; } = new Region("bottomleft", 0.0, 0.028, 0.24, 0.02);
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
