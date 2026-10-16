# DoodhDirect Multi-Branch Dairy Manager — Read-Only Discovery Audit

Discovery only. No code, migrations, tests, configuration, API contracts, or Flutter UI were modified.
**Verified** = traced in the current tree. **Recommendation** / **Assumption** = marked as such.

## 1. Executive finding

No new assignment table is required. Branch scope is already a **set**, end to end, everywhere except the employee-administration write path and staff Flutter surfaces:

- `UserRole` (`Backend/src/DoodhDirect.Domain/Identity/IdentityEntities.cs:199-232`) carries nullable `BranchId` per role-assignment row. One user can already hold N rows.
- EF unique indexes (`DoodhDirectDbContext.cs:276-290`) are `(UserId, RoleId)` filtered `BranchId IS NULL` plus `(UserId, RoleId, BranchId)` filtered `BranchId IS NOT NULL` — i.e. the schema **already permits** `(user, DAIRY_MANAGER, branchA)` + `(user, DAIRY_MANAGER, branchB)`. No migration needed for the join itself.
- `IdentityMappings.ToAuthUserResult` (`OtpService.cs:469-490`) collects `BranchIds` as `Distinct()` across assignments; `JwtTokenService.Create` (`JwtTokenService.cs:46-49`) emits one `branch_id` claim per id; every staff controller builds actors from **all** claims with `Distinct()` (e.g. `DairyController.cs:103-116`, `DeliveriesController.cs:36-58`); every sampled service enforces `HasGlobalAccess || BranchIds.Contains(x)` (Dairy `DairyService.cs:283-287`, Deliveries `DeliveryService.cs:1343-1345`, Cameras `CameraService.cs:267-269`, Refunds `RefundReplacementService.cs:1533-1540`, Orders `OrderServices.cs:271`, MilkTests `MilkTestService.cs:111,618,627`, Reports `ResolveBranchesAsync` + `ValidateActor` at `ReportService.cs:315-342,376`).
- The single-branch constraint lives **only** in `EmployeeService.CreateAsync/UpdateAsync` (`EmployeeService.cs:148-164, 210-262`: single `request.BranchId`, `FirstOrDefault` assignment, `RemoveRange(all) + AssignRole(one)`), the single-`BranchId` API contracts (`EmployeeContracts.cs`), the single-`BranchId` `EmployeeInvitation` (`IdentityEntities.cs:466-557`), `ToEmployeeResult` (`EmployeeService.cs:1074-1097`, `FirstOrDefault`), and Flutter (`branchIds.first` in `role_home_screen.dart:1881,1966,2226-2248`, `camera_screens.dart:525`; single dropdowns in `employee_screens.dart:563,800`).

## 2. Data model (verified)

- `User` has no branch FK. Roles are via `UserRole(UserId, RoleId, BranchId?)`; `User.AssignRole(role, branchId)` (`IdentityEntities.cs:143-151`) dedupes on the triple — calling it twice with two branch ids already creates two rows.
- `Branch` lifecycle (`CatalogueEntities.cs:90-179`): `IsActive` toggle; `Archive()` forces `IsActive=false, IsArchived=true, ArchivedAt`. `BranchService.ListAsync` excludes archived (`BranchService.cs:33-39`); archived branch cannot be reactivated (`:159-161`); delete-vs-archive is server-decided by referencing history (`:184-267`).
- `EmployeeService.EnsureBranchAsync` (`:1003-1008`) rejects inactive branches on create; on update, inactive assignment is tolerated only when deactivating without changing assignment/role (`:231-241`).
- `EmployeeInvitation` holds one `BranchId`; invitee cannot change role/branch at registration (contracts comment, `EmployeeContracts.cs:7-11`); pending invites follow assignment changes via `ChangeRoleAndBranch` (`EmployeeService.cs:256-261`).
- Seeds: dev dairy-manager seed assigns one branch (`DevelopmentDairyManagerSeedService.cs:30-56`); UAT/global seeds use `BranchId=null` (`DevelopmentUatUserSeedService.cs:59-77`, `UatBootstrapSeedService.cs:101-125`).
- DairyManager permission set (`IdentitySeedService.cs:46-67`): `BranchAccess, OrdersReadBranch, MilkTestsReadBranch, DeliveriesReadBranch, DeliveriesAssignBranch, DairyRead/Manage, CamerasRead/Manage, ReportsDashboard/Operations/MilkTests/Export, RefundReplacementRead/ManageBranch, SetupRefundReplacementRead, UsersRead`. Notably **no** `EmployeesManage` — assignment changes stay Owner/SystemAdmin-only (verified; `EnsureRoleAssignableAsync` gates `SYSTEM_ADMIN` to owners, `EmployeeService.cs:805-816`).

## 3. AuthN/Z and sessions (verified)

- Claims: `role` × N, `permission` × N, `branch_id` × N (`JwtTokenService.cs:43-49`). `BranchScopeRequirement` (`AuthorizationRequirements.cs:111-141`) checks a route `branchId` against claims, GlobalAccess bypasses — but the `Branch(...)` policy is **defined and never applied** to any controller (0 usages found); enforcement is service-level via actor sets. No bypass found: every sampled caller-supplied branch id (route `branchId`, report `BranchIds` filter, catalogue `BranchIds` body) is intersected with actor scope (`ResolveBranchesAsync:315-342` throws on out-of-scope requested branches; catalogue `ResolveBranchesAsync` rejects inactive/archived, `CatalogueService.cs:500-513`; refund out-of-scope is masked as NotFound, `RefundReplacementService.cs:1533-1540`).
- Resolution: `ToAuthUserResult` filters to **active roles only**; branch ids are distinct union across assignments. Inactive-role assignments silently drop out of scope.
- Session effect of assignment changes: recomputed on login, OTP-verify login, `GetCurrentUserAsync`, and **every refresh-token rotation** (`AuthenticationService.cs:118-135`). Existing access tokens keep stale scope until expiry/refresh — no forced revocation path for assignment change exists (verified; `UpdateAsync` touches no sessions). Assumption: access-token lifetime (`JwtOptions.AccessTokenMinutes`) bounds the staleness window.
- Missing/inactive/archived/unassigned ids: missing → service `NotFoundException`/`ValidationAppException` (e.g. `GetBranchAsync`, `DairyService.RequireBranchAsync` requires `IsActive`); archived → excluded from lists/selectors, historical rows retained; unassigned id in claims → simply never matches `Contains` (deny by default); empty `BranchIds` + no global → `ValidateActor`/service guards reject (`ReportService.cs:376`, `DeliveryService.cs:1337`).

## 4. Branch-scoped operations (verified pattern per area)

| Area | Enforcement today | Multi-branch delta |
|---|---|---|
| Orders (staff read) | `actor.BranchIds.Contains(order.BranchId)` (`OrderServices.cs:271`) | None — set-membership already generalizes |
| Deliveries/assignment/batches | Query filters + `EnsureBranchAccess` on every mutation (`DeliveryService.cs:103-104,208,288,308,358,408,933,995,1332-1345`) | None |
| Subscriptions | Customer-owned, no staff branch filter found (only allocation branch at creation, `SubscriptionService.cs:77`); staff visibility flows via orders/deliveries/reports | None, but confirm business expectation |
| Replacement deliveries | Vacated-date guard treats vacation/skipped dates as conflicts (`RefundReplacementService.cs:1395-1438`); staff access via `EnsureStaffAccess` set check | None |
| Refund/replacement + milk testing | `EnsureStaffAccess` set check; milk-test queries filtered by set (`MilkTestService.cs:111,618,627`) | None |
| Catalogue availability/inventory | Admin-global (`CatalogueManage`); branch links validated active/non-archived, archived links preserved (`CatalogueService.cs:500-540`); duplicates rejected (`:640-643`) | None; note DairyManager lacks `CatalogueManage`, so no change |
| Dairy production/usage | `RequireBranch` set check + active-branch existence (`DairyService.cs:274-287`) | None |
| Reports/exports | `ResolveBranchesAsync` intersects request filter with actor scope; audit report scopes to own + in-scope users' actions (`ReportService.cs:27-50,155-310`) | None; multi-branch manager automatically sees union |
| Employee admin | Owner/SystemAdmin only; single-branch write path (see §2) | **Change required** (contracts + service + invitation) |
| Notifications/audit history | Scoped reads via same actor pattern; writes attribute `actorUserId` verbatim | None; audit already records assignment deltas (`ActionRoleChanged/ActionBranchChanged`) |

## 5. Flutter employee management (verified)

- Create (`employee_screens.dart:438-651`): role dropdown + single `DropdownButtonFormField<EmployeeBranchOption>` (`:563-585`), `branchOptions` from `GET admin/employees/branches`; SystemAdmin hides branch.
- Edit (`:653-860`): same single dropdown (`:800-818`), helper text "Changing the branch…".
- List/detail (`:184-188,335`): single `branchName` label; models carry single `branchId/branchName` (`employee_models.dart:131-180,268-309`); invitation-verify screen shows read-only branch (`:389-424`).
- Session model already multi-branch: `branchIds: List<int>` + `branchDetails` (`auth_repository.dart:43-152`); staff home tiles use `branchIds.first` (`role_home_screen.dart:1881,1966,2226-2248`).
- Recommendation (smallest UI change): keep single-dropdown for all roles; **only when role == DairyManager** show a multi-select (checkbox list / FilterChip wrap) bound to `branchIds`; detail shows comma-joined names; other roles byte-identical. Backend remains authoritative.

## 6. Recommended implementation design

- **Schema**: reuse `UserRole`; no new table. Constraint already correct (unique `(UserId,RoleId,BranchId)` when branched). Recommendation: add a check/index-level guard that non-branch roles never carry `BranchId` (currently code-convention only), and keep `EmployeeInvitation` single-branch **only if** invites stay single-branch (see decision D2).
- **Default vs selected branch**: Recommendation — no stored default. Backend authorizes against the **set**; Flutter keeps a *selected operational branch* as pure client presentation state (defaulting to first assigned), sending per-branch requests as today. Rationale: every service already accepts any in-scope id; a stored default adds write paths with no authorization value.
- **Per-request enforcement**: unchanged pattern — controllers pass all `branch_id` claims; services keep `HasGlobalAccess || BranchIds.Contains(id)`; report/catalogue filters keep intersecting caller input with actor scope. New code must follow `ResolveBranchesAsync` semantics (requested ∩ allowed, throw on outside).
- **Session/cache effects**: scope refreshes at next refresh-token rotation; access-token window is the staleness bound. Recommendation: on assignment change, revoke the employee's sessions/refresh tokens (same primitive as `DeleteAsync:402-411`) to make revocation immediate; Flutter re-fetches session (`GetCurrentUser`) on resume. Mobile caches `branchIds` in secure storage — must refresh from `GET /me` after the change, never merge locally.
- **Archived/inactive branches**: keep current semantics — archived excluded from assignment options and new activity; historical reports keep resolving names; deactivating a branch must not strand managers: recommendation is to **retain** the assignment row (read-only history) while scope computation ignores inactive branches for new actions (mirrors `UpdateAsync:231-241` tolerance). Requires explicit business sign-off (D3).
- **API changes**: `CreateEmployeeRequest`/`UpdateEmployeeRequest` gain `BranchIds` (recommendation: additive, keep `BranchId` as deprecated single-write alias rejected when both present); `EmployeeResult`/`EmployeeInvitationVerificationResult` gain `BranchIds + BranchDetails`; invitation flow gains either multi-branch invites or one-invite-per-branch (D2). `GetBranchOptions` unchanged.
- **Flutter changes**: models gain `branchIds/branchDetails` (keep `branchId` getter = first for compat during migration); DairyManager-only multi-select; staff surfaces replace `branchIds.first` with selected-branch state + branch switcher where a screen is single-branch (delivery-management, refund list, cameras).
- **Audit**: extend `EMPLOYEE.BRANCH_CHANGED` snapshot to old-set → new-set; keep actor attribution; keep delete→archive behavior.
- **Migration strategy**: zero schema migration (unique indexes already allow multiples); seed/backfill none (existing single rows untouched); feature-flagged writes optional. Assumption: SQL Server filtered-index semantics match current model snapshot — verify with `has-pending-model-changes` at implementation time.

## 7. Risks

1. `UpdateAsync`/`ToEmployeeResult`/`FirstOrDefault` single-assignment assumptions silently drop extra rows today — any partial implementation that writes multi but reads single **loses** assignments. Must change read+write atomically.
2. Stale access tokens retain revoked branch scope until refresh — must decide revoke-vs-window (D4).
3. `BranchScopeRequirement` route policy is dead code; anyone wiring `Branch(...)` onto a route with a different route-key silently fails closed (deny) — document, don't rely on it.
4. Employee list/report projections enumerate `UserRoles` per branch — multi rows multiply naturally, but UI/tests asserting single `branchId` will break (identified: `guest_router_test`, employee tests, `PageEmployees` row shape needs `BranchIds` decision).
5. Concurrent assignment edits: `UpdateAsync` read-modify-write on `UserRoles` without concurrency token — two admins can clobber; recommend row-version or serializable transaction (pattern exists: `ExecuteSerializableAsync`).
6. Scope creep to other roles: `DeliveryStaff`/`DeliveryManager` assignment flows share the same code path — multi-select must be role-gated to DairyManager in both API validation and Flutter.

## 8. Open business decisions

- D1: May a DairyManager also hold a second role (e.g. DeliveryManager) with different branches? (Schema allows; UX/audit implications.)
- D2: One invitation carrying N branches vs N single-branch invitations?
- D3: Deactivated/archived branch while assigned — retain read-only row vs auto-unassign?
- D4: Revoke sessions on assignment change (immediate) vs accept token-lifetime window?
- D5: Cap on branches per manager? Empty-set (all-unassigned) state allowed?
- D6: DairyManager employee-list visibility: all employees vs only in-scope branches' staff?

## 9. Staged plan (implementation, not started)

1. Contracts + `EmployeeService` multi-branch write/read for DairyManager only, session revocation, audit set-deltas; integration tests (scope matrix).
2. Invitation flow decision (D2) + verify/complete honoring branch sets.
3. Flutter models + create/edit multi-select (DairyManager-gated) + detail labels.
4. Staff surfaces: selected-branch state replacing `branchIds.first` per screen.
5. Regression battery (existing 1100+ Flutter tests, backend suites, EF model check) + concurrency test for parallel assignment edits.

## 10. Test matrix (for implementation)

- Auth: manager(A,B) reads A+B data; unassigned C → 404/403 with no oracle difference; global roles unaffected; inactive-role assignment yields no scope; missing id → NotFound; archived → excluded from options, history intact.
- Concurrency: parallel assign/unassign same manager; parallel vacation-style serializable conflicts as reference.
- Regression: single-branch employees byte-identical behavior; order/delivery/refund/subscription/tax/financial rules untouched; report exports equal scoping; invitation single-branch legacy path.
