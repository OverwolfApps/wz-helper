using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GameHelper.Core.Config;
using GameHelper.Core.Events;
using GameHelper.Core.Text;
using GameHelper.Core.Util;

namespace GameHelper.Core.Monitors
{
    /// <summary>An entity a roster can cache and match. Key is the normalized identity used for
    /// fuzzy matching; AltKey is an optional exact secondary key (e.g. an account id).</summary>
    public interface IRosterEntity
    {
        string Id { get; set; }
        string Key { get; set; }
        string AltKey { get; }
        string FirstSeen { get; set; }
        string LastSeen { get; set; }
        int Sightings { get; set; }
        bool Confirmed { get; set; }
        bool InMatch { get; set; }
        IDictionary<string, object> ToDict();
    }

    /// <summary>
    /// Generic, persistent multi-session entity roster: loads/saves a minified JSON cache, indexes
    /// by key + alt-key, resolves noisy names to an existing entry via fuzzy matching (so OCR
    /// variants link instead of spawning duplicates), gates new entries behind a sightings
    /// confidence threshold, drops stale entries from the ACTIVE set while keeping the cache forever,
    /// and emits joined/changed/left deltas. A game subclass adds its domain entity + event intake.
    /// </summary>
    public abstract class EntityRoster<T> : IMonitor where T : class, IRosterEntity
    {
        protected readonly EventBus Bus;
        protected readonly object Lock = new object();
        protected readonly List<T> All = new List<T>();
        protected readonly Dictionary<string, T> ByKey = new Dictionary<string, T>();
        protected readonly Dictionary<string, T> ByAltKey = new Dictionary<string, T>();
        protected volatile bool Dirty;

        protected int ConfidenceEstablish { get; }
        protected int RetainSeconds { get; }

        private readonly string _cacheFileRaw;
        private readonly string _rootProp;
        private readonly double _fuzzy;
        private readonly EventDef _evJoined, _evChanged, _evLeft;
        private string _cachePath;
        private Timer _sweeper, _saver;

        public abstract string Name { get; }

        protected EntityRoster(EventBus bus, string cacheFile, string rootProperty,
            double fuzzyThreshold, int confidenceEstablish, int retainSeconds,
            EventDef joinedEvent, EventDef changedEvent, EventDef leftEvent)
        {
            Bus = bus;
            _cacheFileRaw = cacheFile;
            _rootProp = string.IsNullOrEmpty(rootProperty) ? "items" : rootProperty;
            _fuzzy = fuzzyThreshold;
            ConfidenceEstablish = confidenceEstablish;
            RetainSeconds = retainSeconds;
            _evJoined = joinedEvent; _evChanged = changedEvent; _evLeft = leftEvent;
        }

        public virtual void Start()
        {
            _cachePath = HelperConfig.Expand(_cacheFileRaw);
            Load();
            Bus.OnEvent += OnEvent;
            _sweeper = new Timer(_ => Sweep(), null, 5000, 5000);
            _saver = new Timer(_ => { if (Dirty) Save(); }, null, 15000, 15000);
        }

        public virtual void Stop()
        {
            Bus.OnEvent -= OnEvent;
            _sweeper?.Dispose(); _saver?.Dispose();
            if (Dirty) Save();
        }

        public void Dispose() => Stop();

        /// <summary>Consume bus events and turn them into roster changes (game-specific).</summary>
        protected abstract void OnEvent(HelperEvent evt);

        /// <summary>Construct a new entity for a first-seen name (subclass sets its own fields).</summary>
        protected abstract T CreateEntity(string id, string key, string rawName);

        // ---- persistence ----
        private void Load()
        {
            // Stored as { "<rootProp>": [ ... ] } so the on-disk shape stays stable per game.
            var map = JsonFile.Load<Dictionary<string, List<T>>>(_cachePath);
            if (map != null && map.TryGetValue(_rootProp, out var items) && items != null)
            {
                foreach (var e in items) Index(e);
                Bus.Log($"[{Name}] loaded {All.Count} cached entries");
            }
        }

        protected void Save()
        {
            try
            {
                Dirty = false;
                List<T> snapshot;
                lock (Lock) snapshot = All.Where(e => e.Confirmed).ToList();
                JsonFile.SaveMinified(_cachePath, new Dictionary<string, List<T>> { [_rootProp] = snapshot });
            }
            catch (Exception ex) { Bus.Log($"[{Name}] save error: {ex.Message}"); }
        }

        protected void Index(T e)
        {
            All.Add(e);
            if (!string.IsNullOrEmpty(e.Key)) ByKey[e.Key] = e;
            if (!string.IsNullOrEmpty(e.AltKey)) ByAltKey[e.AltKey] = e;
        }

        /// <summary>Resolve a name (+ optional alt id) to an existing entry or a freshly-created one:
        /// alt-key exact → normalized-key exact → best fuzzy match above the threshold → new.</summary>
        protected T Resolve(string rawName, string altId)
        {
            var key = TextOps.Norm(rawName);
            if (key.Length < 2 && string.IsNullOrEmpty(altId)) return null;

            if (!string.IsNullOrEmpty(altId) && ByAltKey.TryGetValue(altId, out var byId)) return byId;
            if (ByKey.TryGetValue(key, out var exact)) return exact;

            // Fuzzy: link to the most-similar cached entry above the threshold (only compare keys of
            // a comparable length to avoid absurd matches).
            T best = null; double bestScore = 0;
            foreach (var e in All)
            {
                if (e.Key.Length == 0) continue;
                double lr = (double)Math.Min(key.Length, e.Key.Length) / Math.Max(key.Length, e.Key.Length);
                if (lr < 0.6) continue;
                double s = TextOps.Similarity(key, e.Key);
                if (s > bestScore) { bestScore = s; best = e; }
            }
            if (best != null && bestScore >= _fuzzy) return best;

            var np = CreateEntity("n:" + key, key, rawName);
            Index(np);
            return np;
        }

        protected bool MarkConfirmed(T e) { e.Confirmed = true; return true; }

        protected void EmitEntity(EventDef def, T e) =>
            def.Emit(Bus, x => { foreach (var kv in e.ToDict()) x.With(kv.Key, kv.Value); });

        protected void EmitJoined(T e) => EmitEntity(_evJoined, e);
        protected void EmitChanged(T e) => EmitEntity(_evChanged, e);
        protected void EmitLeft(T e) => EmitEntity(_evLeft, e);

        /// <summary>End the current session: everyone drops out of the ACTIVE roster (cache kept).</summary>
        protected virtual void NewMatch()
        {
            List<T> left;
            lock (Lock)
            {
                left = All.Where(e => e.InMatch).ToList();
                foreach (var e in left) e.InMatch = false;
            }
            foreach (var e in left.Where(e => e.Confirmed)) EmitLeft(e);
            Bus.Log($"[{Name}] new session ({left.Count} entries left the active roster)");
        }

        private void Sweep()
        {
            // Drop entries from the ACTIVE roster after inactivity (cache is kept forever).
            var cutoff = DateTime.UtcNow.AddSeconds(-RetainSeconds);
            var gone = new List<T>();
            lock (Lock)
                foreach (var e in All.Where(e => e.InMatch))
                    if (DateTime.TryParse(e.LastSeen, out var ls) && ls.ToUniversalTime() < cutoff) { e.InMatch = false; gone.Add(e); }
            foreach (var e in gone.Where(e => e.Confirmed)) EmitLeft(e);
        }
    }
}
