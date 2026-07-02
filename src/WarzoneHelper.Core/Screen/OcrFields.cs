using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace WarzoneHelper.Core.Screen
{
    /// <summary>Central registry of OCR field specs. Every OCR read goes through one of these.</summary>
    public static class OcrFields
    {
        private static readonly BigInteger LobbyMin = BigInteger.Pow(2, 59);
        private static readonly BigInteger LobbyMax = BigInteger.Pow(2, 64);

        // Names that disqualify a player-name read (lobby/menu UI chrome).
        public static readonly string[] Chrome =
        {
            "inviteplayer", "invite", "party", "yoursquad", "online", "viewallfriends", "friends",
            "weekly", "daily", "rewards", "challenge", "viewall", "available", "tokens", "foes",
            "battlepass", "operators", "searching", "spectating"
        };

        /// <summary>~19-digit session id: 18-20 digits, a 60-64 bit number, not all one digit.</summary>
        public static readonly OcrField LobbyId = new OcrField
        {
            Name = "lobbyId",
            Whitelist = "0123456789",
            SingleLine = true,
            MinLength = 18,
            MaxLength = 20,
            // A 19-digit id fails on any single misread digit, so demand high confidence.
            Establish = 4, Overturn = 8, Window = 24,
            Clean = s => new string(s.Where(char.IsDigit).ToArray()),  // drop OCR spaces/junk
            Pattern = new Regex(@"(?<v>\d{18,20})", RegexOptions.Compiled),
            Validate = v =>
            {
                if (v.All(c => c == v[0])) return false;
                return BigInteger.TryParse(v, out var n) && n >= LobbyMin && n < LobbyMax;
            }
        };

        /// <summary>A player name: 2-24 chars, not UI chrome, contains a real letter run, and has a
        /// lowercase letter / digit / clan-tag (rejects all-caps UI headers).</summary>
        public static readonly OcrField PlayerName = new OcrField
        {
            Name = "playerName",
            Whitelist = null,           // names vary too much to whitelist; validate by shape
            MinLength = 2,
            MaxLength = 24,
            Establish = 2, Overturn = 4, Window = 12,
            Reject = Chrome,
            Pattern = null,
            Validate = v =>
                Regex.IsMatch(v, "[A-Za-z]{3,}") &&
                (v.Any(char.IsLower) || v.Any(char.IsDigit) || v.Contains('['))
        };

        /// <summary>"name#1234567" spectated-player id.</summary>
        public static readonly OcrField SpectateId = new OcrField
        {
            Name = "spectateId",
            MinLength = 3,
            MaxLength = 24,
            Pattern = new Regex(@"([A-Za-z0-9_\-\[\] ]{3,})#(?<v>\d{3,})", RegexOptions.Compiled)
        };

        /// <summary>A small non-negative count/level, 0-9999 (digits only).</summary>
        public static readonly OcrField Level = new OcrField
        {
            Name = "level",
            Whitelist = "0123456789",
            MinLength = 1,
            MaxLength = 4,
            Establish = 3, Overturn = 6, Window = 14,   // level number is often misread
            Pattern = new Regex(@"(?<v>\d{1,4})", RegexOptions.Compiled),
            Validate = v => int.TryParse(v, out var n) && n >= 1 && n <= 1000
        };
    }
}
