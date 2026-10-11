using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.OTPService;
using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.Tests.Helpers;

/// <summary>
/// Test <see cref="IEmailQueue"/> that runs each queued item immediately, against a provider
/// holding the given <see cref="IEmailService"/> and <see cref="IOtpCodeService"/>. Like
/// <see cref="BackgroundEmailSender"/>, a failure is swallowed so it never reaches the caller.
/// </summary>
public sealed class InlineEmailQueue : IEmailQueue
{
    private readonly IServiceProvider _services;

    public InlineEmailQueue(IEmailService emailService, IOtpCodeService otpCodes)
    {
        _services = new ServiceCollection()
            .AddSingleton(emailService)
            .AddSingleton(otpCodes)
            .BuildServiceProvider();
    }

    public int EnqueuedCount { get; private set; }

    public bool TryEnqueue(Func<IServiceProvider, CancellationToken, Task> send, string description)
    {
        EnqueuedCount++;
        try
        {
            send(_services, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Mirrors BackgroundEmailSender: failures are logged there, never thrown.
        }
        return true;
    }
}
