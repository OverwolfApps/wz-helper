using System;
using System.Collections.Generic;
using WarzoneHelper.Core.Config;
using WarzoneHelper.Core.Events;
using WarzoneHelper.Core.Geo;
using WarzoneHelper.Core.Monitors;
using WarzoneHelper.Core.Net;
using WarzoneHelper.Core.Screen;

namespace WarzoneHelper.Core
{
    /// <summary>
    /// Top-level orchestrator. Builds and runs all monitors from a HelperConfig and exposes a
    /// single event stream. Hosted identically by the Overwolf plugin and the console runner.
    /// </summary>
    public sealed class HelperCore : IDisposable
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus = new EventBus();
        private readonly List<IMonitor> _monitors = new List<IMonitor>();

        private GeoIpResolver _geo;
        private ProcessTracker _proc;
        private PushedFrameSource _pushedSource;
        private bool _running;

        public EventBus Bus => _bus;
        public HelperConfig Config => _cfg;

        public HelperCore(HelperConfig cfg) { _cfg = cfg ?? new HelperConfig(); }

        public void Start()
        {
            if (_running) return;
            _running = true;

            _bus.Publish(EventNames.HelperStarted, "core", e => e.With("version", "1.0.0"));

            // GeoIP (shared)
            _geo = new GeoIpResolver();
            try { _geo.Load(_cfg.ExpandedGeoDbDir(), _cfg.AutoDownloadGeoDb, _bus.Log); }
            catch (Exception ex) { _bus.Log($"[geoip] load error: {ex.Message}"); }

            // Process tracker (shared) — always on; other monitors depend on it.
            _proc = new ProcessTracker(_cfg, _bus);
            _monitors.Add(_proc);

            if (_cfg.EnableLogWatch) _monitors.Add(new LogCacheMonitor(_cfg, _bus));

            if (_cfg.EnableNetwork)
            {
                var home = new HomeLocator();
                try { home.Resolve(_cfg.HomeLatitude, _cfg.HomeLongitude, _cfg.AutoResolveHome, _cfg.PublicIpUrl, _geo, _bus.Log); }
                catch (Exception ex) { _bus.Log($"[home] {ex.Message}"); }
                IUdpPeerSource udp = TryCreateEtw();
                _monitors.Add(new NetworkMonitor(_cfg, _bus, _geo, _proc, udp, home));
            }

            if (_cfg.EnableStatusApi) _monitors.Add(new StatusApiMonitor(_cfg, _bus));

            if (_cfg.EnableScreen)
            {
                IOcrEngine ocr = TryCreateOcr();
                var analyzer = new WarzoneScreenAnalyzer(_cfg.Regions, ocr);
                IFrameSource source;
                if (_cfg.SelfCapture)
                {
                    source = new GdiWindowFrameSource(() => _proc.CurrentPids());
                }
                else
                {
                    _pushedSource = new PushedFrameSource();
                    source = _pushedSource;
                }
                _monitors.Add(new ScreenMonitor(_cfg, _bus, _proc, source, analyzer));
            }

            foreach (var m in _monitors)
            {
                try { m.Start(); _bus.Log($"[core] started {m.Name}"); }
                catch (Exception ex) { _bus.Log($"[core] {m.Name} failed to start: {ex.Message}"); }
            }
        }

        private IUdpPeerSource TryCreateEtw()
        {
            try { return new EtwUdpPeerSource(); }
            catch { return new NullUdpPeerSource(); }
        }

        private IOcrEngine TryCreateOcr()
        {
            try { return new TesseractOcrEngine(_cfg.ExpandedTessDir(), _bus.Log); }
            catch (Exception ex) { _bus.Log($"[ocr] init failed: {ex.Message}"); return new NullOcrEngine(); }
        }

        /// <summary>Feed an externally captured frame (Overwolf in-memory screenshot bytes).</summary>
        public void PushFrame(byte[] imageBytes) => _pushedSource?.Push(imageBytes);

        /// <summary>
        /// Relay an Overwolf GEP event into the stream as a low-confidence hint. GEP is unreliable,
        /// so consumers should treat these as corroboration for our own CV/network events.
        /// </summary>
        public void ReportGepEvent(string name, string data)
        {
            string mapped;
            switch (name)
            {
                case "match_start": mapped = EventNames.MatchStarted; break;
                case "match_end": mapped = EventNames.MatchEnded; break;
                case "scene": mapped = EventNames.SceneChanged; break;
                case "mode": mapped = EventNames.ModeChanged; break;
                default: mapped = name; break;
            }
            _bus.Publish(mapped, EventSource.Gep, e => e.With("raw", data).With("gepName", name));
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            foreach (var m in _monitors)
            {
                try { m.Stop(); m.Dispose(); } catch { }
            }
            _monitors.Clear();
            _geo?.Dispose(); _geo = null;
            _pushedSource = null;
            _bus.Publish(EventNames.HelperStopped, "core");
        }

        public void Dispose() => Stop();
    }
}
