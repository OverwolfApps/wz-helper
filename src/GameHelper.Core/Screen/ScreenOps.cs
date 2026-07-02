using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace GameHelper.Core.Screen
{
    /// <summary>
    /// Game-agnostic image / OCR-text helpers shared by any game's screen analyzer: brightness
    /// grayscale, crop upscaling, OCR line splitting and integer clamping. No game knowledge here.
    /// </summary>
    public static class ScreenOps
    {
        /// <summary>
        /// Grayscale by brightness = max(R,G,B) rather than luminance. Bright colored/animated HUD
        /// text (e.g. a rainbow level number) becomes near-white regardless of hue, so Tesseract's
        /// internal Otsu binarization separates it cleanly from the darker background. Returns a new
        /// bitmap; the caller owns both it and the source.
        /// </summary>
        public static Bitmap MaxChannelGray(Bitmap src)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var s = src.Clone(rect, PixelFormat.Format24bppRgb);
            var data = s.LockBits(rect, ImageLockMode.ReadWrite, s.PixelFormat);
            try
            {
                int bytes = Math.Abs(data.Stride) * s.Height;
                var buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);
                for (int i = 0; i + 2 < bytes; i += 3)
                {
                    byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    byte v = Math.Max(r, Math.Max(g, b));
                    buf[i] = buf[i + 1] = buf[i + 2] = v;
                }
                Marshal.Copy(buf, 0, data.Scan0, bytes);
            }
            finally { s.UnlockBits(data); }
            return s;
        }

        /// <summary>Integer scale factor to bring a crop of the given height up to ~<paramref name="target"/>px
        /// tall (Tesseract reads small text poorly), capped at <paramref name="max"/>. 1 = no scaling.</summary>
        public static int TargetScale(int height, int target = 48, int max = 4) =>
            height >= target || height <= 0 ? 1 : Math.Min(max, (int)Math.Ceiling((double)target / height));

        /// <summary>Bicubic-upscale a bitmap by an integer factor. Returns a new bitmap (caller owns
        /// both it and the source); returns the source unchanged when scale &lt;= 1.</summary>
        public static Bitmap Upscale(Bitmap src, int scale)
        {
            if (scale <= 1) return src;
            var big = new Bitmap(src.Width * scale, src.Height * scale);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, big.Width, big.Height);
            }
            return big;
        }

        /// <summary>Split OCR output into trimmed, non-empty lines. Cleaning is left to the parsers.</summary>
        public static string[] SplitLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            return text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l.Any(char.IsLetterOrDigit))
                .ToArray();
        }

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Perceptual luminance of a pixel (Rec. 601).</summary>
        public static double Luminance(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

        /// <summary>Fraction of pixels in <paramref name="rect"/> matching <paramref name="predicate"/>,
        /// sampling every <paramref name="step"/> px on both axes (step &gt; 1 = faster/coarser).</summary>
        public static double PixelRatio(Bitmap b, Rectangle rect, System.Func<Color, bool> predicate, int step = 1)
        {
            if (step < 1) step = 1;
            long match = 0, count = 0;
            for (int y = rect.Y; y < rect.Y + rect.Height; y += step)
                for (int x = rect.X; x < rect.X + rect.Width; x += step)
                {
                    count++;
                    if (predicate(b.GetPixel(x, y))) match++;
                }
            return count == 0 ? 0 : (double)match / count;
        }

        /// <summary>Fraction of pixels along the horizontal mid-line of <paramref name="rect"/>
        /// matching <paramref name="predicate"/> (e.g. a health/progress bar fill).</summary>
        public static double RowRatio(Bitmap b, Rectangle rect, System.Func<Color, bool> predicate)
        {
            int midY = rect.Y + rect.Height / 2;
            int match = 0, total = 0;
            for (int x = rect.X; x < rect.X + rect.Width; x++)
            {
                total++;
                if (predicate(b.GetPixel(x, midY))) match++;
            }
            return total == 0 ? 0 : (double)match / total;
        }
    }
}
