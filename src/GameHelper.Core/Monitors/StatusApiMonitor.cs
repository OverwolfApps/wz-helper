using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using GameHelper.Core.Config;
using GameHelper.Core.Events;
using GameHelper.Core.Net;

namespace GameHelper.Core.Monitors
{
    /// <summary>
    /// Generic status-API poller: on an interval it fetches <see cref="HelperConfig.StatusApiUrl"/>
    /// and hands the raw body to an <see cref="IStatusParser"/>. The parser (game-specific, or the
    /// default <see cref="RawStatusParser"/>) decides how to interpret it and what to emit — this
    /// class knows nothing about any vendor's response shape.
    /// </summary>
    public sealed class StatusApiMonitor : IMonitor
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly IStatusParser _parser;
        private readonly HttpClient _http;
        private Timer _timer;

        public string Name => "statusapi";

        public StatusApiMonitor(HelperConfig cfg, EventBus bus, IStatusParser parser = null)
        {
            _cfg = cfg; _bus = bus; _parser = parser ?? new RawStatusParser();
            _http = CreateHttp(cfg);
        }

        private static HttpClient CreateHttp(HelperConfig cfg)
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var headers = cfg.HttpHeaders ?? new Dictionary<string, string>();
            // Default to a real Chrome UA + JSON Accept; config headers add/override.
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                headers.TryGetValue("User-Agent", out var ua) ? ua : HttpDefaults.ChromeUserAgent);
            if (!headers.ContainsKey("Accept"))
                c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            foreach (var kv in headers)
                if (!string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    c.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);
            return c;
        }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_cfg.StatusApiUrl))
            {
                _bus.Log("[status] no StatusApiUrl configured; poller idle.");
                return;
            }
            _timer = new Timer(async _ => await Poll(), null, 2000, Math.Max(15000, _cfg.StatusPollMs));
        }

        private async System.Threading.Tasks.Task Poll()
        {
            try
            {
                var body = await _http.GetStringAsync(_cfg.StatusApiUrl).ConfigureAwait(false);
                _parser.Handle(body, _bus);
            }
            catch (Exception ex) { _bus.Log($"[status] poll error: {ex.Message}"); }
        }

        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() => Stop();
    }
}
