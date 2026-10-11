using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.Email
{
    /// <summary>
    /// Bounded in-memory <see cref="IEmailQueue"/>, drained by <see cref="BackgroundEmailSender"/>.
    /// Registered as a singleton. Queued emails are lost if the process stops before they are
    /// sent; every OTP flow lets the user request a new code.
    /// </summary>
    public class EmailQueue : IEmailQueue
    {
        public const int Capacity = 1000;

        private readonly Channel<QueuedEmail> _channel = Channel.CreateBounded<QueuedEmail>(
            new BoundedChannelOptions(Capacity) { SingleReader = true });

        public ChannelReader<QueuedEmail> Reader => _channel.Reader;

        public bool TryEnqueue(Func<IServiceProvider, CancellationToken, Task> send, string description)
        {
            return _channel.Writer.TryWrite(new QueuedEmail(send, description));
        }
    }

    public sealed record QueuedEmail(Func<IServiceProvider, CancellationToken, Task> Send, string Description);
}
