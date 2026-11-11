using System;
using System.Text.Json;
using Cocorra.BLL.Base;
using Xunit;

namespace Cocorra.Tests;

public class UtcDateTimeJsonConverterTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        Converters = { new UtcDateTimeJsonConverter() }
    };

    private sealed record NullableDateTimeContainer(DateTime? Value);

    [Fact]
    public void Serialize_UnspecifiedKind_SerializesWithTrailingZ()
    {
        var input = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(input, _options);

        Assert.Equal("\"2026-11-11T10:30:00Z\"", json);
    }

    [Fact]
    public void Serialize_UtcKind_SerializesWithTrailingZ()
    {
        var input = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(input, _options);

        Assert.Equal("\"2026-11-11T10:30:00Z\"", json);
    }

    [Fact]
    public void Serialize_LocalKind_MatchesUniversalTimeJsonWithTrailingZ()
    {
        var input = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Local);
        var expectedJson = JsonSerializer.Serialize(input.ToUniversalTime(), _options);

        var json = JsonSerializer.Serialize(input, _options);

        Assert.Equal(expectedJson, json);
        Assert.EndsWith("Z\"", json);
    }

    [Fact]
    public void Serialize_UnspecifiedKindWithMilliseconds_PreservesFractionalSeconds()
    {
        var input = new DateTime(2026, 11, 11, 10, 30, 0, 123, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(input, _options);

        Assert.Equal("\"2026-11-11T10:30:00.123Z\"", json);
    }

    [Fact]
    public void Serialize_NullableDateTimeProperty_SerializesValueWithTrailingZAndNullAsJsonNull()
    {
        var modelWithValue = new NullableDateTimeContainer(new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Unspecified));
        var modelWithNull = new NullableDateTimeContainer(null);

        var jsonWithValue = JsonSerializer.Serialize(modelWithValue, _options);
        var jsonWithNull = JsonSerializer.Serialize(modelWithNull, _options);

        Assert.EndsWith("Z\"}", jsonWithValue);
        Assert.Equal("{\"Value\":null}", jsonWithNull);
    }

    [Fact]
    public void Deserialize_IsoUtcStringWithZ_ReturnsUtcKind()
    {
        var json = "\"2026-11-11T10:30:00Z\"";
        var expected = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc);

        var result = JsonSerializer.Deserialize<DateTime>(json, _options);

        Assert.Equal(expected, result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Deserialize_IsoStringWithPositiveOffset_ConvertsToUtcKind()
    {
        var json = "\"2026-11-11T12:30:00+02:00\"";
        var expected = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc);

        var result = JsonSerializer.Deserialize<DateTime>(json, _options);

        Assert.Equal(expected, result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Deserialize_OffsetLessIsoString_InterpretsAsUtcKind()
    {
        var json = "\"2026-11-11T10:30:00\"";
        var expected = new DateTime(2026, 11, 11, 10, 30, 0, DateTimeKind.Utc);

        var result = JsonSerializer.Deserialize<DateTime>(json, _options);

        Assert.Equal(expected, result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void Deserialize_NullableDateTimeProperty_Null_ReturnsNull()
    {
        var json = "{\"Value\":null}";

        var result = JsonSerializer.Deserialize<NullableDateTimeContainer>(json, _options);

        Assert.NotNull(result);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Deserialize_InvalidDateString_ThrowsJsonException()
    {
        var json = "\"not-a-date\"";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTime>(json, _options));
    }

    public static IEnumerable<object[]> UnspecifiedTimestampContainers()
    {
        var timestamp = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Unspecified);

        yield return new object[] { new List<DateTime> { timestamp, timestamp.AddHours(1) } };
        yield return new object[] { new { Inner = new { At = timestamp } } };
        yield return new object[] { new { At = (object)timestamp } };
    }

    [Theory]
    [MemberData(nameof(UnspecifiedTimestampContainers))]
    public void Serialize_UnspecifiedTimestampsInContainers_SerializeEveryValueWithTrailingZ(object container)
    {
        var json = JsonSerializer.Serialize(container, container.GetType(), _options);

        using var document = JsonDocument.Parse(json);
        var serializedStrings = StringValues(document.RootElement).ToList();

        Assert.NotEmpty(serializedStrings);
        Assert.All(serializedStrings, value => Assert.EndsWith("Z", value));
    }

    private static IEnumerable<string> StringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var value in StringValues(property.Value))
                        yield return value;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in StringValues(item))
                        yield return value;
                }
                break;
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
        }
    }
}
