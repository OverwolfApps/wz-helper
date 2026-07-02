using System.Collections.Generic;
using System.Text.RegularExpressions;

using GameHelper.Core.Screen;
namespace WarzoneHelper.Game
{
    /// <summary>
    /// Parses the top telemetry overlay strip, e.g.:
    ///   "FPS: 123 LATENCY: 16 MS PACKET LOSS: 0 % GPU: 51 ° GAME LATENCY: 12 MS VRAM USAGE: 30 % 19:07"
    /// The overlay's position/width shifts, so we OCR a wide top band and pull whatever key/values
    /// are present. GAME LATENCY is the real ping to the match server (ICMP is blocked, so this is
    /// the only reliable latency source).
    /// </summary>
    public static class PerfParser
    {
        public static Dictionary<string, object> Parse(string text)
        {
            var d = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(text)) return d;

            AddInt(d, "fps", OcrFields.Fps, text);
            AddInt(d, "gameLatencyMs", OcrFields.GameLatency, text);
            // Strip GAME LATENCY before matching plain LATENCY so it isn't double-counted.
            var noGame = OcrFields.GameLatency.Pattern.Replace(text, "");
            AddInt(d, "latencyMs", OcrFields.Latency, noGame);
            AddInt(d, "packetLossPct", OcrFields.PacketLoss, text);
            AddInt(d, "gpuTemp", OcrFields.GpuTemp, text);
            AddInt(d, "vramPct", OcrFields.VramPct, text);

            var clock = OcrFields.Clock.Parse(text);
            if (clock != null) d["clock"] = clock;
            return d;
        }

        private static void AddInt(Dictionary<string, object> d, string key, OcrField field, string text)
        {
            var v = field.Parse(text);   // label-aware pattern + range validation
            if (v != null && int.TryParse(v, out var n)) d[key] = n;
        }
    }
}
