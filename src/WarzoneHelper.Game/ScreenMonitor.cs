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
        // private double? _lastHealth;   // health disabled
        private bool _lastDead;
        private bool _lastDeploy;

        // Debounce transient banner flicker
        private int _deadStreak;
        private int _deployStreak;

        // Rolling window of recently emitted chat lines to avoid re-firing scrolling text.
        private readonly RecentKeySet _recentChat = new RecentKeySet(40);
        // Chat lingers on screen for seconds while OCR garbage flickers frame-to-frame, so require a
        // message on 3 frames within an 8s window (gap-tolerant) before emitting.
        private readonly WindowedVote _chatVotes = new WindowedVote(3, TimeSpan.FromSeconds(8));
        // Lobby id / party-set jitter between frames: only accept a value that reads identically for
        // several consecutive frames before emitting a change.
        private readonly StableValue<string> _lobby = new StableValue<string>(3);
        private readonly StableValue<string> _party = new StableValue<string>(2);
        private string _lastSpectateKey;
        private DateTime _lastPerfEmit = DateTime.MinValue;
        private string _lastPartyCode;
        private string _lastInspectId;
        private string _lastGameVersion;

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
            // Health disabled — the bar-fill estimate is unreliable and unused. (Commented out.)
            // if (inMatch && s.HealthFraction.HasValue)
            // {
            //     double h = Math.Round(s.HealthFraction.Value, 2);
            //     if (!_lastHealth.HasValue || Math.Abs(h - _lastHealth.Value) >= 0.08)
            //     {
            //         var prev = _lastHealth;
            //         _lastHealth = h;
            //         WarzoneEvents.HealthChanged.Emit(_bus, e => e.With("health", h).With("previous", prev));
            //     }
            // }

            // Death (red banner; require 2 consecutive frames to fire, reset when it clears). In-match only.
            if (inMatch && s.DeathBannerVisible == true) _deadStreak++; else _deadStreak = 0;
            bool dead = _deadStreak >= 2;
            if (dead && !_lastDead)
                WarzoneEvents.PlayerDead.Emit(_bus);
            _lastDead = dead;

            // Deploy prompt
            if (s.DeployBannerVisible == true) _deployStreak++; else _deployStreak = 0;
            bool deploy = _deployStreak >= 2;
            if (deploy && !_lastDeploy)
                WarzoneEvents.Deployed.Emit(_bus);
            _lastDeploy = deploy;

            // Lobby ID — OCR flips a digit between frames (e.g. 59.. vs 55..), so only accept a value
            // that reads identically for several consecutive frames before emitting a change.
            if (!string.IsNullOrEmpty(s.LobbyId))
            {
                var prev = _lobby.HasValue ? _lobby.Value : null;
                if (_lobby.Observe(s.LobbyId))
                    WarzoneEvents.LobbyIdChanged.Emit(_bus, e => e
                        .With("lobbyId", s.LobbyId).With("previous", prev));
            }

            // Chat: parse OCR lines into "[CHANNEL] name" + body messages, emit each once.
            if (s.ChatLines != null && s.ChatLines.Length > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var msg in ChatParser.Parse(s.ChatLines))
                {
                    if (_recentChat.Contains(msg.Key)) continue;   // already emitted this message
                    if (!_chatVotes.Cast(msg.Key, now)) continue;  // not confident yet
                    _recentChat.Add(msg.Key);
                    #region OCR dump — confident chat messages
                    GameHelper.Core.Screen.OcrDump.Dump("chat", $"[{msg.Channel}] {msg.Name}: {msg.Text}");
                    #endregion
                    WarzoneEvents.ChatMessage.Emit(_bus, e2 => e2
                        .With("channel", msg.Channel).With("name", msg.Name).With("text", msg.Text));
                }
                _chatVotes.Prune(now);
            }

            // Party list (lobby): parse OCR lines into structured members, then emit only when the
            // set is STABLE across consecutive frames (OCR jitters frame-to-frame) and has changed.
            if (s.PartyLines != null && s.PartyLines.Length > 0)
            {
                var members = PartyParser.Parse(s.PartyLines);
                #region OCR dump — validated player names
                foreach (var m in members) GameHelper.Core.Screen.OcrDump.Dump("names", m.Name);
                #endregion
                // Include the section in the stable key so a member moving PARTY<->ONLINE re-emits.
                var key = string.Join("|", members.Select(m => (m.Group ?? "") + ":" + m.Key));

                if (key.Length > 0 && _party.Observe(key))
                {
                    var payload = members.Select(m => new Dictionary<string, object> {
                        { "name", m.Name }, { "level", m.Level }, { "group", m.Group } }).ToArray();
                    // A large top-right list is the full match/lobby roster, not your party.
                    var def = s.PartyIsMatchList ? WarzoneEvents.MatchListChanged : WarzoneEvents.PartyListChanged;
                    // Self position: topmost in the lobby party panel, bottommost in the in-game squad.
                    int selfIndex = s.PartyIsMatchList ? -1 : (inMatch ? payload.Length - 1 : 0);
                    def.Emit(_bus, e => e
                        .With("members", payload).With("count", payload.Length).With("selfIndex", selfIndex));
                }
            }

            // Party code (cached; persists until it changes, not per match).
            if (!string.IsNullOrEmpty(s.PartyCode) && s.PartyCode != _lastPartyCode)
            {
                _lastPartyCode = s.PartyCode;
                WarzoneEvents.PartyCodeChanged.Emit(_bus, e => e.With("code", s.PartyCode));
            }

            // Build/version watermark (confidence-gated): a change in ANY part means the game updated.
            if (!string.IsNullOrEmpty(s.GameVersion) && s.GameVersion != _lastGameVersion)
            {
                var prev = _lastGameVersion;
                _lastGameVersion = s.GameVersion;
                var parsed = GameVersionParser.Parse(s.GameVersion);   // raw + version/config/changelist/epoch/platform/hash
                WarzoneEvents.GameVersionChanged.Emit(_bus, e =>
                {
                    foreach (var kv in parsed) e.With(kv.Key, kv.Value);
                    e.With("previous", prev);
                });
            }

            // Inspect-player: emit when a new player's details are read.
            if (s.Inspect != null && s.Inspect.TryGetValue("activisionId", out var aid) && aid != null
                && aid.ToString() != _lastInspectId)
            {
                _lastInspectId = aid.ToString();
                WarzoneEvents.PlayerInspected.Emit(_bus, e =>
                { foreach (var kv in s.Inspect) e.With(kv.Key, kv.Value); });
            }

            // Perf overlay: throttle to ~1/3s since FPS/clock churn every frame.
            if (s.Perf != null && (DateTime.UtcNow - _lastPerfEmit).TotalSeconds >= 3)
            {
                _lastPerfEmit = DateTime.UtcNow;
                WarzoneEvents.PerfStats.Emit(_bus, e =>
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
                    #region OCR dump — parsed killfeed entries
                    GameHelper.Core.Screen.OcrDump.Dump("feed", item.Type == "kill"
                        ? $"{item.Killer} > {item.Victim}" : $"{item.Player} {item.Event}");
                    #endregion
                    if (item.Type == "kill")
                        WarzoneEvents.KillfeedEntry.Emit(_bus, e => e
                            .With("killer", item.Killer).With("victim", item.Victim));
                    else
                        WarzoneEvents.KillfeedEntry.Emit(_bus, e => e
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
                    WarzoneEvents.SpectatingPlayer.Emit(_bus, e => e
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
            _lastDead = false; _lastDeploy = false;
            _deadStreak = _deployStreak = 0;
            _chatVotes.Clear();
            // _lobby (StableValue) intentionally kept across matches until a new id stabilizes.
        }

        public void Stop() { _timer?.Dispose(); _timer = null; }
        public void Dispose() { Stop(); _source?.Dispose(); }
    }
}
