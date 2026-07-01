using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WarzoneHelper.Core.Screen
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
        private static readonly Regex Fps        = new Regex(@"FPS[:\s]+(\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex GameLatency= new Regex(@"GAME\s*LATENCY[:\s]+(\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Latency    = new Regex(@"(?<!GAME[ \t])LATENCY[:\s]+(\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PacketLoss = new Regex(@"PACKET\s*LOSS[:\s]+(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Gpu        = new Regex(@"GPU[:\s]+(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Vram       = new Regex(@"VRAM\s*USAGE[:\s]+(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Clock      = new Regex(@"\b([0-2]?\d:[0-5]\d)\b", RegexOptions.Compiled);

        public static Dictionary<string, object> Parse(string text)
        {
            var d = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(text)) return d;

            AddInt(d, "fps", Fps, text, 1, 1000);
            AddInt(d, "gameLatencyMs", GameLatency, text, 0, 2000);
            // Strip GAME LATENCY before matching plain LATENCY so it isn't double-counted.
            var noGame = GameLatency.Replace(text, "");
            AddInt(d, "latencyMs", Latency, noGame, 0, 2000);
            AddInt(d, "packetLossPct", PacketLoss, text, 0, 100);
            AddInt(d, "gpuTemp", Gpu, text, 0, 130);
            AddInt(d, "vramPct", Vram, text, 0, 100);

            var c = Clock.Match(text);
            if (c.Success) d["clock"] = c.Groups[1].Value;
            return d;
        }

        private static void AddInt(Dictionary<string, object> d, string key, Regex rx, string text, int lo, int hi)
        {
            var m = rx.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && v >= lo && v <= hi)
                d[key] = v;
        }
    }
}
