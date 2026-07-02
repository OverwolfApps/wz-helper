using System.Collections.Generic;
using System.Text.RegularExpressions;

using GameHelper.Core.Screen;
namespace WarzoneHelper.Game
{
    /// <summary>
    /// Parses the Inspect-Player detail panel — the richest per-player data in the game: the
    /// Activision ID (#NNNNNNN), platform, level, WZ rank, and input device. Only returns data when
    /// an Activision ID is present (so it fires only on the actual inspect screen).
    /// </summary>
    public static class InspectParser
    {
        private static readonly Regex ActId = new Regex(@"#(\d{6,})", RegexOptions.Compiled);
        private static readonly Regex Level = new Regex(@"LEVEL\s*(\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Rank = new Regex(
            @"\b(BRONZE|SILVER|GOLD|PLATINUM|DIAMOND|CRIMSON|IRIDESCENT|TOP\s*250)\b(?:\s+([IVX]{1,3}))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Input = new Regex(
            @"Playing on (?:a )?(Controller|Keyboard(?:\s*&?\s*Mouse)?|Mouse)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Platform = new Regex(
            @"\b(Xbox|PlayStation|Steam|Battle\.?net|Activision|PC)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static Dictionary<string, object> Parse(string text)
        {
            var d = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(text)) return d;

            var id = ActId.Match(text);
            if (!id.Success) return d;                 // not the inspect screen
            d["activisionId"] = id.Groups[1].Value;

            var lvl = Level.Match(text);
            if (lvl.Success && int.TryParse(lvl.Groups[1].Value, out var n) && n >= 1 && n <= 1000) d["level"] = n;

            var rk = Rank.Match(text);
            if (rk.Success) d["rank"] = (rk.Groups[1].Value + " " + rk.Groups[2].Value).Trim();

            var inp = Input.Match(text);
            if (inp.Success) d["input"] = inp.Groups[1].Value;

            var plat = Platform.Match(text);
            if (plat.Success) d["platform"] = plat.Groups[1].Value;
            return d;
        }
    }
}
