# Phase 1 Acceptance

**Status:** PASS

This checkpoint was limited to Phase 1 identity, authentication, authorization, security controls, and regression validation. No Phase 2 functionality was started.

## Authentication

- Password registration and login are implemented; passwords are stored as PBKDF2-SHA512 hashes.
- OTP request and verification are implemented with expiration, attempt limits, rate limiting, hashed OTP persistence, and audit events.
- Access tokens are JWTs containing user, session, role, permission, and branch claims.
- Refresh tokens are opaque, stored only as SHA-256 hashes, rotated on use, and linked to their replacement hash.
- Reuse of a rotated refresh token triggers session revocation and an authentication audit event.
- Logout revokes the device-bound session.
- The authenticated current-user endpoint rejects inactive accounts.

Evidence: [AuthenticationService.cs](../Backend/src/DoodhDirect.Infrastructure/Identity/AuthenticationService.cs), [OtpService.cs](../Backend/src/DoodhDirect.Infrastructure/Identity/OtpService.cs), [JwtTokenService.cs](../Backend/src/DoodhDirect.Infrastructure/Identity/JwtTokenService.cs), and [AuthController.cs](../Backend/src/DoodhDirect.Api/Controllers/AuthController.cs).

## Authorization

- Permission policies require the exact permission claim.
- Branch-scoped policies require both the exact permission and a matching branch claim for the requested route branch.
- Owner global access is explicit through the `ACCESS.GLOBAL` permission claim.
- Inactive users are rejected during password login, OTP login, token refresh, and current-user retrieval.
- Authentication failures and authorization challenges/forbidden results are persisted as audit events.

Evidence: [AuthorizationRequirements.cs](../Backend/src/DoodhDirect.Api/Authorization/AuthorizationRequirements.cs) and [AuditingAuthorizationMiddlewareResultHandler.cs](../Backend/src/DoodhDirect.Api/Authorization/AuditingAuthorizationMiddlewareResultHandler.cs).

## Security Checkpoint

- No production credential or private key was found in tracked source/configuration files by the targeted repository scan.
- JWT signing is configuration-driven through the `Authentication:Jwt` options section and validated on startup.
- The checked-in signing value is an explicit placeholder and is not suitable for production.
- The default OTP delivery service is explicitly unconfigured and throws rather than sending or exposing an OTP code.
- Passwords, OTP codes, refresh tokens, and device identifiers are persisted only as derived hashes. Access tokens are not persisted.
- Authentication and authorization audit event paths are present and covered by backend tests.

Evidence: [appsettings.json](../Backend/src/DoodhDirect.Api/appsettings.json), [JwtOptions.cs](../Backend/src/DoodhDirect.Api/Authentication/JwtOptions.cs), [SecurityPrimitives.cs](../Backend/src/DoodhDirect.Infrastructure/Identity/SecurityPrimitives.cs), [UnconfiguredOtpDeliveryService.cs](../Backend/src/DoodhDirect.Infrastructure/Identity/UnconfiguredOtpDeliveryService.cs), and [IdentityEntities.cs](../Backend/src/DoodhDirect.Domain/Identity/IdentityEntities.cs).

## Regression

- `dotnet build -c Release`: PASS, 0 warnings, 0 errors.
- `dotnet test -c Release`: PASS, 38 tests passed, 0 failed, 0 skipped.
- `flutter analyze`: PASS, no issues found.
- `flutter test`: PASS, 7 tests passed.

## Known Production Configuration

Before production deployment:

- Supply a unique high-entropy JWT signing key through deployment configuration or a secret manager; do not use the checked-in placeholder.
- Configure and register a real OTP delivery provider.
- Provide production connection strings and environment-specific operational settings outside committed source configuration.

## Scope

Only [PHASE_1_ACCEPTANCE.md](PHASE_1_ACCEPTANCE.md) was created for this checkpoint. [DECISIONS.md](DECISIONS.md) was not changed because no new architectural decision was required. No Phase 2 source or documentation was added.
