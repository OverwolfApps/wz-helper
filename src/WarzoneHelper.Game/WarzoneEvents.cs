using System.Collections.Generic;
using GameHelper.Core.Events;

namespace WarzoneHelper.Game
{
    /// <summary>Self-describing catalog of the Warzone-specific events (CV, roster, status), merged
    /// with GameHelper.Core's catalog and shipped in HELPER_STARTED.</summary>
    internal static class WarzoneEvents
    {
        private static EventFieldDoc F(string n, string t, string d) => new EventFieldDoc(n, t, d);

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

        public static IReadOnlyList<EventDoc> Catalog { get; } = new List<EventDoc>
        {
            new EventDoc("GAME_STATUS_CHANGED", "statusapi", "Activision status changed (summary or per game/platform issue).",
                F("change", "string", "all_ok | summary | issue_started | issue_updated | issue_resolved"),
                F("ok", "bool", "true when there are no active issues (summary)"),
                F("activeIssues", "int", "active issue count (summary)"),
                F("gameTitle", "string", "affected title (per-issue)"),
                F("platform", "string", "affected platform (per-issue)"),
                F("status", "object", "current status fields (per-issue)"),
                F("previous", "string", "previous signature (per-issue)")),

            new EventDoc("HEALTH_CHANGED", "screen-cv", "Health bar fill changed.",
                F("health", "number", "0..1 fill fraction"),
                F("previous", "number", "prior fraction")),
            new EventDoc("PLAYER_DEAD", "screen-cv", "Local player death detected (red banner).",
                F("health", "number", "health at death")),
            new EventDoc("DEPLOYED", "screen-cv", "Deploy/parachute prompt detected."),
            new EventDoc("LOBBY_ID_CHANGED", "screen-cv", "The ~19-digit lobby/session id changed (confidence-gated).",
                F("lobbyId", "string", "new lobby id"),
                F("previous", "string", "prior lobby id")),
            new EventDoc("CHAT_MESSAGE", "screen-cv", "An in-game chat message (confidence-gated).",
                F("channel", "string", "MATCH/PARTY/SQUAD/ALL"),
                F("name", "string", "sender name"),
                F("text", "string", "message text")),
            new EventDoc("PARTY_LIST_CHANGED", "screen-cv", "Your party/squad panel membership changed.",
                F("members", "object[]", "[{name, level, group}]"),
                F("count", "int", "member count"),
                F("selfIndex", "int", "index of you, or -1")),
            new EventDoc("MATCH_LIST_CHANGED", "screen-cv", "The full lobby/match player list changed.",
                F("members", "object[]", "[{name, level, group}]"),
                F("count", "int", "member count"),
                F("selfIndex", "int", "index of you, or -1")),
            new EventDoc("SPECTATING_PLAYER", "screen-cv", "Spectating panel while dead.",
                F("name", "string", "spectated player"),
                F("id", "string", "#id suffix")),
            new EventDoc("PERF_STATS", "screen-cv", "Perf/telemetry overlay values (throttled ~1/3s).",
                F("fps", "int", "frames per second"),
                F("latencyMs", "int", "network latency"),
                F("gameLatencyMs", "int", "ping to the match server"),
                F("packetLossPct", "int", "packet loss %"),
                F("gpuTemp", "int", "GPU temperature °C"),
                F("vramPct", "int", "VRAM usage %"),
                F("clock", "string", "wall clock HH:MM")),
            new EventDoc("PARTY_CODE_CHANGED", "screen-cv", "The invite/party code changed (cached, not per-match).",
                F("code", "string", "party code, e.g. LLJGJ")),
            new EventDoc("PLAYER_INSPECTED", "screen-cv", "Inspect-player panel details.",
                F("activisionId", "string", "Activision id"),
                F("level", "int", "level"),
                F("rank", "string", "ranked tier"),
                F("platform", "string", "platform"),
                F("input", "string", "input device")),
            new EventDoc("KILLFEED_ENTRY", "screen-cv", "A killfeed or event-log line. Kill: {killer, victim}. Event-log: {player, event}.",
                F("killer", "string", "killer name (kill line)"),
                F("victim", "string", "victim name (kill line)"),
                F("player", "string", "subject (event-log line)"),
                F("event", "string", "disconnected | banned | ... (event-log line)")),

            new EventDoc("PLAYER_JOINED", "screen-cv", "A player entered the active roster (confidence-confirmed).", Player()),
            new EventDoc("PLAYER_CHANGED", "screen-cv", "A tracked player attribute changed.", Player()),
            new EventDoc("PLAYER_LEFT", "screen-cv", "A player dropped from the active roster (cache kept).", Player()),
        };
    }
}
