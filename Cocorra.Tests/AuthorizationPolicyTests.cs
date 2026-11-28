using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.API.Controllers;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests;

public class AuthorizationPolicyTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IAuthorizationService _authorizationService;
    private readonly IAuthorizationPolicyProvider _policyProvider;

    public AuthorizationPolicyTests()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddAuthorization(AuthPolicies.Register);
        _services = sc.BuildServiceProvider();
        _authorizationService = _services.GetRequiredService<IAuthorizationService>();
        _policyProvider = _services.GetRequiredService<IAuthorizationPolicyProvider>();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public void AllApiControllersAndActions_NeverUseAuthorizeRolesProperty()
    {
        var apiAssembly = typeof(AdminController).Assembly;
        var controllers = apiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(controllers);

        var violations = new List<string>();
        foreach (var controller in controllers)
        {
            foreach (var attr in controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            {
                if (!string.IsNullOrWhiteSpace(attr.Roles))
                    violations.Add($"{controller.Name} has [Authorize(Roles = \"{attr.Roles}\")]");
            }

            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var action in actions)
            {
                foreach (var attr in action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                {
                    if (!string.IsNullOrWhiteSpace(attr.Roles))
                        violations.Add($"{controller.Name}.{action.Name} has [Authorize(Roles = \"{attr.Roles}\")]");
                }
            }
        }

        Assert.Empty(violations);
    }

    public static TheoryData<Type, string, string[]> EndpointPolicyMatrix => new()
    {
        { typeof(AdminController), nameof(AdminController.GetAllUsers), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(AdminController), nameof(AdminController.GetUserById), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(AdminController), nameof(AdminController.ChangeStatus), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(AdminController), nameof(AdminController.GetDashboardStats), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(AdminController), nameof(AdminController.GetUserVoiceUrl), new[] { AuthPolicies.AdminOrModerator, AuthPolicies.AdminOnly } },
        { typeof(AdminController), nameof(AdminController.BulkChangeStatus), new[] { AuthPolicies.AdminOrModerator, AuthPolicies.AdminOnly } },
        { typeof(AdminController), nameof(AdminController.BlockDeviceAndEmail), new[] { AuthPolicies.AdminOrModerator, AuthPolicies.AdminOnly } },
        { typeof(AdminController), nameof(AdminController.BroadcastNotification), new[] { AuthPolicies.AdminOrModerator, AuthPolicies.AdminOnly } },

        { typeof(VoiceReviewController), nameof(VoiceReviewController.GetPending), new[] { AuthPolicies.AdminOrRecordReviewer } },
        { typeof(VoiceReviewController), nameof(VoiceReviewController.GetVoiceUrl), new[] { AuthPolicies.AdminOrRecordReviewer } },
        { typeof(VoiceReviewController), nameof(VoiceReviewController.Decide), new[] { AuthPolicies.AdminOrRecordReviewer } },

        { typeof(StaffActivityController), nameof(StaffActivityController.GetStaffActivity), new[] { AuthPolicies.AdminOnly } },

        { typeof(RolesController), nameof(RolesController.GetRoles), new[] { AuthPolicies.AdminOnly } },
        { typeof(RolesController), nameof(RolesController.GetRoleById), new[] { AuthPolicies.AdminOnly } },
        { typeof(RolesController), nameof(RolesController.UpdateRoleDisplayName), new[] { AuthPolicies.AdminOnly } },
        { typeof(RolesController), nameof(RolesController.ManageUserRoles), new[] { AuthPolicies.AdminOnly } },
        { typeof(RolesController), nameof(RolesController.GetUsersInRole), new[] { AuthPolicies.AdminOnly } },
        { typeof(RolesController), nameof(RolesController.CreateUser), new[] { AuthPolicies.AdminOnly } },

        { typeof(AdminLogsController), nameof(AdminLogsController.GetSystemLogs), new[] { AuthPolicies.AdminOnly } },
        { typeof(AdminLogsController), nameof(AdminLogsController.GetAuditLogs), new[] { AuthPolicies.AdminOnly } },

        { typeof(AnalyticsController), nameof(AnalyticsController.GetPlatformSummary), new[] { AuthPolicies.AdminOnly } },
        { typeof(AnalyticsController), nameof(AnalyticsController.GetPlatformHealth), new[] { AuthPolicies.AdminOnly } },
        { typeof(AnalyticsController), nameof(AnalyticsController.GetDecisionCenter), new[] { AuthPolicies.AdminOnly } },
        { typeof(AnalyticsController), nameof(AnalyticsController.RunBackfill), new[] { AuthPolicies.AdminOnly } },

        { typeof(SupportController), nameof(SupportController.GetReports), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.GetReportById), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.GetReportScreenshotUrl), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.GetTickets), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.GetTicketById), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.UpdateTicketStatus), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.GetTicketScreenshotUrl), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.UpdateReportStatus), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.TakeReportAction), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.ClaimChat), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.AdminReply), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.CloseChat), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.GetPendingChats), new[] { AuthPolicies.AdminOrModerator } },
        { typeof(SupportController), nameof(SupportController.GetAdminActiveChats), new[] { AuthPolicies.AdminOrModerator } },

        { typeof(RoomsController), nameof(RoomsController.Create), new[] { AuthPolicies.AdminCoachOrModerator } },
        { typeof(RoomsController), nameof(RoomsController.GetRoomHistory), new[] { AuthPolicies.AdminOnly } },
        { typeof(RoomFeedbackController), nameof(RoomFeedbackController.GetAll), new[] { AuthPolicies.AdminOnly } },
        { typeof(RoomInviteController), nameof(RoomInviteController.GetStats), new[] { AuthPolicies.AdminOnly } }
    };

    [Theory]
    [MemberData(nameof(EndpointPolicyMatrix))]
    public void Endpoint_HasExpectedEffectivePoliciesAndNoAllowAnonymous(
        Type controller,
        string actionName,
        string[] expectedPolicies)
    {
        var method = controller.GetMethod(actionName);
        Assert.NotNull(method);

        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Empty(method!.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var attributes = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .ToList();

        Assert.NotEmpty(attributes);
        Assert.All(attributes, a => Assert.True(string.IsNullOrEmpty(a.Roles)));

        var actualPolicies = attributes
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .OrderBy(p => p)
            .ToArray();

        Assert.Equal(expectedPolicies.OrderBy(p => p).ToArray(), actualPolicies);
    }

    [Fact]
    public void AdminController_BroadcastNotification_BothRoutesMappedUnderAdminOnlyPolicy()
    {
        var method = typeof(AdminController).GetMethod(nameof(AdminController.BroadcastNotification))!;
        var templates = method.GetCustomAttributes<HttpPostAttribute>()
            .Select(a => a.Template)
            .OrderBy(t => t)
            .ToArray();

        var expected = new[]
        {
            Router.AdminRouting.BroadcastNotification,
            Router.AdminRouting.Prefix + "/Notifications/Broadcast"
        }.OrderBy(t => t).ToArray();

        Assert.Equal(expected, templates);

        var methodPolicy = method.GetCustomAttribute<AuthorizeAttribute>()?.Policy;
        Assert.Equal(AuthPolicies.AdminOnly, methodPolicy);
    }

    public static TheoryData<string, string, bool> ActiveRolePolicyMatrix => new()
    {
        { AuthPolicies.AdminOnly, AppRoles.Admin, true },
        { AuthPolicies.AdminOnly, AppRoles.Moderator, false },
        { AuthPolicies.AdminOnly, AppRoles.RecordReviewer, false },
        { AuthPolicies.AdminOnly, AppRoles.Coach, false },
        { AuthPolicies.AdminOnly, AppRoles.User, false },

        { AuthPolicies.AdminOrModerator, AppRoles.Admin, true },
        { AuthPolicies.AdminOrModerator, AppRoles.Moderator, true },
        { AuthPolicies.AdminOrModerator, AppRoles.RecordReviewer, false },
        { AuthPolicies.AdminOrModerator, AppRoles.Coach, false },
        { AuthPolicies.AdminOrModerator, AppRoles.User, false },

        { AuthPolicies.AdminOrRecordReviewer, AppRoles.Admin, true },
        { AuthPolicies.AdminOrRecordReviewer, AppRoles.Moderator, false },
        { AuthPolicies.AdminOrRecordReviewer, AppRoles.RecordReviewer, true },
        { AuthPolicies.AdminOrRecordReviewer, AppRoles.Coach, false },
        { AuthPolicies.AdminOrRecordReviewer, AppRoles.User, false },

        { AuthPolicies.AdminCoachOrModerator, AppRoles.Admin, true },
        { AuthPolicies.AdminCoachOrModerator, AppRoles.Moderator, true },
        { AuthPolicies.AdminCoachOrModerator, AppRoles.RecordReviewer, false },
        { AuthPolicies.AdminCoachOrModerator, AppRoles.Coach, true },
        { AuthPolicies.AdminCoachOrModerator, AppRoles.User, false }
    };

    [Theory]
    [MemberData(nameof(ActiveRolePolicyMatrix))]
    public async Task RolePolicies_ActiveVerificationStatus_EnforceExactRoleAccess(
        string policyName,
        string role,
        bool expectedAllowed)
    {
        var principal = CreatePrincipal(role, AuthPolicies.ActiveStatus);

        var result = await _authorizationService.AuthorizeAsync(principal, policyName);

        Assert.Equal(expectedAllowed, result.Succeeded);
    }

    public static TheoryData<string, string, string?> NonActiveRolePolicyDenialMatrix
    {
        get
        {
            var data = new TheoryData<string, string, string?>();
            var policies = new[]
            {
                AuthPolicies.AdminOnly,
                AuthPolicies.AdminOrModerator,
                AuthPolicies.AdminOrRecordReviewer,
                AuthPolicies.AdminCoachOrModerator
            };
            var roles = new[]
            {
                AppRoles.Admin,
                AppRoles.Moderator,
                AppRoles.RecordReviewer,
                AppRoles.Coach,
                AppRoles.User
            };
            var badStatuses = new string?[] { "Pending", "ReRecord", "Banned", "Rejected", null };

            foreach (var policy in policies)
            {
                foreach (var role in roles)
                {
                    foreach (var status in badStatuses)
                    {
                        data.Add(policy, role, status);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NonActiveRolePolicyDenialMatrix))]
    public async Task RolePolicies_NonActiveOrMissingVerificationStatus_AlwaysDenied(
        string policyName,
        string role,
        string? verificationStatus)
    {
        var principal = CreatePrincipal(role, verificationStatus);

        var result = await _authorizationService.AuthorizeAsync(principal, policyName);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("ReRecord", true)]
    [InlineData("Active", true)]
    [InlineData("Banned", false)]
    [InlineData("Rejected", false)]
    [InlineData(null, false)]
    public async Task VerificationOnlyPolicy_AllowsPendingReRecordActiveAndDeniesOthers(
        string? verificationStatus,
        bool expectedAllowed)
    {
        var principal = CreatePrincipal(AppRoles.User, verificationStatus);

        var result = await _authorizationService.AuthorizeAsync(principal, AuthPolicies.VerificationOnly);

        Assert.Equal(expectedAllowed, result.Succeeded);
    }

    [Theory]
    [InlineData("Active", true)]
    [InlineData("Pending", false)]
    [InlineData("ReRecord", false)]
    [InlineData("Banned", false)]
    [InlineData(null, false)]
    public async Task DefaultPolicy_RequiresAuthenticatedActiveUser(
        string? verificationStatus,
        bool expectedAllowed)
    {
        var defaultPolicy = await _policyProvider.GetDefaultPolicyAsync();
        var principal = CreatePrincipal(AppRoles.User, verificationStatus);

        var result = await _authorizationService.AuthorizeAsync(principal, resource: null, defaultPolicy);

        Assert.Equal(expectedAllowed, result.Succeeded);
    }

    [Theory]
    [InlineData(AuthPolicies.AdminOnly)]
    [InlineData(AuthPolicies.AdminOrModerator)]
    [InlineData(AuthPolicies.AdminOrRecordReviewer)]
    [InlineData(AuthPolicies.AdminCoachOrModerator)]
    [InlineData(AuthPolicies.VerificationOnly)]
    public async Task AllPolicies_UnauthenticatedPrincipal_Denied(string policyName)
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _authorizationService.AuthorizeAsync(anonymous, policyName);

        Assert.False(result.Succeeded);
    }

    private static ClaimsPrincipal CreatePrincipal(string role, string? verificationStatus)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role)
        };
        if (verificationStatus != null)
        {
            claims.Add(new Claim(AuthPolicies.VerificationStatusClaim, verificationStatus));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
