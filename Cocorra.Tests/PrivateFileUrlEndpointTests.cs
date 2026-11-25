using System;
using System.Linq;
using System.Reflection;
using Cocorra.API.Authorization;
using Cocorra.API.Controllers;
using Cocorra.DAL.AppMetaData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Cocorra.Tests;

public class PrivateFileUrlEndpointTests
{
    public static TheoryData<Type, string, string, string[]> Endpoints => new()
    {
        {
            typeof(AdminController),
            nameof(AdminController.GetUserVoiceUrl),
            Router.AdminRouting.VoiceUrl,
            new[] { AuthPolicies.AdminOrModerator, AuthPolicies.AdminOnly }
        },
        {
            typeof(VoiceReviewController),
            nameof(VoiceReviewController.GetVoiceUrl),
            Router.AdminRouting.VoiceReviewVoiceUrl,
            new[] { AuthPolicies.AdminOrRecordReviewer }
        },
        {
            typeof(SupportController),
            nameof(SupportController.GetReportScreenshotUrl),
            Router.SupportRouting.AdminReportScreenshotUrl,
            new[] { AuthPolicies.AdminOrModerator }
        },
        {
            typeof(SupportController),
            nameof(SupportController.GetTicketScreenshotUrl),
            Router.SupportRouting.AdminTicketScreenshotUrl,
            new[] { AuthPolicies.AdminOrModerator }
        }
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void Endpoint_IsRestrictedByExpectedPolicyWithoutRolesAttribute(
        Type controller,
        string action,
        string route,
        string[] expectedPolicies)
    {
        var method = controller.GetMethod(action)!;

        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>());

        var authorize = controller.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(method.GetCustomAttributes<AuthorizeAttribute>())
            .ToList();

        Assert.NotEmpty(authorize);
        Assert.All(authorize, a => Assert.True(string.IsNullOrEmpty(a.Roles)));

        var actualPolicies = authorize
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .OrderBy(p => p)
            .ToArray();

        Assert.Equal(expectedPolicies.OrderBy(p => p).ToArray(), actualPolicies);
        Assert.Equal(route, method.GetCustomAttribute<HttpGetAttribute>()!.Template);
    }

    [Fact]
    public void Routes_FollowTheExistingConventions()
    {
        Assert.Equal("Api/V1/Admin/User/{id:guid}/VoiceUrl", Router.AdminRouting.VoiceUrl);
        Assert.Equal("Api/V1/Admin/VoiceReviews/{id}/VoiceUrl", Router.AdminRouting.VoiceReviewVoiceUrl);
        Assert.Equal("Api/V1/Support/admin/reports/{id:guid}/screenshot-url", Router.SupportRouting.AdminReportScreenshotUrl);
        Assert.Equal("Api/V1/Support/admin/tickets/{id:guid}/screenshot-url", Router.SupportRouting.AdminTicketScreenshotUrl);
    }
}
