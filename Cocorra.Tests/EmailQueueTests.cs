using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.OTPService;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class EmailQueueTests
{
    [Fact]
    public void TryEnqueue_WhenFull_ReturnsFalse()
    {
        var queue = new EmailQueue();
        for (int i = 0; i < EmailQueue.Capacity; i++)
            Assert.True(queue.TryEnqueue((_, _) => Task.CompletedTask, "test"));

        Assert.False(queue.TryEnqueue((_, _) => Task.CompletedTask, "overflow"));
    }

    [Fact]
    public async Task BackgroundEmailSender_SendsQueuedEmails_AndKeepsGoingAfterAFailure()
    {
        var emailServiceMock = new Mock<IEmailService>();
        emailServiceMock.Setup(e => e.SendEmailAsync("fail@cocorra.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Resend API down"));
        var sent = new TaskCompletionSource();
        emailServiceMock.Setup(e => e.SendEmailAsync("ok@cocorra.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => sent.TrySetResult())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection().AddScoped(_ => emailServiceMock.Object).BuildServiceProvider();
        var queue = new EmailQueue();
        var sender = new BackgroundEmailSender(queue, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BackgroundEmailSender>.Instance);

        queue.TryEnqueue((sp, ct) => sp.GetRequiredService<IEmailService>().SendEmailAsync("fail@cocorra.com", "s", "b", ct), "failing email");
        queue.TryEnqueue((sp, ct) => sp.GetRequiredService<IEmailService>().SendEmailAsync("ok@cocorra.com", "s", "b", ct), "ok email");

        await sender.StartAsync(CancellationToken.None);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sender.StopAsync(CancellationToken.None);

        emailServiceMock.Verify(e => e.SendEmailAsync("ok@cocorra.com", "s", "b", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResendOtpAsync_QueueFull_StillReturnsGenericSuccess()
    {
        var userManagerMock = TestIdentityHelper.CreateMockUserManager();
        var email = "pending@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Ali", Email = email, EmailConfirmed = false };
        userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);

        var fullQueue = new Mock<IEmailQueue>();
        fullQueue.Setup(q => q.TryEnqueue(It.IsAny<Func<IServiceProvider, CancellationToken, Task>>(), It.IsAny<string>()))
            .Returns(false);

        var service = new OTPService(
            new Mock<IConfiguration>().Object,
            userManagerMock.Object,
            fullQueue.Object,
            new Mock<IOtpCodeService>().Object,
            new Mock<IHttpContextAccessor>().Object,
            new Mock<IEventTracker>().Object,
            new OtpAttemptLimiter(new MemoryCache(new MemoryCacheOptions())));

        var result = await service.ResendOtpAsync(email);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(OTPService.ResendGenericMessage, result.Data);
        fullQueue.Verify(q => q.TryEnqueue(It.IsAny<Func<IServiceProvider, CancellationToken, Task>>(), It.IsAny<string>()), Times.Once);
    }
}
