namespace GameHelper.Core.Events
{
    /// <summary>
    /// The generic events GameHelper.Core can emit, declared once. Monitors call <c>X.Emit(bus, ...)</c>
    /// and the HELPER_STARTED catalog is reflected from these fields, so emit + docs stay in lockstep.
    /// Names reuse the <see cref="EventNames"/> constants so consumer switch statements keep working.
    /// </summary>
    public static class CoreEvents
    {
        private static EventFieldDoc F(string n, string t, string d) => EventDef.Field(n, t, d);

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
            F("bytesPerSec", "int", "current windowed throughput (real B/s from byte deltas)"),
            F("peakBytesPerSec", "int", "peak windowed throughput reached (used for classification)"),
            F("secondsTracked", "number", "how long this peer has been tracked"),
            // GeoIP enrichment (present when the IP resolves):
            F("countryIso", "string", "ISO country code"),
            F("countryName", "string", "country name"),
            F("city", "string", "city"),
            F("latitude", "number", "latitude"),
            F("longitude", "number", "longitude"),
            F("asn", "int", "autonomous system number"),
            F("asnOrg", "string", "AS organization"),
        };

        public static readonly EventDef HelperStarted = new EventDef(
            EventNames.HelperStarted, EventSource.Core, "Agent started; carries the event catalog so consumers can self-configure.",
            F("version", "string", "agent version"),
            F("game", "string", "active game profile name (e.g. warzone)"),
            F("events", "object[]", "this catalog: {name, source, description, fields[]}"),
            F("eventsHash", "string", "stable hash of the catalog; changes when events/fields change"),
            F("eventCount", "int", "number of catalog entries"));

        public static readonly EventDef HelperStopped = new EventDef(
            EventNames.HelperStopped, EventSource.Core, "Agent stopped gracefully.");

        public static readonly EventDef MatchStateChanged = new EventDef(
            EventNames.MatchStateChanged, EventSource.Core,
            "The agent's derived match phase changed. Driven by how many game servers are connected: " +
            "0=searching, 1=found (pre-game lobby), >=2=started (InMatch), then 2->1=ended (leaving). " +
            "In-match-only CV (health, death, chat, killfeed) runs only while inMatch is true.",
            F("inMatch", "bool", "true when we believe we're in a live match (phase == started)"),
            F("phase", "string", "searching | found | started | ended"));

        public static readonly EventDef HelperError = new EventDef(
            EventNames.HelperError, EventSource.Core, "A fatal/notable agent error.",
            F("message", "string", "error text"));

        public static readonly EventDef LogFileAdded = new EventDef(
            EventNames.LogFileAdded, EventSource.FileWatch, "A watched log/cache file appeared.",
            F("path", "string", "full file path"));

        public static readonly EventDef LogFileRemoved = new EventDef(
            EventNames.LogFileRemoved, EventSource.FileWatch, "A watched file was deleted or renamed away.",
            F("path", "string", "full file path"));

        public static readonly EventDef LogLineAdded = new EventDef(
            EventNames.LogLineAdded, EventSource.FileWatch,
            "A non-empty line was appended to a watched text file. Extra fields are the named capture groups of the first matching LogLinePatterns regex.",
            F("path", "string", "source file"),
            F("line", "string", "the raw appended line"),
            F("*", "string", "any named regex groups (e.g. timestamp, level, message)"));

        public static readonly EventDef GameServerConnected = new EventDef(
            EventNames.GameServerConnected, EventSource.Network, "A peer was classified as the game/match server.", Endpoint());
        public static readonly EventDef GameServerDisconnected = new EventDef(
            EventNames.GameServerDisconnected, EventSource.Network, "A game-server peer dropped.", Endpoint());
        public static readonly EventDef ServiceConnected = new EventDef(
            EventNames.ServiceConnected, EventSource.Network, "A non-gameplay backend endpoint appeared.", Endpoint());
        public static readonly EventDef ServiceDisconnected = new EventDef(
            EventNames.ServiceDisconnected, EventSource.Network, "A backend endpoint dropped.", Endpoint());

        public static readonly EventDef GameProcessStarted = new EventDef(
            EventNames.GameProcessStarted, EventSource.Process, "The game process started (with exe details for update detection).",
            F("pids", "int[]", "matching process ids"),
            F("exe", "string", "full exe path"),
            F("commandLine", "string", "full process command line (uid/session args)"),
            F("sizeBytes", "int", "exe size in bytes (changes on patch)"),
            F("modifiedUtc", "string", "exe last-modified ISO-8601 (changes on patch)"),
            F("fileVersion", "string", "exe FileVersion"),
            F("productVersion", "string", "exe ProductVersion"),
            F("productName", "string", "exe ProductName"),
            F("fileDescription", "string", "exe FileDescription"),
            F("company", "string", "exe CompanyName"));
        public static readonly EventDef GameProcessStopped = new EventDef(
            EventNames.GameProcessStopped, EventSource.Process, "The game process exited.",
            F("pids", "int[]", "process ids that were running"));

        public static readonly EventDef StatusResponse = new EventDef(
            EventNames.StatusResponse, EventSource.StatusApi, "Default status poller output: the raw API body (emitted only when it changes).",
            F("body", "string", "raw response body"));

        public static readonly EventDef MatchStarted = new EventDef(
            EventNames.MatchStarted, EventSource.Gep, "GEP hint: a match started (unreliable; corroborate).",
            F("raw", "string", "raw GEP data"), F("gepName", "string", "original GEP event name"));
        public static readonly EventDef MatchEnded = new EventDef(
            EventNames.MatchEnded, EventSource.Gep, "GEP hint: a match ended.",
            F("raw", "string", "raw GEP data"), F("gepName", "string", "original GEP event name"));
        public static readonly EventDef SceneChanged = new EventDef(
            EventNames.SceneChanged, EventSource.Gep, "GEP hint: scene changed (lobby/in_game/...).",
            F("raw", "string", "scene value"), F("gepName", "string", "original GEP event name"));
        public static readonly EventDef ModeChanged = new EventDef(
            EventNames.ModeChanged, EventSource.Gep, "GEP hint: game mode changed.",
            F("raw", "string", "mode value"), F("gepName", "string", "original GEP event name"));
        public static readonly EventDef GameLaunched = new EventDef(
            EventNames.GameLaunched, EventSource.Gep, "GEP: the game process launched.",
            F("raw", "string", "gameInfo JSON (pid, renderer, exe, resolution, ...)"), F("gepName", "string", "original GEP event name"));
        public static readonly EventDef GameTerminated = new EventDef(
            EventNames.GameTerminated, EventSource.Gep, "GEP: the game process terminated.",
            F("raw", "string", "gameInfo JSON (incl. terminationUnixEpochTime, reason)"), F("gepName", "string", "original GEP event name"));
        public static readonly EventDef GepInfo = new EventDef(
            EventNames.GepInfo, EventSource.Gep, "GEP: a raw info update (game_info / match_info / gep_internal version_info).",
            F("raw", "string", "the full info object as JSON"), F("gepName", "string", "original GEP event name"));
    }
}
