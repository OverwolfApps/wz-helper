using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarzoneHelper.Core.Screen
{
    public struct FeedItem
    {
        public string Type;    // "kill" | "event"
        public string Killer;  // kill
        public string Victim;  // kill
        public string Player;  // event
        public string Event;   // event kind: disconnected | banned | left
        public string Key;     // dedupe
    }

    /// <summary>
    /// Parses the left-middle feed: killfeed ("Killer [weapon-icon] Victim") and the event log
    /// ("Player Disconnected/Banned"). The weapon icon OCRs as a gap/garbage, so a kill line is
    /// split into the first and last name-ish chunk. Event lines are detected by keyword.
    /// </summary>
    public static class FeedParser
    {
        private static readonly Regex EventKind = new Regex(
            @"\b(disconnected|banned|left the game|left|quit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // A name chunk: optional [tag] plus letters/digits/underscore (>=3 chars overall).
        private static readonly Regex NameChunk = new Regex(
            @"(\[?[A-Za-z0-9_]{1,10}\]?\s?)?[A-Za-z][A-Za-z0-9_]{2,}", RegexOptions.Compiled);

        public static List<FeedItem> Parse(IEnumerable<string> lines)
        {
            var items = new List<FeedItem>();
            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var line = (raw ?? "").Trim();
                if (line.Length < 4) continue;

                var ev = EventKind.Match(line);
                if (ev.Success)
                {
                    // "<player> Disconnected" — the player is the text before the keyword.
                    var before = line.Substring(0, ev.Index).Trim();
                    var pl = FirstName(before);
                    if (pl != null)
                        Add(items, new FeedItem { Type = "event", Player = pl, Event = ev.Groups[1].Value.ToLowerInvariant() });
                    continue;
                }

                var names = NameChunk.Matches(line).Cast<Match>().Select(m => m.Value.Trim())
                    .Where(n => Norm(n).Length >= 3).ToList();
                if (names.Count >= 2)
                    Add(items, new FeedItem { Type = "kill", Killer = names.First(), Victim = names.Last() });
            }
            return items;
        }

        private static string FirstName(string s)
        {
            var m = NameChunk.Match(s ?? "");
            return m.Success && Norm(m.Value).Length >= 3 ? m.Value.Trim() : null;
        }

        private static void Add(List<FeedItem> list, FeedItem it)
        {
            it.Key = it.Type == "kill"
                ? "k:" + Norm(it.Killer) + ">" + Norm(it.Victim)
                : "e:" + Norm(it.Player) + ":" + it.Event;
            list.Add(it);
        }

        private static string Norm(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
