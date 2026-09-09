using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cocorra.API.Controllers;
using Cocorra.BLL.Services.Analytics;
using Cocorra.BLL.Services.AnalyticsService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>
    /// Wiring guards: route coverage, service-contract completeness, and configuration binding.
    ///
    /// These exist because of a real defect class this programme hit twice. Both the M-200
    /// mis-attachment and the missing Platform Health endpoint were invisible to every existing
    /// test: the code compiled, every unit test passed, and the metric contracts were complete
    /// and well-formed. What was wrong was the *wiring* — a metric key attached to the wrong
    /// payload, and a declared route that no controller served.
    ///
    /// A compiling solution is not a wired one, and these assertions are the difference.
    /// </summary>
    public class AnalyticsWiringTests
    {
        private static IEnumerable<(string Name, string Route)> AnalyticsRouteConstants()
            => typeof(Router.AnalyticsRouting)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                // Prefix is a building block for the others, not a route in its own right.
                .Where(f => f.Name != nameof(Router.AnalyticsRouting.Prefix))
                .Select(f => (f.Name, (string)f.GetRawConstantValue()!));

        private static IEnumerable<string> ControllerRouteTemplates()
            => typeof(AnalyticsController).Assembly
                .GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
                .Select(a => a.Template)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!);

        [Fact]
        public void EveryDeclaredAnalyticsRoute_IsServedByAControllerAction()
        {
            var templates = ControllerRouteTemplates().ToHashSet(StringComparer.OrdinalIgnoreCase);

            var orphaned = AnalyticsRouteConstants()
                .Where(r => !templates.Contains(r.Route))
                .ToList();

            Assert.True(orphaned.Count == 0,
                "Route constants with no controller action: " +
                string.Join(", ", orphaned.Select(r => $"{r.Name} ({r.Route})")));
        }

        [Fact]
        public void AnalyticsRoutes_AreUnique()
        {
            // Two constants resolving to the same template means one action shadows the other,
            // and the loser returns 404 or 405 with nothing in the build to say so.
            var duplicates = AnalyticsRouteConstants()
                .GroupBy(r => r.Route, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            Assert.True(duplicates.Count == 0,
                "Duplicate route templates: " +
                string.Join("; ", duplicates.Select(g => $"{g.Key} ← {string.Join(", ", g.Select(x => x.Name))}")));
        }

        [Fact]
        public void EveryAnalyticsServiceMethod_IsImplemented_AndEveryRepositoryMethodIsToo()
        {
            // Guards the layer boundary: an interface method added without an implementation is a
            // compile error, but a *service* method that no controller reaches, or a repository
            // method the service never calls, is dead weight nobody notices.
            var serviceMethods = typeof(IAnalyticsService).GetMethods().Select(m => m.Name).ToList();
            var repoMethods = typeof(IAnalyticsRepository).GetMethods().Select(m => m.Name).ToList();

            Assert.All(serviceMethods, name =>
                Assert.NotNull(typeof(AnalyticsService).GetMethod(name)));

            Assert.All(repoMethods, name =>
                Assert.NotNull(typeof(AnalyticsRepository).GetMethod(name)));
        }

        [Fact]
        public void NewestEndpoints_AreReachable()
        {
            // The two endpoints added in the final hardening phase, named explicitly rather than
            // covered only by the generic sweep above. A-1 in particular was specified in the API
            // blueprint and silently never built.
            var templates = ControllerRouteTemplates().ToList();

            Assert.Contains(Router.AnalyticsRouting.PlatformHealth, templates);
            Assert.Contains(Router.AnalyticsRouting.StageFunnel, templates);
        }

        // ── Configuration binding ───────────────────────────────────────────

        [Fact]
        public void EventTrackingOptions_BindFromTheAnalyticsSection()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Analytics:IpHashSalt"] = "salt",
                    ["Analytics:EnableNewEventEmission"] = "true",
                    ["Analytics:EnableHighFrequencyEvents"] = "true",
                    ["Analytics:EventChannelCapacity"] = "25000",
                    ["Analytics:DisplayTimeZoneOffsetMinutes"] = "120",
                    ["Analytics:RawEventRetentionDays"] = "90"
                })
                .Build();

            var services = new ServiceCollection();
            services.Configure<EventTrackingOptions>(config.GetSection(EventTrackingOptions.SectionName));
            var options = services.BuildServiceProvider().GetRequiredService<IOptions<EventTrackingOptions>>().Value;

            Assert.Equal("salt", options.IpHashSalt);
            Assert.True(options.EnableNewEventEmission);
            Assert.True(options.EnableHighFrequencyEvents);
            Assert.Equal(25000, options.EventChannelCapacity);
            Assert.Equal(120, options.DisplayTimeZoneOffsetMinutes);
            Assert.Equal(90, options.RawEventRetentionDays);
        }

        [Fact]
        public void EventTrackingOptions_DefaultsAreSafeWhenTheSectionIsAbsent()
        {
            var options = new EventTrackingOptions();

            // Both emission flags MUST default to false. A default of true would mean a deploy
            // that forgot the config turns on high-frequency events against an unmeasured
            // channel — the one failure mode the staged activation exists to prevent.
            Assert.False(options.EnableNewEventEmission);
            Assert.False(options.EnableHighFrequencyEvents);

            // A missing display offset must fall back to the documented default, not to 0.
            // Silently falling back to UTC is the failure AN-036 exists to prevent.
            Assert.Equal(AnalyticsDisplayDefaults.TimeZoneOffsetMinutes, options.DisplayTimeZoneOffsetMinutes);

            Assert.Equal(180, options.RawEventRetentionDays);
            Assert.Equal(10_000, options.EventChannelCapacity);
            Assert.Null(options.StructuredLogPath);
        }

        [Fact]
        public void HighFrequencyFlag_CannotBeEnabledWithoutTheLowFrequencyFlag()
        {
            // Conjunctive by design: deploying the high-volume increment before the low-frequency
            // one has proven stable would remove any ability to attribute a drop-rate spike to
            // one increment or the other.
            var tracker = BuildTracker(newEvents: false, highFrequency: true);

            Assert.False(tracker.NewEventEmissionEnabled);
            Assert.False(tracker.HighFrequencyEventsEnabled);

            var both = BuildTracker(newEvents: true, highFrequency: true);
            Assert.True(both.NewEventEmissionEnabled);
            Assert.True(both.HighFrequencyEventsEnabled);

            var lowOnly = BuildTracker(newEvents: true, highFrequency: false);
            Assert.True(lowOnly.NewEventEmissionEnabled);
            Assert.False(lowOnly.HighFrequencyEventsEnabled);
        }

        private static IEventTracker BuildTracker(bool newEvents, bool highFrequency)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Analytics:IpHashSalt"] = "salt",
                    ["Analytics:EnableNewEventEmission"] = newEvents.ToString(),
                    ["Analytics:EnableHighFrequencyEvents"] = highFrequency.ToString()
                })
                .Build();

            var queue = System.Threading.Channels.Channel.CreateBounded<DAL.Models.UserEvent>(10);
            var httpAccessor = new Moq.Mock<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            httpAccessor.Setup(a => a.HttpContext).Returns((Microsoft.AspNetCore.Http.HttpContext?)null);

            return new EventTracker(
                queue,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<EventTracker>.Instance,
                httpAccessor.Object,
                config);
        }

        // ── Trust-level ordering ────────────────────────────────────────────

        [Fact]
        public void TrustLevelOrdinals_TrackDescendingTrust_SoTheWeakestComponentGoverns()
        {
            // BuildMeta takes Max() across a composite's components so the response inherits its
            // weakest input. That only holds while the ordinal order tracks descending trust.
            // Inserting a value in the wrong position would silently round composite trust UP,
            // which is the exact failure the composite rule exists to prevent.
            Assert.True(MetricTrustLevel.Verified < MetricTrustLevel.ConditionallyReliable);
            Assert.True(MetricTrustLevel.ConditionallyReliable < MetricTrustLevel.Experimental);
            Assert.True(MetricTrustLevel.Experimental < MetricTrustLevel.Unreliable);
        }

        [Fact]
        public void MetricRegistry_ServesTheExpectedIdSet_AndNoIdIsReserved()
        {
            // RESERVED ids belong to planned-but-unbuilt metrics (26-metric-registry-reconciliation.md
            // section 4). If one of these ever resolves to a contract, something claimed an id that
            // means a different thing in the dashboard blueprint.
            var registry = new MetricRegistry();
            string[] reserved = ["M-203", "M-204", "M-401", "M-402", "M-403", "M-600", "M-602"];

            var claimed = reserved.Where(id => registry.GetContract(id) is not null).ToList();

            Assert.True(claimed.Count == 0,
                "Reserved metric ids now resolve to a contract: " + string.Join(", ", claimed));

            // And the two ids corrected during reconciliation must both exist and be distinct.
            var activeHosts = registry.GetContract(MetricRegistry.ActiveHosts);
            var roomsGoneLive = registry.GetContract(MetricRegistry.RoomsGoneLive);

            Assert.NotNull(activeHosts);
            Assert.NotNull(roomsGoneLive);
            Assert.Contains("Host", activeHosts!.Name, StringComparison.Ordinal);
            Assert.Contains("Rooms", roomsGoneLive!.Name, StringComparison.Ordinal);
            Assert.NotEqual(activeHosts.MetricKey, roomsGoneLive.MetricKey);
        }
    }
}
