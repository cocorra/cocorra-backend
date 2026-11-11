using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class TimestampSerializationTests
{
    private static JsonSerializerOptions CreateMvcJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeJsonConverter());
        return options;
    }

    public static TheoryData<object, string, string> CalendarDateDtos() => new()
    {
        { new WeeklyReturnCohortDto { WeekStart = new DateOnly(2026, 11, 14) }, "weekStart", "2026-11-14" },
        { new CohortGridRowDto { CohortWeekStart = new DateOnly(2026, 11, 14) }, "cohortWeekStart", "2026-11-14" }
    };

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

    [Theory]
    [MemberData(nameof(CalendarDateDtos))]
    public void CalendarDateFields_SerializeAsDateOnly_WithoutTimeOrOffset(
        object dto, string propertyName, string expectedValue)
    {
        var json = JsonSerializer.Serialize(dto, dto.GetType(), CreateMvcJsonOptions());

        using var document = JsonDocument.Parse(json);
        var property = document.RootElement.GetProperty(propertyName);

        Assert.Equal(JsonValueKind.String, property.ValueKind);
        Assert.Equal(expectedValue, property.GetString());
    }

    [Fact]
    public void NullableCalendarDateField_SerializesAsDateOnly_AndNullStaysNull()
    {
        var options = CreateMvcJsonOptions();

        var withDateJson = JsonSerializer.Serialize(
            new PipelineHealthDto { LastSnapshotDate = new DateOnly(2026, 11, 11) },
            options);
        using var withDateDocument = JsonDocument.Parse(withDateJson);
        var withDateProperty = withDateDocument.RootElement.GetProperty("lastSnapshotDate");
        Assert.Equal(JsonValueKind.String, withDateProperty.ValueKind);
        Assert.Equal("2026-11-11", withDateProperty.GetString());

        var withNullJson = JsonSerializer.Serialize(
            new PipelineHealthDto { LastSnapshotDate = null },
            options);
        using var withNullDocument = JsonDocument.Parse(withNullJson);
        var withNullProperty = withNullDocument.RootElement.GetProperty("lastSnapshotDate");
        Assert.Equal(JsonValueKind.Null, withNullProperty.ValueKind);
    }
}
