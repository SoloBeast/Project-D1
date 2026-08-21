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

EF Core targets SQL Server Express in the verified development environment. The schema uses internal `bigint` identity keys, public GUID identifiers, `datetime2` timestamps, explicit foreign keys, bounded string columns, filtered SQL Server indexes for global and branch-scoped role assignments, and restrictive deletes for retained identity/RBAC data. New application-owned business timestamps use India-local wall-clock values from `IIndiaTimeProvider`, represented as `DateTimeKind.Unspecified`; provider/external and deferred infrastructure boundaries retain true UTC instants. Existing physical timestamp columns and historical rows are preserved during the semantic migration.

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

## Phase 10 Live Dairy Camera Boundary

Phase 10 persists a branch-owned `Camera` and one-to-one `CameraStream` metadata row. The model contains public identity, internal identifier, display metadata, visibility/activity flags, ordering, protocol, non-secret provider code, and an opaque non-secret provider reference. Credentials, internal network addresses, hardware configuration, recordings, raw/private stream URLs, and issued playback descriptors do not belong in the domain or SQL schema.

`ICameraStreamGateway` isolates use cases from HLS/WebRTC provider details. Public discovery performs provider capability and availability checks but returns only public display fields. Playback requests produce a short-lived descriptor containing protocol, gateway URI, UTC expiry, and a development marker. Unknown, inactive, and private IDs share one not-found path; unavailable adapters produce a dependency-unavailable result without provider diagnostics.

Public and privileged DTOs are intentionally separate. Public results omit branch, internal, provider, visibility, activity, and timestamp fields. Managed reads require `CAMERAS.READ`; mutations require `CAMERAS.MANAGE`. Branch-scoped users are constrained in service queries and mutations, and reassignment requires both source and destination authorization. `ACCESS.GLOBAL` supplies the explicit global bypass. Creation and update persist audit events.

Infrastructure selects the stream adapter by environment and configuration. Development may explicitly use an HTTPS, HLS-only mock adapter whose descriptors are visibly marked. Production rejects that adapter and otherwise uses a fail-closed unconfigured implementation until an operational production gateway is supplied. Flutter holds descriptors only in Riverpod memory, refreshes expired access from the API, and delegates protocol playback behind injectable player builders.

## Phase 11 Notification Boundary

Business modules own business state and append durable, provider-neutral `NotificationEvent` records in their persistence boundary. They depend only on `INotificationEventWriter`; they do not know about FCM, SMS, WhatsApp, email, templates, provider credentials, or retry behavior. Complaint and replacement event contracts/templates are present, but producer integration remains deferred until those modules exist.

The notification worker owns asynchronous materialization. It claims pending events, renders one of the seeded event/channel/language templates, creates the user inbox record, evaluates critical-event and user-preference rules, and creates independent `NotificationDelivery` records. Every provider invocation appends a `NotificationAttempt`. Delivery states distinguish pending, processing, delivered, retry scheduled, failed, suppressed, and unconfigured outcomes; one channel cannot block the inbox or another channel. Event keys and persistence uniqueness provide idempotency across worker retries.

`INotificationChannelGateway` is the provider-neutral boundary for Push, SMS, WhatsApp, and Email. Development may explicitly select deterministic mock gateways. Outside Development those mocks are rejected, and an absent production provider resolves to a fail-closed unconfigured gateway rather than synthetic success. Retryable failures receive bounded delayed attempts, permanent failures terminate, and invalid push destinations deactivate the associated device.

SQL Server persists events, inbox items, templates, preferences, devices, deliveries, and attempts. A push token hash supports identity and uniqueness; only a data-protected token can be unprotected for provider delivery. Plaintext push tokens are excluded from persistence, API responses, logs, audit records, and diagnostics. Template mutation requires the manage permission and records the authenticated actor and reason; template reads use a separate read permission.

Flutter keeps notification state scoped to the authenticated session through Riverpod generation checks. Startup may inspect push permission but cannot prompt. Explicit user action may request permission, and registration/token refresh runs only for authorized or provisional states. Foreground messages refresh inbox and unread count; opened and cold-start messages pass through one internal-route allowlist before GoRouter navigation. Sign-out/user changes clear inbox, token-sync, and pending-navigation state so stale asynchronous responses cannot cross sessions.

## Application Time Semantics

The configured application time zone is `Asia/Kolkata`. Application-owned business timestamps are obtained through `IIndiaTimeProvider`, serialized without a UTC suffix or offset, and represented as India-local wall-clock values with `DateTimeKind.Unspecified`. Date-only business rules use the provider's India-local date. Provider/external timestamps, payment gateway boundaries, camera descriptor expiry, and deferred audit/notification infrastructure remain true UTC instants until their dedicated migration slices are completed. SQL Server does not generate current-time defaults for these fields; timestamp values are assigned by application code. Historical rows are not bulk-converted.
