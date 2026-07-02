using System.Collections.Generic;

namespace GameHelper.Core.Screen
{
    /// <summary>
    /// Runs a set of label-aware <see cref="OcrField"/> regexes over one OCR'd text strip and
    /// returns the values keyed by each field's Name (int when the value parses as an integer,
    /// otherwise the validated string). Game-agnostic: the field set is supplied by the caller, so
    /// any game's perf/telemetry overlay reuses this by handing over its own fields.
    /// </summary>
    public static class MetricParser
    {
        public static Dictionary<string, object> Parse(string text, IEnumerable<OcrField> fields)
        {
            var d = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(text) || fields == null) return d;
            foreach (var f in fields)
            {
                var v = f?.Parse(text);       // label-aware pattern + range validation
                if (v == null) continue;
                d[f.Name] = int.TryParse(v, out var n) ? (object)n : v;
            }
            return d;
        }
    }
}
