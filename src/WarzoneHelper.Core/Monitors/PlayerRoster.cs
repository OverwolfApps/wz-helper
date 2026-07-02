using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WarzoneHelper.Core.Config;
using WarzoneHelper.Core.Events;

namespace WarzoneHelper.Core.Monitors
{
    /// <summary>
    /// Single unified player roster built from ALL clues — lobby party/match lists, in-game squad
    /// panel, chat names, and the killfeed — replacing the party/match/squad distinction with one
    /// list plus PLAYER_JOINED / PLAYER_CHANGED / PLAYER_LEFT deltas. Clients (the app window)
    /// rebuild and maintain the list from these events.
    ///
    /// Scoped to a match session keyed by the game-server endpoint, with a grace window so the
    /// double connect/disconnect at match start doesn't wipe the roster. Disconnected players are
    /// marked (not removed) so the UI can gray them out and sink them to the bottom.
    /// </summary>
    public sealed class PlayerRoster : IMonitor
    {
        public sealed class Player
        {
            public string Name;
            public string Key;
            public int? Level;
            public string Team = "unknown";   // self | squad | enemy | unknown
            public string Status = "active";  // active | dead | disconnected
            public string FirstSeen;
            public string LastSeen;
            public HashSet<string> Sources = new HashSet<string>();
            public int Sightings;      // OCR-confidence: times observed
            public bool Confirmed;     // only confirmed players are emitted to clients

            public Dictionary<string, object> ToDict() => new Dictionary<string, object>
            {
                { "name", Name }, { "key", Key }, { "level", Level }, { "team", Team },
                { "status", Status }, { "sources", Sources.ToArray() },
                { "firstSeen", FirstSeen }, { "lastSeen", LastSeen }
            };
        }

        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly Dictionary<string, Player> _players = new Dictionary<string, Player>();
        private readonly object _lock = new object();
        private Timer _sweeper;

        // Match session tracking. A match connects to MULTIPLE game servers (session + play server,
        // both on :44998), so track the SET of active servers and only clear the roster on a genuine
        // 0->1 transition into a new match. Empty-for-grace ends the session.
        private readonly HashSet<string> _activeServers = new HashSet<string>();
        private DateTime _emptyAt = DateTime.MinValue;
        private bool _sessionOpen;

        // Single, sticky "self" per match. Position-based self is fragile with incomplete OCR reads,
        // so only (re)assign from a list at least as large as the one that set it.
        private string _selfKey;
        private int _selfBasis;

        public string Name => "roster";

        public PlayerRoster(HelperConfig cfg, EventBus bus) { _cfg = cfg; _bus = bus; }

        public void Start()
        {
            _bus.OnEvent += OnEvent;
            _sweeper = new Timer(_ => Sweep(), null, 5000, 5000);
        }

        public void Stop()
        {
            _bus.OnEvent -= OnEvent;
            _sweeper?.Dispose(); _sweeper = null;
        }
        public void Dispose() => Stop();

        private static string Norm(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private void OnEvent(HelperEvent evt)
        {
            try
            {
                switch (evt.Name)
                {
                    case EventNames.GameServerConnected: OnServer(evt, true); break;
                    case EventNames.GameServerDisconnected: OnServer(evt, false); break;
                    case EventNames.PartyListChanged: OnList(evt, "squad"); break;
                    case EventNames.MatchListChanged: OnList(evt, "unknown"); break;
                    case EventNames.ChatMessage: OnChat(evt); break;
                    case EventNames.KillfeedEntry: OnKillfeed(evt); break;
                }
            }
            catch (Exception ex) { _bus.Log($"[roster] {ex.Message}"); }
        }

        private void OnServer(HelperEvent evt, bool connected)
        {
            var ep = $"{Get(evt, "ip")}:{Get(evt, "port")}";
            bool startNewMatch = false;
            lock (_lock)
            {
                if (connected)
                {
                    bool wasEmpty = _activeServers.Count == 0;
                    _activeServers.Add(ep);
                    // 0 -> 1 means a match is starting. If the previous session fully ended (past the
                    // grace window), this is a NEW match => clear. A quick re-connect within grace
                    // (e.g. brief blip) keeps the roster.
                    if (wasEmpty)
                    {
                        bool newMatch = !_sessionOpen ||
                            (DateTime.UtcNow - _emptyAt).TotalSeconds > _cfg.MatchSessionGraceSec;
                        if (newMatch && _sessionOpen) startNewMatch = true; // clear old roster
                        _sessionOpen = true;
                        _emptyAt = DateTime.MinValue;
                    }
                }
                else
                {
                    _activeServers.Remove(ep);
                    if (_activeServers.Count == 0) _emptyAt = DateTime.UtcNow; // start grace
                }
            }
            if (startNewMatch) ClearRoster("new-match");
        }

        private void OnList(HelperEvent evt, string team)
        {
            if (!(evt.Data.TryGetValue("members", out var m) && m is IEnumerable<object> members)) return;
            int selfIndex = evt.Data.TryGetValue("selfIndex", out var si) && si != null ? Convert.ToInt32(si) : -1;

            var upserted = new List<Player>();
            foreach (var item in members)
            {
                if (!(item is IDictionary<string, object> md)) { upserted.Add(null); continue; }
                var name = md.TryGetValue("name", out var n) ? n?.ToString() : null;
                int? level = md.TryGetValue("level", out var lv) && lv != null ? Convert.ToInt32(lv) : (int?)null;
                upserted.Add(Upsert(name, team, level, evt.Source, statusResetsDisconnect: true));
            }

            // Assign self from a fixed position, but only from a list at least as complete as the
            // one that last set it (an incomplete read can't move "self" onto a teammate).
            if (selfIndex >= 0 && selfIndex < upserted.Count && upserted[selfIndex] != null)
                SetSelf(upserted[selfIndex].Key, upserted.Count);
        }

        private void SetSelf(string key, int basis)
        {
            List<Player> changed = new List<Player>();
            lock (_lock)
            {
                if (_selfKey == key) { if (basis > _selfBasis) _selfBasis = basis; return; }
                if (_selfKey != null && basis <= _selfBasis) return; // don't override on a smaller read
                if (_selfKey != null && _players.TryGetValue(_selfKey, out var old) && old.Team == "self")
                { old.Team = "squad"; changed.Add(old); }
                _selfKey = key; _selfBasis = basis;
                if (_players.TryGetValue(key, out var p) && p.Team != "self") { p.Team = "self"; changed.Add(p); }
            }
            foreach (var p in changed.Where(x => x.Confirmed)) Emit(EventNames.PlayerChanged, p);
        }

        private void OnChat(HelperEvent evt)
        {
            var name = Get(evt, "name");
            if (!string.IsNullOrWhiteSpace(name)) Upsert(name, "enemy", null, "chat");
        }

        private void OnKillfeed(HelperEvent evt)
        {
            // Event-log line: "<player> disconnected/banned/left" — mark, keep in roster.
            var eventKind = Get(evt, "event");
            if (!string.IsNullOrWhiteSpace(eventKind))
            {
                var pl = Upsert(Get(evt, "player"), "enemy", null, "eventlog");
                if (pl != null) SetStatus(pl, "disconnected");
                return;
            }

            var killer = Get(evt, "killer");
            var victim = Get(evt, "victim");
            if (!string.IsNullOrWhiteSpace(killer)) Upsert(killer, "enemy", null, "killfeed");
            if (!string.IsNullOrWhiteSpace(victim))
            {
                var p = Upsert(victim, "enemy", null, "killfeed");
                if (p != null) SetStatus(p, "dead");
            }
        }

        /// <summary>Add or update a player. Team only upgrades toward more-specific (squad/self).</summary>
        private Player Upsert(string rawName, string team, int? level, string source, bool statusResetsDisconnect = false)
        {
            var key = Norm(rawName);
            if (key.Length < 2) return null;
            var now = DateTime.UtcNow.ToString("o");
            bool joined = false, changed = false;

            Player p;
            lock (_lock)
            {
                if (!_players.TryGetValue(key, out p))
                {
                    p = new Player { Name = rawName.Trim(), Key = key, FirstSeen = now };
                    _players[key] = p;
                }
                p.LastSeen = now;
                p.Sightings++;
                // OCR confidence gate: only surface a player after enough sightings. A stronger
                // source (chat/killfeed naming a specific player) counts as immediately reliable.
                bool reliableSource = source == "chat" || source == "killfeed" || source == "eventlog";
                if (!p.Confirmed && (p.Sightings >= _cfg.ConfidenceEstablish || reliableSource))
                {
                    p.Confirmed = true;
                    joined = true;
                }
                if (p.Sources.Add(source ?? "?")) changed = true;

                // self detection
                if (!string.IsNullOrEmpty(_cfg.PlayerSelfName) && Norm(_cfg.PlayerSelfName) == key && p.Team != "self")
                { p.Team = "self"; changed = true; }
                else if (Rank(team) > Rank(p.Team)) { p.Team = team; changed = true; }

                if (level.HasValue && p.Level != level) { p.Level = level; changed = true; }
                if (statusResetsDisconnect && p.Status == "disconnected") { p.Status = "active"; changed = true; }
            }

            if (joined) Emit(EventNames.PlayerJoined, p);
            else if (changed && p.Confirmed) Emit(EventNames.PlayerChanged, p);
            return p;
        }

        private void SetStatus(Player p, string status)
        {
            bool changed;
            lock (_lock) { changed = p.Status != status; if (changed) p.Status = status; }
            if (changed && p.Confirmed) Emit(EventNames.PlayerChanged, p);
        }

        /// <summary>Mark a player disconnected (kept in roster, grayed + sunk by the UI).</summary>
        public void MarkDisconnected(string rawName)
        {
            var key = Norm(rawName);
            Player p;
            lock (_lock) { _players.TryGetValue(key, out p); }
            if (p != null) SetStatus(p, "disconnected");
        }

        private static int Rank(string team)
        {
            switch (team) { case "self": return 3; case "squad": return 2; case "enemy": return 1; default: return 0; }
        }

        private void ClearRoster(string reason)
        {
            List<Player> removed;
            lock (_lock) { removed = _players.Values.ToList(); _players.Clear(); _selfKey = null; _selfBasis = 0; }
            foreach (var p in removed.Where(x => x.Confirmed)) Emit(EventNames.PlayerLeft, p);
            _bus.Log($"[roster] cleared ({reason}), {removed.Count} players");
        }

        private void Sweep()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_cfg.PlayerRetainSec);
            List<Player> gone = new List<Player>();
            lock (_lock)
            {
                foreach (var p in _players.Values.ToList())
                {
                    if (DateTime.TryParse(p.LastSeen, out var ls) && ls.ToUniversalTime() < cutoff)
                    { _players.Remove(p.Key); gone.Add(p); }
                }
            }
            foreach (var p in gone.Where(x => x.Confirmed)) Emit(EventNames.PlayerLeft, p);
        }

        private void Emit(string name, Player p)
        {
            _bus.Publish(name, EventSource.ScreenCv, e => { foreach (var kv in p.ToDict()) e.With(kv.Key, kv.Value); });
        }

        private static string Get(HelperEvent e, string k) =>
            e.Data != null && e.Data.TryGetValue(k, out var v) ? v?.ToString() : null;
    }
}
