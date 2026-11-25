using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SupportHubTests
{
    private static (SupportHub Hub, Mock<IGroupManager> Groups, string ConnectionId) CreateHub(string? role = null)
    {
        var groupManagerMock = new Mock<IGroupManager>();
        var contextMock = new Mock<HubCallerContext>();
        var hub = new SupportHub();

        var userId = Guid.NewGuid().ToString();
        var connectionId = $"support-conn-{Guid.NewGuid():N}";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId)
        };
        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        contextMock.Setup(c => c.User).Returns(principal);
        contextMock.Setup(c => c.ConnectionId).Returns(connectionId);
        contextMock.Setup(c => c.UserIdentifier).Returns(userId);

        hub.Context = contextMock.Object;
        hub.Groups = groupManagerMock.Object;

        return (hub, groupManagerMock, connectionId);
    }

    [Fact]
    public async Task OnConnectedAsync_AdminUser_JoinsAdminsAndSupportAdminsGroups()
    {
        var (hub, groups, connectionId) = CreateHub(AppRoles.Admin);

        try
        {
            await hub.OnConnectedAsync();

            groups.Verify(g => g.AddToGroupAsync(connectionId, SupportHub.AdminsGroup, default), Times.Once);
            groups.Verify(g => g.AddToGroupAsync(connectionId, SupportHub.SupportAdminsGroup, default), Times.Once);
        }
        finally
        {
            await hub.OnDisconnectedAsync(null);
        }
    }

    [Fact]
    public async Task OnConnectedAsync_ModeratorUser_JoinsAdminsGroupOnly()
    {
        var (hub, groups, connectionId) = CreateHub(AppRoles.Moderator);

        try
        {
            await hub.OnConnectedAsync();

            groups.Verify(g => g.AddToGroupAsync(connectionId, SupportHub.AdminsGroup, default), Times.Once);
            groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), SupportHub.SupportAdminsGroup, default), Times.Never);
        }
        finally
        {
            await hub.OnDisconnectedAsync(null);
        }
    }

    [Theory]
    [InlineData(AppRoles.RecordReviewer)]
    [InlineData(AppRoles.Coach)]
    [InlineData(AppRoles.User)]
    [InlineData(null)]
    public async Task OnConnectedAsync_NonSupportRole_JoinsNeitherGroup(string? role)
    {
        var (hub, groups, _) = CreateHub(role);

        try
        {
            await hub.OnConnectedAsync();

            groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        }
        finally
        {
            await hub.OnDisconnectedAsync(null);
        }
    }

    [Fact]
    public void SupportHub_RequiresDefaultActiveAuthorizationWithoutRawRoles()
    {
        var attr = typeof(SupportHub).GetCustomAttributes<AuthorizeAttribute>(false).Single();

        Assert.Null(attr.Roles);
        Assert.Null(attr.Policy);
    }
}
