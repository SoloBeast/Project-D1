# DoodhDirect Phase 0

DoodhDirect is a farm-to-home dairy platform. This repository contains the Phase 0 foundation only: a layered ASP.NET Core API, SQL Server/EF Core persistence scaffolding, and one role-aware Flutter application for Android, iOS, and web.

The specifications in `Document/01_PRD.md` through `Document/09_Development_Roadmap.md` are authoritative. No Phase 0 conflict was identified among them. EF Core migrations are authoritative for the implemented schema; the SQL starter document remains conceptual.

## Repository layout

- `Backend/src/DoodhDirect.Domain`: entities and domain invariants
- `Backend/src/DoodhDirect.Application`: application contracts and standard API envelopes
- `Backend/src/DoodhDirect.Infrastructure`: SQL Server, EF Core, health checks, and migrations
- `Backend/src/DoodhDirect.Api`: HTTP pipeline, JWT validation, authorization, OpenAPI, logging, and health endpoints
- `Backend/tests`: domain and API integration tests
- `mobile`: role-aware Flutter application
- `.github/workflows/ci.yml`: backend and Flutter CI

Dependency direction is `Domain <- Application <- Infrastructure <- API`. Flutter communicates with the API and never accesses SQL Server directly.

## Prerequisites

- .NET SDK 10.0.400, pinned by `global.json`
- Flutter 3.47.0 with Dart 3.13.0
- SQL Server Express with the `SQLEXPRESS` instance running, or another reachable SQL Server configured through environment variables
- Android Studio/Xcode only when running the corresponding mobile target

The verified development environment uses SQL Server Express on `DESKTOP-6LU1CLD\\SQLEXPRESS`. The development connection is environment-specific and is configured in `Backend/src/DoodhDirect.Api/appsettings.Development.json` as `Server=.\\SQLEXPRESS;Database=DoodhDirect;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True`. SQL Server Browser is not required for this local direct named-instance connection. LocalDB is not required or assumed.

## Backend configuration

Shared defaults are in `Backend/src/DoodhDirect.Api/appsettings.json`; the verified SQL Server Express target is in `Backend/src/DoodhDirect.Api/appsettings.Development.json`. Override deployment values through environment variables or a managed secret store:

```text
ConnectionStrings__DoodhDirect=Server=...;Database=DoodhDirect;...;
Authentication__Jwt__Issuer=DoodhDirect.Api
Authentication__Jwt__Audience=DoodhDirect.App
Authentication__Jwt__SigningKey=<at-least-32-random-characters>
```

The checked-in signing key is a non-production placeholder. Never deploy with it. Production should inject secrets from Azure Key Vault or the target platform's secret manager.

Restore and run the API from the repository root:

```powershell
dotnet tool restore
dotnet restore Backend\DoodhDirect.slnx
dotnet run --project Backend\src\DoodhDirect.Api\DoodhDirect.Api.csproj
```

Development endpoints:

- `GET /health/live`: anonymous, dependency-free process liveness
- `GET /health/ready`: anonymous SQL Server readiness
- `GET /openapi/v1.json`: development-only OpenAPI document
- `/scalar/v1`: development-only Scalar API reference

All future API endpoints are protected by the fallback authorization policy unless explicitly marked anonymous. Working OTP, login, user management, role assignment, and permission management belong to Phase 1 and are intentionally not exposed in Phase 0.

## Database migrations

The initial migration has been applied and verified against the development database on `DESKTOP-6LU1CLD\\SQLEXPRESS`:

- Database: `DoodhDirect`
- Migration history: `20260815190759_InitialPhase0Foundation` with EF Core `10.0.11`
- Tables: `User`, `Role`, `Permission`, `UserRole`, `RolePermission`, `RefreshToken`, `AuditLog`, `SystemConfiguration`, and `__EFMigrationsHistory`
- Verification: expected indexes, filtered branch/global uniqueness indexes, `NEWSEQUENTIALID()` public-ID defaults, bounded token-hash columns, foreign keys, and `NO_ACTION` delete behavior were confirmed

Generate the idempotent SQL script without connecting to SQL Server:

```powershell
dotnet ef migrations script --idempotent --project Backend\src\DoodhDirect.Infrastructure\DoodhDirect.Infrastructure.csproj --startup-project Backend\src\DoodhDirect.Api\DoodhDirect.Api.csproj --configuration Release --output artifacts\sql\DoodhDirect.sql
```

Apply migrations only after configuring a reachable SQL Server connection:

```powershell
dotnet ef database update --project Backend\src\DoodhDirect.Infrastructure\DoodhDirect.Infrastructure.csproj --startup-project Backend\src\DoodhDirect.Api\DoodhDirect.Api.csproj
```

The initial migration creates Phase 0 identity, role, permission, refresh-token, audit-log, and system-configuration storage. It uses `bigint` identity keys, GUID public IDs, UTC `datetime2` timestamps, filtered role-assignment uniqueness, restrictive identity/RBAC deletes, and bounded token hashes.

## Flutter application

Resolve packages and run a target from `mobile`:

```powershell
cd mobile
flutter pub get
flutter run -d chrome
```

Use an Android emulator/device or an iOS simulator/device instead of `chrome` for mobile targets. The Phase 0 role picker demonstrates role-aware routing locally; it is explicitly not an authentication implementation.

## Acceptance commands

Run these from the repository root unless a working directory is shown:

```powershell
dotnet restore Backend\DoodhDirect.slnx
dotnet build Backend\DoodhDirect.slnx --configuration Release --no-restore
dotnet test Backend\DoodhDirect.slnx --configuration Release --no-build --no-restore
dotnet ef migrations script --idempotent --no-build --project Backend\src\DoodhDirect.Infrastructure\DoodhDirect.Infrastructure.csproj --startup-project Backend\src\DoodhDirect.Api\DoodhDirect.Api.csproj --configuration Release --output artifacts\sql\DoodhDirect.sql
cd mobile
flutter pub get
flutter analyze
flutter test
flutter build web --release
```

CI performs the same backend build/tests, migration-script generation, Flutter analysis/tests, and Flutter web release build. Database application is intentionally excluded from CI because CI has no controlled SQL Server test service and credentials.

## Phase 0 limitations

- No real OTP or credential login endpoints
- No token issuance or refresh endpoint; JWT bearer validation is only the API security foundation
- No user registration or user-management API
- No role or permission administration API
- No branch authorization enforcement; branch-scoped persistence indexes are foundation-only
- No production identity provider integration
- No customer, ordering, payment, subscription, delivery, complaint, dairy, notification, or reporting workflows
- No external provider integration; later phases must follow the integration specification

Phase 0 is complete after local SQL Server Express connectivity and migration verification. Phase 1 must implement real identity, authentication, token issuance, user management, RBAC administration, permission administration, and branch authorization before those capabilities are treated as production functionality.
