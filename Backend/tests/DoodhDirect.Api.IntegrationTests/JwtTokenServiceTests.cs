using System.IdentityModel.Tokens.Jwt;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Identity;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class JwtTokenServiceTests
{
    [Fact]
    public void Create_KeepsContractExpiriesIndiaLocal_AndConvertsOnlyJwtNumericDatesToUtc()
    {
        var now = new DateTime(
            2026,
            8,
            20,
            3,
            32,
            0,
            DateTimeKind.Unspecified);
        var timeProvider = new TestClock(now);
        var service = new JwtTokenService(
            Options.Create(new JwtOptions
            {
                Issuer = "DoodhDirect.Tests",
                Audience = "DoodhDirect.Tests.Client",
                SigningKey = "doodhdirect-tests-signing-key-at-least-32-bytes",
                AccessTokenMinutes = 15,
                RefreshTokenDays = 30
            }),
            new SecureTokenGenerator(),
            timeProvider);
        var user = new User(UserType.Customer);
        user.SetProfile("JWT Test Customer");
        var session = new UserSession(
            42,
            "device-hash",
            "Test Device",
            "Test Platform",
            "127.0.0.1",
            "Test Agent",
            now);

        var result = service.Create(
            user,
            session,
            ["CUSTOMER"],
            ["profile.read.own"],
            [7],
            now);

        var expectedAccessExpiry = now.AddMinutes(15);
        var expectedRefreshExpiry = now.AddDays(30);
        Assert.Equal(expectedAccessExpiry, result.AccessTokenExpiresAt);
        Assert.Equal(expectedRefreshExpiry, result.RefreshTokenExpiresAt);
        Assert.Equal(DateTimeKind.Unspecified, result.AccessTokenExpiresAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, result.RefreshTokenExpiresAt.Kind);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Equal(timeProvider.ToUtc(now), jwt.ValidFrom);
        Assert.Equal(timeProvider.ToUtc(expectedAccessExpiry), jwt.ValidTo);
        Assert.Equal(DateTimeKind.Utc, jwt.ValidFrom.Kind);
        Assert.Equal(DateTimeKind.Utc, jwt.ValidTo.Kind);
    }
}
