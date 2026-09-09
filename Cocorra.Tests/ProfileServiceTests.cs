using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.ProfileService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.ProfileDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.FriendRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class ProfileServiceTests
{
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IFriendRepository> _friendRepoMock = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly ProfileService _service;

    public ProfileServiceTests()
    {
        _service = new ProfileService(
            _userManagerMock.Object,
            _friendRepoMock.Object,
            _uploadImageMock.Object
        );
    }

    [Fact]
    public async Task GetMyProfileAsync_UserNotFound_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.GetMyProfileAsync(userId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("User not found.", result.Message);
    }

    [Fact]
    public async Task GetMyProfileAsync_UserFound_ReturnsProfileDto()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            FirstName = "John",
            LastName = "Doe",
            Email = "john@cocorra.com",
            Bio = "Hello world",
            Age = 25,
            MBTI = "INTJ",
            ProfilePicturePath = "https://cdn.cocorra.com/pic.jpg"
        };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);

        var result = await _service.GetMyProfileAsync(userId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(userId, result.Data.Id);
        Assert.Equal("John", result.Data.FirstName);
        Assert.Equal("INTJ", result.Data.MBTI);
    }

    [Fact]
    public async Task GetUserProfileAsync_SelfRequested_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var result = await _service.GetUserProfileAsync(userId, userId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("Use the 'My Profile' endpoint", result.Message);
    }

    [Fact]
    public async Task GetUserProfileAsync_TargetNotFound_ReturnsNotFound()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.GetUserProfileAsync(currentUserId, targetUserId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetUserProfileAsync_TargetFoundAndFriends_ReturnsDtoWithIsFriendTrue()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var targetUser = new ApplicationUser
        {
            Id = targetUserId,
            FirstName = "Sarah",
            LastName = "Connor",
            Bio = "Rebel",
            Age = 30,
            MBTI = "ENTJ"
        };
        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync(targetUser);

        var friendship = new FriendRequest
        {
            SenderId = currentUserId,
            ReceiverId = targetUserId,
            Status = FriendRequestStatus.Accepted
        };
        _friendRepoMock.Setup(r => r.GetFriendshipRelationAsync(currentUserId, targetUserId))
            .ReturnsAsync(friendship);

        var result = await _service.GetUserProfileAsync(currentUserId, targetUserId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("Sarah Connor", result.Data.FullName);
        Assert.True(result.Data.IsFriend);
    }

    [Fact]
    public async Task UpdateProfileAsync_UserNotFound_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.UpdateProfileAsync(userId, new UpdateProfileDto());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task UpdateProfileAsync_ValidUser_UpdatesFieldsAndReturnsDto()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            FirstName = "OldFirst",
            LastName = "OldLast",
            Bio = "OldBio",
            Age = 20,
            MBTI = "INTP",
            Email = "u@cocorra.com"
        };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var dto = new UpdateProfileDto
        {
            FirstName = "NewFirst",
            LastName = "NewLast",
            Bio = "NewBio",
            Age = 22
        };

        var result = await _service.UpdateProfileAsync(userId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal("NewFirst", user.FirstName);
        Assert.Equal("NewLast", user.LastName);
        Assert.Equal("NewBio", user.Bio);
        Assert.Equal(22, user.Age);
        _userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task UploadProfilePictureAsync_NullOrEmptyFile_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _uploadImageMock.Setup(u => u.SaveImageAsync(null!, "Profiles")).ReturnsAsync("Error:NoFile");

        var result = await _service.UploadProfilePictureAsync(userId, null!);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task UploadProfilePictureAsync_UploadFails_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);

        var user = new ApplicationUser { Id = userId };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _uploadImageMock.Setup(u => u.SaveImageAsync(fileMock.Object, "Profiles"))
            .ReturnsAsync("Error:InvalidExtension");

        var result = await _service.UploadProfilePictureAsync(userId, fileMock.Object);

        Assert.False(result.Succeeded);
        Assert.Equal("Error:InvalidExtension", result.Message);
        _userManagerMock.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
    }

    [Fact]
    public async Task UploadProfilePictureAsync_SuccessfulUpload_DeletesOldImageAndUpdatesUser()
    {
        var userId = Guid.NewGuid();
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);

        var oldImagePath = "https://cdn.cocorra.com/old.jpg";
        var newImagePath = "https://cdn.cocorra.com/new.jpg";
        var user = new ApplicationUser { Id = userId, ProfilePicturePath = oldImagePath };

        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _uploadImageMock.Setup(u => u.SaveImageAsync(fileMock.Object, "Profiles")).ReturnsAsync(newImagePath);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var result = await _service.UploadProfilePictureAsync(userId, fileMock.Object);

        Assert.True(result.Succeeded);
        Assert.Equal(newImagePath, result.Data);
        Assert.Equal(newImagePath, user.ProfilePicturePath);
        _uploadImageMock.Verify(u => u.DeleteImage(oldImagePath), Times.Once);
        _userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task UpdateAvatarPresetAsync_ValidPreset_UpdatesUser()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var preset = "avatar1_key";
        var result = await _service.UpdateAvatarPresetAsync(userId, new UpdateAvatarPresetDto { AvatarPresetKey = preset });

        Assert.True(result.Succeeded);
        Assert.Equal(preset, user.ProfilePicturePath);
        _userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
    }
}
