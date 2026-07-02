using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using WarzoneHelper.Core.Config;
using WarzoneHelper.Core.Events;

namespace WarzoneHelper.Core.Monitors
{
    /// <summary>
    /// Persistent, multi-session player cache. One record per real player accumulates every field we
    /// can ever OCR (name, level, rank, platform, input, Activision id, team, status, banned, ...),
    /// is matched fuzzily against the existing cache so OCR-variant / made-up names link to the right
    /// player instead of spawning duplicates, persists to a minified players.json between sessions,
    /// and updates in memory on the fly. PLAYER_JOINED/CHANGED/LEFT track the current match roster.
    /// </summary>
    public sealed class PlayerRoster : IMonitor
    {
        public sealed class Player
        {
            public string Id;
            public string Name;
            public string Key;               // normalized letters+digits
            public int? Level;
            public string Rank;
            public string Platform;
            public string Input;
            public string ActivisionId;
            public string Team = "unknown";  // self | squad | enemy | unknown
            public string Status = "active"; // active | dead | disconnected
            public bool Banned;
            public string FirstSeen;
            public string LastSeen;
            public int Sightings;
            public bool Confirmed;
            public List<string> Sources = new List<string>();
            [JsonIgnore] public bool InMatch;

            public Dictionary<string, object> ToDict() => new Dictionary<string, object>
            {
                { "id", Id }, { "name", Name }, { "key", Key }, { "level", Level }, { "rank", Rank },
                { "platform", Platform }, { "input", Input }, { "activisionId", ActivisionId },
                { "team", Team }, { "status", Status }, { "banned", Banned },
                { "firstSeen", FirstSeen }, { "lastSeen", LastSeen }, { "sources", Sources.ToArray() },
            };
        }

        private sealed class CacheFile { public List<Player> players = new List<Player>(); }

        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly object _lock = new object();
        private readonly List<Player> _all = new List<Player>();
        private readonly Dictionary<string, Player> _byKey = new Dictionary<string, Player>();
        private readonly Dictionary<string, Player> _byActId = new Dictionary<string, Player>();
        private Timer _sweeper, _saver;
        private volatile bool _dirty;
        private string _cachePath;

        // Match session (set of active game servers; a match uses >1 server on :44998).
        private readonly HashSet<string> _activeServers = new HashSet<string>();
        private bool _sessionOpen;

        // Self stickiness (see previous logic).
        private string _selfKey; private int _selfBasis;

        public string Name => "roster";
        public PlayerRoster(HelperConfig cfg, EventBus bus) { _cfg = cfg; _bus = bus; }

        public void Start()
        {
            _cachePath = HelperConfig.Expand(_cfg.PlayerCacheFile);
            Load();
            _bus.OnEvent += OnEvent;
            _sweeper = new Timer(_ => Sweep(), null, 5000, 5000);
            _saver = new Timer(_ => { if (_dirty) Save(); }, null, 15000, 15000);
        }

        public void Stop()
        {
            _bus.OnEvent -= OnEvent;
            _sweeper?.Dispose(); _saver?.Dispose();
            if (_dirty) Save();
        }
        public void Dispose() => Stop();

        // ---- persistence ----
        private void Load()
        {
            try
            {
                if (File.Exists(_cachePath))
                {
                    var cf = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(_cachePath));
                    if (cf?.players != null)
                        foreach (var p in cf.players) { Index(p); }
                    _bus.Log($"[roster] loaded {_all.Count} cached players");
                }
            }
            catch (Exception ex) { _bus.Log($"[roster] load error: {ex.Message}"); }
        }

        private void Save()
        {
            try
            {
                _dirty = false;
                var dir = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                List<Player> snapshot;
                lock (_lock) snapshot = _all.Where(p => p.Confirmed).ToList();
                File.WriteAllText(_cachePath, JsonConvert.SerializeObject(new CacheFile { players = snapshot }, Formatting.None));
            }
            catch (Exception ex) { _bus.Log($"[roster] save error: {ex.Message}"); }
        }

        private void Index(Player p)
        {
            _all.Add(p);
            if (!string.IsNullOrEmpty(p.Key)) _byKey[p.Key] = p;
            if (!string.IsNullOrEmpty(p.ActivisionId)) _byActId[p.ActivisionId] = p;
        }

        // ---- name matching ----
        private static string Norm(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>Levenshtein similarity 0..1 between two normalized keys.</summary>
        private static double Sim(string a, string b)
        {
            if (a == b) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;
            int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            int dist = prev[b.Length];
            return 1.0 - (double)dist / Math.Max(a.Length, b.Length);
        }

        /// <summary>Resolve a name (+ optional Activision id) to an existing cached player or a new one.</summary>
        private Player Resolve(string rawName, string activisionId)
        {
            var key = Norm(rawName);
            if (key.Length < 2 && string.IsNullOrEmpty(activisionId)) return null;

            if (!string.IsNullOrEmpty(activisionId) && _byActId.TryGetValue(activisionId, out var byId)) return byId;
            if (_byKey.TryGetValue(key, out var exact)) return exact;

            // Fuzzy: link to the most-similar cached player above the threshold (only compare names of
            // a comparable length to avoid absurd matches).
            Player best = null; double bestScore = 0;
            foreach (var p in _all)
            {
                if (p.Key.Length == 0) continue;
                double lr = (double)Math.Min(key.Length, p.Key.Length) / Math.Max(key.Length, p.Key.Length);
                if (lr < 0.6) continue;
                double s = Sim(key, p.Key);
                if (s > bestScore) { bestScore = s; best = p; }
            }
            if (best != null && bestScore >= _cfg.PlayerFuzzyThreshold) return best;

            var np = new Player { Id = "n:" + key, Name = rawName?.Trim(), Key = key, FirstSeen = DateTime.UtcNow.ToString("o") };
            Index(np);
            return np;
        }

        // ---- event intake ----
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
                    case EventNames.ChatMessage: Observe(Get(evt, "name"), "enemy", null, "chat"); break;
                    case EventNames.KillfeedEntry: OnKillfeed(evt); break;
                    case EventNames.PlayerInspected: OnInspect(evt); break;
                }
            }
            catch (Exception ex) { _bus.Log($"[roster] {ex.Message}"); }
        }

        private void OnServer(HelperEvent evt, bool connected)
        {
            var ep = $"{Get(evt, "ip")}:{Get(evt, "port")}";
            lock (_lock)
            {
                if (connected)
                {
                    bool wasEmpty = _activeServers.Count == 0;
                    _activeServers.Add(ep);
                    if (wasEmpty) { if (_sessionOpen) NewMatch(); _sessionOpen = true; }
                }
                else { _activeServers.Remove(ep); }
            }
        }

        /// <summary>End the current match: everyone drops out of the active roster (cache kept).</summary>
        private void NewMatch()
        {
            List<Player> left;
            lock (_lock)
            {
                left = _all.Where(p => p.InMatch).ToList();
                foreach (var p in left) p.InMatch = false;
                _selfKey = null; _selfBasis = 0;
            }
            foreach (var p in left.Where(p => p.Confirmed)) Emit(EventNames.PlayerLeft, p);
            _bus.Log($"[roster] new match ({left.Count} players left the active roster)");
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
                var group = md.TryGetValue("group", out var g) ? g?.ToString() : null;
                // A social-panel section overrides the event default: PARTY members are your squad,
                // ONLINE friends are their own category, OFFLINE friends aren't tracked at all.
                string memberTeam;
                switch (group)
                {
                    case "party": memberTeam = "squad"; break;
                    case "online": memberTeam = "online"; break;
                    case "offline": upserted.Add(null); continue;
                    default: memberTeam = team; break; // no headers (in-game squad / match list)
                }
                upserted.Add(Observe(name, memberTeam, level, evt.Source));
            }
            if (selfIndex >= 0 && selfIndex < upserted.Count && upserted[selfIndex] != null)
                SetSelf(upserted[selfIndex].Key, upserted.Count);
        }

        private void OnKillfeed(HelperEvent evt)
        {
            var ev = Get(evt, "event");
            if (!string.IsNullOrWhiteSpace(ev))
            {
                var p = Observe(Get(evt, "player"), "enemy", null, "eventlog");
                if (p != null) SetField(p, () => { p.Status = "disconnected"; if (ev == "banned") p.Banned = true; });
                return;
            }
            Observe(Get(evt, "killer"), "enemy", null, "killfeed");
            var v = Observe(Get(evt, "victim"), "enemy", null, "killfeed");
            if (v != null) SetField(v, () => v.Status = "dead");
        }

        private void OnInspect(HelperEvent evt)
        {
            var actId = Get(evt, "activisionId");
            if (string.IsNullOrEmpty(actId)) return;
            // Inspected player has no name in the panel we parse reliably; key by Activision id.
            Player p; bool joined = false;
            lock (_lock)
            {
                if (!_byActId.TryGetValue(actId, out p))
                {
                    p = new Player { Id = "a:" + actId, ActivisionId = actId, Key = "act" + actId, FirstSeen = DateTime.UtcNow.ToString("o") };
                    Index(p); joined = MarkConfirmed(p);
                }
                p.LastSeen = DateTime.UtcNow.ToString("o");
                if (evt.Data.TryGetValue("level", out var lv) && lv != null) p.Level = Convert.ToInt32(lv);
                if (evt.Data.TryGetValue("rank", out var rk)) p.Rank = rk?.ToString();
                if (evt.Data.TryGetValue("platform", out var pl)) p.Platform = pl?.ToString();
                if (evt.Data.TryGetValue("input", out var ip)) p.Input = ip?.ToString();
                _dirty = true;
            }
            Emit(joined ? EventNames.PlayerJoined : EventNames.PlayerChanged, p);
        }

        /// <summary>Add/refresh a player from any source. Confidence-gated before it's surfaced.</summary>
        private Player Observe(string rawName, string team, int? level, string source)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return null;
            Player p; bool joined = false, changed = false;
            lock (_lock)
            {
                p = Resolve(rawName, null);
                if (p == null) return null;
                p.LastSeen = DateTime.UtcNow.ToString("o");
                p.Sightings++;
                if (!p.InMatch) { p.InMatch = true; changed = true; }
                if (!p.Sources.Contains(source ?? "?")) { p.Sources.Add(source ?? "?"); changed = true; }

                if (!string.IsNullOrEmpty(_cfg.PlayerSelfName) && Norm(_cfg.PlayerSelfName) == p.Key && p.Team != "self")
                { p.Team = "self"; changed = true; }
                else if (Rank(team) > Rank(p.Team)) { p.Team = team; changed = true; }
                if (level.HasValue && p.Level != level) { p.Level = level; changed = true; }
                if (p.Status == "disconnected") { p.Status = "active"; changed = true; }

                if (!p.Confirmed && p.Sightings >= _cfg.ConfidenceEstablish) joined = MarkConfirmed(p);
                _dirty = true;
            }
            if (joined) Emit(EventNames.PlayerJoined, p);
            else if (changed && p.Confirmed) Emit(EventNames.PlayerChanged, p);
            return p;
        }

        private bool MarkConfirmed(Player p) { p.Confirmed = true; return true; }

        private void SetField(Player p, Action mutate)
        {
            bool ch; lock (_lock) { var before = p.Status + p.Banned; mutate(); ch = (p.Status + p.Banned) != before; _dirty = true; }
            if (ch && p.Confirmed) Emit(EventNames.PlayerChanged, p);
        }

        private void SetSelf(string key, int basis)
        {
            var changed = new List<Player>();
            lock (_lock)
            {
                if (_selfKey == key) { if (basis > _selfBasis) _selfBasis = basis; return; }
                if (_selfKey != null && basis <= _selfBasis) return;
                if (_selfKey != null && _byKey.TryGetValue(_selfKey, out var old) && old.Team == "self") { old.Team = "squad"; changed.Add(old); }
                _selfKey = key; _selfBasis = basis;
                if (_byKey.TryGetValue(key, out var p) && p.Team != "self") { p.Team = "self"; changed.Add(p); }
            }
            foreach (var p in changed.Where(x => x.Confirmed)) Emit(EventNames.PlayerChanged, p);
        }

        private static int Rank(string team)
        { switch (team) { case "self": return 4; case "squad": return 3; case "enemy": return 2; case "online": return 1; default: return 0; } }

        private void Sweep()
        {
            // Drop players from the ACTIVE roster after inactivity (cache is kept forever).
            var cutoff = DateTime.UtcNow.AddSeconds(-_cfg.PlayerRetainSec);
            List<Player> gone = new List<Player>();
            lock (_lock)
                foreach (var p in _all.Where(p => p.InMatch))
                    if (DateTime.TryParse(p.LastSeen, out var ls) && ls.ToUniversalTime() < cutoff) { p.InMatch = false; gone.Add(p); }
            foreach (var p in gone.Where(p => p.Confirmed)) Emit(EventNames.PlayerLeft, p);
        }

        private void Emit(string name, Player p) =>
            _bus.Publish(name, EventSource.ScreenCv, e => { foreach (var kv in p.ToDict()) e.With(kv.Key, kv.Value); });

        private static string Get(HelperEvent e, string k) =>
            e.Data != null && e.Data.TryGetValue(k, out var v) ? v?.ToString() : null;
    }
}
