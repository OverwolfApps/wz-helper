using System;

namespace WarzoneHelper.Core
{
    /// <summary>
    /// Best-effort "am I actually in a match?" flag, derived from our own CV events and GEP hints.
    /// Shared with the NetworkMonitor so game-server events can be stamped/filtered by match state.
    /// This is intentionally coarse until we have real in-match capture data to tune against.
    /// </summary>
    public sealed class MatchState
    {
        private volatile bool _inMatch;
        public bool InMatch => _inMatch;
        public DateTime? SinceUtc { get; private set; }

        public void Set(bool inMatch)
        {
            if (_inMatch == inMatch) return;
            _inMatch = inMatch;
            SinceUtc = inMatch ? DateTime.UtcNow : (DateTime?)null;
        }
    }
}
