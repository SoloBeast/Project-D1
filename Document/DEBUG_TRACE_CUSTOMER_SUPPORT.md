# Customer Support Runtime Debug Trace

## Implemented Identity Surface

`CUSTOMER_SUPPORT` is a seeded role. It receives profile/session permissions plus user read, customer profile read, order read, dashboard report, and administration report permissions in [IdentitySeedService](../Backend/src/DoodhDirect.Infrastructure/Identity/IdentitySeedService.cs:1). Development seed assigns it to a branch.

## Flutter Runtime

The role maps to `UserRole.support`, but [RoleHomeScreen](../mobile/lib/features/home/role_home_screen.dart:51) renders only a placeholder `StatePanel` stating that support workflows remain outside the identity/RBAC phase. Notifications and sign-out remain available from the common app bar.

`NOT FOUND IN CURRENT IMPLEMENTATION`: dedicated support screens, customer search/edit UI, order support UI, case/ticket workflow, impersonation, or role-specific navigation to reports. API permissions and report endpoints exist, but this role’s home does not expose them.

`DOCUMENTATION != IMPLEMENTATION`: the role is implemented in RBAC and may authorize shared endpoints, while the current Flutter role workspace does not present those operations.

## Debug Path

Login -> auth response roles -> [roleFromCodes()](../mobile/lib/features/auth/auth_repository.dart:26) -> support home placeholder. For unexpected 403, inspect JWT permission and branch claims, identity seed assignment, dynamic policy handler, then service branch filtering.

## Breakpoints

- [roleFromCodes()](../mobile/lib/features/auth/auth_repository.dart:26)
- [RoleHomeScreen.build()](../mobile/lib/features/home/role_home_screen.dart:14)
- [PermissionAuthorizationHandler](../Backend/src/DoodhDirect.Api/Authorization/AuthorizationRequirements.cs:46)
- [ReportService.GetDashboardAsync()](../Backend/src/DoodhDirect.Infrastructure/Reports/ReportService.cs:23)
