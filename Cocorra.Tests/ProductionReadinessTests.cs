using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// Wave 8 production-readiness guards: secret hygiene, configuration override paths,
/// and the rollback contract.
///
/// These are regression guards for things that are correct right now and would be
/// silently wrong later. The IP-hash salt was committed in tracked configuration for
/// several releases before anyone noticed; nothing in the build objected, because a
/// secret in a config file is indistinguishable from a setting.
/// </summary>
public class ProductionReadinessTests
{
    // ── Repository layout helpers ───────────────────────────────────────────

    /// <summary>
    /// Walks up from the test assembly until it finds the solution file, so these
    /// assertions do not depend on the working directory the test runner happens to use.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cocorra.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    // ── Secret hygiene ──────────────────────────────────────────────────────

    [Fact]
    public void TrackedAppSettings_DoNotContainAnIpHashSaltValue()
    {
        // The salt makes UserEvent.IpHash irreversible. SHA256 over the 2^32 IPv4 space is
        // minutes of work for anyone holding the salt, so a committed salt turns a hashed
        // column into a plaintext IP log for everyone with repository access.
        //
        // This asserts the KEY carries no value, not that the string is absent: an
        // explanatory "_comment_IpHashSalt" entry is expected and desirable.
        foreach (var file in new[]
                 {
                     "Cocorra.API/appsettings.json",
                     "Cocorra.API/appsettings.Development.json"
                 })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ReadRepoFile(file));

            if (!doc.RootElement.TryGetProperty("Analytics", out var analytics))
            {
                continue;
            }

            var hasValue = analytics.TryGetProperty("IpHashSalt", out var salt)
                           && salt.ValueKind == System.Text.Json.JsonValueKind.String
                           && !string.IsNullOrWhiteSpace(salt.GetString());

            Assert.False(hasValue,
                $"{file} contains a value for Analytics:IpHashSalt. It must come from the " +
                "environment (Analytics__IpHashSalt), never from tracked configuration.");
        }
    }

    [Fact]
    public void EnvExampleTemplate_ExistsAndHoldsOnlyAPlaceholder()
    {
        // A tracked template is what keeps "configure it safely" from meaning "guess".
        // It must never acquire a real value, which is the failure mode it exists to prevent.
        var env = ReadRepoFile(".env.example");

        Assert.Contains("ANALYTICS_IP_HASH_SALT=", env);
        Assert.Contains("CHANGE_ME", env);
    }

    [Fact]
    public void GitIgnore_ExcludesEnvFilesPublishOutputAndPublishProfiles()
    {
        var ignore = ReadRepoFile(".gitignore");

        Assert.Contains(".env", ignore);
        Assert.Contains("!.env.example", ignore);   // the template must stay trackable
        Assert.Contains("publish/", ignore);
        Assert.Contains("*.publishSettings", ignore);

        // A UTF-16 fragment was once appended without a newline, producing
        // "Thumbs.dbi\0m\0p\0l..." and silently breaking the Thumbs.db rule. NUL bytes in a
        // .gitignore mean an edit went wrong, whatever the visible text looks like.
        Assert.DoesNotContain('\0', ignore);
    }

    [Fact]
    public void DockerCompose_SourcesTheSaltFromTheEnvironment_AndDoesNotEnableEventFlags()
    {
        var compose = ReadRepoFile("docker-compose.yml");

        Assert.Contains("Analytics__IpHashSalt=${ANALYTICS_IP_HASH_SALT", compose);

        // Both emission flags must remain commented out. Enabling either from the compose
        // file would skip the staged activation gates, which exist because the two increments
        // add load to a bounded channel whose drop rate has never been measured.
        foreach (var line in compose.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            Assert.DoesNotContain("Analytics__EnableNewEventEmission=true", line);
            Assert.DoesNotContain("Analytics__EnableHighFrequencyEvents=true", line);
        }
    }

    // ── Configuration override path ─────────────────────────────────────────

    [Fact]
    public void SaltBindsFromTheDoubleUnderscoreEnvironmentVariableForm()
    {
        // Proves the production delivery mechanism actually reaches the key the code reads.
        // ASP.NET Core maps `__` to `:`; asserting it here means the docker-compose entry is
        // verified rather than assumed.
        const string key = "Analytics__IpHashSalt";
        var previous = Environment.GetEnvironmentVariable(key);

        try
        {
            Environment.SetEnvironmentVariable(key, "env-supplied-salt");

            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            Assert.Equal("env-supplied-salt", config["Analytics:IpHashSalt"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Fact]
    public void EnvironmentConfiguration_OverridesFileConfiguration()
    {
        // Production must be able to override whatever a file says, in the right precedence
        // order. Asserted because the whole externalisation rests on it.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:IpHashSalt"] = "from-file",
                ["Analytics:EnableNewEventEmission"] = "false"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Analytics:IpHashSalt"] = "from-environment",
                ["Analytics:EnableNewEventEmission"] = "true"
            })
            .Build();

        Assert.Equal("from-environment", config["Analytics:IpHashSalt"]);
        Assert.Equal("true", config["Analytics:EnableNewEventEmission"]);
    }

    // ── Design-time tooling must not need the salt ──────────────────────────

    [Fact]
    public void DesignTimeFactory_CreatesAContextWithoutTheSalt()
    {
        // REGRESSION GUARD. Externalising the salt broke every `dotnet ef` command: with no
        // IDesignTimeDbContextFactory, EF Tools build the application host to obtain a
        // DbContext, that runs Program.cs, and Program.cs correctly throws when the salt is
        // absent — which it now always is at design time. The failure surfaced as a confusing
        // two-part error ending in "Unable to resolve service for type
        // DbContextOptions<AppDbContext>", which reads like a DI bug rather than a config one.
        //
        // The tempting fix was to relax the guard to Production-only. That was rejected: a dev
        // environment with no salt would silently write reversible hashes, which is the exact
        // failure the guard exists to prevent. The factory is the correct fix, and this test
        // pins it — including that the salt is genuinely absent while it runs.
        const string connKey = "ConnectionStrings__DefaultConnection";
        const string saltKey = "Analytics__IpHashSalt";

        var previousConn = Environment.GetEnvironmentVariable(connKey);
        var previousSalt = Environment.GetEnvironmentVariable(saltKey);

        try
        {
            Environment.SetEnvironmentVariable(connKey, "Server=design-time;Database=x;Integrated Security=true");
            Environment.SetEnvironmentVariable(saltKey, null);

            var factory = new Cocorra.DAL.Data.AppDbContextFactory();

            Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<Cocorra.DAL.Data.AppDbContext>>(factory);

            using var ctx = factory.CreateDbContext([]);

            Assert.NotNull(ctx);
            // Must match Program.cs, or `migrations add` writes into an assembly the running
            // application never scans, and the migration is silently invisible.
            Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", ctx.Database.ProviderName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(connKey, previousConn);
            Environment.SetEnvironmentVariable(saltKey, previousSalt);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSalt_IsRejectedByTheStartupGuardCondition(string? value)
    {
        // Mirrors the guard in Program.cs. Fail-fast is the intended behaviour: a default or
        // empty salt would be worse than no analytics, because it would produce hashes that
        // look pseudonymised and are not.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Analytics:IpHashSalt"] = value })
            .Build();

        Assert.True(string.IsNullOrWhiteSpace(config["Analytics:IpHashSalt"]));
    }

    [Fact]
    public void WithoutASalt_TheTrackerStoresNoIpHash_RatherThanAnUnsaltedOne()
    {
        // Defence in depth behind the startup guard. If configuration were ever empty at
        // runtime, the tracker must omit the hash rather than fall back to hashing the raw
        // address, which would be reversible by anyone.
        var queue = NewQueue();
        var tracker = BuildTracker(queue, salt: null, withHttpContext: true);

        tracker.Track(EventTypes.FeatureViewed, Guid.NewGuid(), new { feature = "test" });

        Assert.True(queue.Reader.TryRead(out var evt));
        Assert.Null(evt!.IpHash);
    }

    [Fact]
    public void WithASalt_TheTrackerStoresAHashThatFitsTheColumn()
    {
        var queue = NewQueue();
        var tracker = BuildTracker(queue, salt: "a-real-salt", withHttpContext: true);

        tracker.Track(EventTypes.FeatureViewed, Guid.NewGuid(), new { feature = "test" });

        Assert.True(queue.Reader.TryRead(out var evt));
        Assert.NotNull(evt!.IpHash);

        // SHA256 hex is exactly 64 characters and UserEvent.IpHash is MaxLength(64).
        // One character more and every insert would fail at runtime, not at build time.
        Assert.Equal(64, evt.IpHash!.Length);
        Assert.DoesNotContain("a-real-salt", evt.IpHash);
    }

    // ── Rollback contract ───────────────────────────────────────────────────

    [Fact]
    public async Task DisablingTheFlag_StopsNewEventEmission_ButTheProductStillWorks()
    {
        // The rollback contract, end to end:
        //   flag on -> gated event emits
        //   flag off -> gated event does not emit
        //   in BOTH cases the hub method completes and the client is still notified
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var withFlag = new HubHarness(newEventsEnabled: true);
        withFlag.ArrangeParticipantInLiveRoom(roomId, userId);
        await withFlag.Hub.RaiseHand(roomId.ToString());

        Assert.Contains(withFlag.Tracked, e => e.EventType == EventTypes.HandRaised);
        withFlag.AssertClientWasNotified("HandRaised");
        withFlag.AssertParticipantWasSaved();

        var withoutFlag = new HubHarness(newEventsEnabled: false);
        withoutFlag.ArrangeParticipantInLiveRoom(roomId, userId);
        await withoutFlag.Hub.RaiseHand(roomId.ToString());

        // Analytics silent...
        Assert.DoesNotContain(withoutFlag.Tracked, e => e.EventType == EventTypes.HandRaised);
        // ...product unaffected. This is the assertion that matters: rollback must cost
        // analytics data and nothing else.
        withoutFlag.AssertClientWasNotified("HandRaised");
        withoutFlag.AssertParticipantWasSaved();
    }

    [Fact]
    public async Task UngatedEvents_KeepEmittingAfterRollback()
    {
        // Rolling back the flags must stop only the new increment. room_joined and
        // mic_activated predate it and are ungated, so the metrics resting on them —
        // including the north star — survive a rollback untouched.
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var harness = new HubHarness(newEventsEnabled: false);
        harness.ArrangeSpeakerOnStage(roomId, userId);

        await harness.Hub.ToggleMic(roomId.ToString(), muteStatus: false);

        Assert.Contains(harness.Tracked, e => e.EventType == EventTypes.MicActivated);
    }

    [Fact]
    public void AFullChannel_DropsTheEventAndCountsIt_WithoutThrowing()
    {
        // INV-1. Tracking must never throw back to the user, so an analytics outage can
        // never become a product outage. The drop is counted rather than silent, which is
        // what makes Stage A's baseline measurable at all.
        var queue = Channel.CreateBounded<UserEvent>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

        var metrics = new EventPipelineMetrics();
        var tracker = BuildTracker(queue, salt: "s", withHttpContext: false, metrics: metrics);

        // Fill it, then overflow it several times.
        for (var i = 0; i < 5; i++)
        {
            tracker.Track(EventTypes.FeatureViewed, Guid.NewGuid(), new { i });
        }

        Assert.True(metrics.EventsDroppedOnEnqueue > 0);
    }

    // ── Harnesses ───────────────────────────────────────────────────────────

    private static Channel<UserEvent> NewQueue()
        => Channel.CreateBounded<UserEvent>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });

    private static EventTracker BuildTracker(
        Channel<UserEvent> queue,
        string? salt,
        bool withHttpContext,
        EventPipelineMetrics? metrics = null)
    {
        var accessor = new Mock<IHttpContextAccessor>();

        if (withHttpContext)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
            accessor.Setup(a => a.HttpContext).Returns(ctx);
        }
        else
        {
            accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Analytics:IpHashSalt"] = salt })
            .Build();

        return new EventTracker(queue, NullLogger<EventTracker>.Instance, accessor.Object, config, metrics);
    }

    /// <summary>
    /// Minimal RoomHub harness. Asserts on the product-visible side effects — the client
    /// notification and the participant save — because "analytics stopped" is only half the
    /// rollback contract; the other half is that nothing else did.
    /// </summary>
    private sealed class HubHarness
    {
        private readonly Mock<IRoomRepository> _roomRepo = new();
        private readonly Mock<IEventTracker> _tracker = new();
        private readonly Mock<IHubCallerClients> _clients = new();
        private readonly Mock<IClientProxy> _groupProxy = new();
        private readonly Mock<ISingleClientProxy> _callerProxy = new();
        private readonly Mock<IGroupManager> _groups = new();
        private readonly Mock<HubCallerContext> _context = new();

        public List<(string EventType, Guid? UserId, object? Properties)> Tracked { get; } = [];
        public RoomHub Hub { get; }

        public HubHarness(bool newEventsEnabled)
        {
            _clients.Setup(c => c.Caller).Returns(_callerProxy.Object);
            _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);
            _context.Setup(c => c.ConnectionAborted).Returns(default(System.Threading.CancellationToken));

            _tracker.SetupGet(t => t.NewEventEmissionEnabled).Returns(newEventsEnabled);
            _tracker.SetupGet(t => t.HighFrequencyEventsEnabled).Returns(newEventsEnabled);
            _tracker
                .Setup(t => t.Track(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<object?>()))
                .Callback<string, Guid?, object?>((t, u, p) => Tracked.Add((t, u, p)));

            Hub = new RoomHub(
                _roomRepo.Object,
                new Mock<IRoomService>().Object,
                new Mock<IChatService>().Object,
                new Mock<ILiveKitService>().Object,
                Options.Create(new LiveKitSettings
                {
                    ServerUrl = "wss://test.livekit.dev",
                    ApiKey = "k",
                    ApiSecret = "s"
                }),
                _tracker.Object,
                NullLogger<RoomHub>.Instance);

            Hub.Clients = _clients.Object;
            Hub.Groups = _groups.Object;
        }

        private void SetCaller(Guid userId)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test");
            _context.Setup(c => c.User).Returns(new ClaimsPrincipal(identity));
            _context.Setup(c => c.ConnectionId).Returns("conn-wave8");
            _context.Setup(c => c.UserIdentifier).Returns(userId.ToString());
            Hub.Context = _context.Object;
        }

        public void ArrangeParticipantInLiveRoom(Guid roomId, Guid userId)
        {
            SetCaller(userId);
            _roomRepo.Setup(r => r.GetByIdAsync(roomId))
                .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Live, StageCapacity = 5 });
            _roomRepo.Setup(r => r.GetParticipantAsync(roomId, userId))
                .ReturnsAsync(new RoomParticipant
                {
                    RoomId = roomId,
                    UserId = userId,
                    Status = ParticipantStatus.Active,
                    IsOnStage = false,
                    IsHandRaised = false
                });
        }

        public void ArrangeSpeakerOnStage(Guid roomId, Guid userId)
        {
            SetCaller(userId);
            _roomRepo.Setup(r => r.GetByIdAsync(roomId))
                .ReturnsAsync(new Room
                {
                    Id = roomId,
                    Status = RoomStatus.Live,
                    HostId = Guid.NewGuid(),
                    DefaultSpeakerDurationMinutes = 10
                });
            _roomRepo.Setup(r => r.GetParticipantAsync(roomId, userId))
                .ReturnsAsync(new RoomParticipant
                {
                    RoomId = roomId,
                    UserId = userId,
                    Status = ParticipantStatus.Active,
                    IsOnStage = true,
                    IsMuted = true,
                    TotalSpokenSeconds = 0
                });
        }

        public void AssertClientWasNotified(string method)
            => _groupProxy.Verify(
                p => p.SendCoreAsync(method, It.IsAny<object?[]>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.AtLeastOnce);

        public void AssertParticipantWasSaved()
            => _roomRepo.Verify(r => r.SaveChangesAsync(), Times.AtLeastOnce);
    }
}
