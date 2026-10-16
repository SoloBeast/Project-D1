namespace DoodhDirect.Application.Identity;

/// <summary>
/// Per-request revalidation of JWT authorization scope. Access tokens are
/// stateless, so without this check a token issued before a branch removal,
/// deactivation, or role change would keep authorizing the old scope until
/// expiry. The JWT <c>scope_v</c> claim mirrors
/// <see cref="DoodhDirect.Domain.Identity.User.AuthorizationVersion"/>, which
/// is bumped on every effective-authorization change; any mismatch (or a
/// missing/inactive account) fails authentication immediately.
/// </summary>
public interface IAuthorizationScopeValidator
{
    Task<bool> IsTokenScopeCurrentAsync(
        long userId,
        long authorizationVersion,
        CancellationToken cancellationToken);
}
