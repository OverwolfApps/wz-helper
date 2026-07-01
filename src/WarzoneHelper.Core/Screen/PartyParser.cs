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
    }

    /// <summary>
    /// Turns raw OCR lines from the lobby party/squad panel into structured members. The panel reads
    /// as rows like "378 [Du] bist gut genuuug" (level, then clan-tag + name, then a platform icon).
    /// We strip UI chrome (INVITE PLAYER / PARTY x/32 / ONLINE / VIEW ALL FRIENDS) and OCR garbage,
    /// pull the leading level number, and clean the trailing icon noise off the name.
    /// </summary>
    public static class PartyParser
    {
        // Lines whose (letters-only) form contains any of these are UI chrome, not a player.
        private static readonly string[] Chrome =
        {
            "inviteplayer", "invite", "party", "yoursquad", "online", "viewallfriends", "friends"
        };

        private static readonly Regex LeadingLevel = new Regex(@"^\D{0,3}(\d{1,4})\D", RegexOptions.Compiled);
        // A plausible name has a run of 3+ letters (optionally within a [tag]).
        private static readonly Regex HasName = new Regex(@"[A-Za-z]{3,}", RegexOptions.Compiled);

        public static List<PartyMember> Parse(IEnumerable<string> lines)
        {
            var result = new List<PartyMember>();
            var seen = new HashSet<string>();

            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var line = (raw ?? "").Trim();
                if (line.Length < 3) continue;

                var lettersOnly = new string(line.Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (lettersOnly.Length < 3) continue;                          // punctuation/garbage
                if (Chrome.Any(c => lettersOnly.Contains(c))) continue;         // UI chrome

                int? level = null;
                var m = LeadingLevel.Match(line);
                var rest = line;
                if (m.Success)
                {
                    if (int.TryParse(m.Groups[1].Value, out var lv) && lv > 0 && lv <= 9999) level = lv;
                    rest = line.Substring(m.Index + m.Length - 1); // keep the char after the level
                }

                var name = CleanName(rest);
                if (name.Length < 3 || !HasName.IsMatch(name)) continue;       // no real name -> skip

                // Identity key is LETTERS-ONLY so frame-to-frame digit jitter (status dots, online
                // counts) doesn't make an otherwise-stable member look like it changed.
                var key = new string(name.Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (key.Length < 3 || !seen.Add(key)) continue;                // dedupe within a frame

                result.Add(new PartyMember { Level = level, Name = name, Key = key });
            }
            return result;
        }

        private static string CleanName(string s)
        {
            s = s.Trim();
            // Trim leading junk up to the first letter or '['.
            int start = 0;
            while (start < s.Length && !(char.IsLetter(s[start]) || s[start] == '[')) start++;
            // Trim trailing junk back to the last letter/digit/']'.
            int end = s.Length - 1;
            while (end >= 0 && !(char.IsLetterOrDigit(s[end]) || s[end] == ']')) end--;
            if (end < start) return "";
            var name = s.Substring(start, end - start + 1).Trim();
            return Regex.Replace(name, @"\s{2,}", " "); // collapse runs of spaces
        }
    }
}
