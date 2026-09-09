using System;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.BlockedDevicesService;
using Cocorra.BLL.Services.BlockService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.UserBlockRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class BlockServiceTests
{
    private readonly Mock<IUserBlockRepository> _blockRepoMock = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IBlockedDevicesService> _blockedDevicesServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly BlockService _service;

    public BlockServiceTests()
    {
        _service = new BlockService(
            _blockRepoMock.Object,
            _userManagerMock.Object,
            _blockedDevicesServiceMock.Object,
            _eventTrackerMock.Object
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlockUserAsync_WhitespaceTarget_ReturnsBadRequest(string? target)
    {
        var result = await _service.BlockUserAsync(Guid.NewGuid(), target!);
        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Target identifier is required.", result.Message);
    }

    [Fact]
    public async Task BlockUserAsync_SelfBlockByGuid_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var result = await _service.BlockUserAsync(userId, userId.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("You cannot block yourself.", result.Message);
    }

    [Fact]
    public async Task BlockUserAsync_SelfBlockByEmail_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var email = "self@cocorra.com";
        var selfUser = new ApplicationUser { Id = userId, Email = email };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(selfUser);

        var result = await _service.BlockUserAsync(userId, email);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("You cannot block yourself.", result.Message);
    }

    [Fact]
    public async Task BlockUserAsync_TargetUserNotFound_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(targetId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.BlockUserAsync(userId, targetId.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Target user not found.", result.Message);
    }

    [Fact]
    public async Task BlockUserAsync_ValidTargetGuid_BlocksUserDestroysTokenAndTracksEvent()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var targetUser = new ApplicationUser
        {
            Id = targetUserId,
            RefreshToken = "active_token",
            RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7)
        };

        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync(targetUser);
        _userManagerMock.Setup(m => m.UpdateAsync(targetUser)).ReturnsAsync(IdentityResult.Success);

        var result = await _service.BlockUserAsync(currentUserId, targetUserId.ToString());

        Assert.True(result.Succeeded);
        Assert.Equal("User blocked successfully.", result.Data);
        _blockRepoMock.Verify(r => r.BlockUserAsync(currentUserId, targetUserId), Times.Once);
        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.UserBlocked,
            currentUserId,
            It.IsAny<object>()), Times.Once);

        Assert.Null(targetUser.RefreshToken);
        Assert.True(targetUser.RefreshTokenExpiryTime <= DateTime.UtcNow);
        _userManagerMock.Verify(m => m.UpdateAsync(targetUser), Times.Once);
    }

    [Fact]
    public async Task BlockUserAsync_ValidTargetEmail_BlocksUserSuccessfully()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var targetEmail = "friend@cocorra.com";
        var targetUser = new ApplicationUser
        {
            Id = targetUserId,
            Email = targetEmail,
            RefreshToken = "token"
        };

        _userManagerMock.Setup(m => m.FindByEmailAsync(targetEmail)).ReturnsAsync(targetUser);
        _userManagerMock.Setup(m => m.UpdateAsync(targetUser)).ReturnsAsync(IdentityResult.Success);

        var result = await _service.BlockUserAsync(currentUserId, targetEmail);

        Assert.True(result.Succeeded);
        _blockRepoMock.Verify(r => r.BlockUserAsync(currentUserId, targetUserId), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnblockUserAsync_WhitespaceTarget_ReturnsBadRequest(string? target)
    {
        var result = await _service.UnblockUserAsync(Guid.NewGuid(), target!);
        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task UnblockUserAsync_TargetNotFound_ReturnsNotFound()
    {
        var currentUserId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(targetId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.UnblockUserAsync(currentUserId, targetId.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task UnblockUserAsync_ValidTarget_UnblocksSuccessfully()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var targetUser = new ApplicationUser { Id = targetUserId };
        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync(targetUser);

        var result = await _service.UnblockUserAsync(currentUserId, targetUserId.ToString());

        Assert.True(result.Succeeded);
        Assert.Equal("User unblocked successfully.", result.Data);
        _blockRepoMock.Verify(r => r.UnblockUserAsync(currentUserId, targetUserId), Times.Once);
    }
}
