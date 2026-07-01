using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
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
        public System.Collections.Generic.Dictionary<string, object> Perf; // top overlay telemetry
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
        // Real Warzone lobby/session ids are ~19 digits; require 17-20 to reject HUD numbers.
        private static readonly Regex LobbyIdRegex = new Regex(@"\d{17,20}", RegexOptions.Compiled);
        // Observed lobby ids are 61-63 bit numbers; require bit-length in [60,64] (i.e. 2^59..2^64).
        private static readonly BigInteger LobbyMin = BigInteger.Pow(2, 59);
        private static readonly BigInteger LobbyMax = BigInteger.Pow(2, 64);
        // "SPECTATING: Kazu_15#4138899" — capture name and the #id suffix.
        private static readonly Regex SpectateRegex = new Regex(
            @"([A-Za-z0-9_\-\[\] ]{3,})#(\d{3,})", RegexOptions.Compiled);
        // Below this crop size Tesseract/leptonica prints "Image too small to scale!!" to stderr.
        private const int MinOcrPx = 8;

        public WarzoneScreenAnalyzer(ScreenRegions regions, IOcrEngine ocr)
        {
            _regions = regions ?? new ScreenRegions();
            _ocr = ocr ?? new NullOcrEngine();
        }

        public ScreenState Analyze(Bitmap frame, bool inMatch)
        {
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
                var perfText = ReadRegion(frame, _regions.TopBar, null, singleLine: true);
                var perf = PerfParser.Parse(perfText);
                if (perf.Count > 0) s.Perf = perf;

                var lobbyText = ReadRegion(frame, _regions.LobbyId, "0123456789", singleLine: true);
                if (lobbyText != null)
                {
                    var digits = new string(lobbyText.Where(char.IsDigit).ToArray());
                    var m = LobbyIdRegex.Match(digits);
                    if (m.Success && IsValidLobbyId(m.Value)) s.LobbyId = m.Value;   // only ~19-digit ids pass
                }

                if (inMatch)
                {
                    // Chat overlay (upper-right) -> grouped into messages by ChatParser.
                    s.ChatLines = SplitLines(ReadRegion(frame, _regions.Chat, null, singleLine: false));
                    // In a match the player list is the bottom-left squad panel.
                    s.PartyLines = SplitLines(ReadRegion(frame, _regions.InGameSquad, null, singleLine: false));
                    // Spectating panel (bottom-center) when dead.
                    var spec = ReadRegion(frame, _regions.Spectating, null, singleLine: false);
                    var sm = spec != null ? SpectateRegex.Match(spec) : Match.Empty;
                    if (sm.Success) { s.SpectatingName = sm.Groups[1].Value.Trim(); s.SpectatingId = sm.Groups[2].Value; }
                }
                else
                {
                    // Lobby: top-right panel. Many members => the full match/lobby list, not your party.
                    s.PartyLines = SplitLines(ReadRegion(frame, _regions.Party, null, singleLine: false));
                    s.PartyIsMatchList = (s.PartyLines?.Length ?? 0) > 8;
                }
            }
            return s;
        }

        /// <summary>
        /// A real lobby id is 18-20 digits, not a repeated single digit, and a 60-64 bit number.
        /// This rejects OCR/HUD noise (short runs, 000000..., and out-of-range magnitudes).
        /// </summary>
        private static bool IsValidLobbyId(string digits)
        {
            if (string.IsNullOrEmpty(digits) || digits.Length < 18 || digits.Length > 20) return false;
            if (digits.All(c => c == digits[0])) return false;                 // 0000..., 1111..., etc.
            if (!BigInteger.TryParse(digits, out var n)) return false;
            return n >= LobbyMin && n < LobbyMax;                              // bit-length in [60,64]
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

        /// <summary>Crop + OCR a region, skipping crops too small for Tesseract to avoid its stderr spam.</summary>
        private string ReadRegion(Bitmap frame, double[] region, string whitelist, bool singleLine)
        {
            var rect = ToRect(frame, region);
            if (rect.Width < MinOcrPx || rect.Height < MinOcrPx) return null;
            using (var crop = frame.Clone(rect, frame.PixelFormat))
                return _ocr.Read(crop, whitelist, singleLine);
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
