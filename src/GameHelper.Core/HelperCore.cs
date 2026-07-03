using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Config;
using GameHelper.Core.Events;
using GameHelper.Core.Geo;
using GameHelper.Core.Monitors;
using GameHelper.Core.Net;
using GameHelper.Core.Screen;

namespace GameHelper.Core
{
    /// <summary>
    /// Top-level orchestrator. Builds and runs the shared monitors from a HelperConfig, then lets
    /// the active <see cref="IGameProfile"/> add game-specific monitors on top, exposing a single
    /// event stream. Hosted identically by the Overwolf plugin and the console runner.
    /// </summary>
    public sealed class HelperCore : IDisposable
    {
        private readonly HelperConfig _cfg;
        private readonly IGameProfile _profile;
        private readonly EventBus _bus = new EventBus();
        private readonly List<IMonitor> _monitors = new List<IMonitor>();

        private GeoIpResolver _geo;
        private ProcessTracker _proc;
        private PushedFrameSource _pushedSource;
        private readonly MatchState _match = new MatchState();
        private bool _running;

        public EventBus Bus => _bus;
        public HelperConfig Config => _cfg;
        public IGameProfile Profile => _profile;

        public HelperCore(HelperConfig cfg, IGameProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _cfg = cfg ?? _profile.CreateDefaultConfig();
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            // Self-describing event catalog (Core + game) so consumers discover events/fields at
            // runtime and can detect schema changes via eventsHash.
            var catalog = EventCatalog.Core.Concat(_profile.Events ?? System.Linq.Enumerable.Empty<EventDoc>()).ToList();
            _bus.Publish(EventNames.HelperStarted, "core", e => e
                .With("version", "1.0.0").With("game", _profile.Name)
                .With("events", catalog)
                .With("eventsHash", EventCatalog.Hash(catalog))
                .With("eventCount", catalog.Count));

            // Externalized OCR rules (whitelists/patterns/lengths/confidence) from ocr.jsonc, applied
            // onto the active game's field registry.
            OcrConfig.LoadOrCreate(OcrConfig.DefaultPath(_profile.Name), _profile.OcrFields,
                () => _profile.PerfStripWhitelist, v => _profile.PerfStripWhitelist = v, _bus.Log);

            // Derive coarse match state from our own CV events + GEP hints, so the NetworkMonitor can
            // stamp/filter game-server events by whether we're actually in a match.
            _bus.OnEvent += UpdateMatchState;

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
                _monitors.Add(new NetworkMonitor(_cfg, _bus, _geo, _proc, udp, home, _match));
            }

            if (_cfg.EnableStatusApi) _monitors.Add(new StatusApiMonitor(_cfg, _bus, _profile.CreateStatusParser(_cfg)));

            // Shared screen plumbing (frame source + OCR engine) that game monitors read from.
            IOcrEngine ocr = null;
            IFrameSource frameSource = null;
            if (_cfg.EnableScreen)
            {
                ocr = TryCreateOcr();
                if (_cfg.SelfCapture)
                    frameSource = new GdiWindowFrameSource(() => _proc.CurrentPids(), _cfg.CaptureExcludeTitles);
                else
                    frameSource = _pushedSource = new PushedFrameSource();
            }

            // Game-specific monitors (screen analysis, player roster, ...).
            var ctx = new GameContext
            {
                Config = _cfg, Bus = _bus, Proc = _proc, Match = _match,
                Geo = _geo, Ocr = ocr, FrameSource = frameSource
            };
            foreach (var m in _profile.CreateGameMonitors(ctx) ?? Array.Empty<IMonitor>())
                if (m != null) _monitors.Add(m);

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

        private void UpdateMatchState(HelperEvent evt)
        {
            switch (evt.Name)
            {
                // A confirmed high-throughput game server is the most reliable "in a match" signal —
                // far more dependable than GEP/CV. Presence => in match, drop => out.
                case EventNames.GameServerConnected:
                    _match.Set(true);
                    break;
                case EventNames.GameServerDisconnected:
                    _match.Set(false);
                    break;
                case EventNames.MatchStarted:
                case EventNames.Deployed:
                    _match.Set(true);
                    break;
                case EventNames.MatchEnded:
                case EventNames.GameProcessStopped:
                    _match.Set(false);
                    break;
                case EventNames.SceneChanged:
                    // GEP scene value arrives as the "raw" payload (e.g. "in_game", "lobby_wz").
                    if (evt.Data != null && evt.Data.TryGetValue("raw", out var raw) && raw != null)
                    {
                        var scene = raw.ToString();
                        if (scene.IndexOf("in_game", StringComparison.OrdinalIgnoreCase) >= 0) _match.Set(true);
                        else if (scene.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) >= 0) _match.Set(false);
                    }
                    break;
            }
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
