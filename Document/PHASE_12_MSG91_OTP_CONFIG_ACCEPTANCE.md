# MSG91 OTP Configuration — Fix and Acceptance Report

**Status:** PASS

This report records the diagnosis and remediation of two defects blocking the MSG91 OTP provider configuration screen: a client-side script 404 (removed obsolete MSG91 SDK reference) and a server-side GET 500 (EF SQL translation failure in `LoadAsync`). The fix is verified end-to-end, including live HTTP verification of all three admin endpoints against the Development API.

## 1. Root-Cause Diagnosis

- **Defect A (client):** [`mobile/web/index.html`](../mobile/web/index.html) referenced the MSG91 Widget SDK script `<script src="https://control.msg91.com/app/assets/scripts/msg91_web_otp.js?widgetId=...">`, which returned 404 and was never used by the Flutter OTP flow.
- **Defect B (server):** [`OtpProviderConfigurationService.LoadAsync`](../Backend/src/DoodhDirect.Infrastructure/OtpProvider/OtpProviderConfigurationService.cs) translated a `Select(p => p.Key)` over an EF Core `IQueryable` into SQL that SQL Server rejected, throwing a `SqlException` and producing HTTP 500 on every `GET /api/v1/admin/setup/otp-provider`.
- **Cosmetic follow-up:** after the above fix, `PUT` responses did not reflect rows created during the same request because the in-memory snapshot dictionary was only updated in the update branch of `SetValueAsync`.

## 2. Client Fix — Removed Obsolete SDK Script

- Removed the dead MSG91 `control.msg91.com` script tag from [`mobile/web/index.html`](../mobile/web/index.html:46).
- Retained a short HTML comment documenting that MSG91 integration is server-side via the Widget API v5 and that the AuthKey is never shipped to the browser.
- Razorpay's legitimate checkout script remains untouched.

## 3. Server Fix — SQL-Translable Load

- Replaced the untranslatable projection in [`OtpProviderConfigurationService.LoadAsync`](../Backend/src/DoodhDirect.Infrastructure/OtpProvider/OtpProviderConfigurationService.cs) with a SQL-translatable query and an in-memory dictionary for lookups.
- Verified that `GET` now returns HTTP 200 with `{"provider":"MSG91","configured":false,"status":"Not Configured"}` when no rows exist, and the configured state (without the AuthKey) when rows exist.

## 4. Stale PUT Readback Fix

- Updated [`SetValueAsync`](../Backend/src/DoodhDirect.Infrastructure/OtpProvider/OtpProviderConfigurationService.cs) so the in-memory snapshot is updated in both the create and update branches.
- Verified that `PUT /api/v1/admin/setup/otp-provider` now returns the newly persisted `widgetId` and `environment` in the response body immediately.

## 5. SQL Server Regression Tests

- Added [`OtpProviderConfigurationServiceSqlServerTests.cs`](../Backend/tests/DoodhDirect.Api.IntegrationTests/OtpProviderConfigurationServiceSqlServerTests.cs) covering:
  - `GetAsync_OnSqlServer_WithZeroRows_ReturnsNotConfigured`
  - `GetAsync_OnSqlServer_WithConfiguredRows_ReturnsConfiguredState`
  - `UpdateAsync_OnSqlServer_PersistsProtectedAuthKeyAndReadsBack`
- All three passed against the real SQL Server Express instance.

## 6. Live HTTP Verification (Development API)

All three admin endpoints were exercised with an authenticated OWNER token against `http://localhost:5209`:

- `GET /api/v1/admin/setup/otp-provider` → 200 `{"provider":"MSG91","configured":true,"widgetId":"widget-verify","environment":"Test","status":"Configured"}` (persisted state read-back).
- `PUT /api/v1/admin/setup/otp-provider` → 200, persisted `widget-verify-2` / `Production`, response reflected the saved state.
- `POST /api/v1/admin/setup/otp-provider/test` → 200 `"Configuration verified. A test OTP was sent successfully."` (Development OTP provider emitted `123456`).

## 7. Backend Build and Test Record

- Backend Release build: **PASS**, zero warnings and zero errors.
- Backend test suite: **781 passed** (domain + API integration), including the three new SQL Server regression tests and the full `OtpProviderConfigurationControllerTests` / `OtpProviderConfigurationServiceTests` coverage (permissions, authorization header forwarding, protection of the AuthKey, environment normalization, disabled/enabled/config-updated audit actions).

## 8. Flutter Analysis and Test Record

- `flutter analyze`: **PASS**, no issues found.
- `flutter test`: **491 passed**, including the new [`otp_config_models_repository_test.dart`](../mobile/test/otp_config_models_repository_test.dart) and [`otp_platform_launcher_test.dart`](../mobile/test/otp_platform_launcher_test.dart).

## 9. Web Release Build and Bundle Verification

- `flutter build web --release`: **PASS**, `build/web` generated. Non-fatal toolchain warnings (Wasm dry-run for `flutter_secure_storage_web`, missing Cupertino icon font) are pre-existing and do not block the JavaScript artifact.
- Bundle inspection: no `control.msg91.com` SDK script present; no `authkey`/`api_key`/secret patterns in the shipped bundle; only the intended server-side-MSG91 explanatory comments remain in the built `index.html`.

## 10. Secret Scan and Hygiene

- Source scan of `OtpProvider` infrastructure and `appsettings*.json` found **no hard-coded secrets**.
- The MSG91 AuthKey is stored protected (sensitive flag) and is never returned by `GET` or logged.
- Scratch verification artifacts (`mobile/*-test-run.txt`, `msgtmp.html`) were removed.

## 11. Documentation and Contract Synchronization

- API specification, UI specification, state machines, security/operations, OpenAPI, assumptions, traceability, and the roadmap were synchronized for the MSG91 OTP provider configuration feature (GET/PUT/test endpoints, `SETUP.OTP_PROVIDER.READ` / `SETUP.OTP_PROVIDER.MANAGE` permissions, sensitive-value handling, and server-side Widget API v5 integration).
- OpenAPI document parsing and local reference resolution remain valid.

## 12. Scope Boundary — Phase 13 Not Started

Phase 13 Hardening (pen testing, performance testing, backup/restore, monitoring, error recovery, app-store release process, production runbooks) remains **NOT STARTED** and was not modified by this work. Android/iOS native release artifacts remain an environment prerequisite for a platform-complete release sign-off.
