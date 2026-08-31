using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AnalyticsService;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AnalyticsControllerTests
{
    private readonly Mock<IAnalyticsService> _analyticsServiceMock = new();

    private AnalyticsController CreateController()
    {
        return new AnalyticsController(_analyticsServiceMock.Object);
    }

    [Fact]
    public async Task GetPlatformSummary_ReturnsOk()
    {
        // Arrange
        var summaryDto = new PlatformSummaryDto();
        var response = new Response<PlatformSummaryDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = summaryDto
        };

        _analyticsServiceMock.Setup(s => s.GetPlatformSummaryAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(response);

        var controller = CreateController();

        // Act
        var result = await controller.GetPlatformSummary(null, null);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetUserGrowth_InvalidLimit_ReturnsBadRequest()
    {
        // Arrange
        var controller = CreateController();

        // Act
        var resultZero = await controller.GetUserGrowth("monthly", null, null, 0);
        var resultTooBig = await controller.GetUserGrowth("monthly", null, null, 101);

        // Assert
        Assert.IsType<BadRequestObjectResult>(resultZero);
        Assert.IsType<BadRequestObjectResult>(resultTooBig);
    }

    [Fact]
    public async Task GetUserGrowth_Success_ReturnsOk()
    {
        // Arrange
        var growthDto = new UserGrowthDto();
        var response = new Response<UserGrowthDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = growthDto
        };

        _analyticsServiceMock.Setup(s => s.GetUserGrowthAsync("monthly", null, null, 10))
            .ReturnsAsync(response);

        var controller = CreateController();

        // Act
        var result = await controller.GetUserGrowth("monthly", null, null, 10);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetRoomAnalytics_InvalidLimit_ReturnsBadRequest()
    {
        var controller = CreateController();
        var result = await controller.GetRoomAnalytics(null, null, 0);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetRoomAnalytics_Success_ReturnsOk()
    {
        var response = new Response<RoomAnalyticsDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new RoomAnalyticsDto()
        };

        _analyticsServiceMock.Setup(s => s.GetRoomAnalyticsAsync(null, null, 10))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetRoomAnalytics(null, null, 10);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetParticipationStats_InvalidLimit_ReturnsBadRequest()
    {
        var controller = CreateController();
        var result = await controller.GetParticipationStats(null, null, 105);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetParticipationStats_Success_ReturnsOk()
    {
        var response = new Response<ParticipationStatsDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new ParticipationStatsDto()
        };

        _analyticsServiceMock.Setup(s => s.GetParticipationStatsAsync(null, null, 10))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetParticipationStats(null, null, 10);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetReportInsights_InvalidLimit_ReturnsBadRequest()
    {
        var controller = CreateController();
        var result = await controller.GetReportInsights(null, null, 0);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetReportInsights_Success_ReturnsOk()
    {
        var response = new Response<ReportInsightsDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new ReportInsightsDto()
        };

        _analyticsServiceMock.Setup(s => s.GetReportInsightsAsync(null, null, 10))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetReportInsights(null, null, 10);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetFunnel_Success_ReturnsOk()
    {
        var response = new Response<Dictionary<string, int>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new Dictionary<string, int> { { "step1", 10 }, { "step2", 5 } }
        };

        _analyticsServiceMock.Setup(s => s.GetFunnelAsync(It.IsAny<string[]>(), null, null))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetFunnel("step1,step2", null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetRetention_Success_ReturnsOk()
    {
        var response = new Response<Dictionary<int, double>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new Dictionary<int, double> { { 1, 50.0 } }
        };

        _analyticsServiceMock.Setup(s => s.GetRetentionCohortAsync("user_registered", "session_started", null, null))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetRetention("user_registered", "session_started", null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetMostActiveRooms_Success_ReturnsOk()
    {
        var response = new Response<List<TopActiveRoomDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new List<TopActiveRoomDto>()
        };

        _analyticsServiceMock.Setup(s => s.GetMostActiveRoomsAsync(null, null, 10))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetMostActiveRooms(null, null, 10);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetPeakActiveHours_Success_ReturnsOk()
    {
        var response = new Response<List<HourlyActivityDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new List<HourlyActivityDto>()
        };

        _analyticsServiceMock.Setup(s => s.GetPeakActiveHoursAsync(null, null))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetPeakActiveHours(null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetVoiceVerificationDropOff_Success_ReturnsOk()
    {
        var response = new Response<VoiceVerificationFunnelDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new VoiceVerificationFunnelDto()
        };

        _analyticsServiceMock.Setup(s => s.GetVoiceVerificationDropOffAsync(null, null))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetVoiceVerificationDropOff(null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }

    [Fact]
    public async Task GetActiveVsPassiveRate_Success_ReturnsOk()
    {
        var response = new Response<ParticipationModeDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new ParticipationModeDto()
        };

        _analyticsServiceMock.Setup(s => s.GetActiveVsPassiveRateAsync(null, null))
            .ReturnsAsync(response);

        var controller = CreateController();
        var result = await controller.GetActiveVsPassiveRate(null, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(response, ok.Value);
    }
}
