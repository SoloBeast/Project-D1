using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Tests;

public sealed class IdentityFoundationTests
{
    [Fact]
    public void UserContact_IsTrimmedAndEmailIsNormalized()
    {
        var user = new User(UserType.Customer);

        user.SetContact(" 9876543210 ", " CUSTOMER@Example.COM ");

        Assert.Equal("9876543210", user.Mobile);
        Assert.Equal("customer@example.com", user.Email);
    }

    [Fact]
    public void AuditTimestamp_RejectsNonUtcValues()
    {
        var user = new User(UserType.Employee);
        var localTimestamp = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Local);

        var exception = Assert.Throws<ArgumentException>(() => user.SetCreated(localTimestamp));

        Assert.Equal("utcNow", exception.ParamName);
    }

    [Fact]
    public void RefreshToken_IsInactiveAtExpiryBoundary()
    {
        var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var token = new RefreshToken(42, "token-hash", now.AddMinutes(5));

        Assert.True(token.IsActive(now));
        Assert.False(token.IsActive(now.AddMinutes(5)));
    }

    [Fact]
    public void RefreshToken_RevocationStoresRotationHash()
    {
        var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var token = new RefreshToken(42, "old-token-hash", now.AddDays(7));

        token.Revoke(now, "replacement-token-hash");

        Assert.False(token.IsActive(now));
        Assert.Equal(now, token.RevokedAtUtc);
        Assert.Equal("replacement-token-hash", token.ReplacedByTokenHash);
    }
}
