# Decisions

## D-001: SQL Server Express for Development

The installed and verified development database is SQL Server Express named instance `DESKTOP-6LU1CLD\\SQLEXPRESS`. The application uses `Server=.\\SQLEXPRESS` with Windows authentication. LocalDB is not required.

## D-002: Environment-Specific Connection Configuration

The shared settings file contains no machine-specific database connection. Development uses `appsettings.Development.json`; deployments override `ConnectionStrings__DoodhDirect` through environment configuration or a secret manager.

## D-003: EF Core Migration Authority

The EF Core migrations and model snapshot are authoritative for the implemented Phase 0 and Phase 1 schema. The SQL starter document is treated as conceptual reference material.

## D-004: Secure-by-Default API

The API uses a fallback policy requiring authenticated users. Health, authentication issuance, refresh, and development documentation endpoints are explicitly anonymous where required. Logout and current-user access remain protected by JWT authentication.

## D-005: Device-Bound Rotating Sessions

Each authentication flow requires a client device identifier. Refresh tokens are stored as hashes, rotate on successful refresh, and record their replacement. Reuse of a revoked refresh token revokes the complete session to prevent replay.

## D-006: Permission-Driven RBAC and Branch Scope

Authorization is based on canonical permission claims rather than role-name checks. Branch-scoped policies require the route branch ID to match an assigned `branch_id` claim. `ACCESS.GLOBAL` is a distinct permission used for explicit global access, including the owner role; it is not an implicit role bypass.

## D-007: Idempotent Identity Seed

Canonical roles, permissions, and role-permission assignments are seeded by code and by canonical identifiers. Re-running the seed does not create duplicates, allowing safe startup initialization and repeatable deployment.

## D-008: Retain Identity and Audit Records

Identity/RBAC relationships use restrictive deletes. Authentication failures, OTP activity, and authorization challenges/forbidden results are persisted as audit records. Audit persistence failure must not change an authorization denial response.

## D-009: Provider and Secret Boundaries

OTP delivery is represented by `IOtpDeliveryService`; the default implementation is intentionally unconfigured and throws until a real provider is supplied. JWT signing keys must be injected through deployment configuration or a secret manager and the checked-in placeholder must never be deployed.
