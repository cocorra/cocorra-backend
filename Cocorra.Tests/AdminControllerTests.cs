using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AdminService;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AdminControllerTests
{
    private readonly Mock<IAdminService> _adminServiceMock = new();
    private readonly Mock<IHubContext<RoomHub>> _roomHubContextMock = new();
    private readonly Mock<IHubClients> _hubClientsMock = new();
    private readonly Mock<ISingleClientProxy> _clientProxyMock = new();
    private readonly Mock<IStaffConnectionRevoker> _revokerMock = new();

    public AdminControllerTests()
    {
        _roomHubContextMock.Setup(h => h.Clients).Returns(_hubClientsMock.Object);
        _hubClientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(_clientProxyMock.Object);
    }

    private AdminController CreateController(
        Guid? actorId = null,
        string email = "admin@example.com",
        string role = AppRoles.Admin) =>
        CreateController(new[] { role }, actorId, email);

    private AdminController CreateController(
        IEnumerable<string> roles,
        Guid? actorId = null,
        string email = "admin@example.com")
    {
        var controller = new AdminController(
            _adminServiceMock.Object,
            _roomHubContextMock.Object,
            _revokerMock.Object);

        var uid = actorId ?? Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, uid.ToString()),
            new(ClaimTypes.Email, email)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task GetAllUsers_PassesCallerRoleFlagAndReturnsOkWithPagedList(string role, bool expectedCallerIsAdmin)
    {
        var pagedResponse = new PagedResponse<UserDto>
        {
            Data = new List<UserDto>
            {
                new() { Id = Guid.NewGuid().ToString(), Email = expectedCallerIsAdmin ? "user1@example.com" : null, FullName = "User One" }
            },
            CurrentPage = 1,
            PageSize = 10,
            TotalCount = 1,
            Succeeded = true,
            StatusCode = HttpStatusCode.OK
        };

        _adminServiceMock
            .Setup(s => s.GetAllUsersAsync("test", 1, 10, expectedCallerIsAdmin))
            .ReturnsAsync(pagedResponse);

        var controller = CreateController(role: role);
        var result = await controller.GetAllUsers("test", 1, 10);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(pagedResponse, ok.Value);
        _adminServiceMock.Verify(s => s.GetAllUsersAsync("test", 1, 10, expectedCallerIsAdmin), Times.Once);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task GetUserById_Success_PassesCallerRoleFlagAndReturnsOk(string role, bool expectedCallerIsAdmin)
    {
        var userId = Guid.NewGuid();
        var userDto = new UserDto { Id = userId.ToString(), Email = expectedCallerIsAdmin ? "user@example.com" : null, FullName = "User" };
        var serviceResponse = new Response<UserDto>
        {
            Succeeded = true,
            StatusCode = HttpStatusCode.OK,
            Data = userDto
        };

        _adminServiceMock.Setup(s => s.GetUserByIdAsync(userId, expectedCallerIsAdmin)).ReturnsAsync(serviceResponse);

        var controller = CreateController(role: role);
        var result = await controller.GetUserById(userId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task GetUserById_NotFound_Propagates404Status()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<UserDto>
        {
            Succeeded = false,
            StatusCode = HttpStatusCode.NotFound,
            Message = "User not found"
        };

        _adminServiceMock.Setup(s => s.GetUserByIdAsync(userId, true)).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.GetUserById(userId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task ChangeStatus_SelfChange_ReturnsBadRequestWithoutCallingServiceOrRevoker()
    {
        var adminId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Banned, Reason = "Self test" };

        var result = await controller.ChangeStatus(adminId, model);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        _adminServiceMock.Verify(
            s => s.ChangeUserStatusAsync(It.IsAny<Guid>(), It.IsAny<UserStatus>(), It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ChangeStatus_SuccessWithoutSessionRevocation_ReturnsOkAndDoesNotInvokeRevoker()
    {
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Active, Reason = "Approved after review" };
        var serviceResponse = StatusChangeResponse.From(
            new Response<string>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = "User status changed from Pending to Active"
            },
            sessionRevoked: false);

        _adminServiceMock
            .Setup(s => s.ChangeUserStatusAsync(targetUserId, UserStatus.Active, model.Reason, adminId, true, false))
            .ReturnsAsync(serviceResponse);

        var result = await controller.ChangeStatus(targetUserId, model);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ChangeStatus_SuccessWithSessionRevocation_InvokesRevoker()
    {
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Banned, Reason = "Repeated harassment" };
        var serviceResponse = StatusChangeResponse.From(
            new Response<string>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = "User status changed from Active to Banned"
            },
            sessionRevoked: true);

        _adminServiceMock
            .Setup(s => s.ChangeUserStatusAsync(targetUserId, UserStatus.Banned, model.Reason, adminId, true, false))
            .ReturnsAsync(serviceResponse);

        var result = await controller.ChangeStatus(targetUserId, model);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
        _revokerMock.Verify(r => r.RevokeAsync(targetUserId), Times.Once);
    }

    [Fact]
    public async Task ChangeStatus_ServiceRefusesWithForbidden_Propagates403AndDoesNotInvokeRevoker()
    {
        var moderatorId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var controller = CreateController(moderatorId, email: "mod@example.com", role: AppRoles.Moderator);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Banned, Reason = "Attempt on staff" };
        var serviceResponse = StatusChangeResponse.From(
            new Response<string>
            {
                Succeeded = false,
                StatusCode = HttpStatusCode.Forbidden,
                Message = "Moderators cannot change the status of staff or admin accounts."
            },
            sessionRevoked: false);

        _adminServiceMock
            .Setup(s => s.ChangeUserStatusAsync(targetUserId, UserStatus.Banned, model.Reason, moderatorId, false, false))
            .ReturnsAsync(serviceResponse);

        var result = await controller.ChangeStatus(targetUserId, model);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ChangeStatus_ServiceReturnsNotFound_Propagates404AndDoesNotInvokeRevoker()
    {
        var moderatorId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var controller = CreateController(moderatorId, email: "mod@example.com", role: AppRoles.Moderator);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Banned, Reason = "Target does not exist" };
        var serviceResponse = StatusChangeResponse.From(
            new Response<string>
            {
                Succeeded = false,
                StatusCode = HttpStatusCode.NotFound,
                Message = "User not found"
            },
            sessionRevoked: false);

        _adminServiceMock
            .Setup(s => s.ChangeUserStatusAsync(targetUserId, UserStatus.Banned, model.Reason, moderatorId, false, false))
            .ReturnsAsync(serviceResponse);

        var result = await controller.ChangeStatus(targetUserId, model);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task BulkChangeStatus_InvalidModel_ReturnsBadRequestWithoutRevoking()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("UserIds", "Required");

        var result = await controller.BulkChangeStatus(new BulkChangeStatusDto());

        Assert.IsType<BadRequestObjectResult>(result);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task BulkChangeStatus_MixedOutcomes_RevokesOnlySucceededItemsWithSessionRevoked()
    {
        var adminId = Guid.NewGuid();
        var revokedUserId = Guid.NewGuid();
        var nonRevokedUserId = Guid.NewGuid();
        var failedUserId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new BulkChangeStatusDto
        {
            UserIds = new List<Guid> { revokedUserId, nonRevokedUserId, failedUserId },
            NewStatus = UserStatus.Active,
            Reason = "Bulk review batch"
        };

        var serviceResponse = new Response<BulkChangeStatusResultDto>
        {
            Succeeded = true,
            StatusCode = HttpStatusCode.OK,
            Data = new BulkChangeStatusResultDto
            {
                TotalRequested = 3,
                SucceededCount = 2,
                FailedCount = 1,
                Results = new List<BulkItemResultDto>
                {
                    new() { UserId = revokedUserId, Succeeded = true, SessionRevoked = true },
                    new() { UserId = nonRevokedUserId, Succeeded = true, SessionRevoked = false },
                    new() { UserId = failedUserId, Succeeded = false, SessionRevoked = true }
                }
            }
        };

        _adminServiceMock.Setup(s => s.BulkChangeUserStatusAsync(model, adminId)).ReturnsAsync(serviceResponse);

        var result = await controller.BulkChangeStatus(model);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
        _revokerMock.Verify(r => r.RevokeAsync(revokedUserId), Times.Once);
        _revokerMock.Verify(r => r.RevokeAsync(nonRevokedUserId), Times.Never);
        _revokerMock.Verify(r => r.RevokeAsync(failedUserId), Times.Never);
    }

    public static TheoryData<string[], bool> DashboardStatsRoleCases => new()
    {
        { new[] { AppRoles.Admin }, true },
        { new[] { AppRoles.Moderator }, false },
        { new[] { AppRoles.Moderator, AppRoles.Coach }, false },
        { new[] { AppRoles.Admin, AppRoles.Moderator }, true }
    };

    [Theory]
    [MemberData(nameof(DashboardStatsRoleCases))]
    public async Task GetDashboardStats_ByCallerRoles_PassesExpectedCallerIsAdminFlagAndReturnsOk(
        string[] roles, bool expectedCallerIsAdmin)
    {
        var stats = new DashboardStatsDto
        {
            TotalUsers = 100,
            ActiveUsers = 80,
            BannedUsers = 5,
            PendingUsers = 15
        };
        var serviceResponse = new Response<DashboardStatsDto>
        {
            Succeeded = true,
            StatusCode = HttpStatusCode.OK,
            Data = stats
        };

        _adminServiceMock
            .Setup(s => s.GetDashboardStatsAsync(expectedCallerIsAdmin))
            .ReturnsAsync(serviceResponse);
        var controller = CreateController(roles);

        var result = await controller.GetDashboardStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
        _adminServiceMock.Verify(s => s.GetDashboardStatsAsync(expectedCallerIsAdmin), Times.Once);
    }

    [Fact]
    public async Task BlockDeviceAndEmail_SelfBlock_ReturnsBadRequestWithoutCallingServiceOrRevoker()
    {
        var adminEmail = "admin@example.com";
        var controller = CreateController(email: adminEmail);

        var model = new BlockDeviceAndEmailDto { Email = adminEmail, Reason = "Self test" };

        var result = await controller.BlockDeviceAndEmail(model);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
        _adminServiceMock.Verify(
            s => s.BlockDeviceAndEmailAsync(It.IsAny<BlockDeviceAndEmailDto>(), It.IsAny<Guid>()),
            Times.Never);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task BlockDeviceAndEmail_Success_ForwardsActingAdminIdAndRevokesSession()
    {
        var adminId = Guid.NewGuid();
        var bannedUserId = Guid.NewGuid();
        var controller = CreateController(actorId: adminId, email: "admin@example.com");

        var model = new BlockDeviceAndEmailDto { Email = "baduser@example.com", Reason = "Severe policy violation" };

        var serviceResponse = new Response<BlockDeviceAndEmailResultDto>
        {
            Succeeded = true,
            StatusCode = HttpStatusCode.OK,
            Data = new BlockDeviceAndEmailResultDto
            {
                UserId = bannedUserId,
                Email = model.Email,
                AccountBanned = true,
                DevicesBlocked = 3
            }
        };

        _adminServiceMock
            .Setup(s => s.BlockDeviceAndEmailAsync(model, adminId))
            .ReturnsAsync(serviceResponse);

        var result = await controller.BlockDeviceAndEmail(model);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
        _adminServiceMock.Verify(s => s.BlockDeviceAndEmailAsync(model, adminId), Times.Once);
        _revokerMock.Verify(r => r.RevokeAsync(bannedUserId), Times.Once);
    }

    [Fact]
    public async Task BlockDeviceAndEmail_UserNotFound_Propagates404AndDoesNotRevoke()
    {
        var controller = CreateController(email: "admin@example.com");
        var model = new BlockDeviceAndEmailDto { Email = "ghost@example.com", Reason = "Account abuse" };

        _adminServiceMock
            .Setup(s => s.BlockDeviceAndEmailAsync(model, It.IsAny<Guid>()))
            .ReturnsAsync(new Response<BlockDeviceAndEmailResultDto>
            {
                Succeeded = false,
                StatusCode = HttpStatusCode.NotFound,
                Message = "User not found with the provided email."
            });

        var result = await controller.BlockDeviceAndEmail(model);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, status.StatusCode);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task BroadcastNotification_ValidModel_CallsServiceAndReturnsOk()
    {
        var adminId = Guid.NewGuid();
        var controller = CreateController(actorId: adminId);
        var model = new SendBroadcastNotificationDto
        {
            Title = "System Notice",
            Body = "Welcome to the new release"
        };

        var expectedResult = new Response<BroadcastNotificationResultDto>
        {
            Succeeded = true,
            StatusCode = HttpStatusCode.OK,
            Data = new BroadcastNotificationResultDto
            {
                TotalTargetUsers = 10,
                TokensCount = 8,
                SuccessCount = 8,
                FailureCount = 0,
                InAppNotificationsCreated = 10
            }
        };

        _adminServiceMock
            .Setup(s => s.BroadcastNotificationAsync(model, adminId))
            .ReturnsAsync(expectedResult);

        var result = await controller.BroadcastNotification(model);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expectedResult, okResult.Value);
    }

    [Fact]
    public async Task BroadcastNotification_EmptyBody_ReturnsBadRequest()
    {
        var controller = CreateController();
        var model = new SendBroadcastNotificationDto
        {
            Title = "System Notice",
            Body = ""
        };

        var result = await controller.BroadcastNotification(model);

        var badResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badResult.Value);
    }

    [Theory]
    [InlineData(0, -1, 1, 1)]
    [InlineData(-5, 10000, 1, 100)]
    public async Task GetAllUsers_OutOfRangePaging_ClampsBeforeCallingService(
        int requestedPage, int requestedPageSize, int expectedPage, int expectedPageSize)
    {
        var paged = new PagedResponse<UserDto>(new List<UserDto>(), 0, expectedPage, expectedPageSize);
        _adminServiceMock
            .Setup(s => s.GetAllUsersAsync(null, expectedPage, expectedPageSize, true))
            .ReturnsAsync(paged);

        var controller = CreateController(role: AppRoles.Admin);
        var result = await controller.GetAllUsers(null, requestedPage, requestedPageSize);

        Assert.IsType<OkObjectResult>(result);
        _adminServiceMock.Verify(s => s.GetAllUsersAsync(null, expectedPage, expectedPageSize, true), Times.Once);
    }

    [Fact]
    public void StatusDtos_MissingNewStatus_FailDataAnnotationsValidation()
    {
        var singleDto = new ChangeStatusDto { NewStatus = null, Reason = "Valid moderation reason" };
        var singleErrors = new List<ValidationResult>();
        var singleValid = Validator.TryValidateObject(singleDto, new ValidationContext(singleDto), singleErrors, validateAllProperties: true);

        Assert.False(singleValid);
        Assert.Contains(singleErrors, e => e.MemberNames.Contains(nameof(ChangeStatusDto.NewStatus)));

        var bulkDto = new BulkChangeStatusDto
        {
            UserIds = new List<Guid> { Guid.NewGuid() },
            NewStatus = null,
            Reason = "Valid moderation reason"
        };
        var bulkErrors = new List<ValidationResult>();
        var bulkValid = Validator.TryValidateObject(bulkDto, new ValidationContext(bulkDto), bulkErrors, validateAllProperties: true);

        Assert.False(bulkValid);
        Assert.Contains(bulkErrors, e => e.MemberNames.Contains(nameof(BulkChangeStatusDto.NewStatus)));
    }

    [Fact]
    public async Task ChangeStatus_MissingNewStatus_Returns400WithoutCallingServiceOrRevoker()
    {
        var controller = CreateController();
        var result = await controller.ChangeStatus(Guid.NewGuid(), new ChangeStatusDto { NewStatus = null, Reason = "Valid reason" });

        Assert.IsType<BadRequestObjectResult>(result);
        _adminServiceMock.Verify(
            s => s.ChangeUserStatusAsync(It.IsAny<Guid>(), It.IsAny<UserStatus>(), It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }
}
