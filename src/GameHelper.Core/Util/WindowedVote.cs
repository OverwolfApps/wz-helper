using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHelper.Core.Util
{
    /// <summary>
    /// Gap-tolerant confidence vote over keys: a key must be cast <c>threshold</c> times within a
    /// rolling time <c>window</c> before it "passes" (fires once, then its tally is cleared). Unlike
    /// strict consecutive counting, a dropped/late sighting doesn't reset progress as long as it
    /// recurs within the window — good for content (e.g. chat lines) that lingers on screen while
    /// OCR flickers. Call <see cref="Prune"/> each tick to expire stale tallies. Not thread-safe.
    /// </summary>
    public sealed class WindowedVote
    {
        private readonly int _threshold;
        private readonly TimeSpan _window;
        private readonly Dictionary<string, (int count, DateTime last)> _votes =
            new Dictionary<string, (int, DateTime)>();

        public WindowedVote(int threshold, TimeSpan window)
        {
            _threshold = threshold < 1 ? 1 : threshold;
            _window = window;
        }

        /// <summary>Record a sighting of <paramref name="key"/> at <paramref name="now"/>. Returns
        /// true when it reaches the threshold within the window (and clears it so it fires once).</summary>
        public bool Cast(string key, DateTime now)
        {
            if (key == null) return false;
            var count = _votes.TryGetValue(key, out var e) && now - e.last <= _window ? e.count + 1 : 1;
            if (count >= _threshold) { _votes.Remove(key); return true; }
            _votes[key] = (count, now);
            return false;
        }

        /// <summary>Drop tallies not seen within the window (bounds memory).</summary>
        public void Prune(DateTime now)
        {
            if (_votes.Count == 0) return;
            foreach (var k in _votes.Where(kv => now - kv.Value.last > _window).Select(kv => kv.Key).ToList())
                _votes.Remove(k);
        }

        public void Clear() => _votes.Clear();
    }
}
