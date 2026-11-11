using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cocorra.API.Controllers;
using Cocorra.API.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Cocorra.Tests;

public class RequireUtcOffsetAttributeTests
{
    private static ActionExecutingContext Context(
        IDictionary<string, object?> arguments,
        params object[] endpointMetadata)
    {
        var actionDescriptor = new ActionDescriptor
        {
            EndpointMetadata = endpointMetadata.ToList()
        };
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), actionDescriptor);

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            arguments,
            new object());
    }

    private static string? Message(ActionExecutingContext context) =>
        context.Result is BadRequestObjectResult result
            ? result.Value?.GetType().GetProperty("message")?.GetValue(result.Value) as string
            : null;

    public static TheoryData<string[]> OffsetlessArgumentSets => new()
    {
        new[] { "from" },
        new[] { "from", "to" }
    };

    [Theory]
    [MemberData(nameof(OffsetlessArgumentSets))]
    public void OffsetlessDateTimes_ReturnBadRequestNamingEveryArgument(string[] names)
    {
        var arguments = names.ToDictionary(
            name => name,
            _ => (object?)new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Unspecified));
        var context = Context(arguments);

        new RequireUtcOffsetAttribute().OnActionExecuting(context);

        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        Assert.Equal(400, badRequest.StatusCode);
        var message = Message(context);
        Assert.NotNull(message);
        Assert.All(names, name => Assert.Contains(name, message!));
    }

    [Fact]
    public void UtcKindArgument_IsAccepted()
    {
        var context = Context(new Dictionary<string, object?>
        {
            ["from"] = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc)
        });

        new RequireUtcOffsetAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void NullArgument_IsIgnored()
    {
        var context = Context(new Dictionary<string, object?>
        {
            ["from"] = null
        });

        new RequireUtcOffsetAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void NonDateTimeArguments_AreIgnored()
    {
        var context = Context(new Dictionary<string, object?>
        {
            ["name"] = "room",
            ["id"] = Guid.NewGuid(),
            ["page"] = 3
        });

        new RequireUtcOffsetAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void CalendarDatesEndpoint_AllowsOffsetlessArguments()
    {
        var context = Context(
            new Dictionary<string, object?>
            {
                ["from"] = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Unspecified),
                ["to"] = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Unspecified)
            },
            new CalendarDatesAttribute());

        new RequireUtcOffsetAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void AnalyticsController_RequiresUtcOffsets_AndBackfillIsMarkedAsCalendarDates()
    {
        Assert.NotNull(typeof(AnalyticsController).GetCustomAttribute<RequireUtcOffsetAttribute>());

        var runBackfill = typeof(AnalyticsController).GetMethod(nameof(AnalyticsController.RunBackfill));

        Assert.NotNull(runBackfill);
        Assert.NotNull(runBackfill!.GetCustomAttribute<CalendarDatesAttribute>());
    }

    [Fact]
    public void RoomInviteGetStats_RequiresUtcOffsets()
    {
        var getStats = typeof(RoomInviteController).GetMethod(nameof(RoomInviteController.GetStats));

        Assert.NotNull(getStats);
        Assert.NotNull(getStats!.GetCustomAttribute<RequireUtcOffsetAttribute>());
    }
}
