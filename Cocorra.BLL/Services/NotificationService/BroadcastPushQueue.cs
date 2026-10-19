using System;
using System.Collections.Generic;
using System.Threading.Channels;

namespace Cocorra.BLL.Services.NotificationService
{
    public sealed record PushRecipient(Guid UserId, string FcmToken);

    public sealed record BroadcastPushJob(Guid BroadcastId, string Title, string Message, IReadOnlyList<PushRecipient> Recipients);

    /// <summary>
    /// Hands a broadcast's pushes off to <see cref="BroadcastPushSender"/> so the admin's request
    /// returns once the in-app notifications are saved, rather than after one FCM call per user.
    /// </summary>
    public interface IBroadcastPushQueue
    {
        /// <summary>Returns false (and the job is dropped) when the queue is full.</summary>
        bool TryEnqueue(BroadcastPushJob job);
    }

    /// <summary>
    /// Bounded in-memory <see cref="IBroadcastPushQueue"/>, registered as a singleton. A job is lost
    /// if the process stops before it is sent; the in-app notifications it pushes are already saved.
    /// </summary>
    public class BroadcastPushQueue : IBroadcastPushQueue
    {
        public const int Capacity = 20;

        private readonly Channel<BroadcastPushJob> _channel = Channel.CreateBounded<BroadcastPushJob>(
            new BoundedChannelOptions(Capacity) { SingleReader = true });

        public ChannelReader<BroadcastPushJob> Reader => _channel.Reader;

        public bool TryEnqueue(BroadcastPushJob job) => _channel.Writer.TryWrite(job);
    }
}
