using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Cocorra.BLL.Services.TextRooms
{
    /// <summary>
    /// One gate per overflow family, so capacity checks and overflow spawns for a family are
    /// serialized. In-process: correct for the single API instance this deployment runs.
    /// </summary>
    public sealed class TextRoomFamilyLockProvider : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

        public SemaphoreSlim ForFamily(Guid primaryRoomId) =>
            _locks.GetOrAdd(primaryRoomId, static _ => new SemaphoreSlim(1, 1));

        public void Dispose()
        {
            foreach (var gate in _locks.Values)
                gate.Dispose();
            _locks.Clear();
        }
    }
}
