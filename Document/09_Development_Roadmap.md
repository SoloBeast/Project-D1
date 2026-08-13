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
- Orders cannot silently exceed configured availability rules.

## Phase 9 — Doorstep Testing

Deliver:
- Test request
- Delivery test entry
- MilkTest + parameters
- Device master

Acceptance:
- Test is linked to delivery/order.
- Device integration boundary is ready.

## Phase 10 — Camera

Deliver:
- Public camera configuration
- Secure stream display
- Offline state

Acceptance:
- Selected camera streams work inside authenticated app.

## Phase 11 — Notifications

Deliver:
- FCM
- SMS
- WhatsApp abstraction
- Templates
- Retry queue

## Phase 12 — Admin & Reports

Deliver:
- All dashboards
- Search/filter
- CSV/XLSX exports
- Global owner access
- Audit viewer

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
