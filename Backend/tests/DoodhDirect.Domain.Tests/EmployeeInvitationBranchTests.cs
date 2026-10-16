using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Tests;

/// <summary>
/// Stage 2 multi-branch Dairy Manager: invitation branch-link behavior at the
/// domain level (ordering, de-duplication, legacy scalar compatibility, fallback).
/// </summary>
public sealed class EmployeeInvitationBranchTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void AttachBranchLinks_OrdersAndDeduplicates()
    {
        var invitation = NewInvitation(11);

        invitation.AttachBranchLinks(new[] { 22L, 11L, 22L });

        Assert.Equal(new[] { 11L, 22L }, invitation.GetBranchIds());
    }

    [Fact]
    public void ChangeRoleAndBranches_TracksLowestBranchOnLegacyScalar()
    {
        var invitation = NewInvitation(null);

        invitation.ChangeRoleAndBranches("DAIRY_MANAGER", new[] { 22L, 11L });

        Assert.Equal(11L, invitation.BranchId);
        Assert.Equal("DAIRY_MANAGER", invitation.RoleCode);
    }

    [Fact]
    public void GetBranchIds_FallsBackToLegacyScalarWithoutLinks()
    {
        var invitation = NewInvitation(11);

        Assert.Equal(new[] { 11L }, invitation.GetBranchIds());
    }

    [Fact]
    public void GetBranchIds_PrefersLinksOverLegacyScalar()
    {
        var invitation = NewInvitation(99);
        invitation.AttachBranchLinks(new[] { 11L, 22L });

        Assert.Equal(new[] { 11L, 22L }, invitation.GetBranchIds());
    }

    private static EmployeeInvitation NewInvitation(long? branchId) => new(
        "Stage2 Invitee",
        "+919876543210",
        "invitee@example.com",
        "DAIRY_MANAGER",
        branchId,
        "token-hash",
        Now,
        Now.AddDays(7),
        42);
}
