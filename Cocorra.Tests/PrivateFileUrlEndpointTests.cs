using System;
using System.Linq;
using System.Reflection;
using Cocorra.API.Controllers;
using Cocorra.DAL.AppMetaData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// The presigned-URL endpoints hand out private files (voice recordings, screenshots). Only
/// staff may call them: Admin and Moderator, and no one else, including the file's owner.
/// </summary>
public class PrivateFileUrlEndpointTests
{
    public static TheoryData<Type, string, string> Endpoints => new()
    {
        { typeof(AdminController), nameof(AdminController.GetUserVoiceUrl), Router.AdminRouting.VoiceUrl },
        { typeof(SupportController), nameof(SupportController.GetReportScreenshotUrl), Router.SupportRouting.AdminReportScreenshotUrl },
        { typeof(SupportController), nameof(SupportController.GetTicketScreenshotUrl), Router.SupportRouting.AdminTicketScreenshotUrl },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void Endpoint_IsRestrictedToAdminAndModerator(Type controller, string action, string route)
    {
        var method = controller.GetMethod(action)!;

        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>());

        // Every [Authorize] that applies must allow exactly these two roles. ASP.NET combines
        // class- and method-level attributes with AND, so each one must name a role set.
        var authorize = controller.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(method.GetCustomAttributes<AuthorizeAttribute>())
            .ToList();
        Assert.NotEmpty(authorize);
        Assert.All(authorize, a =>
        {
            var roles = (a.Roles ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[] { "Admin", "Moderator" }, roles.OrderBy(r => r));
        });

        Assert.Equal(route, method.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    [Fact]
    public void Routes_FollowTheExistingConventions()
    {
        Assert.Equal("Api/V1/Admin/User/{id:guid}/VoiceUrl", Router.AdminRouting.VoiceUrl);
        Assert.Equal("Api/V1/Support/admin/reports/{id:guid}/screenshot-url", Router.SupportRouting.AdminReportScreenshotUrl);
        Assert.Equal("Api/V1/Support/admin/tickets/{id:guid}/screenshot-url", Router.SupportRouting.AdminTicketScreenshotUrl);
    }
}
