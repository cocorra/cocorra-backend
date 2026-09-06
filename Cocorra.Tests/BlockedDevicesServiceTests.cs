using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Services.BlockedDevicesService;
using Cocorra.DAL.DTOS.BlockedDevicesDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class BlockedDevicesServiceTests
{
    private readonly Mock<IBlockedDevicesRepository> _repoMock = new();
    private readonly BlockedDevicesService _service;

    public BlockedDevicesServiceTests()
    {
        _service = new BlockedDevicesService(_repoMock.Object);
    }

    [Fact]
    public async Task BlockDeviceAsync_NullDevice_ReturnsFalse()
    {
        var result = await _service.BlockDeviceAsync(null!);
        Assert.False(result);
        _repoMock.Verify(r => r.AddBlockedDeviceAsync(It.IsAny<BlockedDevices>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlockDeviceAsync_EmptyOrWhitespaceDeviceId_ReturnsFalse(string? deviceId)
    {
        var dto = new BlockedDevicesDto { DeviceId = deviceId! };
        var result = await _service.BlockDeviceAsync(dto);
        Assert.False(result);
        _repoMock.Verify(r => r.AddBlockedDeviceAsync(It.IsAny<BlockedDevices>()), Times.Never);
    }

    [Fact]
    public async Task BlockDeviceAsync_DeviceAlreadyBlocked_ReturnsTrueWithoutAdding()
    {
        var deviceId = "device-123";
        var existing = new BlockedDevices { DeviceId = deviceId, IsBlocked = true };
        _repoMock.Setup(r => r.GetByDeviceIdAsync(deviceId)).ReturnsAsync(existing);

        var dto = new BlockedDevicesDto { DeviceId = deviceId };
        var result = await _service.BlockDeviceAsync(dto);

        Assert.True(result);
        _repoMock.Verify(r => r.UpdateBlockedDeviceAsync(It.IsAny<BlockedDevices>()), Times.Never);
        _repoMock.Verify(r => r.AddBlockedDeviceAsync(It.IsAny<BlockedDevices>()), Times.Never);
    }

    [Fact]
    public async Task BlockDeviceAsync_DeviceExistsButUnblocked_UpdatesToBlocked()
    {
        var deviceId = "device-123";
        var existing = new BlockedDevices { DeviceId = deviceId, IsBlocked = false };
        _repoMock.Setup(r => r.GetByDeviceIdAsync(deviceId)).ReturnsAsync(existing);
        _repoMock.Setup(r => r.UpdateBlockedDeviceAsync(existing)).ReturnsAsync(true);

        var dto = new BlockedDevicesDto { DeviceId = deviceId };
        var result = await _service.BlockDeviceAsync(dto);

        Assert.True(result);
        Assert.True(existing.IsBlocked);
        _repoMock.Verify(r => r.UpdateBlockedDeviceAsync(existing), Times.Once);
        _repoMock.Verify(r => r.AddBlockedDeviceAsync(It.IsAny<BlockedDevices>()), Times.Never);
    }

    [Fact]
    public async Task BlockDeviceAsync_NewDevice_AddsBlockedDevice()
    {
        var deviceId = "device-456";
        var userId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByDeviceIdAsync(deviceId)).ReturnsAsync((BlockedDevices?)null);
        _repoMock.Setup(r => r.AddBlockedDeviceAsync(It.Is<BlockedDevices>(d =>
            d.DeviceId == deviceId &&
            d.DeviceName == "Pixel" &&
            d.IsBlocked == true &&
            d.ApplicationUserId == userId))).ReturnsAsync(true);

        var dto = new BlockedDevicesDto
        {
            DeviceId = deviceId,
            DeviceName = "Pixel",
            DeviceModel = "7 Pro",
            DeviceType = "Mobile",
            DeviceOs = "Android",
            ApplicationUserId = userId
        };

        var result = await _service.BlockDeviceAsync(dto);

        Assert.True(result);
        _repoMock.Verify(r => r.AddBlockedDeviceAsync(It.IsAny<BlockedDevices>()), Times.Once);
    }

    [Fact]
    public async Task GetUserBlockedDevicesAsync_EmptyGuid_ReturnsEmptyList()
    {
        var result = await _service.GetUserBlockedDevicesAsync(Guid.Empty);
        Assert.Empty(result);
        _repoMock.Verify(r => r.GetBlockedDevicesByUserAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetUserBlockedDevicesAsync_ValidUser_MapsAndReturnsList()
    {
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var devices = new List<BlockedDevices>
        {
            new()
            {
                DeviceId = "dev-1",
                DeviceName = "iPhone",
                DeviceModel = "15",
                DeviceType = "Phone",
                DeviceOs = "iOS",
                ApplicationUserId = userId,
                CreatedAt = now
            }
        };
        _repoMock.Setup(r => r.GetBlockedDevicesByUserAsync(userId)).ReturnsAsync(devices);

        var result = await _service.GetUserBlockedDevicesAsync(userId);

        Assert.Single(result);
        Assert.Equal("dev-1", result[0].DeviceId);
        Assert.Equal("iPhone", result[0].DeviceName);
        Assert.Equal(userId, result[0].ApplicationUserId);
        Assert.Equal(now, result[0].BlockedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IsDeviceBlockedAsync_NullOrWhitespace_ReturnsFalse(string? deviceId)
    {
        var result = await _service.IsDeviceBlockedAsync(deviceId!);
        Assert.False(result);
        _repoMock.Verify(r => r.IsDeviceBlockedAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IsDeviceBlockedAsync_ValidDeviceId_ReturnsRepoValue()
    {
        var deviceId = "dev-abc";
        _repoMock.Setup(r => r.IsDeviceBlockedAsync(deviceId)).ReturnsAsync(true);

        var result = await _service.IsDeviceBlockedAsync(deviceId);

        Assert.True(result);
        _repoMock.Verify(r => r.IsDeviceBlockedAsync(deviceId), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnblockDeviceAsync_NullOrWhitespace_ReturnsFalse(string? deviceId)
    {
        var result = await _service.UnblockDeviceAsync(deviceId!);
        Assert.False(result);
        _repoMock.Verify(r => r.RemoveBlockedDeviceAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnblockDeviceAsync_ValidDeviceId_CallsRepoAndReturnsTrue()
    {
        var deviceId = "dev-abc";
        _repoMock.Setup(r => r.RemoveBlockedDeviceAsync(deviceId)).ReturnsAsync(true);

        var result = await _service.UnblockDeviceAsync(deviceId);

        Assert.True(result);
        _repoMock.Verify(r => r.RemoveBlockedDeviceAsync(deviceId), Times.Once);
    }
}
