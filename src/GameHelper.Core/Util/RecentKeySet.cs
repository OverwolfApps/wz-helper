using System.Collections.Generic;

namespace GameHelper.Core.Util
{
    /// <summary>
    /// A fixed-capacity FIFO set of recently-seen keys, for de-duping scrolling / repeated events
    /// (e.g. chat lines, killfeed entries that linger on screen). O(1) Contains/Add; the oldest key
    /// is evicted once capacity is exceeded. Not thread-safe — use from a single monitor thread.
    /// </summary>
    public sealed class RecentKeySet
    {
        private readonly LinkedList<string> _order = new LinkedList<string>();
        private readonly HashSet<string> _set = new HashSet<string>();
        private readonly int _capacity;

        public RecentKeySet(int capacity = 40) { _capacity = capacity < 1 ? 1 : capacity; }

        public bool Contains(string key) => key != null && _set.Contains(key);

        /// <summary>Record a key as seen, evicting the oldest past capacity. Returns false if it was
        /// already present.</summary>
        public bool Add(string key)
        {
            if (key == null || !_set.Add(key)) return false;
            _order.AddLast(key);
            while (_order.Count > _capacity)
            {
                var first = _order.First.Value;
                _order.RemoveFirst();
                _set.Remove(first);
            }
            return true;
        }

        public void Clear() { _order.Clear(); _set.Clear(); }
    }
}
