using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using WarzoneHelper.Core.Config;

namespace WarzoneHelper.Core.Screen
{
    /// <summary>Result of analysing a single frame. Nulls mean "not determined this frame".</summary>
    public sealed class ScreenState
    {
        public double? HealthFraction;   // 0..1 estimate from the health bar fill
        public bool? DeathBannerVisible;
        public bool? DeployBannerVisible;
        public string LobbyId;
        public string[] ChatLines;    // OCR'd in-game chat lines (in-match only)
        public string[] PartyLines;   // OCR'd player-list lines (party or match/lobby list)
        public bool PartyIsMatchList; // true when the player list is the large match/lobby list
        public string SpectatingName; // player currently being spectated (id stripped)
        public string SpectatingId;   // the #NNNN suffix, if read
        public string PartyCode;      // cached party/invite code (persists until it changes)
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

        public ScreenState Analyze(Bitmap frame, bool inMatch, IList<Rectangle> excluded = null)
        {
            _excluded = excluded;
            var s = new ScreenState();
            if (frame == null) return s;

            s.HealthFraction = EstimateHealth(frame, _regions.Health);
            s.DeathBannerVisible = DetectReddishBanner(frame, _regions.DeathBanner, minRatio: 0.10);
            s.DeployBannerVisible = DetectBrightBanner(frame, _regions.DeployBanner, minRatio: 0.25);

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

                if (inMatch)
                {
                    // Chat overlay (upper-right) -> grouped into messages by ChatParser.
                    s.ChatLines = SplitLines(ReadRegion(frame, _regions.Chat, null, singleLine: false));
                    // In a match the player list is the bottom-left squad panel.
                    s.PartyLines = SplitLines(ReadRegion(frame, _regions.InGameSquad, null, singleLine: false));
                    // Killfeed + event log (left-middle) — names of enemies and disconnects.
                    s.FeedLines = SplitLines(ReadRegion(frame, _regions.Feed, null, singleLine: false));
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
                    s.PartyLines = SplitLines(ReadRegion(frame, _regions.Party, null, singleLine: false));
                    s.PartyIsMatchList = (s.PartyLines?.Length ?? 0) > 8;
                    // Party/invite code (party-code menu). Confidence-gated; persists once set.
                    _partyCode.Observe(ReadRegion(frame, _regions.PartyCode, OcrFields.PartyCode.Whitelist, singleLine: true));
                }
                s.PartyCode = _partyCode.Value;   // cached value (never cleared per match)
            }
            return s;
        }

        /// <summary>
        /// Grayscale by brightness = max(R,G,B) rather than luminance. Bright colored/animated HUD
        /// text (the rainbow level number) becomes near-white regardless of hue, so Tesseract's
        /// internal Otsu binarization then separates it cleanly from the darker background.
        /// </summary>
        private static Bitmap ToValueGray(Bitmap src)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var s = src.Clone(rect, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var data = s.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, s.PixelFormat);
            try
            {
                int bytes = Math.Abs(data.Stride) * s.Height;
                var buf = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, bytes);
                for (int i = 0; i + 2 < bytes; i += 3)
                {
                    byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    byte v = Math.Max(r, Math.Max(g, b));
                    buf[i] = buf[i + 1] = buf[i + 2] = v;
                }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, bytes);
            }
            finally { s.UnlockBits(data); }
            return s;
        }

        private static Rectangle ToRect(Bitmap b, double[] r)
        {
            int x = (int)(r[0] * b.Width), y = (int)(r[1] * b.Height);
            int w = (int)(r[2] * b.Width), h = (int)(r[3] * b.Height);
            x = Clamp(x, 0, b.Width - 1); y = Clamp(y, 0, b.Height - 1);
            w = Clamp(w, 1, b.Width - x); h = Clamp(h, 1, b.Height - y);
            return new Rectangle(x, y, w, h);
        }

        private static Bitmap Crop(Bitmap b, double[] r) => b.Clone(ToRect(b, r), b.PixelFormat);

        /// <summary>Crop + OCR a region. Skips tiny crops, and upscales short ones (thin HUD strips
        /// like the perf overlay / lobby id) since Tesseract reads small text poorly.</summary>
        private string ReadRegion(Bitmap frame, double[] region, string whitelist, bool singleLine)
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
                // Aim for ~48px tall text for OCR; upscale thin strips.
                int scale = rect.Height < 48 ? Math.Min(4, (int)Math.Ceiling(48.0 / rect.Height)) : 1;
                if (scale > 1)
                {
                    var big = new Bitmap(rect.Width * scale, rect.Height * scale);
                    using (var g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(crop, 0, 0, big.Width, big.Height);
                    }
                    crop.Dispose(); crop = big;
                }
                if (_grayByValue)
                {
                    var gray = ToValueGray(crop);
                    crop.Dispose(); crop = gray;
                }
                return _ocr.Read(crop, whitelist, singleLine);
            }
            finally { crop.Dispose(); }
        }

        /// <summary>Health bar: fraction of the bar width that is "filled" (bright, non-dark) pixels.</summary>
        private static double EstimateHealth(Bitmap b, double[] region)
        {
            var rect = ToRect(b, region);
            int midY = rect.Y + rect.Height / 2;
            int filled = 0, total = 0;
            for (int x = rect.X; x < rect.X + rect.Width; x++)
            {
                var c = b.GetPixel(x, midY);
                total++;
                // Filled HUD bar segments are notably brighter than the dark empty track.
                double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                if (lum > 90) filled++;
            }
            return total == 0 ? 0 : (double)filled / total;
        }

        /// <summary>Detects a predominantly red banner (death / damage indicator).</summary>
        private static bool DetectReddishBanner(Bitmap b, double[] region, double minRatio)
        {
            var rect = ToRect(b, region);
            long red = 0, count = 0;
            for (int y = rect.Y; y < rect.Y + rect.Height; y += 2)
                for (int x = rect.X; x < rect.X + rect.Width; x += 2)
                {
                    var c = b.GetPixel(x, y);
                    count++;
                    if (c.R > 110 && c.R > c.G * 1.6 && c.R > c.B * 1.6) red++;
                }
            return count > 0 && (double)red / count >= minRatio;
        }

        /// <summary>Detects a bright/high-contrast prompt banner (deploy / parachute prompt).</summary>
        private static bool DetectBrightBanner(Bitmap b, double[] region, double minRatio)
        {
            var rect = ToRect(b, region);
            long bright = 0, count = 0;
            for (int y = rect.Y; y < rect.Y + rect.Height; y += 3)
                for (int x = rect.X; x < rect.X + rect.Width; x += 3)
                {
                    var c = b.GetPixel(x, y);
                    count++;
                    double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    if (lum > 190) bright++;
                }
            return count > 0 && (double)bright / count >= minRatio;
        }

        /// <summary>Split OCR output into trimmed, non-empty lines. Cleaning is left to the parsers.</summary>
        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            return text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.Any(char.IsLetterOrDigit))
                .ToArray();
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
