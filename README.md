# DoodhDirect Phase 1

DoodhDirect is a farm-to-home dairy platform. This repository contains the accepted Phase 0 foundation and the Phase 1 identity and role-based access control implementation: a layered ASP.NET Core API, SQL Server/EF Core persistence, and one authenticated, role-aware Flutter application for Android, iOS, and web.

The specifications in `Document/01_PRD.md` through `Document/09_Development_Roadmap.md` are authoritative. EF Core migrations are authoritative for the implemented schema; the SQL starter document remains conceptual. Phase 2 customer and address workflows and all later business workflows remain out of scope.

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

Restore and run the API from the repository root. The Development HTTP launch profile listens on `http://localhost:5209`:

```powershell
dotnet tool restore
dotnet restore Backend\DoodhDirect.slnx
dotnet run --project Backend\src\DoodhDirect.Api\DoodhDirect.Api.csproj --launch-profile http
```

Development endpoints:

- `GET /health/live`: anonymous, dependency-free process liveness
- `GET /health/ready`: anonymous SQL Server readiness
- `GET /openapi/v1.json`: development-only OpenAPI document with JWT bearer metadata
- `/scalar/v1`: development-only Scalar API reference

Phase 1 authentication endpoints:

- Anonymous: `POST /api/v1/auth/register`, `POST /api/v1/auth/login`, `POST /api/v1/auth/send-otp`, `POST /api/v1/auth/verify-otp`, and `POST /api/v1/auth/refresh`
- Authenticated: `POST /api/v1/auth/logout` and `GET /api/v1/auth/me`

The fallback authorization policy protects every endpoint unless it is explicitly anonymous. JWTs carry user, session, role, permission, and branch claims. Dynamic policies enforce exact permissions and branch assignments; `ACCESS.GLOBAL` is the explicit owner bypass for branch scope. Canonical roles and permissions are seeded idempotently at API startup. Phase 1 does not expose role, permission, employee, or account-administration CRUD endpoints.

## Database migrations

The Phase 0 migration was applied and verified against the development database on `DESKTOP-6LU1CLD\\SQLEXPRESS`. The repository now contains two migrations:

- `20260815190759_InitialPhase0Foundation`
- `20260815201425_Phase1IdentitySessions`

The Phase 1 migration adds OTP challenges and device-bound user sessions and expands refresh-token persistence for rotation and reuse detection. The configured SQL Server Express database is current through `20260815201425_Phase1IdentitySessions`; `dotnet ef database update` reported that no migrations were pending.

Generate the idempotent SQL script without connecting to SQL Server:

```powershell
dotnet ef migrations script --idempotent --project Backend\src\DoodhDirect.Infrastructure\DoodhDirect.Infrastructure.csproj --startup-project Backend\src\DoodhDirect.Api\DoodhDirect.Api.csproj --configuration Release --output artifacts\sql\DoodhDirect.sql
```

Apply migrations only after configuring a reachable SQL Server connection:

```powershell
dotnet ef database update --project Backend\src\DoodhDirect.Infrastructure\DoodhDirect.Infrastructure.csproj --startup-project Backend\src\DoodhDirect.Api\DoodhDirect.Api.csproj
```

The migrations use `bigint` identity keys, GUID public IDs, `datetime2` timestamp columns, filtered role-assignment uniqueness, restrictive identity/RBAC deletes, and bounded token hashes. New application-owned business timestamps use India-local wall-clock semantics from `IIndiaTimeProvider`; provider/external and deferred infrastructure timestamps retain UTC semantics. Refresh tokens are stored only as hashes and are linked to device-bound sessions.

## Flutter application

The Flutter app implements registration, password login, OTP login/registration, secure session persistence, refresh-based restoration, logout, expired-session handling, and server-derived role navigation. Development Web uses one canonical launcher so the repository-root `.env` Maps browser key is loaded once and passed as the `DOOHDIRECT_GOOGLE_MAPS_API_KEY` Dart define. The launcher also supplies the Development API URL and development tools define:

```powershell
.\scripts\run-flutter-web-development.ps1
```

The same launcher is available through VS Code as **Flutter Web: Development** in Run and Debug and as the default **Flutter Web: Development** build task. Do not use a direct `flutter run -d chrome` command for Development Web because it omits the `.env`-derived Maps define. Verify the configuration without launching Flutter or printing the value with:

```powershell
.\scripts\run-flutter-web-development.ps1 -CheckConfiguration
```

Use an Android emulator/device or an iOS simulator/device instead of `chrome` for mobile targets. An Android emulator connecting to the Development HTTP profile commonly uses `http://10.0.2.2:5209` because emulator `localhost` refers to the emulator itself. Physical devices require the development machine's reachable LAN hostname or address. Session and device identifiers are stored with `flutter_secure_storage`.

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

## Phase 1 security defaults and limitations

- Access tokens expire after 15 minutes; refresh tokens expire after 30 days.
- Refresh tokens rotate on use. Reuse of a revoked token revokes the complete session.
- OTPs expire after 5 minutes, allow 5 failed attempts, and are limited to 3 requests per mobile/purpose in a 15-minute window.
- Passwords use PBKDF2 with 120,000 iterations.
- Authentication events and authorization challenges/denials are persisted in the audit log.
- `UnconfiguredOtpDeliveryService` does not send SMS. A production `IOtpDeliveryService` integration is required before OTP can be used outside controlled testing.
- The checked-in JWT signing key is a placeholder and must be supplied from a deployment secret store.
- Role and permission administration APIs, password reset, external identity providers, and production account-management workflows are not implemented in Phase 1.
- No customer/address, ordering, payment, subscription, delivery, complaint, dairy, notification, or reporting workflows are implemented.
