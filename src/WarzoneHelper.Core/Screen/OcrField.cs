using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarzoneHelper.Core.Screen
{
    /// <summary>
    /// Declarative spec for a single OCR'd field: what chars Tesseract may emit, how long the value
    /// may be, a required pattern (with optional extraction group), words that disqualify it, and an
    /// optional custom predicate. One <see cref="Parse"/> call validates + extracts, returning null
    /// when the read doesn't meet the spec. This makes every OCR read resilient and consistent.
    /// </summary>
    public sealed class OcrField
    {
        public string Name;
        /// <summary>Tesseract tessedit_char_whitelist for this field (null = allow all).</summary>
        public string Whitelist;
        public bool SingleLine = true;
        public int MinLength = 1;
        public int MaxLength = 256;
        /// <summary>Value must match this. If it has a capture group named "v" or group[1], that is the
        /// extracted value; otherwise the whole (trimmed) match is used.</summary>
        public Regex Pattern;
        /// <summary>Reject if the letters-only form contains any of these (UI chrome / noise).</summary>
        public string[] Reject;
        /// <summary>Extra check on the extracted value (e.g. numeric range, bit-length).</summary>
        public Func<string, bool> Validate;
        /// <summary>Optional cleanup applied to the raw text before matching.</summary>
        public Func<string, string> Clean;

        /// <summary>Validate + extract, or null if the OCR text doesn't satisfy the spec.</summary>
        public string Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();
            if (Clean != null) { s = Clean(s)?.Trim(); if (string.IsNullOrEmpty(s)) return null; }

            if (Reject != null && Reject.Length > 0)
            {
                var letters = new string(s.Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (Reject.Any(r => letters.Contains(r))) return null;
            }

            string value = s;
            if (Pattern != null)
            {
                var m = Pattern.Match(s);
                if (!m.Success) return null;
                var g = m.Groups["v"];
                value = (g != null && g.Success) ? g.Value : (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value);
                value = value.Trim();
            }

            if (value.Length < MinLength || value.Length > MaxLength) return null;
            if (Validate != null && !Validate(value)) return null;
            return value;
        }
    }
}
