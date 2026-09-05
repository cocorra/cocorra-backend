using System;

namespace Cocorra.BLL.Services.EventTracking
{
    public interface IEventTracker
    {
        /// <summary>
        /// AN-017/AN-018: whether Analytics:EnableNewEventEmission is on. Checked at the emit
        /// site rather than swallowed inside Track so the gate is visible where the event is
        /// written, and so a reader can tell which events are new instrumentation.
        /// </summary>
        bool NewEventEmissionEnabled { get; }

        /// <summary>
        /// AN-018: whether Analytics:EnableHighFrequencyEvents is on. Separate from
        /// NewEventEmissionEnabled so the high-volume increment can be reverted on its own.
        /// Implies NewEventEmissionEnabled — the low-frequency increment must be proven stable
        /// first, so this returns false unless both flags are set.
        /// </summary>
        bool HighFrequencyEventsEnabled { get; }

        /// <summary>Fire-and-forget event tracking. Enqueues event and returns immediately.</summary>
        void Track(string eventType, Guid? userId = null, object? properties = null);

        /// <summary>
        /// Extended fire-and-forget event tracking with idempotency natural key (eventKey),
        /// explicit sessionId (e.g. from SignalR hub), and correlationId.
        /// </summary>
        void Track(
            string eventType,
            Guid? userId,
            object? properties,
            string? eventKey = null,
            Guid? sessionId = null,
            Guid? correlationId = null,
            byte schemaVersion = 1);
    }
}
