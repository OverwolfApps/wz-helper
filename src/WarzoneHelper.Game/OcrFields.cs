using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using GameHelper.Core.Screen;
namespace WarzoneHelper.Game
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

        /// <summary>On-screen build/version watermark, e.g.
        /// "12.11.27503415[66-0.1019]10413+11:[7400][15][1783011671.pl.Ga.bnet][0001228ec00]".
        /// We capture the WHOLE token (a far more unique fingerprint than the bare version) and
        /// confidence-gate it; GameVersionParser then splits it into named groups. Whitelist covers
        /// the full charset; Clean strips OCR spaces so the token stays contiguous.</summary>
        public static readonly OcrField GameVersion = new OcrField
        {
            Name = "gameVersion",
            Whitelist = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz.[]+-:",
            SingleLine = true,
            MinLength = 20,
            MaxLength = 80,
            Establish = 5, Overturn = 8, Window = 24,
            Clean = s => s.Replace(" ", ""),
            // v = version core + everything after it (the whole watermark token).
            Pattern = new Regex(@"(?<v>\d{1,2}\.\d{1,2}\.\d{4,}\S+)", RegexOptions.Compiled),
            Validate = v => Regex.IsMatch(v, @"^\d{1,2}\.\d{1,2}\.\d{4,}") && v.Length >= 20,
        };

        /// <summary>A Warzone/Activision display name. Activision IDs are 2-16 characters; the
        /// authoritative rule is server-side (profile.callofduty.com/cod/checkUsername) — the site
        /// itself only length-checks client-side, and that endpoint accepts spaces (e.g. "bist gut
        /// genuuug" is valid). So we enforce 2-16 with a lenient charset (unicode letters + digits +
        /// space/underscore/period/hyphen), which still rejects OCR garbage (brackets, commas,
        /// slashes) while not over-restricting real names. Optionally confirmed online via
        /// CodUsernameVerifier. We strip an optional leading clan tag "[TAG]" and a trailing
        /// "#activisionId" first; still rejects UI chrome and requires a letter.</summary>
        public static readonly OcrField PlayerName = new OcrField
        {
            Name = "playerName",
            Whitelist = null,           // names vary too much to whitelist; validate by shape
            MinLength = 2,
            MaxLength = 30,             // coarse guard (tag + name + #id); the 2-16 rule is enforced on the core
            Establish = 2, Overturn = 4, Window = 12,
            Reject = Chrome,
            Pattern = null,
            // Drop rank-emblem / platform-icon OCR bleed (1-2 char edge tokens) before validating,
            // so every name path (party, squad, killfeed, chat, spectate) benefits.
            Clean = StripEdgeTokens,
            Validate = v => NamePattern.IsMatch(v),
        };

        /// <summary>Player-name regex (named groups tag/name/discriminator). The active game profile
        /// sets this from IGameProfile.PlayerNamePattern; the default is the Warzone pattern (clan tag
        /// 1-5 chars, 2-16 unicode name requiring a letter, optional #discriminator).</summary>
        public static Regex NamePattern = new Regex(
            @"^(?:\[(?<tag>[^\]]{1,5})\])?\s*(?<name>(?=[\p{L}\p{N} _.\-]*\p{L})[\p{L}\p{N} _.\-]{2,16})\s*(?:#(?<discriminator>\d{5,12}))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LeadingEdgeToken = new Regex(@"^[\p{L}\p{N}]{1,2}[\s,]+", RegexOptions.Compiled);
        private static readonly Regex TrailingEdgeToken = new Regex(@"[\s]+[\p{L}\p{N}]{1,2}$", RegexOptions.Compiled);

        /// <summary>Drop a leading/trailing 1-2 char OCR-artifact token next to a name — rank-emblem
        /// or platform-icon bleed misread as a short token, e.g. "1f RealName" or "RealName xx" →
        /// "RealName". Only applied when a real name (>= 2 chars) remains, so it never nukes the name.</summary>
        public static string StripEdgeTokens(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var t = TrailingEdgeToken.Replace(LeadingEdgeToken.Replace(name, ""), "").Trim();
            return t.Length >= 2 ? t : name;
        }

        /// <summary>Strip an optional leading clan tag "[TAG]" and trailing "#1234567" to the bare name.</summary>
        public static string CoreName(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            var core = Regex.Replace(v, @"^\[[^\]]{1,6}\]\s*", "");
            core = Regex.Replace(core, @"\s*#\d+$", "");
            return core.Trim();
        }

        /// <summary>Activision display-name shape: 2-16 chars, at least one letter, and only unicode
        /// letters/digits plus space/underscore/period/hyphen (matches what checkUsername accepts,
        /// while rejecting OCR bracket/comma/slash garbage).</summary>
        public static bool IsValidDisplayName(string core) =>
            !string.IsNullOrEmpty(core) && core.Length >= 2 && core.Length <= 16
            && Regex.IsMatch(core, @"^[\p{L}\p{N} _.\-]+$")
            && Regex.IsMatch(core, @"\p{L}");

        /// <summary>"name#1234567" spectated-player id.</summary>
        public static readonly OcrField SpectateId = new OcrField
        {
            Name = "spectateId",
            MinLength = 3,
            MaxLength = 24,
            Pattern = new Regex(@"([A-Za-z0-9_\-\[\] ]{3,})#(?<v>\d{3,})", RegexOptions.Compiled)
        };

        /// <summary>Chars that appear in the top telemetry strip (labels + values). Constrains OCR at
        /// the source so it can't emit stray symbols, without dropping the letter labels the parser
        /// needs. (Name regions are NOT whitelisted — that would drop unicode names.)</summary>
        public static string PerfStripWhitelist = "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789:%.°/";

        // --- Top telemetry overlay metrics (label-aware patterns + sane numeric ranges) ---
        private static OcrField Metric(string name, string pattern, int lo, int hi) => new OcrField
        {
            Name = name,
            Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled),
            MinLength = 1, MaxLength = 5,
            Validate = v => int.TryParse(v, out var n) && n >= lo && n <= hi
        };
        public static readonly OcrField Fps         = Metric("fps",           @"FPS[:\s]+(?<v>\d{1,4})", 1, 1000);
        public static readonly OcrField GameLatency = Metric("gameLatencyMs", @"GAME\s*LATENCY[:\s]+(?<v>\d{1,4})", 0, 2000);
        public static readonly OcrField Latency     = Metric("latencyMs",     @"(?<!GAME[ \t])LATENCY[:\s]+(?<v>\d{1,4})", 0, 2000);
        public static readonly OcrField PacketLoss  = Metric("packetLossPct", @"PACKET\s*LOSS[:\s]+(?<v>\d{1,3})", 0, 100);
        public static readonly OcrField GpuTemp     = Metric("gpuTemp",       @"GPU[:\s]+(?<v>\d{1,3})", 0, 130);
        public static readonly OcrField VramPct     = Metric("vramPct",       @"VRAM\s*USAGE[:\s]+(?<v>\d{1,3})", 0, 100);
        public static readonly OcrField Clock       = new OcrField
        {
            Name = "clock", MinLength = 3, MaxLength = 5,
            Pattern = new Regex(@"(?<v>[0-2]?\d:[0-5]\d)", RegexOptions.Compiled)
        };

        /// <summary>Party/invite code: EXACTLY 5 uppercase letters/digits (e.g. 6V4DK, KDFLK, LLJGJ),
        /// isolated. High confidence since the center region also sees other UI text.</summary>
        public static readonly OcrField PartyCode = new OcrField
        {
            Name = "partyCode",
            Whitelist = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            SingleLine = true,
            MinLength = 5,
            MaxLength = 5,
            Establish = 3, Overturn = 6, Window = 16,
            Clean = s => new string(s.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToUpperInvariant(),
            Pattern = new Regex(@"^(?<v>[A-Z0-9]{5})$", RegexOptions.Compiled),
            // "SSSION"/"SESSION" is a recurring OCR false positive (from the word SESSION nearby).
            Reject = new[] { "sssion", "session" },
        };

        /// <summary>Chat channel tag: MATCH / PARTY / SQUAD / ALL.</summary>
        public static readonly OcrField ChatChannel = new OcrField
        {
            Name = "chatChannel", MinLength = 3, MaxLength = 5,
            Pattern = new Regex(@"(?<v>MATCH|PARTY|SQUAD|ALL)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            Clean = s => s.ToUpperInvariant()
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
