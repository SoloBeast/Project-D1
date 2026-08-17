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

## Phase 9 — Doorstep Testing

| Requirement area | Phase 9 implementation | Evidence |
|---|---|---|
| Delivery-owned test lifecycle | One `MilkTest` is allowed per delivery; request, completion, and terminal customer decision are persisted | `Backend/src/DoodhDirect.Domain/MilkTesting/MilkTestEntities.cs`; `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs`; `Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260817113835_Phase9DoorstepTesting.cs` |
| Customer ownership | Customers can request/read only the milk test for their own delivery and can confirm or reject only their own completed test | `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestServiceTests.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestsControllerTests.cs` |
| Assigned-staff and branch authorization | Staff operations require current delivery assignment, matching branch scope, or explicit global access | `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs`; `Backend/src/DoodhDirect.Application/Identity/AuthenticationContracts.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestServiceTests.cs` |
| Image validation and storage | JPEG, PNG, and WebP signatures are detected and checked against the declared MIME type and configured size limit; bytes are stored through the provider-neutral media abstraction | `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestImageValidator.cs`; `Backend/src/DoodhDirect.Infrastructure/MilkTesting/LocalMediaStorage.cs`; `Backend/src/DoodhDirect.Application/MilkTesting/MilkTestContracts.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestMediaTests.cs` |
| Metadata-only persistence | SQL persistence stores image metadata and an external storage key, with no image-byte column | `Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260817113835_Phase9DoorstepTesting.cs`; `Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/DoodhDirectDbContextModelSnapshot.cs` |
| Completion gates | Completion requires `Arrived` delivery status, at least one valid reading, and at least one image | `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs`; `Backend/tests/DoodhDirect.Domain.Tests/MilkTestDomainTests.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestServiceTests.cs` |
| Reading precision and uniqueness | Numeric readings use `decimal(18,6)` and case-insensitive codes are unique per test | `Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260817113835_Phase9DoorstepTesting.cs`; `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestServiceTests.cs` |
| Customer-safe disclosure | Customer DTOs expose lifecycle, decision, timestamp, and completed-test images, but omit numeric readings and staff remarks | `Backend/src/DoodhDirect.Application/MilkTesting/MilkTestContracts.cs`; `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs`; `mobile/lib/features/milk_testing/milk_test_screens.dart` |
| Protected image access | Image content is streamed through an authenticated ownership/assignment-scoped endpoint and remains hidden from customers until completion | `Backend/src/DoodhDirect.Api/Controllers/MilkTestsController.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestMediaTests.cs`; `mobile/lib/features/milk_testing/milk_test_repository.dart` |
| Audit coverage | Request, creation, image upload, completion, confirmation, and rejection actions are persisted | `Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestServiceTests.cs` |
| Flutter workflows | Customer request/decision and staff upload/readings/completion flows use server-authoritative refresh and explicit loading, error, offline, and terminal states | `mobile/lib/features/milk_testing/milk_test_controller.dart`; `mobile/lib/features/milk_testing/milk_test_screens.dart`; `mobile/test/milk_test_controller_test.dart`; `mobile/test/milk_test_screens_test.dart` |
| API contract | Customer, staff, mutation, multipart upload, and protected media routes are documented with role-specific schemas | `Backend/src/DoodhDirect.Api/Controllers/MilkTestsController.cs`; `Document/05_API_Specification.md`; `Document/11_openapi_starter.yaml` |

## Phase 10 — Live Dairy Cameras

| Requirement area | Phase 10 implementation | Evidence |
|---|---|---|
| Metadata-only persistence | Branch-owned `Camera` and one-to-one `CameraStream` store bounded display, visibility, protocol, and opaque provider metadata only | `Backend/src/DoodhDirect.Domain/Cameras/CameraEntities.cs`; `Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260817132613_Phase10LiveDairyCameras.cs` |
| Public privacy boundary | Public list returns active/public cameras only; public DTOs omit branch, internal, provider, administration, and timestamp fields | `Backend/src/DoodhDirect.Application/Cameras/CameraContracts.cs`; `Backend/src/DoodhDirect.Infrastructure/Cameras/CameraService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/CameraServiceTests.cs` |
| Enumeration resistance | Unknown, inactive, and private stream requests share the same not-found behavior | `Backend/src/DoodhDirect.Infrastructure/Cameras/CameraService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/CameraServiceTests.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/CamerasControllerTests.cs` |
| Permission enforcement | Public viewing, managed reads, and mutations require `CAMERAS.VIEW_PUBLIC`, `CAMERAS.READ`, and `CAMERAS.MANAGE` respectively | `Backend/src/DoodhDirect.Application/Identity/AuthenticationContracts.cs`; `Backend/src/DoodhDirect.Api/Controllers/CamerasController.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/CamerasControllerTests.cs` |
| Branch authorization | Managed queries and mutations are branch-filtered; reassignment requires both source and destination scope; `ACCESS.GLOBAL` is explicit | `Backend/src/DoodhDirect.Infrastructure/Cameras/CameraService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/CameraServiceTests.cs` |
| Stream abstraction | `ICameraStreamGateway` provides capability, availability, and short-lived descriptor issuance independent of provider | `Backend/src/DoodhDirect.Application/Cameras/CameraContracts.cs`; `Backend/src/DoodhDirect.Infrastructure/Cameras/CameraStreamGateways.cs` |
| Production fail-closed | Unconfigured gateway refuses issuance; DevelopmentMock is HLS/HTTPS-only and prohibited outside Development | `Backend/src/DoodhDirect.Infrastructure/Cameras/CameraStreamOptions.cs`; `Backend/src/DoodhDirect.Infrastructure/Cameras/CameraStreamGateways.cs`; `Backend/src/DoodhDirect.Infrastructure/DependencyInjection.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/CameraServiceTests.cs` |
| Audit coverage | Camera creation and update persist `CAMERA.CREATE` and `CAMERA.UPDATE` audit events | `Backend/src/DoodhDirect.Infrastructure/Cameras/CameraService.cs`; `Backend/tests/DoodhDirect.Api.IntegrationTests/CameraServiceTests.cs` |
| Flutter transport and state | Repository uses exact authenticated routes; controller maps unauthorized, unavailable, offline, expired, and stale descriptor behavior | `mobile/lib/features/cameras/camera_repository.dart`; `mobile/lib/features/cameras/camera_controller.dart`; `mobile/test/camera_repository_test.dart`; `mobile/test/camera_controller_test.dart` |
| Flutter workflows | Public list, viewer, and management screens provide required loading, empty, failure, retry, unsupported-protocol, and development-warning states | `mobile/lib/features/cameras/camera_screens.dart`; `mobile/test/camera_screens_test.dart` |
| API contract | Public and administration paths, parameters, privacy descriptions, requests, schemas, and error responses are synchronized | `Backend/src/DoodhDirect.Api/Controllers/CamerasController.cs`; `Document/05_API_Specification.md`; `Document/11_openapi_starter.yaml` |

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

### Phase 9 Acceptance Evidence

Evidence confirmed during Phase 9 implementation and release validation:

- Backend Release build: PASS, zero warnings and zero errors.
- Backend solution tests: PASS, 292 tests total: 57 domain and 235 API integration.
- Flutter analysis: PASS, no issues found.
- Flutter tests: PASS, 115 tests, including two OpenAPI contract tests.
- OpenAPI document validation: PASS; YAML parsing, local component references, and templated route parameters were verified by `mobile/test/openapi_document_test.dart`.
- EF model/snapshot alignment: PASS, with no pending model changes.
- Phase 9 migration script: PASS, generated and inspected at `Backend/artifacts/sql/Phase9DoorstepTesting.sql`.
- SQL Server Express migration: PASS on `DoodhDirect` at `DESKTOP-6LU1CLD\\SQLEXPRESS`; `20260817113835_Phase9DoorstepTesting` applied successfully and the repeat update was idempotent.
- Physical SQL Server schema: PASS; all expected Phase 9 tables, columns, nullability, `decimal(18,6)` precision, constraints, indexes, and restrictive `NO_ACTION` foreign keys were present.
- Image-byte exclusion: PASS; catalog inspection found zero `binary`, `varbinary`, or `image` columns in the Phase 9 tables.
- Scope boundary: PASS; Phase 10 camera functionality remains unimplemented.

Detailed evidence is recorded in `Document/PHASE_9_ACCEPTANCE.md`.

### Phase 10 Acceptance Evidence

Evidence confirmed during Phase 10 implementation and final release validation:

- Focused backend camera tests: PASS, 23 tests across service and controller coverage.
- Focused Flutter camera tests: PASS, 30 tests across models, repository, controller, and widgets.
- Backend Release build: PASS, zero warnings and zero errors.
- Backend solution tests: PASS, 315 tests total: 57 domain and 258 API integration.
- Flutter analysis: PASS, no issues found.
- Flutter tests: PASS, 145 tests, including camera and OpenAPI contract coverage.
- Flutter web Release build: PASS; the JavaScript artifact was generated with non-blocking secure-storage WebAssembly dry-run and unused Cupertino font warnings.
- OpenAPI validation: PASS; YAML parsing, local component references, and templated route parameters were verified.
- EF model/snapshot alignment: PASS, with no pending model changes.
- Phase 10 migration script: PASS, generated and inspected at `Backend/artifacts/sql/Phase10LiveDairyCameras.sql`.
- SQL Server Express migration: PASS on `DoodhDirect` at `DESKTOP-6LU1CLD\\SQLEXPRESS`; `20260817132613_Phase10LiveDairyCameras` applied successfully and the repeat update was idempotent.
- Physical SQL Server schema: PASS; both camera tables have the expected bounded columns, nullability, defaults, checks, indexes, and restrictive `NO_ACTION` foreign keys.
- Prohibited-storage exclusion: PASS; catalog inspection found zero credential, internal-address, hardware, recording, raw/private/playback URL, or binary columns.
- Scope boundary: PASS; Phase 11 notification functionality has not started.

Detailed evidence is recorded in `Document/PHASE_10_ACCEPTANCE.md`.

## Deployment Blockers

- `UnconfiguredOtpDeliveryService` intentionally does not send OTP messages; configure a production `IOtpDeliveryService` implementation.
- The checked-in JWT signing key is a non-production placeholder; inject a secret with at least 32 random characters.
- Password reset, external identity providers, and role/permission administration APIs remain outside the implemented Phase 1 surface.
