namespace GameHelper.Core.Events
{
    /// <summary>
    /// Canonical event names dispatched by the helper. These strings are what the
    /// Overwolf JS layer (and the console host) receive, so keep them stable.
    /// </summary>
    public static class EventNames
    {
        // --- Lifecycle ---
        public const string HelperStarted = "HELPER_STARTED";
        public const string HelperStopped = "HELPER_STOPPED";
        public const string HelperError = "HELPER_ERROR";

        // --- Logs / cache directory watching ---
        public const string LogFileAdded = "LOG_FILE_ADDED";       // a watched file appeared
        public const string LogFileRemoved = "LOG_FILE_REMOVED";   // a watched file was deleted/renamed away
        public const string LogLineAdded = "LOG_LINE_ADDED";       // { path, line, + named regex groups }

        // --- Network (cod.exe connection tracking) ---
        public const string GameServerConnected = "GAME_SERVER_CONNECTED";
        public const string GameServerDisconnected = "GAME_SERVER_DISCONNECTED";
        public const string ServiceConnected = "SERVICE_CONNECTED";       // non-gameplay backend endpoints
        public const string ServiceDisconnected = "SERVICE_DISCONNECTED";
        public const string GameProcessStarted = "GAME_PROCESS_STARTED";
        public const string GameProcessStopped = "GAME_PROCESS_STOPPED";

        // --- Status API (generic poll; game parses the body) ---
        public const string StatusResponse = "STATUS_RESPONSE";     // default raw-body event
        public const string GameStatusChanged = "GAME_STATUS_CHANGED"; // emitted by a game's status parser

        // --- Screen / CV derived (ours), plus GEP hints layered on top ---
        public const string MatchStarted = "MATCH_STARTED";
        public const string MatchEnded = "MATCH_ENDED";
        public const string SceneChanged = "SCENE_CHANGED";     // lobby_wz / in_game / ...
        public const string ModeChanged = "MODE_CHANGED";
        public const string GameLaunched = "GAME_LAUNCHED";     // GEP: game process launched (full gameInfo)
        public const string GameTerminated = "GAME_TERMINATED"; // GEP: game process terminated
        public const string GepInfo = "GEP_INFO";               // GEP: any raw info update (game_info/match_info/gep_internal)
        public const string Deployed = "DEPLOYED";
        public const string HealthChanged = "HEALTH_CHANGED";
        public const string PlayerDead = "PLAYER_DEAD";
        public const string LobbyIdChanged = "LOBBY_ID_CHANGED";
        public const string ChatMessage = "CHAT_MESSAGE";
        public const string PartyListChanged = "PARTY_LIST_CHANGED";
        public const string MatchListChanged = "MATCH_LIST_CHANGED";
        public const string SpectatingPlayer = "SPECTATING_PLAYER";
        public const string PerfStats = "PERF_STATS";
        public const string PartyCodeChanged = "PARTY_CODE_CHANGED";
        public const string PlayerInspected = "PLAYER_INSPECTED";

        // --- Unified player roster (built from party/match/squad/chat/killfeed sources) ---
        public const string PlayerJoined = "PLAYER_JOINED";
        public const string PlayerLeft = "PLAYER_LEFT";       // left/removed (rare; usually marked disconnected instead)
        public const string PlayerChanged = "PLAYER_CHANGED"; // any tracked attribute changed
        public const string KillfeedEntry = "KILLFEED_ENTRY"; // "<killer> killed <victim>"
    }

    /// <summary>Where an event was derived from, so consumers can weigh reliability.</summary>
    public static class EventSource
    {
        public const string Core = "core";
        public const string Network = "network";
        public const string FileWatch = "filewatch";
        public const string StatusApi = "statusapi";
        public const string ScreenCv = "screen-cv";
        public const string Gep = "gep";        // Overwolf Game Events (unreliable, treated as a hint)
        public const string Process = "process";
    }
}
