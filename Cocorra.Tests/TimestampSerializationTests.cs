using System;
using System.Text.Json;
using System.Threading.Channels;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class TimestampSerializationTests
{
    [Fact]
    public void Track_UnspecifiedDateTimeProperty_SerializesWithZAndKeepsItsPropertyName()
    {
        var queue = Channel.CreateBounded<UserEvent>(
            new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropWrite });

        var httpAccessor = new Mock<IHttpContextAccessor>();
        httpAccessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(c => c["Analytics:IpHashSalt"]).Returns("test-salt");

        var tracker = new EventTracker(
            queue,
            NullLogger<EventTracker>.Instance,
            httpAccessor.Object,
            configuration.Object);

        tracker.Track(EventTypes.FeatureViewed, Guid.NewGuid(), new
        {
            ScheduledFor = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Unspecified)
        });

        Assert.True(queue.Reader.TryRead(out var queued));
        Assert.NotNull(queued!.PropertiesJson);

        using var document = JsonDocument.Parse(queued.PropertiesJson!);
        var scheduledFor = document.RootElement.GetProperty("ScheduledFor");

        Assert.Equal("2026-11-11T10:00:00Z", scheduledFor.GetString());
    }
}
