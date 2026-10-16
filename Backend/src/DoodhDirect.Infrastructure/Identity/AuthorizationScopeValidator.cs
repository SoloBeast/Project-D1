using DoodhDirect.Application.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class AuthorizationScopeValidator(DoodhDirectDbContext dbContext) : IAuthorizationScopeValidator
{
    public async Task<bool> IsTokenScopeCurrentAsync(
        long userId,
        long authorizationVersion,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.IsActive, x.AuthorizationVersion })
            .SingleOrDefaultAsync(cancellationToken);
        return current is not null &&
            current.IsActive &&
            current.AuthorizationVersion == authorizationVersion;
    }
}
