// Owns: BE-INVITE-001, BE-INVITE-002, BE-INVITE-003, BE-INVITE-004, BE-INVITE-005
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Cocorra.BLL.Services.RoomInviteService;
using Xunit;

namespace Cocorra.Tests;

public class InviteCodeGeneratorTests
{
    private static readonly Regex ExpectedShapeRegex = new("^[A-Za-z0-9_-]{22}$", RegexOptions.Compiled);

    [Fact]
    public void Generate_OneThousandCodes_AllMatchShapeAndAreDistinct()
    {
        const int count = 1000;
        var generatedCodes = new List<string>(count);
        var uniqueCodes = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            var code = InviteCodeGenerator.Generate();
            generatedCodes.Add(code);
            uniqueCodes.Add(code);
        }

        Assert.Equal(count, generatedCodes.Count);
        Assert.Equal(count, uniqueCodes.Count);

        foreach (var code in generatedCodes)
        {
            Assert.Equal(InviteCodeGenerator.CodeLength, code.Length);
            Assert.Matches(ExpectedShapeRegex, code);
            Assert.True(InviteCodeGenerator.IsWellFormed(code), $"Expected '{code}' to be reported as well-formed.");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void IsWellFormed_NullOrEmptyOrWhitespace_ReturnsFalse(string? code)
    {
        var result = InviteCodeGenerator.IsWellFormed(code);
        Assert.False(result);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("abc")]
    [InlineData("abcdefghij1234567890")] // 20 chars
    [InlineData("abcdefghij12345678901")] // 21 chars
    [InlineData("abcdefghij1234567890123")] // 23 chars
    [InlineData("abcdefghij1234567890123456789012")] // 32 chars
    public void IsWellFormed_LengthNot22_ReturnsFalse(string code)
    {
        var result = InviteCodeGenerator.IsWellFormed(code);
        Assert.False(result);
    }

    [Theory]
    [InlineData("abcdefghij12345678901=")] // 22 chars with '=' padding
    [InlineData("abcdefghij1234567890==")] // 22 chars with '==' padding
    [InlineData("abcdefghij12345678901+")] // 22 chars with '+' (standard Base64)
    [InlineData("abcdefghij12345678901/")] // 22 chars with '/' (standard Base64)
    [InlineData("abcdefghij 12345678901")] // contains whitespace
    [InlineData("abcdefghij.12345678901")] // contains period
    [InlineData("abcdefghij:12345678901")] // contains colon
    [InlineData("abcdefghij?12345678901")] // contains question mark
    [InlineData("abcdefghij#12345678901")] // contains hash
    [InlineData("abcdefghij$12345678901")] // contains dollar
    [InlineData("abcdefghij!12345678901")] // contains exclamation
    [InlineData("abcdefghij@12345678901")] // contains at
    public void IsWellFormed_ContainsDisallowedCharacters_ReturnsFalse(string code)
    {
        var result = InviteCodeGenerator.IsWellFormed(code);
        Assert.False(result);
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuv")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("0123456789012345678901")]
    [InlineData("-_-_-_-_-_-_-_-_-_-_-_")]
    [InlineData("aB3-_kL90-qZ1_8xW2mN5P")]
    public void IsWellFormed_Valid22CharBase64UrlString_ReturnsTrue(string code)
    {
        var result = InviteCodeGenerator.IsWellFormed(code);
        Assert.True(result);
    }
}
