using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;
using WarzoneHelper.Core.Config;
using WarzoneHelper.Core.Events;

namespace WarzoneHelper.Core.Monitors
{
    /// <summary>
    /// Polls Activision's public status API (same endpoint the codstatus Discord cog uses) and
    /// emits COD_STATUS_CHANGED whenever a game/platform entry appears, disappears, or changes
    /// state in "serverStatuses". Entries present in serverStatuses represent an active issue.
    /// </summary>
    public sealed class StatusApiMonitor : IMonitor
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private static readonly HttpClient Http = CreateHttp();
        private Timer _timer;

        // key "gameTitle|platform" -> compact status signature
        private Dictionary<string, string> _last = new Dictionary<string, string>();
        private bool _primed;

        public string Name => "statusapi";

        public StatusApiMonitor(HelperConfig cfg, EventBus bus) { _cfg = cfg; _bus = bus; }

        private static HttpClient CreateHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.Add("User-Agent", "WarzoneHelper/1.0");
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            return c;
        }

        public void Start()
        {
            _timer = new Timer(async _ => await Poll(), null, 2000, Math.Max(15000, _cfg.StatusPollMs));
        }

        private async System.Threading.Tasks.Task Poll()
        {
            try
            {
                var json = await Http.GetStringAsync(_cfg.StatusApiUrl).ConfigureAwait(false);
                var root = JObject.Parse(json);
                var statuses = root["serverStatuses"] as JArray ?? new JArray();

                var current = new Dictionary<string, JObject>();
                foreach (var item in statuses.OfType<JObject>())
                {
                    var game = item.Value<string>("gameTitle") ?? "";
                    var platform = item.Value<string>("platform") ?? "";
                    if (game.Length == 0 && platform.Length == 0) continue;
                    current[$"{game}|{platform}"] = item;
                }

                if (!_primed)
                {
                    _primed = true;
                    _last = current.ToDictionary(kv => kv.Key, kv => Signature(kv.Value));
                    _bus.Log($"[status] primed with {current.Count} active issue(s).");
                    return;
                }

                // New or changed issues
                foreach (var kv in current)
                {
                    var sig = Signature(kv.Value);
                    if (!_last.TryGetValue(kv.Key, out var prev))
                        Emit(kv.Key, "issue_started", null, kv.Value);
                    else if (prev != sig)
                        Emit(kv.Key, "issue_updated", prev, kv.Value);
                }
                // Resolved issues
                foreach (var kv in _last)
                    if (!current.ContainsKey(kv.Key))
                        Emit(kv.Key, "issue_resolved", kv.Value, null);

                _last = current.ToDictionary(kv => kv.Key, kv => Signature(kv.Value));
            }
            catch (Exception ex) { _bus.Log($"[status] poll error: {ex.Message}"); }
        }

        private static string Signature(JObject o)
        {
            // Compact, order-stable signature of the status fields we care about.
            return string.Join(";", o.Properties()
                .Where(p => p.Name != "gameTitle" && p.Name != "platform")
                .OrderBy(p => p.Name)
                .Select(p => $"{p.Name}={p.Value}"));
        }

        private void Emit(string key, string change, object prev, JObject cur)
        {
            var parts = key.Split('|');
            var evt = new HelperEvent(EventNames.CodStatusChanged, EventSource.StatusApi)
                .With("gameTitle", parts.Length > 0 ? parts[0] : "")
                .With("platform", parts.Length > 1 ? parts[1] : "")
                .With("change", change);
            if (cur != null) evt.With("status", cur.ToObject<Dictionary<string, object>>());
            if (prev != null) evt.With("previous", prev.ToString());
            _bus.Publish(evt);
        }

        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() => Stop();
    }
}
