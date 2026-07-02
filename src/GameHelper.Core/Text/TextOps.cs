using System;
using System.Linq;

namespace GameHelper.Core.Text
{
    /// <summary>
    /// Game-agnostic string helpers shared by parsers and rosters: identity normalization, fuzzy
    /// similarity and a quick letter check. No game knowledge here.
    /// </summary>
    public static class TextOps
    {
        /// <summary>Normalized identity key: letters + digits only, lowercased. Stable against
        /// punctuation / spacing / case jitter (used for de-dupe and matching).</summary>
        public static string Norm(string s) =>
            new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>True if the string contains at least one letter.</summary>
        public static bool HasLetters(string s) => s != null && s.Any(char.IsLetter);

        /// <summary>Levenshtein similarity in [0,1] between two strings (1 = identical, 0 = nothing
        /// in common). Used for fuzzy-linking OCR-variant names to an existing cache entry.</summary>
        public static double Similarity(string a, string b)
        {
            a = a ?? string.Empty; b = b ?? string.Empty;
            if (a == b) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;
            int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            int dist = prev[b.Length];
            return 1.0 - (double)dist / Math.Max(a.Length, b.Length);
        }
    }
}
