using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class JwtTokenService(IOptions<JwtOptions> jwtOptions, SecureTokenGenerator tokenGenerator) : ITokenService
{
    private readonly JwtOptions _options = jwtOptions.Value;

    public TokenPair Create(
        User user,
        UserSession session,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions,
        IReadOnlyCollection<long> branchIds,
        DateTime utcNow)
    {
        var accessExpiry = utcNow.AddMinutes(_options.AccessTokenMinutes);
        var refreshExpiry = utcNow.AddDays(_options.RefreshTokenDays);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.PublicId.ToString()),
            new("user_id", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("session_id", session.PublicId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.Name, user.DisplayName ?? user.Email ?? user.Mobile ?? user.PublicId.ToString())
        };
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        claims.AddRange(permissions.Select(permission =>
            new Claim(AuthorizationCodes.PermissionClaim, permission)));
        claims.AddRange(branchIds.Select(branchId =>
            new Claim(
                AuthorizationCodes.BranchClaim,
                branchId.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var accessToken = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: utcNow,
            expires: accessExpiry,
            signingCredentials: signingCredentials);

        return new TokenPair(
            new JwtSecurityTokenHandler().WriteToken(accessToken),
            tokenGenerator.Create(),
            accessExpiry,
            refreshExpiry);
    }

    public string HashRefreshToken(string token) => tokenGenerator.Hash(token);
}
