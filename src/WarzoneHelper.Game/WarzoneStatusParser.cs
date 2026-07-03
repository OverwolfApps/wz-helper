using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GameHelper.Core.Events;
using GameHelper.Core.Monitors;

namespace WarzoneHelper.Game
{
    /// <summary>
    /// Parses Activision's public status API (the same endpoint the codstatus Discord cog uses) and
    /// emits GAME_STATUS_CHANGED whenever a game/platform entry appears, disappears, or changes state
    /// in "serverStatuses". Entries present in serverStatuses represent an active issue. This is the
    /// CoD-specific interpretation kept out of the generic StatusApiMonitor.
    /// </summary>
    public sealed class WarzoneStatusParser : IStatusParser
    {
        private readonly string[] _titles;
        // key "gameTitle|platform" -> compact status signature
        private Dictionary<string, string> _last = new Dictionary<string, string>();
        private bool _primed;
        private int _lastCount = -1;

        public WarzoneStatusParser(string[] gameTitles) { _titles = gameTitles; }

        public void Handle(string responseBody, EventBus bus)
        {
            var root = JObject.Parse(responseBody);
            var statuses = root["serverStatuses"] as JArray ?? new JArray();

            var current = new Dictionary<string, JObject>();
            foreach (var item in statuses.OfType<JObject>())
            {
                var game = item.Value<string>("gameTitle") ?? "";
                var platform = item.Value<string>("platform") ?? "";
                if (game.Length == 0 && platform.Length == 0) continue;
                // Only CoD titles — the API also returns Crash, Skylanders, etc.
                if (_titles != null && _titles.Length > 0 &&
                    !_titles.Any(t => game.ToLowerInvariant().Contains(t))) continue;
                current[$"{game}|{platform}"] = item;
            }

            if (!_primed)
            {
                _primed = true;
                _last = current.ToDictionary(kv => kv.Key, kv => Signature(kv.Value));
                if (current.Count == 0)
                    bus.Log("[status] no active Activision/CoD issues.");
                else
                {
                    bus.Log($"[status] {current.Count} active issue(s) at startup:");
                    foreach (var kv in current)
                        bus.Log($"[status]   - {kv.Key.Replace("|", " [")}] : {Signature(kv.Value)}");
                }
                EmitSummary(bus, current.Count);
                return;
            }

            // New or changed issues
            foreach (var kv in current)
            {
                var sig = Signature(kv.Value);
                if (!_last.TryGetValue(kv.Key, out var prev))
                    Emit(bus, kv.Key, "issue_started", null, kv.Value);
                else if (prev != sig)
                    Emit(bus, kv.Key, "issue_updated", prev, kv.Value);
            }
            // Resolved issues
            foreach (var kv in _last)
                if (!current.ContainsKey(kv.Key))
                    Emit(bus, kv.Key, "issue_resolved", kv.Value, null);

            _last = current.ToDictionary(kv => kv.Key, kv => Signature(kv.Value));
            EmitSummary(bus, current.Count);
        }

        /// <summary>Emit an overall status summary when the active-issue count changes (0 = all OK).</summary>
        private void EmitSummary(EventBus bus, int count)
        {
            if (count == _lastCount) return;
            _lastCount = count;
            WarzoneEvents.GameStatusChanged.Emit(bus, e => e
                .With("change", count == 0 ? "all_ok" : "summary")
                .With("activeIssues", count)
                .With("ok", count == 0));
        }

        private static string Signature(JObject o)
        {
            // Compact, order-stable signature of the status fields we care about.
            return string.Join(";", o.Properties()
                .Where(p => p.Name != "gameTitle" && p.Name != "platform")
                .OrderBy(p => p.Name)
                .Select(p => $"{p.Name}={p.Value}"));
        }

        private static void Emit(EventBus bus, string key, string change, object prev, JObject cur)
        {
            var parts = key.Split('|');
            WarzoneEvents.GameStatusChanged.Emit(bus, evt =>
            {
                evt.With("gameTitle", parts.Length > 0 ? parts[0] : "")
                    .With("platform", parts.Length > 1 ? parts[1] : "")
                    .With("change", change);
                if (cur != null) evt.With("status", cur.ToObject<Dictionary<string, object>>());
                if (prev != null) evt.With("previous", prev.ToString());
            });
        }
    }
}
