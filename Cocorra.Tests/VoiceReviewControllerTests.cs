using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.API.Controllers;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.VoiceReviewService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.StorageDto;
using Cocorra.DAL.DTOS.VoiceReviewDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class VoiceReviewControllerTests
{
    private readonly Mock<IVoiceReviewService> _voiceReviewServiceMock = new();
    private readonly Mock<IStaffConnectionRevoker> _revokerMock = new();

    private VoiceReviewController CreateController(Guid? reviewerId = null)
    {
        var claims = reviewerId.HasValue
            ? new[]
            {
                new Claim(ClaimTypes.NameIdentifier, reviewerId.Value.ToString()),
                new Claim(ClaimTypes.Role, AppRoles.RecordReviewer)
            }
            : Array.Empty<Claim>();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, reviewerId.HasValue ? "TestAuth" : null));
        return new VoiceReviewController(_voiceReviewServiceMock.Object, _revokerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    [Fact]
    public void Controller_HasAdminOrRecordReviewerPolicyAndExpectedRoutes()
    {
        var controllerType = typeof(VoiceReviewController);
        Assert.Empty(controllerType.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var authAttrs = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        var auth = Assert.Single(authAttrs);
        Assert.Equal(AuthPolicies.AdminOrRecordReviewer, auth.Policy);
        Assert.True(string.IsNullOrEmpty(auth.Roles));

        Assert.Equal(
            Router.AdminRouting.VoiceReviewsPending,
            controllerType.GetMethod(nameof(VoiceReviewController.GetPending))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal(
            Router.AdminRouting.VoiceReviewVoiceUrl,
            controllerType.GetMethod(nameof(VoiceReviewController.GetVoiceUrl))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal(
            Router.AdminRouting.VoiceReviewDecision,
            controllerType.GetMethod(nameof(VoiceReviewController.Decide))!.GetCustomAttribute<HttpPostAttribute>()!.Template);
    }

    [Fact]
    public async Task GetPending_ClampsPaginationAndReturnsServiceStatus()
    {
        var response = new PagedResponse<VoiceReviewItemDto>(
            new List<VoiceReviewItemDto>(),
            totalCount: 0,
            currentPage: 1,
            pageSize: 50);

        _voiceReviewServiceMock.Setup(s => s.GetPendingAsync(1, 50)).ReturnsAsync(response);

        var controller = CreateController(Guid.NewGuid());
        var result = await controller.GetPending(pageNumber: 0, pageSize: 200);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Same(response, obj.Value);
        _voiceReviewServiceMock.Verify(s => s.GetPendingAsync(1, 50), Times.Once);
    }

    [Fact]
    public async Task GetVoiceUrl_PropagatesServiceStatusCode()
    {
        var response = new Response<PrivateFileUrlDto>
        {
            Succeeded = false,
            StatusCode = HttpStatusCode.NotFound,
            Message = "Voice review item not found or expired."
        };
        _voiceReviewServiceMock.Setup(s => s.GetVoiceUrlAsync("bad-ref")).ReturnsAsync(response);

        var controller = CreateController(Guid.NewGuid());
        var result = await controller.GetVoiceUrl("bad-ref");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Same(response, obj.Value);
    }

    [Fact]
    public async Task Decide_WhenRevokedUserIdPresent_InvokesRevokerAndResponseJsonNeverContainsRevokedUserId()
    {
        var reviewerId = Guid.NewGuid();
        var revokedUserId = Guid.NewGuid();
        var dto = new VoiceReviewDecisionDto { Decision = UserStatus.Rejected, Reason = "Audio mismatch" };

        var serviceResponse = VoiceReviewDecisionResponse.From(
            new Response<string>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = "Voice review decision 'Rejected' applied."
            },
            revokedUserId: revokedUserId);

        _voiceReviewServiceMock
            .Setup(s => s.DecideAsync("opaque-ref", dto, reviewerId))
            .ReturnsAsync(serviceResponse);

        var controller = CreateController(reviewerId);
        var result = await controller.Decide("opaque-ref", dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(serviceResponse, ok.Value);
        _revokerMock.Verify(r => r.RevokeAsync(revokedUserId), Times.Once);

        var serialized = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(revokedUserId.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(revokedUserId.ToString("N"), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("revokedUserId", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Decide_WhenNoRevocationRequired_ReturnsOkWithoutCallingRevoker()
    {
        var reviewerId = Guid.NewGuid();
        var dto = new VoiceReviewDecisionDto { Decision = UserStatus.Active };

        var serviceResponse = VoiceReviewDecisionResponse.From(
            new Response<string>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = "Voice review decision 'Active' applied."
            },
            revokedUserId: null);

        _voiceReviewServiceMock
            .Setup(s => s.DecideAsync("opaque-ref", dto, reviewerId))
            .ReturnsAsync(serviceResponse);

        var controller = CreateController(reviewerId);
        var result = await controller.Decide("opaque-ref", dto);

        Assert.IsType<OkObjectResult>(result);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Conflict, 409)]
    public async Task Decide_FailedServiceResult_PropagatesStatusCodeAndNeverCallsRevoker(HttpStatusCode statusCode, int expectedCode)
    {
        var reviewerId = Guid.NewGuid();
        var dto = new VoiceReviewDecisionDto { Decision = UserStatus.Rejected, Reason = "Duplicate decision" };

        var serviceResponse = VoiceReviewDecisionResponse.From(
            new Response<string>
            {
                Succeeded = false,
                StatusCode = statusCode,
                Message = "Refused"
            },
            revokedUserId: Guid.NewGuid());

        _voiceReviewServiceMock
            .Setup(s => s.DecideAsync("opaque-ref", dto, reviewerId))
            .ReturnsAsync(serviceResponse);

        var controller = CreateController(reviewerId);
        var result = await controller.Decide("opaque-ref", dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedCode, obj.StatusCode);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Decide_MissingActorIdentity_ReturnsBadRequestWithoutCallingServiceOrRevoker()
    {
        var controller = CreateController(reviewerId: null);
        var result = await controller.Decide("opaque-ref", new VoiceReviewDecisionDto { Decision = UserStatus.Active });

        Assert.IsType<BadRequestObjectResult>(result);
        _voiceReviewServiceMock.Verify(
            s => s.DecideAsync(It.IsAny<string>(), It.IsAny<VoiceReviewDecisionDto>(), It.IsAny<Guid>()),
            Times.Never);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }
}
