# Owner Runtime Debug Trace

## Authorization Model

`OWNER` is assigned every permission from the authorization-code dictionary, including `ACCESS.GLOBAL`, by [IdentitySeedService](../Backend/src/DoodhDirect.Infrastructure/Identity/IdentitySeedService.cs:1). Owner behavior is therefore permission/global-claim based; authorization handlers do not test the role name.

## Flutter Runtime

Owner maps to `UserRole.owner` and shares `_AdminHomeActions`. Reports, catalogue, camera management, and public catalogue are exposed according to permissions. Dairy and delivery cards require a non-empty branch list in the session. Development Owner is seeded without a branch, so those cards are normally absent even though backend actor checks allow global access.

## Scope Bypass

- Permission handler: Owner succeeds because all exact permission claims exist.
- Branch-aware services: Owner succeeds through `ACCESS.GLOBAL`.
- Administrative order/payment reads use explicit ownership bypass where controller/service methods provide it.
- Customer-owned endpoints still derive a customer from `user_id`; global access does not automatically make every own-resource route meaningful.

## Gaps

`NOT FOUND IN CURRENT IMPLEMENTATION`: dedicated Owner UI, cross-branch selector, user/role administration UI, and special Owner-only endpoints.

`DOCUMENTATION != IMPLEMENTATION`: “Owner bypass” is not hard-coded by role; it is the consequence of seeded permission and global claims. Flutter branch cards can remain hidden for a globally scoped Owner.

## Breakpoints

- [roleFromCodes()](../mobile/lib/features/auth/auth_repository.dart:26)
- [BranchScopeAuthorizationHandler](../Backend/src/DoodhDirect.Api/Authorization/AuthorizationRequirements.cs:59)
- [IdentitySeedService.SeedAsync()](../Backend/src/DoodhDirect.Infrastructure/Identity/IdentitySeedService.cs:136)
- [ReportService.GetDashboardAsync()](../Backend/src/DoodhDirect.Infrastructure/Reports/ReportService.cs:23)
