using System;
using System.Net.Http;
using System.Threading;
using GameHelper.Core.Config;
using GameHelper.Core.Events;

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
        private static readonly HttpClient Http = CreateHttp();
        private Timer _timer;

        public string Name => "statusapi";

        public StatusApiMonitor(HelperConfig cfg, EventBus bus, IStatusParser parser = null)
        {
            _cfg = cfg; _bus = bus; _parser = parser ?? new RawStatusParser();
        }

        private static HttpClient CreateHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.Add("User-Agent", "GameHelper/1.0");
            c.DefaultRequestHeaders.Add("Accept", "application/json");
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
                var body = await Http.GetStringAsync(_cfg.StatusApiUrl).ConfigureAwait(false);
                _parser.Handle(body, _bus);
            }
            catch (Exception ex) { _bus.Log($"[status] poll error: {ex.Message}"); }
        }

        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() => Stop();
    }
}
