using System;
using Cocorra.BLL.Base;
using Xunit;

namespace Cocorra.Tests;

public class UtcDateTimeTests
{
    [Fact]
    public void Normalize_UtcKind_ReturnsSameTicksAndUtcKind()
    {
        var input = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc);

        var result = UtcDateTime.Normalize(input);

        Assert.Equal(input.Ticks, result.Ticks);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Normalize_UnspecifiedKind_PreservesTicksAndSetsUtcKind()
    {
        var input = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Unspecified);

        var result = UtcDateTime.Normalize(input);

        Assert.Equal(input.Ticks, result.Ticks);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Normalize_LocalKind_ConvertsToUniversalTimeAndSetsUtcKind()
    {
        var input = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Local);
        var expectedTicks = input.ToUniversalTime().Ticks;

        var result = UtcDateTime.Normalize(input);

        Assert.Equal(expectedTicks, result.Ticks);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Normalize_NullableNull_ReturnsNull()
    {
        DateTime? input = null;

        var result = UtcDateTime.Normalize(input);

        Assert.Null(result);
    }

    [Fact]
    public void Normalize_NullableUnspecifiedKind_PreservesTicksAndSetsUtcKind()
    {
        DateTime? input = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Unspecified);

        var result = UtcDateTime.Normalize(input);

        Assert.NotNull(result);
        Assert.Equal(input.Value.Ticks, result.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, result.Value.Kind);
    }
}
