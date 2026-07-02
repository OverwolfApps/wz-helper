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
        // Perf fields, each keyed by its own Name (fps, gameLatencyMs, packetLossPct, ...). Plain
        // LATENCY is handled separately (below) so GAME LATENCY can't be double-counted.
        private static readonly OcrField[] Fields =
        {
            OcrFields.Fps, OcrFields.GameLatency, OcrFields.PacketLoss,
            OcrFields.GpuTemp, OcrFields.VramPct, OcrFields.Clock,
        };

        public static Dictionary<string, object> Parse(string text)
        {
            var d = MetricParser.Parse(text, Fields);
            if (string.IsNullOrWhiteSpace(text)) return d;
            // Strip GAME LATENCY before matching plain LATENCY so it isn't double-counted.
            var noGame = OcrFields.GameLatency.Pattern.Replace(text, "");
            var lat = OcrFields.Latency.Parse(noGame);
            if (lat != null && int.TryParse(lat, out var n)) d[OcrFields.Latency.Name] = n;
            return d;
        }
    }
}
