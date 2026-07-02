using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarzoneHelper.Core.Screen
{
    public struct PartyMember
    {
        public int? Level;
        public string Name;
        /// <summary>Normalized identity for stable-set comparison.</summary>
        public string Key;
        /// <summary>Which section header this row fell under: "party" | "online" | "offline",
        /// or null when the panel has no headers (in-game squad / match list).</summary>
        public string Group;
    }

    /// <summary>
    /// Turns raw OCR lines from the lobby party/squad panel into structured members. The panel reads
    /// as rows like "378 [Du] bist gut genuuug" (level, then clan-tag + name, then a platform icon).
    /// The social menu stacks section headers — "PARTY 1/4", then "ONLINE", then "OFFLINE" — over
    /// their members; we detect those headers and tag each row with the section it falls under so the
    /// roster can tell your actual party from friends who merely happen to be online. UI chrome
    /// (INVITE PLAYER / VIEW ALL FRIENDS) and OCR garbage are stripped; the leading level number is
    /// pulled and trailing icon noise cleaned off the name.
    /// </summary>
    public static class PartyParser
    {
        private static readonly Regex LeadingLevel = new Regex(@"^\D{0,3}(\d{1,4})\D", RegexOptions.Compiled);

        public static List<PartyMember> Parse(IEnumerable<string> lines)
        {
            var result = new List<PartyMember>();
            var seen = new HashSet<string>();
            string group = null; // current section; set once we pass a "PARTY"/"ONLINE"/"OFFLINE" header

            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var line = (raw ?? "").Trim();
                if (line.Length < 3) continue;

                var lettersOnly = new string(line.Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (lettersOnly.Length < 3) continue;                          // punctuation/garbage

                // A real party/squad row starts with the player's level. Require a valid level in
                // [1,1000] — this rejects OCR junk and UI text (e.g. "SEARCHING FOR PLAYERS") that
                // has no leading level number.
                var m = LeadingLevel.Match(line);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out var lv) || lv < 1 || lv > 1000)
                {
                    // Not a member row — is it a section header? (Checked here, after the level test,
                    // so a real player like "45 Onliner" isn't mistaken for the ONLINE header.)
                    var sect = SectionOf(lettersOnly);
                    if (sect != null) group = sect;
                    continue;
                }
                int? level = lv;
                var rest = line.Substring(m.Index + m.Length - 1); // keep the char after the level

                // Validate the cleaned name against the shared player-name field spec (length,
                // chrome rejection, real letter run, not all-caps UI text).
                var name = OcrFields.PlayerName.Parse(CleanName(rest));
                if (name == null) continue;

                // Identity key is LETTERS-ONLY so frame-to-frame digit jitter (status dots, online
                // counts) doesn't make an otherwise-stable member look like it changed.
                var key = new string(name.Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (key.Length < 3 || !seen.Add(key)) continue;                // dedupe within a frame

                result.Add(new PartyMember { Level = level, Name = name, Key = key, Group = group });
            }
            return result;
        }

        /// <summary>Detect a lobby social-panel section header from a non-member line.</summary>
        private static string SectionOf(string lettersOnly)
        {
            if (lettersOnly.StartsWith("party")) return "party";
            if (lettersOnly.StartsWith("offline")) return "offline";
            if (lettersOnly.StartsWith("online")) return "online";
            return null;
        }

        private static readonly Regex TrailingJunk = new Regex(@"\s+\p{L}{1,2}$", RegexOptions.Compiled);

        private static string CleanName(string s)
        {
            s = (s ?? "").Trim();
            // Rows read as "<emblem-noise> [Tag] Name <icon-noise>". If a clan tag is near the start,
            // anchor the name at it so the rank-emblem OCR garbage (e.g. "ff,", "3K,") is dropped.
            int br = s.IndexOf('[');
            if (br > 0 && br <= 6) s = s.Substring(br);
            // Trim leading junk up to the first letter or '['.
            int start = 0;
            while (start < s.Length && !(char.IsLetter(s[start]) || s[start] == '[')) start++;
            // Trim trailing junk back to the last letter/digit/']'.
            int end = s.Length - 1;
            while (end >= 0 && !(char.IsLetterOrDigit(s[end]) || s[end] == ']')) end--;
            if (end < start) return "";
            var name = Regex.Replace(s.Substring(start, end - start + 1).Trim(), @"\s{2,}", " ");
            // Drop a leading emblem remnant: a 1-2 letter token followed by a comma (e.g. "K, her again").
            name = Regex.Replace(name, @"^\p{L}{1,2},\s*", "");
            // Strip a trailing 1-2 letter token (platform/icon OCR bleed), e.g. "... genuuug re".
            return TrailingJunk.Replace(name, "").Trim();
        }
    }
}
