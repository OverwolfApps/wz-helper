using System;
using GameHelper.Core.Events;

namespace GameHelper.Core.Monitors
{
    /// <summary>
    /// Interprets the raw body returned by the configured status API each poll. The API shape is
    /// game/vendor-specific, so <see cref="StatusApiMonitor"/> stays generic (fetch only) and hands
    /// the body here. Implementations are stateful — they may diff against previous polls — and emit
    /// whatever events they like via the bus.
    /// </summary>
    public interface IStatusParser
    {
        /// <summary>Handle one poll's response body (already fetched).</summary>
        void Handle(string responseBody, EventBus bus);
    }

    /// <summary>
    /// Default parser: emits a STATUS_RESPONSE event carrying the raw body, only when the body
    /// changes between polls (so a static endpoint doesn't spam the stream). Games that understand
    /// their API supply their own <see cref="IStatusParser"/> instead.
    /// </summary>
    public sealed class RawStatusParser : IStatusParser
    {
        private string _last;

        public void Handle(string responseBody, EventBus bus)
        {
            if (responseBody == null || responseBody == _last) return;
            _last = responseBody;
            CoreEvents.StatusResponse.Emit(bus, e => e.With("body", responseBody));
        }
    }
}
