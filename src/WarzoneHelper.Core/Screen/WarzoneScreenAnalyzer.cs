using System;
using System.Drawing;
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
        public string[] PartyLines;   // OCR'd lobby party-list lines (lobby only)
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
        private static readonly Regex LobbyIdRegex = new Regex(@"\d{6,}", RegexOptions.Compiled);

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

            if (_ocr.Available)
            {
                using (var crop = Crop(frame, _regions.LobbyId))
                {
                    var text = _ocr.Read(crop, whitelist: "0123456789");
                    if (text != null)
                    {
                        var m = LobbyIdRegex.Match(text);
                        if (m.Success) s.LobbyId = m.Value;
                    }
                }

                if (inMatch)
                {
                    // Chat only exists in-match / preparing; OCR the chat region.
                    using (var crop = Crop(frame, _regions.Chat))
                        s.ChatLines = CleanChat(_ocr.Read(crop, whitelist: null, singleLine: false));
                }
                else
                {
                    // In the lobby there is no chat, but the party list (top-right) is worth reading.
                    using (var crop = Crop(frame, _regions.Party))
                        s.PartyLines = CleanChat(_ocr.Read(crop, whitelist: null, singleLine: false));
                }
            }
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

        /// <summary>Split OCR output into trimmed, non-trivial chat lines. Drops obvious noise.</summary>
        private static string[] CleanChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            var lines = new System.Collections.Generic.List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 2) continue;                 // stray single chars
                int letters = 0;
                foreach (var c in line) if (char.IsLetterOrDigit(c)) letters++;
                if (letters < 2) continue;                     // punctuation-only garbage
                lines.Add(line);
            }
            return lines.ToArray();
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
