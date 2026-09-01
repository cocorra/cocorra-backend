using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.BLL.Services.AnalyticsService;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>
    /// AN-012 step 5: a served metric without a complete contract must fail the build.
    ///
    /// A compile-time analyzer would be heavier than the problem warrants; a test that fails CI
    /// is the same guarantee at a fraction of the cost. What matters is that re-accumulating
    /// undocumented metrics becomes impossible rather than merely discouraged.
    /// </summary>
    public class MetricRegistryContractTests
    {
        private readonly IMetricRegistry _registry = new MetricRegistry();

        [Fact]
        public void EveryMetricKeyConstant_HasAContract()
        {
            var keys = typeof(MetricRegistry)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (Name: f.Name, Key: (string)f.GetRawConstantValue()!))
                .ToList();

            Assert.NotEmpty(keys);

            var missing = keys.Where(k => _registry.GetContract(k.Key) is null).ToList();

            Assert.True(missing.Count == 0,
                "Metric keys declared with no contract: " + string.Join(", ", missing.Select(m => $"{m.Name} ({m.Key})")));
        }

        [Fact]
        public void EveryContract_HasAllMandatoryFields()
        {
            var incomplete = _registry.GetAllContracts()
                .Select(c => new
                {
                    c.MetricKey,
                    Missing = new[]
                    {
                        string.IsNullOrWhiteSpace(c.Name) ? "Name" : null,
                        string.IsNullOrWhiteSpace(c.BusinessPurpose) ? "BusinessPurpose" : null,
                        string.IsNullOrWhiteSpace(c.TechnicalDefinition) ? "TechnicalDefinition" : null,
                        string.IsNullOrWhiteSpace(c.Formula) ? "Formula" : null,
                        string.IsNullOrWhiteSpace(c.ValidationMethod) ? "ValidationMethod" : null
                    }.Where(m => m is not null).ToList()
                })
                .Where(x => x.Missing.Count > 0)
                .ToList();

            Assert.True(incomplete.Count == 0,
                "Contracts missing mandatory fields: " +
                string.Join("; ", incomplete.Select(x => $"{x.MetricKey}: {string.Join(", ", x.Missing)}")));
        }

        [Fact]
        public void MetricsWithKnownCaveats_AreNotGradedVerified()
        {
            // The framework is only worth anything if a caveated metric is actually marked as
            // one. Anything carrying a stated limitation must not claim VERIFIED.
            var overclaiming = _registry.GetAllContracts()
                .Where(c => c.TrustLevel == MetricTrustLevel.Verified
                            && c.Limitations.Any(l => l.Contains("scheduled StartDate", StringComparison.OrdinalIgnoreCase)
                                                   || l.Contains("not measurable", StringComparison.OrdinalIgnoreCase)
                                                   || l.Contains("UTC while", StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.MetricKey)
                .ToList();

            Assert.True(overclaiming.Count == 0,
                "Metrics graded VERIFIED despite a stated limitation: " + string.Join(", ", overclaiming));
        }

        [Fact]
        public void LegacyRetentionMetric_IsGradedUnreliable()
        {
            // The endpoint stays live until cutover, but must not present itself as sound.
            var legacy = _registry.GetContract(MetricRegistry.LegacyRetentionCohort);

            Assert.NotNull(legacy);
            Assert.Equal(MetricTrustLevel.Unreliable, legacy!.TrustLevel);
            Assert.Contains(legacy.Limitations, l => l.Contains("session_started", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task AnalyticsResponses_CarryTrustMetadataOnMeta()
        {
            // Response<T>.Meta already existed on every response and was always null. This
            // asserts it is now populated, because the backend work is worthless if the client
            // cannot tell a VERIFIED number from an UNRELIABLE one.
            var repo = new Mock<IAnalyticsRepository>();
            repo.Setup(r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(new ParticipationStatsDto());

            var service = new AnalyticsService(repo.Object, new MemoryCache(new MemoryCacheOptions()), _registry);

            var response = await service.GetParticipationStatsAsync();

            Assert.True(response.Succeeded);
            Assert.NotNull(response.Meta);

            var meta = response.Meta!;
            var metaType = meta.GetType();

            var trustLevel = metaType.GetProperty("trustLevel")?.GetValue(meta) as string;
            Assert.False(string.IsNullOrWhiteSpace(trustLevel));

            var metrics = metaType.GetProperty("metrics")?.GetValue(meta);
            Assert.NotNull(metrics);
            Assert.NotEmpty((System.Collections.IEnumerable)metrics!);

            Assert.NotNull(metaType.GetProperty("computedAtUtc"));
        }

        [Fact]
        public async Task CompositeResponse_InheritsItsWeakestComponentTrustLevel()
        {
            // Room participation is CONDITIONALLY RELIABLE and speaking conversion is VERIFIED,
            // so the response must report the weaker of the two. Rounding up would defeat the
            // purpose of carrying a trust level at all.
            var repo = new Mock<IAnalyticsRepository>();
            repo.Setup(r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(new ParticipationStatsDto());

            var service = new AnalyticsService(repo.Object, new MemoryCache(new MemoryCacheOptions()), _registry);
            var response = await service.GetParticipationStatsAsync();

            var trustLevel = response.Meta!.GetType().GetProperty("trustLevel")!.GetValue(response.Meta) as string;

            Assert.Equal(MetricTrustLevel.ConditionallyReliable.ToString(), trustLevel);
        }
    }
}
