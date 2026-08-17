# Architecture

## Phase 0 Structure

DoodhDirect is a modular monolith with one Flutter client and one ASP.NET Core API.

```text
Flutter (Android / iOS / web)
          |
          v
ASP.NET Core API
          |
          v
Application contracts and use-case boundary
          |
          v
Domain entities and invariants
          |
          v
Infrastructure: EF Core, SQL Server, health checks, migrations
```

Dependency direction is `Domain <- Application <- Infrastructure <- API`. Flutter never connects directly to SQL Server.

## API Foundation

The API provides JWT bearer validation, a secure authenticated fallback authorization policy, correlation IDs, global exception mapping, structured Serilog request logging, OpenAPI/Scalar in development, anonymous liveness/readiness health endpoints, and controller support. No business endpoints or authentication issuance endpoints were added in Phase 0.

## Persistence Foundation

EF Core targets SQL Server Express in the verified development environment. The schema uses internal `bigint` identity keys, public GUID identifiers, UTC `datetime2` timestamps, explicit foreign keys, bounded string columns, filtered SQL Server indexes for global and branch-scoped role assignments, and restrictive deletes for retained identity/RBAC data.

Development configuration is environment-specific in `Backend/src/DoodhDirect.Api/appsettings.Development.json`; production values must be injected through environment variables or a managed secret store.

## Flutter Foundation

Flutter uses Riverpod for session state and GoRouter for role-aware navigation. The application has a login placeholder, an explicit local foundation-session picker, role workspace placeholders, sign-out behavior, a typed HTTP client, and reusable loading/empty/error/unauthorized/offline panels.

## Phase Boundary

Phase 0 establishes contracts and persistence scaffolding. Phase 1 owns real identity and authorization behavior: OTP, credential login, registration, token issuance/refresh, user administration, RBAC administration, permission administration, branch authorization enforcement, and production identity providers.

## Phase 9 Doorstep Testing and Media Boundary

Phase 9 models a doorstep milk test as a delivery-owned aggregate. The application service coordinates customer ownership, assigned-employee and branch authorization, delivery-arrival gating, numeric reading validation, external media persistence, customer disclosure, terminal decisions, and audit records. API DTOs are role-specific: staff receive parameters and operational remarks; customers never receive numeric parameters and receive image metadata only after completion.

Image content crosses a provider-neutral `IMediaStorage` boundary. SQL Server stores metadata and a storage key only. Development uses local filesystem storage; a production object-storage adapter can replace it without changing domain entities. `IMilkTestImageValidator` verifies configured file size, actual JPEG/PNG/WebP signatures, and declared MIME compatibility before storage. Persistence failures trigger cleanup of the newly stored object.

The test can complete only after delivery arrival and only with at least one stored image and one valid `decimal(18,6)` reading. Device readings are manually entered in Phase 9; future Bluetooth or vendor SDK support remains behind an adapter and is not implemented.

## Phase 8 Dairy Consistency Boundary

Dairy operations are branch-scoped. Recording production creates the production entry and exactly one milk batch in a serializable transaction. Milk usage is an append-only ledger, and current availability is derived as `quantityProduced - quantityUsed`. A usage transaction locks the consistency boundary, rejects quantities above the batch's current availability, and marks the batch exhausted when usage consumes the exact remainder.

Phase 8 does not reserve or allocate dairy inventory to one-time orders or subscriptions. No contract currently defines when capacity is reserved, how reservations expire or are released, allocation priority, cancellation behavior, substitutions between batches, or reconciliation between planned deliveries and physical dispatch. Introducing any of those behaviors without an explicit cross-domain contract would create speculative coupling between dairy, order, subscription, and delivery modules.

A future capacity integration must define those semantics and their transaction/idempotency boundaries before order confirmation can depend on dairy availability. Until then, dairy availability prevents overdraw only when an actual milk usage record is appended; it is an operational inventory view, not a promise of order capacity.
