# Phase 9 Acceptance

**Status:** PASS

This checkpoint is limited to Phase 9 doorstep milk testing and regression validation. Phase 10 camera functionality has not been started.

## Workflow

- A customer can request one milk test for an eligible delivery they own.
- The currently assigned delivery employee can read and operate the requested test within their branch scope, with explicit global-access bypass where authorized.
- Staff can upload a validated image, record configurable numeric readings and optional remarks, and complete the test only after the delivery reaches `Arrived`.
- Completion requires at least one persisted image and one valid reading.
- The customer can view completed-test images and completion time, then confirm or reject once with optional remarks.
- Confirmation and rejection are terminal and mutually exclusive.
- Duplicate requests, stale assignments, terminal deliveries, invalid uploads, premature completion, and conflicting decisions are rejected.

Evidence: [MilkTestService.cs](../Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs), [MilkTestsController.cs](../Backend/src/DoodhDirect.Api/Controllers/MilkTestsController.cs), [MilkTestDomainTests.cs](../Backend/tests/DoodhDirect.Domain.Tests/MilkTestDomainTests.cs), and [MilkTestServiceTests.cs](../Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestServiceTests.cs).

## Authorization and Disclosure

- Customer operations require the canonical `MILK_TESTS.REQUEST_OWN`, `MILK_TESTS.READ_OWN`, or `MILK_TESTS.DECIDE_OWN` permission and delivery ownership.
- Staff operations require `MILK_TESTS.OPERATE_ASSIGNED`, current delivery assignment, and matching branch scope unless `ACCESS.GLOBAL` is present.
- Staff DTOs include numeric parameters and staff remarks.
- Customer DTOs omit numeric parameters and staff remarks.
- Customer image metadata and content remain unavailable until the test is completed.
- Image bytes are served only through an authenticated, ownership/assignment-scoped content endpoint.

Evidence: [AuthenticationContracts.cs](../Backend/src/DoodhDirect.Application/Identity/AuthenticationContracts.cs), [MilkTestContracts.cs](../Backend/src/DoodhDirect.Application/MilkTesting/MilkTestContracts.cs), [MilkTestService.cs](../Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs), and [MilkTestsControllerTests.cs](../Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestsControllerTests.cs).

## Media and Persistence

- `IMediaStorage` isolates application behavior from the physical media provider.
- Development image bytes are stored by the local filesystem provider.
- Upload validation detects actual JPEG, PNG, or WebP signatures, requires the declared MIME type to match, and enforces the configured size limit.
- A failed database persistence operation triggers best-effort deletion of the newly stored media object.
- SQL Server stores image metadata, uploader identity, timestamps, and the external storage key; it does not store image bytes.
- Reading values are configured as `decimal(18,6)`.
- Unique indexes enforce one milk test per delivery, unique image storage keys, and one normalized reading code per test.
- Check constraints enforce positive image size and coherent lifecycle timestamps and decisions.
- Foreign-key deletes are restrictive.

Evidence: [MilkTestContracts.cs](../Backend/src/DoodhDirect.Application/MilkTesting/MilkTestContracts.cs), [LocalMediaStorage.cs](../Backend/src/DoodhDirect.Infrastructure/MilkTesting/LocalMediaStorage.cs), [MilkTestImageValidator.cs](../Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestImageValidator.cs), [20260817113835_Phase9DoorstepTesting.cs](../Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260817113835_Phase9DoorstepTesting.cs), and [MilkTestMediaTests.cs](../Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestMediaTests.cs).

## Audit

The service persists the following lifecycle actions:

```text
MILK_TEST.REQUEST
MILK_TEST.CREATE
MILK_TEST.IMAGE_UPLOAD
MILK_TEST.COMPLETE
MILK_TEST.CONFIRM
MILK_TEST.REJECT
```

Evidence: [MilkTestService.cs](../Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs) and [MilkTestServiceTests.cs](../Backend/tests/DoodhDirect.Api.IntegrationTests/MilkTestServiceTests.cs).

## Flutter

- Customer UI supports request, waiting, completed-image viewing, completion timestamp, confirmation, rejection, terminal decisions, and retry/error states.
- Staff UI supports camera/gallery selection, upload progress and failure states, configurable reading rows, remarks, completion prerequisites, and stale-assignment or terminal-delivery errors.
- Controllers refresh from the server after mutations instead of treating local optimistic state as authoritative.
- Customer models and screens do not expose numeric readings or staff remarks.

Evidence: [milk_test_repository.dart](../mobile/lib/features/milk_testing/milk_test_repository.dart), [milk_test_controller.dart](../mobile/lib/features/milk_testing/milk_test_controller.dart), [milk_test_screens.dart](../mobile/lib/features/milk_testing/milk_test_screens.dart), [milk_test_controller_test.dart](../mobile/test/milk_test_controller_test.dart), and [milk_test_screens_test.dart](../mobile/test/milk_test_screens_test.dart).

## Verification Record

Final Phase 9 release validation:

- Backend Release build: PASS, zero warnings and zero errors.
- Backend solution tests: PASS, 292 tests total: 57 domain and 235 API integration.
- Flutter analysis: PASS, no issues found.
- Flutter tests: PASS, 115 tests, including two repository-owned OpenAPI contract tests.
- OpenAPI validation: PASS; the YAML parses, every local component reference resolves, and every templated route parameter is declared.
- EF model/snapshot alignment: PASS, no model changes remain pending after the Phase 9 migration.
- Migration SQL inspection: PASS; `Backend/artifacts/sql/Phase9DoorstepTesting.sql` contains only the Phase 9 transaction, tables, constraints, indexes, migration-history insert, and commit.
- SQL Server Express application: PASS on database `DoodhDirect` at `DESKTOP-6LU1CLD\\SQLEXPRESS`; migration `20260817113835_Phase9DoorstepTesting` was applied successfully and a second update confirmed the database was already current.
- Physical schema inspection: PASS for `dbo.MilkTest`, `dbo.MilkTestParameter`, and `dbo.MilkTestImage`; expected columns, nullability, `decimal(18,6)`, checks, unique/supporting indexes, and restrictive `NO_ACTION` foreign keys were present.
- Image-byte exclusion: PASS; the SQL Server catalog reported zero `binary`, `varbinary`, or `image` columns across the Phase 9 tables.

## Production Configuration

Before production deployment:

- Replace local filesystem media storage with an operationally managed `IMediaStorage` provider where required.
- Configure durable media retention, backup, access monitoring, and cleanup policies.
- Set an environment-appropriate upload-size limit and filesystem/object-store location.
- Retain authenticated application-mediated image access or use equivalently scoped short-lived provider URLs.
- Treat manually entered Phase 9 readings as indicative doorstep results, not accredited laboratory results.

## Scope

Phase 9 adds doorstep milk testing only. It does not implement Phase 10 live camera feeds, CCTV playback, camera health, or signed camera-stream access.
