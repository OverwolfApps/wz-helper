using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using GameHelper.Core.Config;
using Region = WarzoneHelper.Game.Region;

using GameHelper.Core.Screen;
namespace WarzoneHelper.Game
{
    /// <summary>Result of analysing a single frame. Nulls mean "not determined this frame".</summary>
    public sealed class ScreenState
    {
        // public double? HealthFraction;   // health disabled (unreliable, unused)
        public bool? DeathBannerVisible;
        public bool? DeployBannerVisible;
        public string LobbyId;
        public string[] ChatLines;    // OCR'd in-game chat lines (in-match only)
        public string[] PartyLines;   // OCR'd player-list lines (party or match/lobby list)
        public bool PartyIsMatchList; // true when the player list is the large match/lobby list
        public string SpectatingName; // player currently being spectated (id stripped)
        public string SpectatingId;   // the #NNNN suffix, if read
        public string PartyCode;      // cached party/invite code (persists until it changes)
        public string GameVersion;    // on-screen build/version watermark (confidence-gated)
        public Dictionary<string, object> Inspect; // inspect-player panel details (activisionId, ...)
        public System.Collections.Generic.Dictionary<string, object> Perf; // top overlay telemetry
        public string[] FeedLines;    // killfeed + event-log lines (in-match)
    }

    /// <summary>
    /// Heuristic CV over configured HUD regions. Color/fill sampling is resolution-independent
    /// (regions are normalized); lobby ID uses OCR. Thresholds are deliberately conservative and
    /// tunable against the frames in wz-helper/.references/Screenshots.
    /// </summary>
    public sealed class WarzoneScreenAnalyzer
    {
        private readonly ScreenRegions _regions;
        private readonly IOcrEngine _ocr;
        private readonly bool _grayByValue;
        // "SPECTATING: Kazu_15#4138899" — capture name and the #id suffix.
        private static readonly Regex SpectateRegex = new Regex(
            @"([A-Za-z0-9_\-\[\] ]{3,})#(\d{3,})", RegexOptions.Compiled);
        // Below this crop size Tesseract/leptonica prints "Image too small to scale!!" to stderr.
        private const int MinOcrPx = 8;

        public WarzoneScreenAnalyzer(ScreenRegions regions, IOcrEngine ocr, bool grayscaleByValue = true)
        {
            _regions = regions ?? new ScreenRegions();
            _ocr = ocr ?? new NullOcrEngine();
            _grayByValue = grayscaleByValue;
        }

        private IList<Rectangle> _excluded;

        // Per-field confidence gates (rolling-window vote before a value is set).
        private readonly FieldTracker _lobbyId = new FieldTracker(OcrFields.LobbyId);
        private readonly FieldTracker _spectateName = new FieldTracker(OcrFields.PlayerName);
        private readonly FieldTracker _spectateId = new FieldTracker(OcrFields.SpectateId);
        private readonly FieldTracker _partyCode = new FieldTracker(OcrFields.PartyCode);
        private readonly FieldTracker _gameVersion = new FieldTracker(OcrFields.GameVersion);

        public ScreenState Analyze(Bitmap frame, bool inMatch, IList<Rectangle> excluded = null)
        {
            _excluded = excluded;
            var s = new ScreenState();
            if (frame == null) return s;

            // Deploy is a match-START signal, so it must run even out of match. Health and the death
            // banner only make sense during an actual match — don't sample them otherwise (avoids
            // false reds from menus and wasted work).
            s.DeployBannerVisible = DetectBrightBanner(frame, _regions.DeployBanner, minRatio: 0.25);
            if (inMatch)
            {
                // Health disabled (unreliable, unused): s.HealthFraction = EstimateHealth(frame, _regions.Health);
                s.DeathBannerVisible = DetectReddishBanner(frame, _regions.DeathBanner, minRatio: 0.10);
            }

            // Skip all OCR for undersized frames (e.g. loading), which otherwise trip Tesseract's
            // "Image too small to scale" errors and produce garbage.
            if (_ocr.Available && frame.Width >= 320 && frame.Height >= 240)
            {
                // Top telemetry overlay (FPS / LATENCY / GAME LATENCY / ...). Shown in lobby and match.
                var perfText = ReadRegion(frame, _regions.TopBar, OcrFields.PerfStripWhitelist, singleLine: true);
                var perf = PerfParser.Parse(perfText);
                if (perf.Count > 0) s.Perf = perf;

                var lobbyText = ReadRegion(frame, _regions.LobbyId, OcrFields.LobbyId.Whitelist, singleLine: true);
                _lobbyId.Observe(lobbyText);       // confidence-gated (validated inside)
                s.LobbyId = _lobbyId.Value;
                #region OCR dump — only successful (validated) reads
                OcrDump.Dump("lobbyid", OcrFields.LobbyId.Parse(lobbyText));
                #endregion

                // Build/version watermark (shown in menus and in-match). Confidence-gated so it only
                // surfaces once it's been read consistently — then a change means the game updated.
                var versionText = ReadRegion(frame, _regions.Version, OcrFields.GameVersion.Whitelist, singleLine: true);
                _gameVersion.Observe(versionText);
                s.GameVersion = _gameVersion.Value;
                #region OCR dump
                OcrDump.Dump("version", OcrFields.GameVersion.Parse(versionText));
                #endregion

                if (inMatch)
                {
                    // Chat overlay (upper-right) -> grouped into messages by ChatParser.
                    s.ChatLines = ScreenOps.SplitLines(ReadRegion(frame, _regions.Chat, null, singleLine: false));
                    // In a match the player list is the bottom-left squad panel.
                    s.PartyLines = ScreenOps.SplitLines(ReadRegion(frame, _regions.InGameSquad, null, singleLine: false));
                    // Killfeed + event log (left-middle) — names of enemies and disconnects.
                    s.FeedLines = ScreenOps.SplitLines(ReadRegion(frame, _regions.Feed, null, singleLine: false));
                    // Spectating panel (bottom-center) when dead.
                    var spec = ReadRegion(frame, _regions.Spectating, null, singleLine: false);
                    var sm = spec != null ? SpectateRegex.Match(spec) : Match.Empty;
                    if (sm.Success) { _spectateName.Observe(sm.Groups[1].Value); _spectateId.Observe(spec); }
                    s.SpectatingName = _spectateName.Value;
                    s.SpectatingId = _spectateId.Value;
                }
                else
                {
                    // Lobby: top-right panel. Many members => the full match/lobby list, not your party.
                    s.PartyLines = ScreenOps.SplitLines(ReadRegion(frame, _regions.Party, null, singleLine: false));
                    s.PartyIsMatchList = (s.PartyLines?.Length ?? 0) > 8;
                    // Party/invite code (party-code menu). Confidence-gated; persists once set.
                    // Party/invite code: the center-region OCR is unreliable (reads noise / codes that
                    // don't match), so we DON'T emit from it — the ClipboardPartyCodeMonitor is the
                    // authoritative source. We still read + dump it so the region can be tuned later.
                    var partyCodeRaw = ReadRegion(frame, _regions.PartyCode, OcrFields.PartyCode.Whitelist, singleLine: true);
                    // _partyCode.Observe(partyCodeRaw);   // disabled: clipboard is the source
                    // Inspect-player detail panel (rich per-player data; only parses on that screen).
                    s.Inspect = InspectParser.Parse(ReadRegion(frame, _regions.Inspect, null, singleLine: false));
                    #region OCR dump — only the validated code
                    OcrDump.Dump("partycode", OcrFields.PartyCode.Parse(partyCodeRaw));
                    #endregion
                }
                s.PartyCode = _partyCode.Value;   // cached value (never cleared per match)
            }
            return s;
        }

        /// <summary>Resolve an anchored Region to a pixel rectangle within the frame.</summary>
        private static Rectangle ToRect(Bitmap b, Region r)
        {
            int fw = b.Width, fh = b.Height;
            int w = (int)(r.W * fw), h = (int)(r.H * fh);
            var a = (r.Anchor ?? "topleft").ToLowerInvariant();

            int x;
            if (a.Contains("right")) x = fw - (int)(r.X * fw) - w;       // X = offset from right edge
            else if (a.Contains("left")) x = (int)(r.X * fw);           // X = offset from left edge
            else x = (fw - w) / 2 + (int)(r.X * fw);                    // centered + X offset

            int y;
            if (a.Contains("bottom")) y = fh - (int)(r.Y * fh) - h;      // Y = offset from bottom edge
            else if (a.Contains("top")) y = (int)(r.Y * fh);           // Y = offset from top edge
            else y = (fh - h) / 2 + (int)(r.Y * fh);                    // centered + Y offset

            x = ScreenOps.Clamp(x, 0, fw - 1); y = ScreenOps.Clamp(y, 0, fh - 1);
            w = ScreenOps.Clamp(w, 1, fw - x); h = ScreenOps.Clamp(h, 1, fh - y);
            return new Rectangle(x, y, w, h);
        }

        /// <summary>Crop + OCR a region. Skips tiny crops, and upscales short ones (thin HUD strips
        /// like the perf overlay / lobby id) since Tesseract reads small text poorly.</summary>
        private string ReadRegion(Bitmap frame, Region region, string whitelist, bool singleLine)
        {
            var rect = ToRect(frame, region);
            if (rect.Width < MinOcrPx || rect.Height < MinOcrPx) return null;
            // Skip regions our own overlay windows are covering, so we never OCR ourselves.
            if (_excluded != null)
                foreach (var ex in _excluded)
                    if (ex.IntersectsWith(rect)) return null;
            Bitmap crop = frame.Clone(rect, frame.PixelFormat);
            try
            {
                // Aim for ~48px tall text for OCR; upscale thin strips (perf overlay / lobby id).
                int scale = ScreenOps.TargetScale(rect.Height);
                if (scale > 1) { var big = ScreenOps.Upscale(crop, scale); crop.Dispose(); crop = big; }
                if (_grayByValue)
                {
                    var gray = ScreenOps.MaxChannelGray(crop);
                    crop.Dispose(); crop = gray;
                }
                return _ocr.Read(crop, whitelist, singleLine);
            }
            finally { crop.Dispose(); }
        }

        // HUD CV heuristics — thin Warzone wrappers over the generic ScreenOps pixel samplers; only
        // the region + pixel predicate/threshold are game-specific.

        // Health disabled (unreliable, unused):
        // private static double EstimateHealth(Bitmap b, Region region) =>
        //     ScreenOps.RowRatio(b, ToRect(b, region), c => ScreenOps.Luminance(c) > 90);

        /// <summary>Detects a predominantly red banner (death / damage indicator).</summary>
        private static bool DetectReddishBanner(Bitmap b, Region region, double minRatio) =>
            ScreenOps.PixelRatio(b, ToRect(b, region),
                c => c.R > 110 && c.R > c.G * 1.6 && c.R > c.B * 1.6, step: 2) >= minRatio;

        /// <summary>Detects a bright/high-contrast prompt banner (deploy / parachute prompt).</summary>
        private static bool DetectBrightBanner(Bitmap b, Region region, double minRatio) =>
            ScreenOps.PixelRatio(b, ToRect(b, region), c => ScreenOps.Luminance(c) > 190, step: 3) >= minRatio;
    }
}
