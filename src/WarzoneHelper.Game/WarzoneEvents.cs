using GameHelper.Core.Events;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// The Warzone-specific events (CV, roster, status), declared once as <see cref="EventDef"/>s.
    /// Monitors call <c>X.Emit(bus, ...)</c>; the HELPER_STARTED catalog reflects these, so emit and
    /// docs never drift. Names reuse the Core <see cref="EventNames"/> constants.
    /// </summary>
    public static class WarzoneEvents
    {
        private static EventFieldDoc F(string n, string t, string d) => EventDef.Field(n, t, d);

        private static EventFieldDoc[] Player() => new[]
        {
            F("id", "string", "stable roster id"),
            F("name", "string", "player name"),
            F("key", "string", "normalized identity key"),
            F("level", "int", "player level, or null"),
            F("rank", "string", "ranked tier, or null"),
            F("platform", "string", "Battle.net/Xbox/PlayStation/..., or null"),
            F("input", "string", "Controller/MouseKeyboard, or null"),
            F("activisionId", "string", "Activision numeric id, or null"),
            F("team", "string", "self | squad | enemy | online | unknown"),
            F("status", "string", "active | dead | disconnected"),
            F("banned", "bool", "flagged banned in the event log"),
            F("firstSeen", "string", "ISO-8601 first seen"),
            F("lastSeen", "string", "ISO-8601 last seen"),
            F("sources", "string[]", "which detectors contributed (party/chat/killfeed/...)"),
        };

        public static readonly EventDef GameStatusChanged = new EventDef(
            EventNames.GameStatusChanged, EventSource.StatusApi, "Activision status changed (summary or per game/platform issue).",
            F("change", "string", "all_ok | summary | issue_started | issue_updated | issue_resolved"),
            F("ok", "bool", "true when there are no active issues (summary)"),
            F("activeIssues", "int", "active issue count (summary)"),
            F("gameTitle", "string", "affected title (per-issue)"),
            F("platform", "string", "affected platform (per-issue)"),
            F("status", "object", "current status fields (per-issue)"),
            F("previous", "string", "previous signature (per-issue)"));

        public static readonly EventDef HealthChanged = new EventDef(
            EventNames.HealthChanged, EventSource.ScreenCv, "Health bar fill changed.",
            F("health", "number", "0..1 fill fraction"), F("previous", "number", "prior fraction"));

        public static readonly EventDef PlayerDead = new EventDef(
            EventNames.PlayerDead, EventSource.ScreenCv, "Local player death detected (red banner).",
            F("health", "number", "health at death"));

        public static readonly EventDef Deployed = new EventDef(
            EventNames.Deployed, EventSource.ScreenCv, "Deploy/parachute prompt detected.");

        public static readonly EventDef LobbyIdChanged = new EventDef(
            EventNames.LobbyIdChanged, EventSource.ScreenCv, "The ~19-digit lobby/session id changed (confidence-gated).",
            F("lobbyId", "string", "new lobby id"), F("previous", "string", "prior lobby id"));

        public static readonly EventDef ChatMessage = new EventDef(
            EventNames.ChatMessage, EventSource.ScreenCv, "An in-game chat message (confidence-gated).",
            F("channel", "string", "MATCH/PARTY/SQUAD/ALL"),
            F("name", "string", "sender name"),
            F("text", "string", "message text"));

        public static readonly EventDef PartyListChanged = new EventDef(
            EventNames.PartyListChanged, EventSource.ScreenCv, "Your party/squad panel membership changed.",
            F("members", "object[]", "[{name, level, group}]"),
            F("count", "int", "member count"),
            F("selfIndex", "int", "index of you, or -1"));

        public static readonly EventDef MatchListChanged = new EventDef(
            EventNames.MatchListChanged, EventSource.ScreenCv, "The full lobby/match player list changed.",
            F("members", "object[]", "[{name, level, group}]"),
            F("count", "int", "member count"),
            F("selfIndex", "int", "index of you, or -1"));

        public static readonly EventDef SpectatingPlayer = new EventDef(
            EventNames.SpectatingPlayer, EventSource.ScreenCv, "Spectating panel while dead.",
            F("name", "string", "spectated player"), F("id", "string", "#id suffix"));

        public static readonly EventDef PerfStats = new EventDef(
            EventNames.PerfStats, EventSource.ScreenCv, "Perf/telemetry overlay values (throttled ~1/3s).",
            F("fps", "int", "frames per second"),
            F("latencyMs", "int", "network latency"),
            F("gameLatencyMs", "int", "ping to the match server"),
            F("packetLossPct", "int", "packet loss %"),
            F("gpuTemp", "int", "GPU temperature °C"),
            F("vramPct", "int", "VRAM usage %"),
            F("clock", "string", "wall clock HH:MM"));

        public static readonly EventDef PartyCodeChanged = new EventDef(
            EventNames.PartyCodeChanged, EventSource.ScreenCv, "The invite/party code changed (cached, not per-match).",
            F("code", "string", "party code, e.g. LLJGJ"));

        public static readonly EventDef GameVersionChanged = new EventDef(
            EventNames.GameVersionChanged, EventSource.ScreenCv,
            "The on-screen build/version watermark changed (confidence-gated on the WHOLE token) — " +
            "i.e. the game updated. Split into named parts; each is best-effort.",
            F("raw", "string", "the whole watermark token"),
            F("version", "string", "season.minor.build, e.g. 12.11.27503415"),
            F("config", "string", "config flags in the first brackets, e.g. 66-0.1019"),
            F("changelist", "string", "changelist number, e.g. 10413"),
            F("patch", "string", "patch/increment after the '+', e.g. 11"),
            F("epoch", "string", "build unix timestamp, e.g. 1783011671"),
            F("platform", "string", "platform tag, e.g. pl.Ga.bnet"),
            F("hash", "string", "trailing hex build hash, e.g. 0001228ec00"),
            F("previous", "string", "prior raw token, or null on first read"));

        public static readonly EventDef PlayerInspected = new EventDef(
            EventNames.PlayerInspected, EventSource.ScreenCv, "Inspect-player panel details.",
            F("activisionId", "string", "Activision id"),
            F("level", "int", "level"),
            F("rank", "string", "ranked tier"),
            F("platform", "string", "platform"),
            F("input", "string", "input device"));

        public static readonly EventDef KillfeedEntry = new EventDef(
            EventNames.KillfeedEntry, EventSource.ScreenCv, "A killfeed or event-log line. Kill: {killer, victim}. Event-log: {player, event}.",
            F("killer", "string", "killer name (kill line)"),
            F("victim", "string", "victim name (kill line)"),
            F("player", "string", "subject (event-log line)"),
            F("event", "string", "disconnected | banned | ... (event-log line)"));

        public static readonly EventDef PlayerJoined = new EventDef(
            EventNames.PlayerJoined, EventSource.ScreenCv, "A player entered the active roster (confidence-confirmed).", Player());
        public static readonly EventDef PlayerChanged = new EventDef(
            EventNames.PlayerChanged, EventSource.ScreenCv, "A tracked player attribute changed.", Player());
        public static readonly EventDef PlayerLeft = new EventDef(
            EventNames.PlayerLeft, EventSource.ScreenCv, "A player dropped from the active roster (cache kept).", Player());
    }
}
