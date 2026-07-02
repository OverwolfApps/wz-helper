using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GameHelper.Core.Config;
using GameHelper.Core.Events;
using GameHelper.Core.Screen;

using GameHelper.Core;
using GameHelper.Core.Monitors;
using GameHelper.Core.Util;
using GameHelper.Core.Text;
namespace WarzoneHelper.Game
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
        private readonly MatchState _match;
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
        private readonly RecentKeySet _recentChat = new RecentKeySet(40);
        // Chat lingers on screen for seconds, so a real message is read across many frames while
        // OCR garbage flickers frame-to-frame (each garbled read is a different key). Require the
        // same message on N frames before emitting, but count sightings within a rolling time
        // WINDOW rather than strictly consecutively — so a dropped OCR frame or a fast-scrolling
        // line still accumulates its votes. Votes expire once a message hasn't been seen for the
        // window (bounds memory and stops long-gone text from ever firing).
        private readonly Dictionary<string, (int count, DateTime last)> _chatVotes =
            new Dictionary<string, (int, DateTime)>();
        private const int ChatStableFrames = 3;
        private static readonly TimeSpan ChatVoteWindow = TimeSpan.FromSeconds(8);
        private string _lastPartyKey;
        private string _pendingPartyKey;
        private int _partyStable;
        private const int PartyStableFrames = 2;
        private string _lastSpectateKey;
        private DateTime _lastPerfEmit = DateTime.MinValue;
        private string _pendingLobbyId;
        private int _lobbyStable;
        private const int LobbyStableFrames = 3;
        private string _lastPartyCode;
        private string _lastInspectId;

        public string Name => "screen";
        public IFrameSource Source => _source;

        public ScreenMonitor(HelperConfig cfg, EventBus bus, ProcessTracker proc,
            IFrameSource source, WarzoneScreenAnalyzer analyzer, MatchState match)
        {
            _cfg = cfg; _bus = bus; _proc = proc; _source = source; _analyzer = analyzer; _match = match;
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
                    bool inMatch = _match != null && _match.InMatch;
                    var s = _analyzer.Analyze(frame.Bitmap, inMatch, frame.ExcludedRects);
                    Evaluate(s, inMatch);
                }
            }
            catch (Exception ex) { _bus.Log($"[screen] {ex.Message}"); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private void Evaluate(ScreenState s, bool inMatch)
        {
            // Health (only meaningful in a match; skip lobby/menu preview noise)
            if (inMatch && s.HealthFraction.HasValue)
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

            // Death (require 2 consecutive frames to fire, and reset when it clears). In-match only.
            if (inMatch && s.DeathBannerVisible == true) _deadStreak++; else _deadStreak = 0;
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

            // Lobby ID — OCR flips a digit between frames (e.g. 59.. vs 55..), so only accept a value
            // that reads identically for several consecutive frames before emitting a change.
            if (!string.IsNullOrEmpty(s.LobbyId))
            {
                if (s.LobbyId == _pendingLobbyId) _lobbyStable++;
                else { _pendingLobbyId = s.LobbyId; _lobbyStable = 1; }

                if (_lobbyStable == LobbyStableFrames && s.LobbyId != _lastLobbyId)
                {
                    var prev = _lastLobbyId;
                    _lastLobbyId = s.LobbyId;
                    _bus.Publish(EventNames.LobbyIdChanged, EventSource.ScreenCv, e => e
                        .With("lobbyId", s.LobbyId).With("previous", prev));
                }
            }

            // Chat: parse OCR lines into "[CHANNEL] name" + body messages, emit each once.
            if (s.ChatLines != null && s.ChatLines.Length > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var msg in ChatParser.Parse(s.ChatLines))
                {
                    if (_recentChat.Contains(msg.Key)) continue;   // already emitted this message
                    // Fresh vote if never seen or the previous sighting fell outside the window.
                    var count = _chatVotes.TryGetValue(msg.Key, out var e) && now - e.last <= ChatVoteWindow
                        ? e.count + 1 : 1;
                    _chatVotes[msg.Key] = (count, now);
                    if (count < ChatStableFrames) continue;           // not confident yet
                    _recentChat.Add(msg.Key);
                    _chatVotes.Remove(msg.Key);
                    _bus.Publish(EventNames.ChatMessage, EventSource.ScreenCv, e2 => e2
                        .With("channel", msg.Channel).With("name", msg.Name).With("text", msg.Text));
                }
                // Expire votes for messages not seen within the window (bounds memory; stops
                // long-scrolled-off garbage from ever reaching the threshold).
                if (_chatVotes.Count > 0)
                    foreach (var k in _chatVotes.Where(kv => now - kv.Value.last > ChatVoteWindow)
                                                .Select(kv => kv.Key).ToList())
                        _chatVotes.Remove(k);
            }

            // Party list (lobby): parse OCR lines into structured members, then emit only when the
            // set is STABLE across consecutive frames (OCR jitters frame-to-frame) and has changed.
            if (s.PartyLines != null && s.PartyLines.Length > 0)
            {
                var members = PartyParser.Parse(s.PartyLines);
                // Include the section in the stable key so a member moving PARTY<->ONLINE re-emits.
                var key = string.Join("|", members.Select(m => (m.Group ?? "") + ":" + m.Key));

                if (key == _pendingPartyKey) _partyStable++;
                else { _pendingPartyKey = key; _partyStable = 1; }

                if (_partyStable == PartyStableFrames && key.Length > 0 && key != _lastPartyKey)
                {
                    _lastPartyKey = key;
                    var payload = members.Select(m => new Dictionary<string, object> {
                        { "name", m.Name }, { "level", m.Level }, { "group", m.Group } }).ToArray();
                    // A large top-right list is the full match/lobby roster, not your party.
                    var name = s.PartyIsMatchList ? EventNames.MatchListChanged : EventNames.PartyListChanged;
                    // Self position: topmost in the lobby party panel, bottommost in the in-game squad.
                    int selfIndex = s.PartyIsMatchList ? -1 : (inMatch ? payload.Length - 1 : 0);
                    _bus.Publish(name, EventSource.ScreenCv, e => e
                        .With("members", payload).With("count", payload.Length).With("selfIndex", selfIndex));
                }
            }

            // Party code (cached; persists until it changes, not per match).
            if (!string.IsNullOrEmpty(s.PartyCode) && s.PartyCode != _lastPartyCode)
            {
                _lastPartyCode = s.PartyCode;
                _bus.Publish(EventNames.PartyCodeChanged, EventSource.ScreenCv, e => e.With("code", s.PartyCode));
            }

            // Inspect-player: emit when a new player's details are read.
            if (s.Inspect != null && s.Inspect.TryGetValue("activisionId", out var aid) && aid != null
                && aid.ToString() != _lastInspectId)
            {
                _lastInspectId = aid.ToString();
                _bus.Publish(EventNames.PlayerInspected, EventSource.ScreenCv, e =>
                { foreach (var kv in s.Inspect) e.With(kv.Key, kv.Value); });
            }

            // Perf overlay: throttle to ~1/3s since FPS/clock churn every frame.
            if (s.Perf != null && (DateTime.UtcNow - _lastPerfEmit).TotalSeconds >= 3)
            {
                _lastPerfEmit = DateTime.UtcNow;
                _bus.Publish(EventNames.PerfStats, EventSource.ScreenCv, e =>
                {
                    foreach (var kv in s.Perf) e.With(kv.Key, kv.Value);
                });
            }

            // Killfeed + event log: emit each new entry once (feed lines persist across frames).
            if (s.FeedLines != null && s.FeedLines.Length > 0)
            {
                foreach (var item in FeedParser.Parse(s.FeedLines))
                {
                    if (_recentChat.Contains(item.Key)) continue;
                    _recentChat.Add(item.Key);
                    if (item.Type == "kill")
                        _bus.Publish(EventNames.KillfeedEntry, EventSource.ScreenCv, e => e
                            .With("killer", item.Killer).With("victim", item.Victim));
                    else
                        _bus.Publish(EventNames.KillfeedEntry, EventSource.ScreenCv, e => e
                            .With("player", item.Player).With("event", item.Event));
                }
            }

            // Spectating (when dead): emit on change of spectated player.
            if (!string.IsNullOrEmpty(s.SpectatingName))
            {
                var specKey = TextOps.Norm(s.SpectatingName) + s.SpectatingId;
                if (specKey != _lastSpectateKey)
                {
                    _lastSpectateKey = specKey;
                    _bus.Publish(EventNames.SpectatingPlayer, EventSource.ScreenCv, e => e
                        .With("name", s.SpectatingName).With("id", s.SpectatingId));
                }
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
            _chatVotes.Clear();
            // keep _lastLobbyId across matches until a new one appears
        }

        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() { Stop(); _source?.Dispose(); }
    }
}
