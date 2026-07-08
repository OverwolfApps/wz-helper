using System;

namespace GameHelper.Core
{
    /// <summary>The match lifecycle, derived from how many game servers are connected.</summary>
    public enum MatchPhase
    {
        /// <summary>No game server connected — in menus / matchmaking.</summary>
        Searching,
        /// <summary>The first game server connected (lobby/pre-game); match found but not started.</summary>
        Found,
        /// <summary>A second game server connected — the match is live (this is when InMatch is true).</summary>
        Started,
        /// <summary>Dropped from two servers back to one after a match — the match has ended (leaving).</summary>
        Ended,
    }

    /// <summary>
    /// "Am I in a match?" derived from the game-server connection pattern. In the real event data a
    /// match runs the whole time on ONE sustained high-throughput server (ramping to 30-66 kb/s),
    /// with only brief low-traffic lobby/hand-off relays around the edges — so the match signal is
    /// "a high-traffic gameplay server is connected OR two+ game servers are connected" (computed by
    /// the NetworkMonitor from live per-poll throughput, since a server's CONNECTED event fires
    /// before it ramps). Phases:
    ///   Searching  0 game servers
    ///   Found      >=1 game server but the match signal isn't (yet) true (a single not-yet-ramped
    ///              or low-traffic lobby server)
    ///   Started    the match signal is true (InMatch)
    ///   Ended      after a match, still >=1 server but the signal dropped (leaving)
    /// Shared with the NetworkMonitor so game-server events can be stamped/filtered by match state.
    /// </summary>
    public sealed class MatchState
    {
        private MatchPhase _phase = MatchPhase.Searching;
        private bool _reachedStarted;   // this match has been Started (so a lingering server reads Ended, not Found)

        public MatchPhase Phase => _phase;
        public bool InMatch => _phase == MatchPhase.Started;
        public DateTime? SinceUtc { get; private set; }

        /// <summary>Raised (phase, inMatch) whenever the phase actually changes.</summary>
        public event Action<MatchPhase, bool> Changed;

        /// <summary>Recompute the phase from the connected game-server count and the derived match
        /// signal (high-traffic gameplay server present, or 2+ servers connected).</summary>
        public void Update(int serverCount, bool inMatchSignal)
        {
            MatchPhase next;
            if (serverCount <= 0) { next = MatchPhase.Searching; _reachedStarted = false; }
            else if (inMatchSignal) { next = MatchPhase.Started; _reachedStarted = true; }
            else next = _reachedStarted ? MatchPhase.Ended : MatchPhase.Found;

            if (next == _phase) return;
            _phase = next;
            SinceUtc = next == MatchPhase.Started ? DateTime.UtcNow : (DateTime?)null;
            try { Changed?.Invoke(next, InMatch); } catch { }
        }

        /// <summary>Force back to searching (e.g. the game process exited).</summary>
        public void Reset()
        {
            _reachedStarted = false;
            if (_phase == MatchPhase.Searching) return;
            _phase = MatchPhase.Searching;
            SinceUtc = null;
            try { Changed?.Invoke(_phase, false); } catch { }
        }
    }
}
