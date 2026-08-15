# Traceability

| Requirement area | Phase 1 implementation | Evidence |
|---|---|---|
| Layered backend | Domain, Application, Infrastructure, and API projects retain one-way dependencies | `Backend/DoodhDirect.slnx`; `Document/ARCHITECTURE.md` |
| Identity persistence | Separate customer/employee-capable users, roles, permissions, assignments, OTP challenges, device sessions, refresh tokens, and audits | `Backend/src/DoodhDirect.Domain/Identity/IdentityEntities.cs`; `Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs` |
| Database evolution | Phase 1 migration adds OTP and session persistence and refresh-token rotation metadata | `Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260815201425_Phase1IdentitySessions.cs` |
| Password authentication | Email/mobile login, PBKDF2 verification, active-account checks, and failed-login auditing | `Backend/src/DoodhDirect.Infrastructure/Identity/AuthenticationService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/AuthenticationServiceTests.cs` |
| Registration | Contact uniqueness, customer-role assignment, password hashing, session creation, and token issuance | `Backend/src/DoodhDirect.Infrastructure/Identity/AuthenticationService.cs`; `Backend/src/DoodhDirect.Api/Controllers/AuthController.cs` |
| OTP authentication | Request, six-digit verification, expiration, failed-attempt limit, rate limit, registration purpose, login purpose, and audit persistence | `Backend/src/DoodhDirect.Infrastructure/Identity/OtpService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/AuthenticationServiceTests.cs` |
| Sessions and refresh | Device-bound sessions, hashed refresh tokens, rotation, replacement tracking, expiry, logout revocation, replay detection, and session-wide revocation | `Backend/src/DoodhDirect.Infrastructure/Identity/AuthenticationService.cs`; `Backend/src/DoodhDirect.Infrastructure/Identity/JwtTokenService.cs` |
| RBAC seed | Eight canonical roles, nine permissions, role-permission mappings, owner global permission, and repeatable seeding | `Backend/src/DoodhDirect.Application/Identity/AuthenticationContracts.cs`; `Backend/src/DoodhDirect.Infrastructure/Identity/IdentitySeedService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/IdentitySeedAndAuthorizationAuditTests.cs` |
| Permission authorization | Dynamic exact-permission policies | `Backend/src/DoodhDirect.Api/Authorization/AuthorizationRequirements.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/AuthorizationHandlerTests.cs` |
| Branch authorization | Route branch matching with explicit `ACCESS.GLOBAL` bypass | `Backend/src/DoodhDirect.Api/Authorization/AuthorizationRequirements.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/AuthorizationHandlerTests.cs` |
| Secure API boundary | JWT validation, authenticated fallback policy, anonymous authentication issuance endpoints, and protected logout/current-user endpoints | `Backend/src/DoodhDirect.Api/Program.cs`; `Backend/src/DoodhDirect.Api/Controllers/AuthController.cs` |
| Authorization audit | Persistent challenge and forbidden events with standard 401/403 envelopes; audit failures do not alter denial behavior | `Backend/src/DoodhDirect.Api/Authorization/AuditingAuthorizationMiddlewareResultHandler.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/IdentitySeedAndAuthorizationAuditTests.cs` |
| OpenAPI | Reusable HTTP bearer/JWT scheme, anonymous-operation exceptions, and protected-operation requirements | `Backend/src/DoodhDirect.Api/Program.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/UnitTest1.cs` |
| Flutter authentication | Registration, password login, OTP, secure persistence, refresh restoration, current user, logout, expiration handling, and role-derived navigation | `mobile/lib/features/auth`; `mobile/test/widget_test.dart` |
| Flutter transport | JSON GET/POST, bearer headers, success envelopes, structured errors, and fallback errors | `mobile/lib/core/network/api_client.dart`; `mobile/test/api_client_test.dart` |
| Phase boundary | No Phase 2 customer/address or later business workflows implemented | Repository source tree; `README.md` |

## Acceptance Evidence

Evidence confirmed during Phase 1 implementation and release validation:

- Backend Release restore/build: PASS, zero warnings and zero errors.
- Backend solution tests: PASS, 38 tests total: 4 domain and 34 API integration.
- Idempotent EF migration script: PASS, generated at `artifacts/sql/DoodhDirect.sql` with non-zero output.
- SQL Server Express migration application: PASS, database `DoodhDirect` on `DESKTOP-6LU1CLD\\SQLEXPRESS` was already current through `20260815201425_Phase1IdentitySessions`.
- Flutter analysis: PASS, no findings.
- Flutter tests: PASS, 7 tests.
- Flutter web Release build: PASS, `mobile/build/web` generated.
- Generated OpenAPI bearer and anonymous-operation contract assertions: PASS within the API integration suite.

The Flutter web build emits non-blocking toolchain warnings for the secure-storage package's WebAssembly dry run and an unused Cupertino icon font. The JavaScript web artifact still builds successfully.

## Deployment Blockers

- `UnconfiguredOtpDeliveryService` intentionally does not send OTP messages; configure a production `IOtpDeliveryService` implementation.
- The checked-in JWT signing key is a non-production placeholder; inject a secret with at least 32 random characters.
- Password reset, external identity providers, and role/permission administration APIs remain outside the implemented Phase 1 surface.
