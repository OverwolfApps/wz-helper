using System;
using System.Collections.Generic;

namespace WarzoneHelper.Core
{
    /// <summary>
    /// OCR-noise-resistant value holder with asymmetric hysteresis: a candidate value must be
    /// observed <c>establish</c> times to become the current value, and once established a
    /// DIFFERENT value must be observed <c>overturn</c> (more) times in a row to replace it.
    /// A single bad OCR frame can therefore never flip established state mid-round.
    /// </summary>
    public sealed class ConfidenceValue<T>
    {
        private readonly int _establish;
        private readonly int _overturn;
        private readonly IEqualityComparer<T> _cmp;

        private T _value;
        private bool _has;
        private T _candidate;
        private int _candidateCount;

        public ConfidenceValue(int establish = 2, int overturn = 3, IEqualityComparer<T> comparer = null)
        {
            _establish = Math.Max(1, establish);
            _overturn = Math.Max(1, overturn);
            _cmp = comparer ?? EqualityComparer<T>.Default;
        }

        public T Value => _value;
        public bool Has => _has;

        /// <summary>Feed one observation. Returns true when the established value changed.</summary>
        public bool Observe(T v)
        {
            if (_has && _cmp.Equals(v, _value))
            {
                _candidateCount = 0; // current value re-confirmed; discard any pending challenger
                return false;
            }

            if (_candidateCount > 0 && _cmp.Equals(v, _candidate)) _candidateCount++;
            else { _candidate = v; _candidateCount = 1; }

            int needed = _has ? _overturn : _establish;
            if (_candidateCount >= needed)
            {
                _value = _candidate;
                _has = true;
                _candidateCount = 0;
                return true;
            }
            return false;
        }
    }
}
