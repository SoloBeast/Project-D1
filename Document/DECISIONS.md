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

## D-010: Delivery-Owned Doorstep Test Aggregate

Phase 9 attaches at most one doorstep test directly to a delivery. The aggregate records the customer, branch, requester, optional completer, lifecycle timestamps, readings, image metadata, and terminal customer decision. Batch and laboratory testing are separate future designs.

## D-011: External Provider-Neutral Test Media

Image bytes are stored through `IMediaStorage`; SQL Server stores only metadata and a provider storage key. Development uses a local filesystem provider. Uploads are signature-, MIME-, and size-validated before storage, and a database failure after storage triggers object cleanup.

## D-012: Role-Specific Milk-Test Disclosure

Staff DTOs contain configurable readings and operational remarks. Customer DTOs exclude both, hide all image metadata until completion, and expose image content only through an authenticated ownership-scoped endpoint.

## D-013: Arrival-Gated Completion and Terminal Decision

A test completes only when its delivery is `Arrived` and at least one validated image and one `decimal(18,6)` reading exist. Customer confirmation or rejection is allowed only after completion, and the first decision is terminal.

## D-014: Manual Phase 9 Readings

Phase 9 records manually entered physical-device readings. Future Bluetooth or vendor SDK support must implement a device adapter and must not introduce vendor-specific types into the domain model.
