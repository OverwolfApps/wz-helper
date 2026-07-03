using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace GameHelper.Core.Screen
{
    #region OCR pattern dump (debug data collection)
    /// <summary>
    /// Debug helper: appends UNIQUE raw OCR reads per category to txt files so we can eyeball the
    /// real shapes of strings whose exact pattern we don't know yet (party codes, lobby ids, version
    /// watermarks, chat/feed/inspect text, ...). Off unless <see cref="Enabled"/> is set. One file
    /// per category under the configured dump dir; duplicates are skipped in-memory.
    /// </summary>
    public static class OcrDump
    {
        public static bool Enabled;
        private static string _dir;
        private static readonly object _io = new object();
        private static readonly ConcurrentDictionary<string, HashSet<string>> _seen =
            new ConcurrentDictionary<string, HashSet<string>>();

        public static void Init(string dir)
        {
            _dir = dir;
            try { if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); } catch { }
        }

        /// <summary>Record a raw read under a category (e.g. "partycode"); only new values are written.</summary>
        public static void Dump(string category, string text)
        {
            if (!Enabled || _dir == null || string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            var set = _seen.GetOrAdd(category, _ => new HashSet<string>());
            bool isNew;
            lock (set) isNew = set.Add(text);
            if (!isNew) return;
            try
            {
                lock (_io)
                    File.AppendAllText(Path.Combine(_dir, category + ".txt"), text + Environment.NewLine);
            }
            catch { /* best-effort */ }
        }
    }
    #endregion
}
