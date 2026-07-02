using System.Collections.Generic;

namespace GameHelper.Core.Util
{
    /// <summary>
    /// Debounces a jittery per-frame value: only accepts a candidate once it has been observed the
    /// same for N consecutive frames, and only reports it when it differs from the last accepted
    /// value. Ideal for OCR fields that flip between frames (lobby id, a party-set key). The
    /// accepted <see cref="Value"/> persists until a new value stabilizes. Not thread-safe.
    /// </summary>
    public sealed class StableValue<T>
    {
        private readonly int _frames;
        private readonly IEqualityComparer<T> _cmp;
        private T _pending;
        private int _count;

        public T Value { get; private set; }
        public bool HasValue { get; private set; }

        public StableValue(int frames, IEqualityComparer<T> comparer = null)
        {
            _frames = frames < 1 ? 1 : frames;
            _cmp = comparer ?? EqualityComparer<T>.Default;
        }

        /// <summary>Feed the current frame's candidate. Returns true exactly once when a NEW value
        /// has held for the required number of consecutive frames (and differs from Value).</summary>
        public bool Observe(T candidate)
        {
            if (_count > 0 && _cmp.Equals(candidate, _pending)) _count++;
            else { _pending = candidate; _count = 1; }

            if (_count == _frames && !(HasValue && _cmp.Equals(candidate, Value)))
            {
                Value = candidate;
                HasValue = true;
                return true;
            }
            return false;
        }

        public void Reset() { _pending = default; _count = 0; Value = default; HasValue = false; }
    }
}
