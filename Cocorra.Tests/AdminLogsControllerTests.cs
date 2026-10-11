using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.SystemLogs;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.LogsDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AdminLogsControllerTests
{
    private readonly Mock<ISystemLogService> _systemLogServiceMock = new();
    private readonly Mock<IAuditLogService> _auditLogServiceMock = new();

    private AdminLogsController CreateController() =>
        new(_systemLogServiceMock.Object, _auditLogServiceMock.Object);

    [Fact]
    public void Controller_HasAuthorizeAttributeWithAdminRoleAndNotModerator()
    {
        var controllerType = typeof(AdminLogsController);

        var authorizeAttributes = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .OfType<AuthorizeAttribute>()
            .ToList();

        Assert.NotEmpty(authorizeAttributes);
        var authAttr = authorizeAttributes.First();
        Assert.Equal("Admin", authAttr.Roles);
        Assert.DoesNotContain("Moderator", authAttr.Roles ?? string.Empty);
    }

    [Fact]
    public void GetSystemLogs_HasCorrectHttpGetRouteTemplate()
    {
        var method = typeof(AdminLogsController).GetMethod(nameof(AdminLogsController.GetSystemLogs));
        Assert.NotNull(method);

        var httpGetAttr = method!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .OfType<HttpGetAttribute>()
            .FirstOrDefault();

        Assert.NotNull(httpGetAttr);
        Assert.Equal(Router.AdminRouting.SystemLogs, httpGetAttr!.Template);
        Assert.Equal("Api/V1/Admin/SystemLogs", Router.AdminRouting.SystemLogs);
    }

    [Fact]
    public void GetAuditLogs_HasCorrectHttpGetRouteTemplate()
    {
        var method = typeof(AdminLogsController).GetMethod(nameof(AdminLogsController.GetAuditLogs));
        Assert.NotNull(method);

        var httpGetAttr = method!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .OfType<HttpGetAttribute>()
            .FirstOrDefault();

        Assert.NotNull(httpGetAttr);
        Assert.Equal(Router.AdminRouting.AuditLogs, httpGetAttr!.Template);
        Assert.Equal("Api/V1/Admin/AuditLogs", Router.AdminRouting.AuditLogs);
    }

    [Fact]
    public async Task GetSystemLogs_ClampsPageSize500To50_AndPageNumber0To1()
    {
        var controller = CreateController();

        var dummyResponse = new PagedResponse<SystemLogDto>(
            new List<SystemLogDto>(), totalCount: 0, currentPage: 1, pageSize: 50);

        _systemLogServiceMock.Setup(s => s.GetSystemLogsAsync(It.IsAny<SystemLogQueryDto>()))
            .ReturnsAsync(dummyResponse);

        var query = new SystemLogQueryDto { PageNumber = 0, PageSize = 500 };
        var result = await controller.GetSystemLogs(query);

        _systemLogServiceMock.Verify(s => s.GetSystemLogsAsync(
            It.Is<SystemLogQueryDto>(q => q.PageNumber == 1 && q.PageSize == 50)), Times.Once);

        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, objResult.StatusCode);
    }

    [Fact]
    public async Task GetAuditLogs_ClampsPageSize500To50_AndPageNumber0To1()
    {
        var controller = CreateController();

        var dummyResponse = new PagedResponse<AuditLogDto>(
            new List<AuditLogDto>(), totalCount: 0, currentPage: 1, pageSize: 50);

        _auditLogServiceMock.Setup(s => s.GetAuditLogsAsync(It.IsAny<AuditLogQueryDto>()))
            .ReturnsAsync(dummyResponse);

        var query = new AuditLogQueryDto { PageNumber = 0, PageSize = 500 };
        var result = await controller.GetAuditLogs(query);

        _auditLogServiceMock.Verify(s => s.GetAuditLogsAsync(
            It.Is<AuditLogQueryDto>(q => q.PageNumber == 1 && q.PageSize == 50)), Times.Once);

        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, objResult.StatusCode);
    }

    [Fact]
    public async Task GetSystemLogs_ReturnsServiceStatusCode400Passthrough()
    {
        var controller = CreateController();

        var serviceResponse = new Response<IEnumerable<SystemLogDto>>
        {
            StatusCode = HttpStatusCode.BadRequest,
            Succeeded = false,
            Message = "Invalid level."
        };

        _systemLogServiceMock.Setup(s => s.GetSystemLogsAsync(It.IsAny<SystemLogQueryDto>()))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetSystemLogs(new SystemLogQueryDto());

        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objResult.StatusCode);
        Assert.Same(serviceResponse, objResult.Value);
    }

    [Fact]
    public async Task GetAuditLogs_ReturnsServiceStatusCode400Passthrough()
    {
        var controller = CreateController();

        var serviceResponse = new Response<IEnumerable<AuditLogDto>>
        {
            StatusCode = HttpStatusCode.BadRequest,
            Succeeded = false,
            Message = "Invalid action."
        };

        _auditLogServiceMock.Setup(s => s.GetAuditLogsAsync(It.IsAny<AuditLogQueryDto>()))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetAuditLogs(new AuditLogQueryDto());

        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objResult.StatusCode);
        Assert.Same(serviceResponse, objResult.Value);
    }
}
