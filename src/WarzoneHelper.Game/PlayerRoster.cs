using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using GameHelper.Core.Events;
using GameHelper.Core.Monitors;
using GameHelper.Core.Text;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// Warzone player roster: the game-specific layer over the generic <see cref="EntityRoster{T}"/>.
    /// Adds the Player entity (team / rank / platform / banned / ...), consumes the CV/network events
    /// (party & match lists, chat, killfeed, inspect, game-server connect/disconnect), applies team
    /// ranking + sticky "self" detection, and tracks the match session. Persistence, fuzzy matching,
    /// the confidence gate, active-set sweeping and PLAYER_* deltas all come from the base.
    /// </summary>
    public sealed class PlayerRoster : EntityRoster<PlayerRoster.Player>
    {
        public sealed class Player : IRosterEntity
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Key { get; set; }               // normalized letters+digits
            public int? Level { get; set; }
            public string Rank { get; set; }
            public string Platform { get; set; }
            public string Input { get; set; }
            public string ActivisionId { get; set; }
            public string Team { get; set; } = "unknown"; // self | squad | enemy | online | unknown
            public string Status { get; set; } = "active"; // active | dead | disconnected
            public bool Banned { get; set; }
            public string FirstSeen { get; set; }
            public string LastSeen { get; set; }
            public int Sightings { get; set; }
            public bool Confirmed { get; set; }
            public List<string> Sources { get; set; } = new List<string>();
            [JsonIgnore] public bool InMatch { get; set; }
            [JsonIgnore] public string AltKey => ActivisionId;   // exact secondary key

            public IDictionary<string, object> ToDict() => new Dictionary<string, object>
            {
                { "id", Id }, { "name", Name }, { "key", Key }, { "level", Level }, { "rank", Rank },
                { "platform", Platform }, { "input", Input }, { "activisionId", ActivisionId },
                { "team", Team }, { "status", Status }, { "banned", Banned },
                { "firstSeen", FirstSeen }, { "lastSeen", LastSeen }, { "sources", Sources.ToArray() },
            };
        }

        private readonly WarzoneConfig _cfg;

        // Match session (set of active game servers; a match uses >1 server on :44998).
        private readonly HashSet<string> _activeServers = new HashSet<string>();
        private bool _sessionOpen;

        // Sticky self.
        private string _selfKey; private int _selfBasis;

        public override string Name => "roster";

        public PlayerRoster(WarzoneConfig cfg, EventBus bus)
            : base(bus, cfg.PlayerCacheFile, "players", cfg.PlayerFuzzyThreshold,
                   cfg.ConfidenceEstablish, cfg.PlayerRetainSec)
        {
            _cfg = cfg;
        }

        protected override Player CreateEntity(string id, string key, string rawName) =>
            new Player { Id = id, Key = key, Name = rawName?.Trim(), FirstSeen = DateTime.UtcNow.ToString("o") };

        // ---- event intake ----
        protected override void OnEvent(HelperEvent evt)
        {
            try
            {
                switch (evt.Name)
                {
                    case EventNames.GameServerConnected: OnServer(evt, true); break;
                    case EventNames.GameServerDisconnected: OnServer(evt, false); break;
                    case EventNames.PartyListChanged: OnList(evt, "squad"); break;
                    case EventNames.MatchListChanged: OnList(evt, "unknown"); break;
                    case EventNames.ChatMessage: Observe(evt.Str("name"), "enemy", null, "chat"); break;
                    case EventNames.KillfeedEntry: OnKillfeed(evt); break;
                    case EventNames.PlayerInspected: OnInspect(evt); break;
                }
            }
            catch (Exception ex) { Bus.Log($"[roster] {ex.Message}"); }
        }

        private void OnServer(HelperEvent evt, bool connected)
        {
            var ep = $"{evt.Str("ip")}:{evt.Str("port")}";
            lock (Lock)
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

        /// <summary>End the current match: reset self, then let the base drop the active roster.</summary>
        protected override void NewMatch()
        {
            lock (Lock) { _selfKey = null; _selfBasis = 0; }
            base.NewMatch();
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
            var ev = evt.Str("event");
            if (!string.IsNullOrWhiteSpace(ev))
            {
                var p = Observe(evt.Str("player"), "enemy", null, "eventlog");
                if (p != null) SetField(p, () => { p.Status = "disconnected"; if (ev == "banned") p.Banned = true; });
                return;
            }
            Observe(evt.Str("killer"), "enemy", null, "killfeed");
            var v = Observe(evt.Str("victim"), "enemy", null, "killfeed");
            if (v != null) SetField(v, () => v.Status = "dead");
        }

        private void OnInspect(HelperEvent evt)
        {
            var actId = evt.Str("activisionId");
            if (string.IsNullOrEmpty(actId)) return;
            // Inspected player has no name in the panel we parse reliably; key by Activision id.
            Player p; bool joined = false;
            lock (Lock)
            {
                if (!ByAltKey.TryGetValue(actId, out p))
                {
                    p = new Player { Id = "a:" + actId, ActivisionId = actId, Key = "act" + actId, FirstSeen = DateTime.UtcNow.ToString("o") };
                    Index(p); joined = MarkConfirmed(p);
                }
                p.LastSeen = DateTime.UtcNow.ToString("o");
                if (evt.Data.TryGetValue("level", out var lv) && lv != null) p.Level = Convert.ToInt32(lv);
                if (evt.Data.TryGetValue("rank", out var rk)) p.Rank = rk?.ToString();
                if (evt.Data.TryGetValue("platform", out var pl)) p.Platform = pl?.ToString();
                if (evt.Data.TryGetValue("input", out var ip)) p.Input = ip?.ToString();
                Dirty = true;
            }
            if (joined) EmitJoined(p); else EmitChanged(p);
        }

        /// <summary>Add/refresh a player from any source. Confidence-gated before it's surfaced.</summary>
        private Player Observe(string rawName, string team, int? level, string source)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return null;
            Player p; bool joined = false, changed = false;
            lock (Lock)
            {
                p = Resolve(rawName, null);
                if (p == null) return null;
                p.LastSeen = DateTime.UtcNow.ToString("o");
                p.Sightings++;
                if (!p.InMatch) { p.InMatch = true; changed = true; }
                if (!p.Sources.Contains(source ?? "?")) { p.Sources.Add(source ?? "?"); changed = true; }

                if (!string.IsNullOrEmpty(_cfg.PlayerSelfName) && TextOps.Norm(_cfg.PlayerSelfName) == p.Key && p.Team != "self")
                { p.Team = "self"; changed = true; }
                else if (Rank(team) > Rank(p.Team)) { p.Team = team; changed = true; }
                if (level.HasValue && p.Level != level) { p.Level = level; changed = true; }
                if (p.Status == "disconnected") { p.Status = "active"; changed = true; }

                if (!p.Confirmed && p.Sightings >= ConfidenceEstablish) joined = MarkConfirmed(p);
                Dirty = true;
            }
            if (joined) EmitJoined(p);
            else if (changed && p.Confirmed) EmitChanged(p);
            return p;
        }

        private void SetField(Player p, Action mutate)
        {
            bool ch; lock (Lock) { var before = p.Status + p.Banned; mutate(); ch = (p.Status + p.Banned) != before; Dirty = true; }
            if (ch && p.Confirmed) EmitChanged(p);
        }

        private void SetSelf(string key, int basis)
        {
            var changed = new List<Player>();
            lock (Lock)
            {
                if (_selfKey == key) { if (basis > _selfBasis) _selfBasis = basis; return; }
                if (_selfKey != null && basis <= _selfBasis) return;
                if (_selfKey != null && ByKey.TryGetValue(_selfKey, out var old) && old.Team == "self") { old.Team = "squad"; changed.Add(old); }
                _selfKey = key; _selfBasis = basis;
                if (ByKey.TryGetValue(key, out var p) && p.Team != "self") { p.Team = "self"; changed.Add(p); }
            }
            foreach (var p in changed.Where(x => x.Confirmed)) EmitChanged(p);
        }

        private static int Rank(string team)
        { switch (team) { case "self": return 4; case "squad": return 3; case "enemy": return 2; case "online": return 1; default: return 0; } }
    }
}
