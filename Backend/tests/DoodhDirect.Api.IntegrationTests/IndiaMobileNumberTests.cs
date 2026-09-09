using DoodhDirect.Infrastructure.Identity;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Verifies the shared Indian mobile canonicalization used across registration,
/// login, OTP and password-reset flows. The helper mirrors
/// <c>canonicalizeIndianMobile</c> in the Flutter app.
/// </summary>
public sealed class IndiaMobileNumberTests
{
    public static TheoryData<string?, string?> ValidCases => new()
    {
        { "9876543210", "+919876543210" },
        { "09876543210", "+919876543210" },
        { "919876543210", "+919876543210" },
        { "+91 98765 43210", "+919876543210" },
        { "0091 9876543210", "+919876543210" },
        { "  +91  (98765)  43210  ", "+919876543210" },
    };

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void Canonicalize_ValidIndianNumbers_ProducesE164Form(string? input, string? expected) =>
        Assert.Equal(expected, IndiaMobileNumber.Canonicalize(input));

    public static TheoryData<string?> InvalidCases => new()
    {
        { null },
        { "" },
        { "   " },
        { "12345" },
        { "98765432" },
        { "5876543210" },   // leading digit outside the 6..9 range
        { "0876543210" },   // local number may not start with 0
        { "+91987654321" }, // 11 digits after stripping, first not 0
        { "+91998765432" }, // 11 digits after stripping, first not 0
        { "99987654321" },  // 11 digits, first not 0
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Canonicalize_InvalidOrNonIndianNumbers_ReturnsNull(string? input) =>
        Assert.Null(IndiaMobileNumber.Canonicalize(input));

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void ToNational_StripsCountryCode(string? input, string? expected)
    {
        Assert.NotNull(expected);
        var national = IndiaMobileNumber.ToNational(input);
        Assert.Equal(expected[3..], national);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void ToNational_InvalidInput_ReturnsNull(string? input) =>
        Assert.Null(IndiaMobileNumber.ToNational(input));

    [Theory]
    [InlineData("+91 98765 43210", "919876543210")]
    [InlineData("9876543210", "9876543210")]
    public void ToDigits_StripsPlusAndSeparators(string input, string expected) =>
        Assert.Equal(expected, IndiaMobileNumber.ToDigits(input));
}
