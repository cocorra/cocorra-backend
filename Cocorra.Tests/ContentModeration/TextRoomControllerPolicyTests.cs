using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cocorra.API.Authorization;
using Cocorra.API.Controllers;
using Cocorra.DAL.AppMetaData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class TextRoomControllerPolicyTests
{
    private static readonly Assembly ApiAssembly = typeof(AdminController).Assembly;

    private static Type RequireApiType(string typeName)
    {
        var match = ApiAssembly.GetTypes().SingleOrDefault(t => t.Name == typeName);
        Assert.NotNull(match);
        return match!;
    }

    private static MethodInfo RequireActionByRoute<THttpAttr>(string expectedRoute, Type? scopedController = null)
        where THttpAttr : HttpMethodAttribute
    {
        var candidates = scopedController is not null
            ? new[] { scopedController }
            : ApiAssembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

        var normalizedExpected = expectedRoute.Trim('/');
        var matches = new List<MethodInfo>();

        foreach (var controller in candidates)
        {
            var classRoutes = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(r => r.Template)
                .DefaultIfEmpty(string.Empty)
                .ToList();

            var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                foreach (var attr in method.GetCustomAttributes<THttpAttr>(inherit: true))
                {
                    var template = attr.Template ?? string.Empty;
                    if (string.Equals(template.Trim('/'), normalizedExpected, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(method);
                        break;
                    }

                    if (classRoutes.Any(prefix =>
                        string.Equals(CombineRoute(prefix, template), normalizedExpected, StringComparison.OrdinalIgnoreCase)))
                    {
                        matches.Add(method);
                        break;
                    }
                }
            }
        }

        return Assert.Single(matches);
    }

    private static string CombineRoute(string? prefix, string? template)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return (template ?? string.Empty).Trim('/');
        if (string.IsNullOrWhiteSpace(template))
            return prefix.Trim('/');
        return $"{prefix.Trim('/')}/{template.Trim('/')}";
    }

    [Fact]
    public void AllControllersAndHubsInApiAssembly_NeverUseAuthorizeRoles()
    {
        var types = ApiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                && (typeof(ControllerBase).IsAssignableFrom(t) || typeof(Hub).IsAssignableFrom(t)))
            .ToList();

        Assert.NotEmpty(types);

        var violations = new List<string>();
        foreach (var type in types)
        {
            foreach (var attr in type.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            {
                if (!string.IsNullOrWhiteSpace(attr.Roles))
                    violations.Add($"{type.Name} has [Authorize(Roles = \"{attr.Roles}\")]");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attr in method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                {
                    if (!string.IsNullOrWhiteSpace(attr.Roles))
                        violations.Add($"{type.Name}.{method.Name} has [Authorize(Roles = \"{attr.Roles}\")]");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void WordDictionaryController_ClassRequiresAdminOnlyPolicyAndDisallowsAnonymous()
    {
        var controller = RequireApiType("WordDictionaryController");
        Assert.True(typeof(ControllerBase).IsAssignableFrom(controller));
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var classAuth = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.NotEmpty(classAuth);
        Assert.All(classAuth, a => Assert.True(string.IsNullOrEmpty(a.Roles)));
        Assert.Contains(classAuth, a => a.Policy == AuthPolicies.AdminOnly);

        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(actions);
        Assert.All(actions, m => Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true)));
    }

    [Fact]
    public void TextRoomsAdminController_ClassRequiresAdminOrModeratorAndPutSettingsRequiresAdminOnly()
    {
        var controller = RequireApiType("TextRoomsAdminController");
        Assert.True(typeof(ControllerBase).IsAssignableFrom(controller));
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var classAuth = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.NotEmpty(classAuth);
        Assert.All(classAuth, a => Assert.True(string.IsNullOrEmpty(a.Roles)));
        Assert.Contains(classAuth, a => a.Policy == AuthPolicies.AdminOrModerator);

        var putSettings = RequireActionByRoute<HttpPutAttribute>(Router.TextRoomAdminRouting.Settings, controller);
        Assert.Empty(putSettings.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        var methodAuth = putSettings.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.Contains(methodAuth, a => a.Policy == AuthPolicies.AdminOnly);
        Assert.All(methodAuth, a => Assert.True(string.IsNullOrEmpty(a.Roles)));
    }

    [Fact]
    public void ReportContextAction_RequiresAdminOrModeratorPolicy()
    {
        var action = RequireActionByRoute<HttpGetAttribute>(Router.SupportRouting.AdminReportMessageContext);
        var controller = action.DeclaringType!;

        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var effectiveAuth = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .ToList();

        Assert.NotEmpty(effectiveAuth);
        Assert.All(effectiveAuth, a => Assert.True(string.IsNullOrEmpty(a.Roles)));
        Assert.Equal(
            new[] { AuthPolicies.AdminOrModerator },
            effectiveAuth.Select(a => a.Policy).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray());
    }

    [Fact]
    public void MobileReportMessageAction_UsesPlainAuthorizeWithoutRolePolicy()
    {
        var action = RequireActionByRoute<HttpPostAttribute>(Router.TextRoomRouting.ReportMessage);
        var controller = action.DeclaringType!;

        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var effectiveAuth = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .ToList();

        Assert.NotEmpty(effectiveAuth);
        Assert.All(effectiveAuth, a =>
        {
            Assert.True(string.IsNullOrEmpty(a.Policy));
            Assert.True(string.IsNullOrEmpty(a.Roles));
        });
    }

    [Fact]
    public void TextRoomsController_UsesPlainAuthorize()
    {
        var controller = RequireApiType("TextRoomsController");
        Assert.True(typeof(ControllerBase).IsAssignableFrom(controller));
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var classAuth = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.NotEmpty(classAuth);
        Assert.All(classAuth, a =>
        {
            Assert.True(string.IsNullOrEmpty(a.Policy));
            Assert.True(string.IsNullOrEmpty(a.Roles));
        });

        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(actions);
        Assert.All(actions, m => Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true)));
    }

    [Fact]
    public void TextRoomHub_UsesPlainAuthorize()
    {
        var hub = RequireApiType("TextRoomHub");
        Assert.True(typeof(Hub).IsAssignableFrom(hub));
        Assert.Empty(hub.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var hubAuth = hub.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.NotEmpty(hubAuth);
        Assert.All(hubAuth, a =>
        {
            Assert.True(string.IsNullOrEmpty(a.Policy));
            Assert.True(string.IsNullOrEmpty(a.Roles));
        });

        var methods = hub.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.All(methods, m => Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true)));
    }
}
