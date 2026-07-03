using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GameHelper.Core.Events
{
    /// <summary>One field of an event's payload, for the self-describing catalog.</summary>
    public sealed class EventFieldDoc
    {
        public string name { get; set; }
        public string type { get; set; }           // string | int | number | bool | string[] | int[] | object | object[]
        public string description { get; set; }
        public EventFieldDoc() { }
        public EventFieldDoc(string name, string type, string description)
        { this.name = name; this.type = type; this.description = description; }
    }

    /// <summary>Describes one event name: its source, purpose and payload fields.</summary>
    public sealed class EventDoc
    {
        public string name { get; set; }
        public string source { get; set; }
        public string description { get; set; }
        public List<EventFieldDoc> fields { get; set; } = new List<EventFieldDoc>();
        public EventDoc() { }
        public EventDoc(string name, string source, string description, params EventFieldDoc[] fields)
        { this.name = name; this.source = source; this.description = description; this.fields = fields.ToList(); }
    }

    /// <summary>
    /// Aggregates the self-describing event catalog sent in HELPER_STARTED. The actual declarations
    /// live as <see cref="EventDef"/> fields on holder classes (CoreEvents + the game's); this just
    /// reflects them and hashes the result so consumers can detect schema changes across runs.
    /// </summary>
    public static class EventCatalog
    {
        /// <summary>Generic Core event docs, reflected from the CoreEvents declarations.</summary>
        public static IReadOnlyList<EventDoc> Core { get; } = EventDef.DocsFrom(typeof(CoreEvents));

        /// <summary>Stable (process-independent) hash of a catalog: FNV-1a over each event's name and
        /// field name:type pairs, order-normalized. Consumers persist it to detect schema changes.</summary>
        public static string Hash(IEnumerable<EventDoc> docs)
        {
            var sb = new StringBuilder();
            foreach (var d in docs.OrderBy(d => d.name, System.StringComparer.Ordinal))
            {
                sb.Append(d.name).Append('|').Append(d.source).Append('(');
                foreach (var f in d.fields.OrderBy(f => f.name, System.StringComparer.Ordinal))
                    sb.Append(f.name).Append(':').Append(f.type).Append(',');
                sb.Append(");");
            }
            uint h = 2166136261;
            foreach (var ch in sb.ToString())
            {
                h ^= ch;
                h *= 16777619;
            }
            return h.ToString("x8");
        }
    }
}
