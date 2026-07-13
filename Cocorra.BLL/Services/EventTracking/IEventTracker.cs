using System;

namespace Cocorra.BLL.Services.EventTracking
{
    public interface IEventTracker
    {
        /// <summary>Fire-and-forget event tracking. Enqueues event and returns immediately.</summary>
        void Track(string eventType, Guid? userId = null, object? properties = null);
    }
}
