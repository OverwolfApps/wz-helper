using System.Collections.Generic;
using System.Linq;

namespace GameHelper.Core.Screen
{
    /// <summary>
    /// Confidence gate for a single OCR field. Feed it raw OCR text each frame; it parses/validates
    /// via the field spec and tallies a rolling window of recent VALID reads. A value is only
    /// "set" once it wins the window vote by the field's Establish count (not necessarily consecutive
    /// frames), and an established value is only replaced when a different value reaches the (larger)
    /// Overturn count. One bad frame therefore can never flip an established value.
    /// </summary>
    public sealed class FieldTracker
    {
        private readonly OcrField _field;
        private readonly Queue<string> _window = new Queue<string>();
        private string _value;
        private bool _established;

        public FieldTracker(OcrField field) { _field = field; }

        public string Value => _value;
        public bool Has => _established;

        /// <summary>Feed one raw OCR read. Returns true when the established value changed.</summary>
        public bool Observe(string rawOcr)
        {
            var v = _field.Parse(rawOcr);
            if (v == null) return false;                 // invalid reads don't vote

            _window.Enqueue(v);
            while (_window.Count > _field.Window) _window.Dequeue();

            var top = _window.GroupBy(x => x).OrderByDescending(g => g.Count()).First();
            int needed = _established ? _field.Overturn : _field.Establish;
            if (top.Count() >= needed && top.Key != _value)
            {
                _value = top.Key;
                _established = true;
                return true;
            }
            return false;
        }

        public void Reset() { _window.Clear(); _value = null; _established = false; }
    }
}
