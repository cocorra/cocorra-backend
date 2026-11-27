using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class StaffConnectionRevokerTests
{
    private static async Task<(SupportHub Hub, Mock<HubCallerContext> Context, string ConnectionId)> ConnectUserAsync(
        Guid userId,
        string role)
    {
        var connectionId = $"staff-conn-{Guid.NewGuid():N}";
        var contextMock = new Mock<HubCallerContext>();
        var hubGroupsMock = new Mock<IGroupManager>();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        contextMock.Setup(c => c.User).Returns(principal);
        contextMock.Setup(c => c.UserIdentifier).Returns(userId.ToString());
        contextMock.Setup(c => c.ConnectionId).Returns(connectionId);

        var hub = new SupportHub
        {
            Context = contextMock.Object,
            Groups = hubGroupsMock.Object
        };

        await hub.OnConnectedAsync();
        return (hub, contextMock, connectionId);
    }

    private static (StaffConnectionRevoker Revoker, Mock<IGroupManager> Groups) CreateRevoker()
    {
        var hubContextMock = new Mock<IHubContext<SupportHub>>();
        var groupsMock = new Mock<IGroupManager>();
        hubContextMock.Setup(h => h.Groups).Returns(groupsMock.Object);

        return (new StaffConnectionRevoker(hubContextMock.Object), groupsMock);
    }

    [Theory]
    [InlineData(AppRoles.Admin)]
    [InlineData(AppRoles.Moderator)]
    public async Task RevokeAsync_TrackedStaffConnection_RemovesFromBothGroupsAndAborts(string role)
    {
        var userId = Guid.NewGuid();
        var (_, contextMock, connectionId) = await ConnectUserAsync(userId, role);
        var (revoker, groupsMock) = CreateRevoker();

        await revoker.RevokeAsync(userId);

        groupsMock.Verify(g => g.RemoveFromGroupAsync(connectionId, SupportHub.AdminsGroup, default), Times.Once);
        groupsMock.Verify(g => g.RemoveFromGroupAsync(connectionId, SupportHub.SupportAdminsGroup, default), Times.Once);
        contextMock.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task RevokeAsync_MultipleTrackedConnections_RevokesAndAbortsAll()
    {
        var userId = Guid.NewGuid();
        var (_, context1, conn1) = await ConnectUserAsync(userId, AppRoles.Admin);
        var (_, context2, conn2) = await ConnectUserAsync(userId, AppRoles.Admin);
        var (revoker, groupsMock) = CreateRevoker();

        await revoker.RevokeAsync(userId);

        groupsMock.Verify(g => g.RemoveFromGroupAsync(conn1, SupportHub.AdminsGroup, default), Times.Once);
        groupsMock.Verify(g => g.RemoveFromGroupAsync(conn1, SupportHub.SupportAdminsGroup, default), Times.Once);
        groupsMock.Verify(g => g.RemoveFromGroupAsync(conn2, SupportHub.AdminsGroup, default), Times.Once);
        groupsMock.Verify(g => g.RemoveFromGroupAsync(conn2, SupportHub.SupportAdminsGroup, default), Times.Once);
        context1.Verify(c => c.Abort(), Times.Once);
        context2.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task RevokeAsync_UnknownUser_IsNoOp()
    {
        var (revoker, groupsMock) = CreateRevoker();

        await revoker.RevokeAsync(Guid.NewGuid());

        groupsMock.Verify(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task RevokeAsync_SecondCallForSameUser_IsNoOp()
    {
        var userId = Guid.NewGuid();
        var (_, contextMock, connectionId) = await ConnectUserAsync(userId, AppRoles.Moderator);
        var (revoker, groupsMock) = CreateRevoker();

        await revoker.RevokeAsync(userId);
        await revoker.RevokeAsync(userId);

        groupsMock.Verify(g => g.RemoveFromGroupAsync(connectionId, SupportHub.AdminsGroup, default), Times.Once);
        groupsMock.Verify(g => g.RemoveFromGroupAsync(connectionId, SupportHub.SupportAdminsGroup, default), Times.Once);
        contextMock.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task RevokeAsync_GroupRemovalThrows_StillRemovesRemainingGroupAndCallsAbort()
    {
        var userId = Guid.NewGuid();
        var (_, contextMock, connectionId) = await ConnectUserAsync(userId, AppRoles.Admin);
        var (revoker, groupsMock) = CreateRevoker();

        groupsMock
            .Setup(g => g.RemoveFromGroupAsync(connectionId, SupportHub.AdminsGroup, default))
            .ThrowsAsync(new InvalidOperationException("Transient group error"));

        var exception = await Record.ExceptionAsync(() => revoker.RevokeAsync(userId));

        Assert.Null(exception);
        groupsMock.Verify(g => g.RemoveFromGroupAsync(connectionId, SupportHub.AdminsGroup, default), Times.Once);
        groupsMock.Verify(g => g.RemoveFromGroupAsync(connectionId, SupportHub.SupportAdminsGroup, default), Times.Once);
        contextMock.Verify(c => c.Abort(), Times.Once);
    }

    [Fact]
    public async Task RevokeAsync_AfterOnDisconnectedAsync_IsNoOp()
    {
        var userId = Guid.NewGuid();
        var (hub, contextMock, _) = await ConnectUserAsync(userId, AppRoles.Moderator);
        var (revoker, groupsMock) = CreateRevoker();

        await hub.OnDisconnectedAsync(null);
        await revoker.RevokeAsync(userId);

        groupsMock.Verify(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        contextMock.Verify(c => c.Abort(), Times.Never);
    }

    [Theory]
    [InlineData(AppRoles.RecordReviewer)]
    [InlineData(AppRoles.Coach)]
    [InlineData(AppRoles.User)]
    public async Task RevokeAsync_NonSupportRoleConnection_IsNoOp(string role)
    {
        var userId = Guid.NewGuid();
        var (_, contextMock, _) = await ConnectUserAsync(userId, role);
        var (revoker, groupsMock) = CreateRevoker();

        await revoker.RevokeAsync(userId);

        groupsMock.Verify(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        contextMock.Verify(c => c.Abort(), Times.Never);
    }
}
