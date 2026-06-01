using Cocorra.BLL.Services.LiveKit;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;

namespace Cocorra.Tests;

public class LiveKitServiceTests
{
    private readonly LiveKitSettings _settings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "APIbnoqt7M8yCA4",
        ApiSecret = "4vQLn7Z9GienF7cPZ54SOcA4fYyec5fTSAevDQh2DU5G"
    };

    private ILiveKitService CreateService()
    {
        var options = Options.Create(_settings);
        return new LiveKitService(options);
    }

    [Fact]
    public void GenerateToken_ReturnsNonEmptyJwt()
    {
        // Arrange
        var service = CreateService();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Act
        var token = service.GenerateToken(roomId, userId, "Test User");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public void GenerateToken_ReturnsValidJwtFormat()
    {
        // Arrange
        var service = CreateService();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Act
        var token = service.GenerateToken(roomId, userId, "Test User");

        // Assert — JWT has 3 dot-separated segments
        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);
    }

    [Fact]
    public void GenerateToken_ContainsCorrectIdentityClaim()
    {
        // Arrange
        var service = CreateService();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var displayName = "Kareem Ahmed";

        // Act
        var token = service.GenerateToken(roomId, userId, displayName);

        // Assert — decode and check the 'sub' claim (identity)
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        Assert.Equal(userId.ToString(), jwt.Subject);
    }

    [Fact]
    public void GenerateToken_ContainsRoomInGrants()
    {
        // Arrange
        var service = CreateService();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Act
        var token = service.GenerateToken(roomId, userId, "Test User");

        // Assert — the 'video' claim should contain the room name
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var videoClaim = jwt.Claims.FirstOrDefault(c => c.Type == "video");
        Assert.NotNull(videoClaim);
        Assert.Contains(roomId.ToString(), videoClaim.Value);
    }

    [Fact]
    public void GenerateToken_HasFutureExpiration()
    {
        // Arrange
        var service = CreateService();

        // Act
        var token = service.GenerateToken(Guid.NewGuid(), Guid.NewGuid(), "Test");

        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        Assert.True(jwt.ValidTo > DateTime.UtcNow, "Token should expire in the future.");
    }

    [Fact]
    public void GenerateToken_ExpiresWithinExpectedTtl()
    {
        // Arrange
        var service = CreateService();

        // Act
        var token = service.GenerateToken(Guid.NewGuid(), Guid.NewGuid(), "Test");

        // Assert — TTL should be ~4 hours (within 5-minute tolerance)
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var ttl = jwt.ValidTo - DateTime.UtcNow;
        Assert.InRange(ttl.TotalHours, 3.9, 4.1);
    }

    [Fact]
    public void GenerateToken_DifferentInputs_ProduceDifferentTokens()
    {
        // Arrange
        var service = CreateService();
        var roomId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        // Act
        var token1 = service.GenerateToken(roomId, user1, "User One");
        var token2 = service.GenerateToken(roomId, user2, "User Two");

        // Assert
        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void GenerateToken_SameInputs_SameRoom()
    {
        // Arrange
        var service = CreateService();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Act
        var token1 = service.GenerateToken(roomId, userId, "Same User");
        var token2 = service.GenerateToken(roomId, userId, "Same User");

        // Assert — both tokens should target the same room
        var handler = new JwtSecurityTokenHandler();
        var jwt1 = handler.ReadJwtToken(token1);
        var jwt2 = handler.ReadJwtToken(token2);

        Assert.Equal(jwt1.Subject, jwt2.Subject);
    }

    [Fact]
    public void GenerateToken_IssuerMatchesApiKey()
    {
        // Arrange
        var service = CreateService();

        // Act
        var token = service.GenerateToken(Guid.NewGuid(), Guid.NewGuid(), "Test");

        // Assert — LiveKit SDK uses ApiKey as the issuer
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        Assert.Equal(_settings.ApiKey, jwt.Issuer);
    }
}
