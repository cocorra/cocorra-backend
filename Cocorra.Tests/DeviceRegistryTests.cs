using Cocorra.API.Extensions;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// The permanent-block endpoint used to require the caller to supply the offender's device
/// details. Nothing in the system ever recorded a device, so the only id an admin dashboard
/// could send was its own — which DeviceBlockingMiddleware then used to lock the admin out.
///
/// These tests cover the registry that replaced it: BlockedDevices rows written at the
/// offender's own login (IsBlocked = false), promoted to blocked by an email-only ban.
///
/// Real SQLite rather than the InMemory provider, because the (ApplicationUserId, DeviceId)
/// unique index is load-bearing here and InMemory does not enforce indexes.
/// </summary>
public class DeviceRegistryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly BlockedDevicesRepository _repo;

    public DeviceRegistryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _repo = new BlockedDevicesRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> SeedUserAsync(string email)
    {
        // BlockedDevices.ApplicationUserId is a real FK, so the user row must exist.
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user.Id;
    }

    private static DeviceInfoDto Device(string id, string? model = "Pixel 7") => new()
    {
        DeviceId = id,
        DeviceName = "Test Phone",
        DeviceModel = model,
        DeviceType = "Android",
        DeviceOs = "14"
    };

    [Fact]
    public async Task RegisterDeviceAsync_RecordsDeviceUnblocked()
    {
        var userId = await SeedUserAsync("user@test.com");

        var registered = await _repo.RegisterDeviceAsync(userId, Device("device-a"));

        Assert.True(registered);
        var row = await _repo.GetByUserAndDeviceIdAsync(userId, "device-a");
        Assert.NotNull(row);
        // Critical: registering must never block. A registry row that arrives blocked would
        // ban every user the moment they log in.
        Assert.False(row!.IsBlocked);
        Assert.Null(row.BlockedAt);
        Assert.NotNull(row.LastSeenAt);
        Assert.False(await _repo.IsDeviceBlockedAsync("device-a"));
    }

    [Fact]
    public async Task RegisterDeviceAsync_IsIdempotent_AndRefreshesLastSeen()
    {
        var userId = await SeedUserAsync("user@test.com");

        await _repo.RegisterDeviceAsync(userId, Device("device-a"));
        var firstSeen = (await _repo.GetByUserAndDeviceIdAsync(userId, "device-a"))!.LastSeenAt;

        await Task.Delay(10);
        await _repo.RegisterDeviceAsync(userId, Device("device-a"));

        // Every refresh-token call registers, so repeated logins must update one row rather
        // than growing the table without bound.
        var rows = await _repo.GetDevicesByUserAsync(userId);
        Assert.Single(rows);
        Assert.True(rows[0].LastSeenAt > firstSeen);
    }

    [Fact]
    public async Task RegisterDeviceAsync_DoesNotClearAnExistingBlock()
    {
        var userId = await SeedUserAsync("banned@test.com");
        await _repo.RegisterDeviceAsync(userId, Device("device-a"));
        await _repo.BlockAllDevicesForUserAsync(userId);

        // A banned user re-logging in (or an old refresh token firing) must not launder
        // their own block by re-registering the device.
        await _repo.RegisterDeviceAsync(userId, Device("device-a"));

        var row = await _repo.GetByUserAndDeviceIdAsync(userId, "device-a");
        Assert.True(row!.IsBlocked);
        Assert.NotNull(row.BlockedAt);
        Assert.True(await _repo.IsDeviceBlockedAsync("device-a"));
    }

    [Fact]
    public async Task RegisterDeviceAsync_KeepsKnownMetadata_WhenClientSendsOnlyDeviceId()
    {
        var userId = await SeedUserAsync("user@test.com");
        await _repo.RegisterDeviceAsync(userId, Device("device-a", model: "Pixel 7"));

        await _repo.RegisterDeviceAsync(userId, new DeviceInfoDto { DeviceId = "device-a" });

        // A client that sends the id but omits the metadata headers must not blank out the
        // model/OS an admin relies on to identify the device.
        var row = await _repo.GetByUserAndDeviceIdAsync(userId, "device-a");
        Assert.Equal("Pixel 7", row!.DeviceModel);
    }

    [Fact]
    public async Task BlockAllDevicesForUserAsync_BlocksEveryRegisteredDevice()
    {
        var userId = await SeedUserAsync("bad@test.com");
        await _repo.RegisterDeviceAsync(userId, Device("phone"));
        await _repo.RegisterDeviceAsync(userId, Device("tablet"));

        var count = await _repo.BlockAllDevicesForUserAsync(userId);

        Assert.Equal(2, count);
        Assert.True(await _repo.IsDeviceBlockedAsync("phone"));
        Assert.True(await _repo.IsDeviceBlockedAsync("tablet"));
    }

    [Fact]
    public async Task BlockAllDevicesForUserAsync_ReturnsZero_WhenUserHasNoRegisteredDevices()
    {
        var userId = await SeedUserAsync("nodevices@test.com");

        // Users who only ever used a client that omits X-Device-Id have nothing to block.
        // The count has to be honest so the dashboard does not claim otherwise.
        Assert.Equal(0, await _repo.BlockAllDevicesForUserAsync(userId));
    }

    [Fact]
    public async Task BlockAllDevicesForUserAsync_IsIdempotent_AndPreservesOriginalBlockTime()
    {
        var userId = await SeedUserAsync("bad@test.com");
        await _repo.RegisterDeviceAsync(userId, Device("phone"));

        await _repo.BlockAllDevicesForUserAsync(userId);
        var firstBlockedAt = (await _repo.GetByUserAndDeviceIdAsync(userId, "phone"))!.BlockedAt;

        var secondCount = await _repo.BlockAllDevicesForUserAsync(userId);

        Assert.Equal(1, secondCount);
        Assert.Equal(firstBlockedAt, (await _repo.GetByUserAndDeviceIdAsync(userId, "phone"))!.BlockedAt);
    }

    [Fact]
    public async Task BlockingOneUsersDevice_AlsoBlocksASecondAccountOnTheSameDevice()
    {
        var offenderId = await SeedUserAsync("offender@test.com");
        var altAccountId = await SeedUserAsync("evader@test.com");

        await _repo.RegisterDeviceAsync(offenderId, Device("shared-handset"));
        await _repo.RegisterDeviceAsync(altAccountId, Device("shared-handset"));

        await _repo.BlockAllDevicesForUserAsync(offenderId);

        // This is the point of device blocking: the ban survives making a new account on the
        // same handset. Enforcement matches on device id, not on which account owns the row.
        Assert.True(await _repo.IsDeviceBlockedAsync("shared-handset"));
        Assert.False((await _repo.GetByUserAndDeviceIdAsync(altAccountId, "shared-handset"))!.IsBlocked);
    }

    [Fact]
    public async Task GetBlockedDevicesByUserAsync_ExcludesMerelyRegisteredDevices()
    {
        var userId = await SeedUserAsync("user@test.com");
        await _repo.RegisterDeviceAsync(userId, Device("phone"));
        await _repo.RegisterDeviceAsync(userId, Device("tablet"));

        // Promote only one of them.
        var tablet = await _repo.GetByUserAndDeviceIdAsync(userId, "tablet");
        tablet!.IsBlocked = true;
        tablet.BlockedAt = DateTime.UtcNow;
        await _repo.UpdateBlockedDeviceAsync(tablet);

        var blocked = await _repo.GetBlockedDevicesByUserAsync(userId);

        // Now that this table also holds unblocked registrations, the admin-facing
        // "blocked devices" view would otherwise report every device the user ever used.
        Assert.Single(blocked);
        Assert.Equal("tablet", blocked[0].DeviceId);
        Assert.Equal(2, (await _repo.GetDevicesByUserAsync(userId)).Count);
    }

    [Fact]
    public async Task UnblockDeviceAsync_DemotesToRegistration_WithoutLosingTheRow()
    {
        var userId = await SeedUserAsync("forgiven@test.com");
        await _repo.RegisterDeviceAsync(userId, Device("phone"));
        await _repo.BlockAllDevicesForUserAsync(userId);

        Assert.True(await _repo.UnblockDeviceAsync("phone"));

        // Deleting the row would lose the device history for this user; demote instead.
        var row = await _repo.GetByUserAndDeviceIdAsync(userId, "phone");
        Assert.NotNull(row);
        Assert.False(row!.IsBlocked);
        Assert.Null(row.BlockedAt);
        Assert.False(await _repo.IsDeviceBlockedAsync("phone"));
    }

    [Fact]
    public async Task RegisterDeviceAsync_IgnoresBlankDeviceId()
    {
        var userId = await SeedUserAsync("user@test.com");

        Assert.False(await _repo.RegisterDeviceAsync(userId, new DeviceInfoDto { DeviceId = "   " }));
        Assert.Empty(await _repo.GetDevicesByUserAsync(userId));
    }

    [Theory]
    [InlineData("device-123", "device-123")]
    [InlineData("  device-123  ", "device-123")]
    public void GetDeviceInfo_ReadsDeviceIdHeader(string header, string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[DeviceHeaderExtensions.DeviceIdHeader] = header;

        Assert.Equal(expected, request.GetDeviceInfo()!.DeviceId);
    }

    [Fact]
    public void GetDeviceInfo_ReturnsNull_WhenHeaderMissingOrBlank()
    {
        // Older app builds send no device headers at all. That is not an error — it just
        // means there is nothing to register, and login must proceed normally.
        Assert.Null(new DefaultHttpContext().Request.GetDeviceInfo());

        var blank = new DefaultHttpContext().Request;
        blank.Headers[DeviceHeaderExtensions.DeviceIdHeader] = "  ";
        Assert.Null(blank.GetDeviceInfo());
    }

    [Fact]
    public void GetDeviceInfo_ReadsMetadataHeaders()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[DeviceHeaderExtensions.DeviceIdHeader] = "device-123";
        request.Headers[DeviceHeaderExtensions.DeviceNameHeader] = "Kareem's Phone";
        request.Headers[DeviceHeaderExtensions.DeviceModelHeader] = "Pixel 7";
        request.Headers[DeviceHeaderExtensions.DeviceTypeHeader] = "Android";
        request.Headers[DeviceHeaderExtensions.DeviceOsHeader] = "14";

        var device = request.GetDeviceInfo()!;

        Assert.Equal("Kareem's Phone", device.DeviceName);
        Assert.Equal("Pixel 7", device.DeviceModel);
        Assert.Equal("Android", device.DeviceType);
        Assert.Equal("14", device.DeviceOs);
    }

    [Fact]
    public void GetDeviceInfo_TruncatesOverlongDeviceId()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[DeviceHeaderExtensions.DeviceIdHeader] = new string('x', 500);

        Assert.Equal(200, request.GetDeviceInfo()!.DeviceId.Length);
    }
}
