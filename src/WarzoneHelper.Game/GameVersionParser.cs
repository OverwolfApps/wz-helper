using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// Splits the on-screen build/version watermark into named parts, e.g.
    ///   12.11.27503415 [66-0.1019] 10413+11: [7400] [15] [1783011671.pl.Ga.bnet] [0001228ec00]
    ///   └─ version ──┘ └ config ─┘ └changelist┘ └b1┘ └b2┘ └─ epoch . platform ─┘ └── hash ──┘
    /// Each part is best-effort (extracted independently) so one garbled segment doesn't lose the
    /// rest. Together they form a much more unique fingerprint than the leading version alone.
    /// </summary>
    public static class GameVersionParser
    {
        private static readonly Regex Version = new Regex(@"\d{1,2}\.\d{1,2}\.\d{4,}", RegexOptions.Compiled);
        private static readonly Regex Config = new Regex(@"\d{1,2}\.\d{1,2}\.\d{4,}\[([^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex Changelist = new Regex(@"(\d+)\+(\d+)", RegexOptions.Compiled);
        private static readonly Regex EpochPlatform = new Regex(@"\[(\d{9,10})\.([A-Za-z][\w.]*)\]", RegexOptions.Compiled);
        private static readonly Regex Hash = new Regex(@"\[([0-9a-fA-F]{6,})\]\s*$", RegexOptions.Compiled);

        public static Dictionary<string, object> Parse(string s)
        {
            var d = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(s)) return d;
            d["raw"] = s;

            var v = Version.Match(s); if (v.Success) d["version"] = v.Value;
            var cfg = Config.Match(s); if (cfg.Success) d["config"] = cfg.Groups[1].Value;
            var cl = Changelist.Match(s); if (cl.Success) { d["changelist"] = cl.Groups[1].Value; d["patch"] = cl.Groups[2].Value; }
            var ep = EpochPlatform.Match(s);
            if (ep.Success) { d["epoch"] = ep.Groups[1].Value; d["platform"] = ep.Groups[2].Value; }
            var h = Hash.Match(s); if (h.Success) d["hash"] = h.Groups[1].Value;
            return d;
        }
    }
}
