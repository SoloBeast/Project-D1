# DoodhDirect Development Roadmap

## Development principle

Build vertical slices. Each phase must end with working UI + API + database + tests rather than creating the entire backend first and integrating later.

## Phase 0 — Foundation

Deliver:
- Git repository
- Flutter workspace
- ASP.NET Core solution
- SQL Server database
- CI/CD
- Environments
- Logging
- OpenAPI
- Base exception handling
- Authentication framework

Acceptance:
- Build/deploy succeeds in development.
- Health endpoint works.
- Database migration succeeds.

## Phase 1 — Identity & RBAC

Deliver:
- OTP
- Email/password
- Google/Apple foundation
- Users
- Roles
- Permissions
- Branch-scoped employee roles

Acceptance:
- Customer and employee login work.
- Unauthorized endpoints are blocked.

## Phase 2 — Customer & Address

Deliver:
- Customer profile
- Multiple addresses
- Map pin
- Geocoding
- Default address
- Address validation

Acceptance:
- Valid address coordinates are stored.

## Phase 3 — Products & One-Time Orders

Deliver:
- Product catalogue
- Loose milk quantity
- Branch serviceability
- One-time order
- Checkout

Acceptance:
- Customer can order a quantity of milk.
- Backend determines final price and branch.

## Phase 4 — Razorpay & Wallet

Deliver:
- Payment initiation
- Signature verification
- Webhook handling
- Refund foundation
- Wallet ledger

Acceptance:
- Test payment can complete.
- Duplicate webhook does not duplicate payment/wallet effects.

## Phase 5 — Subscription

Deliver:
- Prepaid subscription
- Delivery-day schedules
- Subscription calendar
- Skip
- Pause/resume
- Entitlement accounting

Acceptance:
- Delivered consumes entitlement.
- Skipped/failed does not.

## Phase 6 — Delivery Operations

Deliver:
- Delivery assignment
- Delivery staff views
- GPS
- Navigation
- Start/pickup
- OTP
- Completed/failed delivery

Acceptance:
- Customer sees active delivery location.
- OTP is required to complete delivery.

## Phase 7 — Complaints & Replacement

Deliver:
- Complaint creation
- Attachments
- Configurable 5-hour window
- Support/admin review
- Replacement order
- Refund option
- Repeated complaint alerts

Acceptance:
- Expired complaint is blocked/flagged by backend.

## Phase 8 — Dairy Operations

Deliver:
- Production entry
- Milk batch
- Available milk
- Dispatch/availability logic
- Reports

Acceptance:
- Branch can record today's production.
- Availability is derived from production minus append-only usage, and actual usage cannot overdraw a batch.
- Order/subscription capacity reservation remains a future integration; Phase 8 does not claim or enforce reserved order capacity without an approved cross-domain contract.

## Phase 9 — Doorstep Testing

Deliver:
- Customer request and owned status workflow
- Assigned delivery-staff workflow after arrival
- Delivery-owned MilkTest, configurable numeric parameters, and image metadata
- Provider-neutral external media storage with local development provider
- Image signature, MIME, and configured-size validation
- Customer-safe disclosure, authenticated image retrieval, and terminal confirm/reject decision
- Audit events for request, creation, upload, completion, confirmation, and rejection
- Backend integration/domain coverage and Flutter controller/widget coverage
- Device adapter boundary for future integration; Phase 9 uses manual reading entry

Acceptance:
- One test is linked to one delivery and duplicate requests are rejected.
- Only the owning customer and currently assigned, branch-authorized staff can access their respective workflows.
- Completion requires delivery arrival, one validated externally stored image, and one valid reading.
- SQL Server contains metadata and storage keys but no image bytes.
- Customers see status, completed images, completion time, and terminal decision controls, but no numeric readings.
- Migration is applied and physically verified on SQL Server Express; backend and Flutter verification suites pass.

## Phase 10 — Camera

Deliver:
- Branch-linked, metadata-only `Camera` and `CameraStream` persistence.
- Authenticated public camera discovery and short-lived stream-descriptor APIs.
- Branch-scoped camera administration with global owner/system-administrator access.
- Provider-neutral HLS/WebRTC stream gateway and fail-closed Production configuration.
- Explicit, visibly marked, Development-only HLS adapter.
- Flutter public list, live viewer, and camera administration workflows.
- Loading, empty, unavailable, offline, unauthorized, expired, unsupported-protocol, stream-failure, retry, and development-warning states.
- API, service, authorization, security, controller, repository, controller-state, and widget coverage.

Acceptance:
- Only active, explicitly public cameras appear to authenticated users with `CAMERAS.VIEW_PUBLIC`.
- Unknown, inactive, and private cameras are publicly indistinguishable.
- Public DTOs and persistence contain no credentials, internal addresses, hardware configuration, recordings, or raw/private stream URLs.
- Playback uses a short-lived gateway descriptor kept in Flutter memory only.
- `CAMERAS.READ`, `CAMERAS.MANAGE`, branch scope, global access, and source/destination reassignment rules are enforced server-side.
- Production refuses development/mock streaming and fails closed without a configured production adapter.
- Release builds, regressions, OpenAPI validation, migration application, and physical SQL Server inspection pass.

## Phase 11 — Notifications

Deliver:
- Durable provider-neutral notification events written by business modules without direct provider calls.
- In-app notification inbox, unread count, ownership-scoped mark-read behavior, pagination, and event deep links.
- Push, SMS, WhatsApp, and email channel gateways with independent delivery and attempt persistence.
- Background processing, bounded retries, terminal failure classification, preference suppression, and invalid-destination handling.
- User device registration and token rotation with hashed identity and protected provider payload.
- Event/channel preferences with protected critical-notification rules.
- Seeded English templates for all configured event/channel combinations and permission/audit-protected administration.
- Explicit Development mock providers and fail-closed Production provider selection.
- Flutter explicit permission flow, authorized/provisional token synchronization, foreground refresh, home badge, cold-start/opened-message handling, and internal-only deep-link validation.
- Loading, empty, offline, API-error, refresh, pagination, permission-denied, and unavailable states with session/user isolation.
- Android and iOS notification platform configuration plus backend, Flutter, OpenAPI, migration, and security coverage.

Acceptance:
- Core business transactions commit durable notification events and remain independent of provider availability.
- Inbox, unread count, mark-read, device, preference, and template APIs enforce authentication, ownership, permissions, validation, and auditing.
- Push tokens have no plaintext persistence or API/log exposure; token rotation and invalidation are deterministic.
- A failing, suppressed, permanently invalid, or unconfigured channel does not block the inbox or other channels and is represented truthfully in delivery/attempt state.
- Production rejects Development mocks and fails closed for unconfigured providers.
- Flutter never prompts at startup, registers tokens only for authorized/provisional permission, validates notification routes internally, and discards stale session responses.
- Complaint and replacement templates exist, while producer integration remains deferred until those business modules exist.
- Release builds, complete backend and Flutter regressions, OpenAPI validation, migration idempotency, and physical SQL Server inspection pass.

## Phase 12 — Admin & Reports

Deliver:
- Permission-aware admin dashboard with date and branch scope, including global-owner scope where `ACCESS.GLOBAL` is granted.
- Twelve report modules: Customers, Employees, Orders, Subscriptions, Payments, Wallets, Deliveries, Dairy, Milk tests, Cameras, Notifications, and Audit.
- Server-side search, status/date/branch and module-specific filters, sorting, page size, pagination, and responsive Flutter table/list presentation.
- Loading, empty, offline, unauthorized, stale-session, and general-error state handling.
- Permission-protected synchronous CSV/XLSX generation with direct binary response, safe filename/content-type handling, row-count header, and native/browser save handoff.
- Backend report contracts, optimized indexes/migration, Flutter Riverpod and `go_router` integration, focused tests, OpenAPI, and synchronized Phase 12 documentation.

Acceptance:
- Every dashboard metric, module route, filter, sort, page, and export is constrained by server-side permission and branch/global scope; client visibility is not authorization.
- All twelve modules expose truthful loading, empty, unauthorized, offline/error, stale-data, and export success/failure states on desktop, tablet, and narrow layouts.
- CSV and XLSX exports contain the selected authorized report data, are handed to the platform save boundary, and transient export bytes are cleared after handoff.
- Backend and Flutter focused/full regressions, Release/web builds, OpenAPI validation, migration/model alignment, and SQL schema inspection pass.
- Phase 13 hardening work remains deferred until this acceptance is complete.

## Phase 13 — Hardening

Deliver:
- Pen testing/remediation
- Performance testing
- Backup/restore test
- Monitoring
- Error recovery
- App-store release process
- Production runbooks

---

# Definition of Done

A feature is not Done until:

- UI is complete.
- API is implemented.
- Database/migrations are complete.
- Authorization is implemented.
- Validation is implemented.
- Error handling is implemented.
- Audit is implemented when required.
- Unit tests exist.
- Integration tests exist for critical flows.
- API documentation is updated.
- No hard-coded business values where configuration is required.
- Acceptance criteria pass.
