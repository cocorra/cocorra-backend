using System.Security.Claims;
using Cocorra.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SupportHubTests
{
    private readonly Mock<IGroupManager> _groupManagerMock = new();
    private readonly Mock<HubCallerContext> _contextMock = new();

    private SupportHub CreateHub(string? role = null)
    {
        var hub = new SupportHub();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        _contextMock.Setup(c => c.User).Returns(principal);
        _contextMock.Setup(c => c.ConnectionId).Returns("test-support-conn");
        _contextMock.Setup(c => c.UserIdentifier).Returns(claims[0].Value);

        hub.Context = _contextMock.Object;
        hub.Groups = _groupManagerMock.Object;

        return hub;
    }

    [Fact]
    public async Task OnConnectedAsync_AdminUser_AddedToAdminsGroup()
    {
        var hub = CreateHub("Admin");

        await hub.OnConnectedAsync();

        _groupManagerMock.Verify(g => g.AddToGroupAsync("test-support-conn", "Admins", default), Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_RegularUser_NotAddedToAdminsGroup()
    {
        var hub = CreateHub("User");

        await hub.OnConnectedAsync();

        _groupManagerMock.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), "Admins", default), Times.Never);
    }
}
