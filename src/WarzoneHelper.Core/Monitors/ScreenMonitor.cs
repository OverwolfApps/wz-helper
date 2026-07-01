using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WarzoneHelper.Core.Config;
using WarzoneHelper.Core.Events;
using WarzoneHelper.Core.Screen;

namespace WarzoneHelper.Core.Monitors
{
    /// <summary>
    /// Drives the CV pipeline on a fixed interval: capture a frame, analyze it, diff against
    /// prior state, and emit DEPLOYED / HEALTH_CHANGED / PLAYER_DEAD / LOBBY_ID_CHANGED.
    /// These are our own events and run regardless of whether Overwolf GEP fires.
    /// </summary>
    public sealed class ScreenMonitor : IMonitor
    {
        private readonly HelperConfig _cfg;
        private readonly EventBus _bus;
        private readonly ProcessTracker _proc;
        private readonly IFrameSource _source;
        private readonly WarzoneScreenAnalyzer _analyzer;
        private Timer _timer;
        private int _busy;
        private bool? _wasCapturing;

        // Prior state for change detection
        private double? _lastHealth;
        private bool _lastDead;
        private bool _lastDeploy;
        private string _lastLobbyId;

        // Debounce transient banner flicker
        private int _deadStreak;
        private int _deployStreak;

        // Rolling window of recently emitted chat lines to avoid re-firing scrolling text.
        private readonly LinkedList<string> _recentChat = new LinkedList<string>();
        private readonly HashSet<string> _recentChatSet = new HashSet<string>();
        private const int RecentChatMax = 40;

        public string Name => "screen";
        public IFrameSource Source => _source;

        public ScreenMonitor(HelperConfig cfg, EventBus bus, ProcessTracker proc,
            IFrameSource source, WarzoneScreenAnalyzer analyzer)
        {
            _cfg = cfg; _bus = bus; _proc = proc; _source = source; _analyzer = analyzer;
        }

        public void Start()
        {
            _timer = new Timer(_ => Tick(), null, 1500, Math.Max(200, _cfg.ScreenPollMs));
        }

        private void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return; // skip if previous frame still processing
            try
            {
                if (!_proc.IsRunning) { SetCapturing(false); Reset(); return; }
                using (var frame = _source.Capture())
                {
                    // Null frame = game not topmost/visible. Analyze (and OCR) only when we have a
                    // real game frame, so we never read the desktop or other apps.
                    if (frame?.Bitmap == null) { SetCapturing(false); return; }
                    SetCapturing(true);
                    var s = _analyzer.Analyze(frame.Bitmap);
                    Evaluate(s);
                }
            }
            catch (Exception ex) { _bus.Log($"[screen] {ex.Message}"); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private void Evaluate(ScreenState s)
        {
            // Health
            if (s.HealthFraction.HasValue)
            {
                double h = Math.Round(s.HealthFraction.Value, 2);
                if (!_lastHealth.HasValue || Math.Abs(h - _lastHealth.Value) >= 0.08)
                {
                    var prev = _lastHealth;
                    _lastHealth = h;
                    _bus.Publish(EventNames.HealthChanged, EventSource.ScreenCv, e => e
                        .With("health", h).With("previous", prev));
                }
            }

            // Death (require 2 consecutive frames to fire, and reset when it clears)
            if (s.DeathBannerVisible == true) _deadStreak++; else _deadStreak = 0;
            bool dead = _deadStreak >= 2;
            if (dead && !_lastDead)
                _bus.Publish(EventNames.PlayerDead, EventSource.ScreenCv, e => e.With("health", _lastHealth));
            _lastDead = dead;

            // Deploy prompt
            if (s.DeployBannerVisible == true) _deployStreak++; else _deployStreak = 0;
            bool deploy = _deployStreak >= 2;
            if (deploy && !_lastDeploy)
                _bus.Publish(EventNames.Deployed, EventSource.ScreenCv);
            _lastDeploy = deploy;

            // Lobby ID
            if (!string.IsNullOrEmpty(s.LobbyId) && s.LobbyId != _lastLobbyId)
            {
                var prev = _lastLobbyId;
                _lastLobbyId = s.LobbyId;
                _bus.Publish(EventNames.LobbyIdChanged, EventSource.ScreenCv, e => e
                    .With("lobbyId", s.LobbyId).With("previous", prev));
            }

            // Chat: emit each newly-seen line once (OCR is noisy and scrolls).
            if (s.ChatLines != null)
            {
                foreach (var line in s.ChatLines)
                {
                    var key = Normalize(line);
                    if (key.Length == 0 || _recentChatSet.Contains(key)) continue;
                    RememberChat(key);
                    _bus.Publish(EventNames.ChatMessage, EventSource.ScreenCv, e => e.With("text", line));
                }
            }
        }

        private static string Normalize(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private void RememberChat(string key)
        {
            _recentChat.AddLast(key);
            _recentChatSet.Add(key);
            while (_recentChat.Count > RecentChatMax)
            {
                var first = _recentChat.First.Value;
                _recentChat.RemoveFirst();
                _recentChatSet.Remove(first);
            }
        }

        private void SetCapturing(bool active)
        {
            if (_wasCapturing == active) return;
            _wasCapturing = active;
            _bus.Log(active
                ? "[screen] game is foreground — CV active"
                : "[screen] game not foreground — CV idle (no capture/OCR)");
        }

        private void Reset()
        {
            _lastHealth = null; _lastDead = false; _lastDeploy = false;
            _deadStreak = _deployStreak = 0;
            // keep _lastLobbyId across matches until a new one appears
        }

        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() { Stop(); _source?.Dispose(); }
    }
}
