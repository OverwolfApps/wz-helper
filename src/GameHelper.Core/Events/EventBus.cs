using System;

namespace GameHelper.Core.Events
{
    /// <summary>
    /// Central fan-out point. Monitors publish here; the plugin host and console host
    /// subscribe. Exceptions in one subscriber never break dispatch to the others.
    /// </summary>
    public sealed class EventBus
    {
        /// <summary>Raised for every helper event. Payload is the structured event.</summary>
        public event Action<HelperEvent> OnEvent;

        /// <summary>Convenience log channel independent of the structured stream.</summary>
        public event Action<string> OnLog;

        public void Publish(HelperEvent evt)
        {
            if (evt == null) return;
            var handlers = OnEvent;
            if (handlers == null) return;

            foreach (Action<HelperEvent> h in handlers.GetInvocationList())
            {
                try { h(evt); }
                catch { /* never let a bad subscriber kill the pipeline */ }
            }
        }

        public void Publish(string name, string source, Action<HelperEvent> build = null)
        {
            var evt = new HelperEvent(name, source);
            build?.Invoke(evt);
            Publish(evt);
        }

        public void Log(string message)
        {
            var handlers = OnLog;
            if (handlers == null) return;
            foreach (Action<string> h in handlers.GetInvocationList())
            {
                try { h(message); } catch { }
            }
        }
    }
}
