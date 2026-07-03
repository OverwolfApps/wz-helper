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
    /// Self-describing catalog of the events the agent can emit. Sent in HELPER_STARTED (Core events
    /// plus the active game's events) so consumers can discover event names/fields at runtime instead
    /// of hardcoding them, and can compare <see cref="Hash"/> to the last run to detect changes.
    /// </summary>
    public static class EventCatalog
    {
        private static EventFieldDoc F(string n, string t, string d) => new EventFieldDoc(n, t, d);

        // Shared payload for network endpoint events (game servers + backend services).
        private static EventFieldDoc[] Endpoint() => new[]
        {
            F("ip", "string", "remote IP"),
            F("port", "int", "remote UDP/TCP port"),
            F("protocol", "string", "UDP or TCP"),
            F("isGameServer", "bool", "true if classified as the match/game server"),
            F("inMatch", "bool", "whether we believed we were in a match at the time"),
            F("pingMs", "int", "ICMP ping in ms, or null if unresolved"),
            F("distanceKm", "number", "great-circle distance from home, or null"),
            F("isLikelyVPN", "bool", "VPN/proxy heuristic result"),
            F("vpnReason", "string", "why it looked like VPN (ping/distance), or null"),
            F("bytes", "int", "total bytes observed in the window"),
            F("bytesSent", "int", "bytes sent in the window"),
            F("bytesRecv", "int", "bytes received in the window"),
            F("bytesPerSec", "int", "sustained throughput"),
            F("secondsTracked", "number", "how long this peer has been tracked"),
        };

        public static IReadOnlyList<EventDoc> Core { get; } = new List<EventDoc>
        {
            new EventDoc("HELPER_STARTED", "core", "Agent started; carries the event catalog so consumers can self-configure.",
                F("version", "string", "agent version"),
                F("game", "string", "active game profile name (e.g. warzone)"),
                F("events", "object[]", "this catalog: {name, source, description, fields[]}"),
                F("eventsHash", "string", "stable hash of the catalog; changes when events/fields change"),
                F("eventCount", "int", "number of catalog entries")),
            new EventDoc("HELPER_STOPPED", "core", "Agent stopped gracefully."),
            new EventDoc("HELPER_ERROR", "core", "A fatal/notable agent error.",
                F("message", "string", "error text")),

            new EventDoc("LOG_FILE_ADDED", "filewatch", "A watched log/cache file appeared.",
                F("path", "string", "full file path")),
            new EventDoc("LOG_FILE_REMOVED", "filewatch", "A watched file was deleted or renamed away.",
                F("path", "string", "full file path")),
            new EventDoc("LOG_LINE_ADDED", "filewatch", "A non-empty line was appended to a watched text file. Extra fields are the named capture groups of the first matching LogLinePatterns regex.",
                F("path", "string", "source file"),
                F("line", "string", "the raw appended line"),
                F("*", "string", "any named regex groups (e.g. timestamp, level, message)")),

            new EventDoc("GAME_SERVER_CONNECTED", "network", "A peer was classified as the game/match server.", Endpoint()),
            new EventDoc("GAME_SERVER_DISCONNECTED", "network", "A game-server peer dropped.", Endpoint()),
            new EventDoc("SERVICE_CONNECTED", "network", "A non-gameplay backend endpoint appeared.", Endpoint()),
            new EventDoc("SERVICE_DISCONNECTED", "network", "A backend endpoint dropped.", Endpoint()),

            new EventDoc("GAME_PROCESS_STARTED", "process", "The game process started.",
                F("pids", "int[]", "matching process ids")),
            new EventDoc("GAME_PROCESS_STOPPED", "process", "The game process exited.",
                F("pids", "int[]", "process ids that were running")),

            new EventDoc("STATUS_RESPONSE", "statusapi", "Default status poller output: the raw API body (emitted only when it changes).",
                F("body", "string", "raw response body")),

            new EventDoc("MATCH_STARTED", "gep", "GEP hint: a match started (unreliable; corroborate).",
                F("raw", "string", "raw GEP data"), F("gepName", "string", "original GEP event name")),
            new EventDoc("MATCH_ENDED", "gep", "GEP hint: a match ended.",
                F("raw", "string", "raw GEP data"), F("gepName", "string", "original GEP event name")),
            new EventDoc("SCENE_CHANGED", "gep", "GEP hint: scene changed (lobby/in_game/...).",
                F("raw", "string", "scene value"), F("gepName", "string", "original GEP event name")),
            new EventDoc("MODE_CHANGED", "gep", "GEP hint: game mode changed.",
                F("raw", "string", "mode value"), F("gepName", "string", "original GEP event name")),
        };

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
