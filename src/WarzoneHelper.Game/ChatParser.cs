using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using GameHelper.Core.Screen;
using GameHelper.Core.Text;
namespace WarzoneHelper.Game
{
    public struct ChatMessage
    {
        public string Channel;   // MATCH / PARTY / SQUAD / ALL
        public string Name;
        public string Text;
        /// <summary>Normalized identity for dedupe.</summary>
        public string Key;
    }

    /// <summary>
    /// Parses the in-game chat overlay. Each message is a header line "[CHANNEL] name" followed by
    /// one or more body lines, e.g.:
    ///     [SQUAD] bist gut genuuug
    ///     hallo
    /// A line is only treated as a message once we see a valid channel header; loose OCR lines with
    /// no header (HUD text, the "Press F2 ... Chat Channels" hint) are ignored.
    /// </summary>
    public static class ChatParser
    {
        // Channel keyword in brackets at the line start, capturing the trailing name. The CLOSING
        // bracket is required so ordinary words containing a channel token (e.g. "h[ALL]o" in
        // "hallo") aren't misread as headers.
        private static readonly Regex Header = new Regex(
            @"^.{0,3}[\[\(]\s*(MATCH|PARTY|SQUAD|ALL)\s*[\]\)]\s*(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // UI text that leaks into the chat region and must never become a "message". Covers the
        // "Press F2 in game to access the Chat Channels" hint (and its many garbled OCR forms —
        // "access the Chat Chai LE", "at the Chat Channe", "Chat Channt SE 4 y") plus the chat input
        // box placeholder ("Match: TAB to change text chat, type / for commands"). "chat cha" alone
        // catches Channel/Channels/Chai/Channt/Cha, which every hint variant contains.
        private static readonly Regex Noise = new Regex(
            @"press\s*f2|chat\s*cha|access.{0,12}chat|in\s*game\s*to|tab\s*to\s*change|text\s*chat|for\s*command",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const int MaxBodyLines = 3;

        public static List<ChatMessage> Parse(IEnumerable<string> lines)
        {
            var msgs = new List<ChatMessage>();
            string channel = null, name = null;
            var body = new List<string>();

            void Flush()
            {
                if (channel != null)
                {
                    var text = string.Join(" ", body).Trim();
                    if (text.Length > 0 && TextOps.HasLetters(name + text))
                    {
                        var cleanName = Clean(name);
                        var msg = new ChatMessage
                        {
                            // Validated channel + player-name via the shared field specs (fall back to
                            // the cleaned raw name so a hard-to-OCR name never drops a real message).
                            Channel = OcrFields.ChatChannel.Parse(channel) ?? channel.ToUpperInvariant(),
                            Name = OcrFields.PlayerName.Parse(cleanName) ?? cleanName,
                            Text = text
                        };
                        msg.Key = TextOps.Norm(msg.Channel + msg.Name + msg.Text);
                        if (msg.Key.Length >= 4) msgs.Add(msg);
                    }
                }
                channel = null; name = null; body.Clear();
            }

            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || Noise.IsMatch(line)) continue;

                var m = Header.Match(line);
                if (m.Success)
                {
                    Flush();                                  // previous message complete
                    channel = m.Groups[1].Value;
                    name = m.Groups[2].Value.Trim();
                }
                else if (channel != null && body.Count < MaxBodyLines)
                {
                    body.Add(line);                            // message body line(s)
                }
                // else: stray line before any header -> ignore
            }
            Flush();
            return msgs;
        }


        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int start = 0; while (start < s.Length && !(char.IsLetterOrDigit(s[start]) || s[start] == '[')) start++;
            int end = s.Length - 1; while (end >= 0 && !(char.IsLetterOrDigit(s[end]) || s[end] == ']')) end--;
            return end < start ? "" : Regex.Replace(s.Substring(start, end - start + 1).Trim(), @"\s{2,}", " ");
        }
    }
}
