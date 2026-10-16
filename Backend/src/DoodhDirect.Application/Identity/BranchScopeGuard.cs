using DoodhDirect.Application.Common;

namespace DoodhDirect.Application.Identity;

/// <summary>
/// Stage 1 multi-branch safety invariant: permissions are unioned across roles
/// and branch ids are unioned across assignments, so a user holding Role A on
/// branch 1 and Role B on branch 2 would otherwise exercise Role A's
/// permissions on branch 2 (and vice versa). Until a role-aware
/// permission/scope mechanism exists, branch-scoped assignments are confined
/// to a single role code per user — multiple branches within that role are
/// fine. Global (branch-less) assignments never participate in the cross
/// product and are ignored here.
/// </summary>
public static class BranchScopeGuard
{
    public static void EnsureSingleBranchedRole(
        IEnumerable<(string RoleCode, long? BranchId)> assignments)
    {
        var branchedRoles = assignments
            .Where(x => x.BranchId.HasValue && !string.IsNullOrWhiteSpace(x.RoleCode))
            .Select(x => x.RoleCode.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (branchedRoles.Length > 1)
        {
            throw new BusinessRuleException(
                $"Conflicting branch-scoped roles ({string.Join(", ", branchedRoles)}) cannot be " +
                "combined on one account. Assign branches under a single role.");
        }
    }
}
