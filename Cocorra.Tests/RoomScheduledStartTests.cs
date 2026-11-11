using System;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RoomScheduledStartTests
{
    private readonly Mock<IRoomRepository> _roomRepoMock = new();
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<ILiveKitService> _liveKitServiceMock = new();
    private readonly LiveKitSettings _liveKitSettings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "testkey",
        ApiSecret = "testsecret"
    };

    private RoomService CreateService()
    {
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.test.com");

        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        return new RoomService(
            _roomRepoMock.Object,
            _mediatorMock.Object,
            _uploadImageMock.Object,
            configMock.Object,
            _pushServiceMock.Object,
            userManager.Object,
            _liveKitServiceMock.Object,
            Options.Create(_liveKitSettings),
            _eventTrackerMock.Object,
            new Mock<ILogger<RoomService>>().Object);
    }

    private static CreateRoomDto Dto(DateTime? scheduledStartDate) => new()
    {
        RoomTitle = "Scheduled start test",
        Description = "Test room",
        ScheduledStartDate = scheduledStartDate,
        DurationHours = 2,
        TotalCapacity = 50,
        StageCapacity = 5,
        DefaultSpeakerDurationMinutes = 5,
        IsPrivate = false,
        SelectionMode = RoomSelectionMode.Manual_CoachDecision,
        Category = RoomCategory.Relationships
    };

    private Func<Room?> CaptureSavedRoom()
    {
        Room? saved = null;
        _roomRepoMock
            .Setup(r => r.AddAsync(It.IsAny<Room>()))
            .Callback<Room>(room => saved = room)
            .ReturnsAsync((Room room) => room);
        return () => saved;
    }

    [Fact]
    public async Task OffsetlessScheduledStart_IsRejected_AndNothingIsPersisted()
    {
        var scheduledStart = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(1), DateTimeKind.Unspecified);
        var service = CreateService();

        var result = await service.CreateRoomAsync(Dto(scheduledStart), Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        _roomRepoMock.Verify(r => r.AddAsync(It.IsAny<Room>()), Times.Never);
    }

    [Fact]
    public async Task FutureUtcScheduledStart_IsPersistedAsScheduled()
    {
        var scheduledStart = DateTime.UtcNow.AddHours(2);
        var savedRoom = CaptureSavedRoom();
        var service = CreateService();

        var result = await service.CreateRoomAsync(Dto(scheduledStart), Guid.NewGuid());

        var saved = savedRoom();
        Assert.True(result.Succeeded);
        Assert.NotNull(saved);
        Assert.Equal(RoomStatus.Scheduled, saved!.Status);
        Assert.Equal(scheduledStart, saved.StartDate);
        Assert.Null(saved.WentLiveAt);
    }

    [Fact]
    public async Task PastUtcScheduledStart_StartsNow_AndRecordsWhenItWentLive()
    {
        var scheduledStart = DateTime.UtcNow.AddHours(-2);
        var savedRoom = CaptureSavedRoom();
        var service = CreateService();

        var before = DateTime.UtcNow;
        var result = await service.CreateRoomAsync(Dto(scheduledStart), Guid.NewGuid());
        var after = DateTime.UtcNow;

        var saved = savedRoom();
        Assert.True(result.Succeeded);
        Assert.NotNull(saved);
        Assert.Equal(RoomStatus.Live, saved!.Status);
        Assert.InRange(saved.StartDate, before, after);
        Assert.NotEqual(scheduledStart, saved.StartDate);
        Assert.NotNull(saved.WentLiveAt);
    }

    [Fact]
    public async Task NoScheduledStart_CreatesTheRoomLive()
    {
        var savedRoom = CaptureSavedRoom();
        var service = CreateService();

        var result = await service.CreateRoomAsync(Dto(null), Guid.NewGuid());

        var saved = savedRoom();
        Assert.True(result.Succeeded);
        Assert.NotNull(saved);
        Assert.Equal(RoomStatus.Live, saved!.Status);
        Assert.NotNull(saved.WentLiveAt);
    }
}
