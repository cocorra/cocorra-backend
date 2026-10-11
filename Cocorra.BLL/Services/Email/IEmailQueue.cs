using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.Email
{
    /// <summary>
    /// Hands email work off to <see cref="BackgroundEmailSender"/> so the request returns without
    /// waiting on it. Used by the anonymous OTP endpoints: doing the work only for registered
    /// emails would make their response time reveal which emails exist.
    /// </summary>
    public interface IEmailQueue
    {
        /// <summary>
        /// Queues <paramref name="send"/> for background execution in its own DI scope; resolve
        /// services such as <see cref="IEmailService"/> from the provider it is given. Returns
        /// false (and the work is dropped) when the queue is full. <paramref name="description"/>
        /// identifies the email in logs and must not contain the OTP or the recipient address.
        /// </summary>
        bool TryEnqueue(Func<IServiceProvider, CancellationToken, Task> send, string description);
    }
}
