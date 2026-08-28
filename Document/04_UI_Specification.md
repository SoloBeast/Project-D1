# DoodhDirect UI / Screen Specification

## 1. Common Application Shell

All role-based experiences use one Flutter application with role-aware navigation.

### Customer navigation
Home | Orders | Subscription | Wallet | Live Dairy | Profile

### Employee navigation
Today | Deliveries | Test | Replacement | Profile

### Dairy Manager navigation
Dashboard | Production | Batches | Testing | Delivery Management | Reports

Dairy Manager's `Delivery Management` home card opens the shared branch-scoped delivery-management workspace. It uses the existing delivery list/detail flows for filtering, subscription delivery generation, individual assignment, reassignment, and bulk assignment.

### Delivery Manager navigation
Dashboard | Live Deliveries | Assignments | Staff | Reports

### Support navigation
Dashboard | Complaints | Replacements | Customers | Reports

### Owner/Admin navigation
Dashboard | Customers | Orders | Subscriptions | Delivery | Dairy | Products | Payments | Complaints | Cameras | Reports | Settings | Audit

Administration is a single grouped home (`Administration` section on the Owner/Admin role home) that renders only the groups the actor is permitted to see:

- **User & Access** — Employees (visible with `EMPLOYEES.READ`/`EMPLOYEES.MANAGE`).
- **Master Data** — Branches (visible with `BRANCHES.READ`) and Catalogue (visible with `CATALOGUE.READ`).
- **System Setup** — Number Series (visible with `SETUP.NUMBER_SERIES.READ`).
- **Monitoring & Operations** — Cameras (visible with `CAMERAS.READ`).

Each tile opens its dedicated management flow; there are no duplicate per-role administration screens.

---

## 2. Global UI Rules

- Show loading state for every network operation.
- Show empty state where a list has no records.
- Show retry action for recoverable API/network errors.
- Never trust client-side business validation alone; repeat critical validation on the backend.
- Currency: INR.
- Quantity: litres for milk, decimal with up to 3 fractional digits.
- Display dates/times in India local time. Application-owned business timestamps are stored and transported as India-local wall-clock values without a `Z` suffix or offset; provider/external and deferred infrastructure timestamps remain UTC until their dedicated migration slices are completed.
- Destructive actions require confirmation.
- Owner/admin sees explicit audit-relevant warning before financial overrides.

---

## 3. Customer Screens

### 3.1 Splash
Purpose: initialize app, load remote configuration, restore session.

States: loading, maintenance, update-required, authenticated, unauthenticated.

### 3.2 Login
Methods: mobile OTP, email/password, Google, Apple.

Fields:
- Mobile or email depending on selected method.
- Password for email/password.

Actions:
- Continue
- Google
- Apple
- Forgot password

### 3.3 OTP Verification
Fields:
- OTP

Actions:
- Verify
- Resend

Rules:
- Rate-limited.
- Maximum configurable attempts.
- Expiry controlled server-side.

### 3.4 Home
Sections:
- Current active subscription card.
- Today's order/delivery card.
- Milk quick-order CTA.
- Recent orders.
- Complaint status.
- Live Dairy CTA.
- Wallet balance.
- Notification inbox action with a server-authoritative unread-count badge; selecting it opens the inbox.

### 3.4.1 Notification Inbox

Content:
- Paginated notifications ordered newest first.
- Unread rows have a distinct accessible visual treatment.
- Each row shows title, body, timestamp, and read state.
- Pull-to-refresh reloads the first page and unread count.
- Scrolling loads the next page without replacing already loaded rows.

Behavior:
- Opening an unread notification marks it read, updates the inbox, and refreshes the home badge.
- Foreground push messages refresh inbox and unread state.
- A notification deep link navigates only when it is a valid allowlisted internal route. Invalid, external, or unauthorized routes remain in the inbox without navigation.
- Opened-notification and cold-start delivery use the same validation path.
- Loading, empty, offline, API-error, refresh-error, and pagination-error states provide a retry action where applicable.
- Signing out or changing users clears notification, token-sync, and pending-navigation state. Responses started for an earlier session must not update the current session.

Push permission:
- Startup may display the current permission state but must not trigger the system prompt.
- A dedicated user action requests permission and explains denied/unavailable state through normal UI status.
- Device token registration and refresh synchronization occur only for authorized or provisional permission.

### 3.5 Product List
Initial product: loose buffalo milk.

Fields shown:
- Product name
- Unit price
- Available quantity/availability state

Action: Order.

### 3.6 Milk Order
Fields:
- Quantity in litres.
- Delivery address.
- One-time vs subscription.

Pricing:
- Quantity × configured price.
- Coupon/wallet/payment shown at checkout.

### 3.7 Address List
Functions:
- Add address
- Edit
- Delete/deactivate
- Set default

### 3.8 Add/Edit Address
Fields:
- Label
- Address lines
- Locality
- City
- State
- PIN
- Landmark
- Map pin
- Contact name
- Contact mobile
- Delivery instructions

Location must be geocoded and stored as latitude/longitude. Selecting or moving the map pin performs provider-neutral reverse lookup when a provider is configured. Non-empty returned address fields may populate the form, while manually entered values remain intact when a provider omits a field or lookup fails. Latitude and longitude remain internal map state and are never editable as customer text fields. Existing saved coordinates initialize the map.

### 3.9 Checkout
Sections:
- Item summary
- Quantity
- Address
- Coupon
- Wallet amount
- Payment method
- Final amount

Actions:
- Pay and place order

### 3.10 Payment Result
States:
- Success
- Failed
- Pending verification

Do not mark payment successful solely from client callback; refresh order/payment state from server.

### 3.11 Order Detail
Fields:
- Order number
- Status
- Branch
- Items
- Quantity
- Amount
- Payment status
- Delivery address
- Delivery timeline
- Test request option when eligible
- Complaint option when eligible

### 3.12 Live Delivery Tracking
Show:
- Map
- Delivery employee marker
- Customer location
- Delivery status
- ETA if reliable route calculation exists
- Support action

Location visibility starts only after active delivery begins.

### 3.13 Doorstep Milk Test

The owned delivery detail presents the request action only while the delivery is active and no test exists. It describes the result as an indicative doorstep check, not laboratory certification.

Customer states:
- Loading, offline, unauthorized, error, and retry.
- No test requested, with the request action when eligible.
- Requested, waiting for assigned delivery staff; no image or reading is visible.
- Completed, showing completion time and authenticated images.
- Pending decision, with Confirm and Reject actions and optional remarks.
- Confirmed or Rejected terminal outcome; decision actions are removed.

The customer UI never displays numeric readings or staff remarks. It refreshes server-authoritative state after each mutation and cannot cancel a persisted request in Phase 9.

### 3.14 Complaint
Fields:
- Category
- Description
- Photo/video attachments

System shows:
- Complaint eligibility deadline
- Current status

Expired complaint shows a clear reason and support escalation path.

### 3.15 Replacement Detail
Shows:
- Original order
- Complaint
- Resolution
- Replacement order
- Status
- Scheduled/complete time

### 3.16 Subscription Setup
Fields:
- Quantity
- Delivery days
- Delivery slot: Morning or Evening
- Start date
- Entitlement period / prepaid amount
- Address
- Payment method

Morning is the compatibility default for legacy or omitted slot values. The subscription delivery slot is separate from dairy production shifts.

### 3.17 Subscription Calendar
Each scheduled date has status:
- Scheduled
- Delivered
- Skipped
- Failed
- Cancelled
- Replacement

Actions:
- Skip upcoming day when cutoff allows.
- Pause subscription.
- Resume subscription.

### 3.18 Subscription Detail
Shows:
- Plan
- Milk quantity
- Delivery days
- Delivery slot: Morning or Evening
- Paid amount
- Total entitlement
- Used entitlement
- Remaining entitlement
- Address
- Branch
- Calendar
- Pause/resume

### 3.19 Wallet
Shows:
- Current balance
- Add money
- Transaction history

### 3.20 Live Dairy

Authenticated users with `CAMERAS.VIEW_PUBLIC` follow:

```text
Live Dairy -> Selected Public Cameras -> Open Camera -> Live Stream
```

The list shows only active cameras explicitly configured as public, in configured display order. Each row shows the public display name and current availability; it does not expose branch, internal identifier, provider, or stream-reference metadata.

Opening an available camera requests a short-lived playback descriptor and starts HLS or WebRTC playback according to the returned protocol. The descriptor remains in memory only. Expired descriptors are discarded and refreshed from the API. Recordings and playback history are not available.

Required states:

- Loading list or playback descriptor.
- Empty public-camera list.
- Camera unavailable or stream-gateway failure with retry.
- Offline with retry.
- Unauthorized with safe navigation.
- Unknown/private/inactive camera shown only as unavailable/not found.
- Expired playback descriptor with refresh action.
- Unsupported playback protocol without attempting playback.
- Player initialization or playback failure with retry.
- Visible warning whenever `isDevelopmentStream` is true.

### 3.20.1 Camera Administration

Users with `CAMERAS.READ` can list camera metadata for authorized branches. Users with `CAMERAS.MANAGE` can create and edit the branch, internal identifier, display name, public visibility, active status, display order, protocol, non-secret provider code, and opaque provider stream reference.

The branch selector contains only authorized branches unless `ACCESS.GLOBAL` is present. Reassignment is unavailable unless the user is authorized for both source and destination branches. Forms validate required bounded fields and non-negative display order, preserve input after recoverable errors, and refresh server-authoritative state after a successful mutation.

Administration provides loading, empty, unauthorized, offline, validation, conflict, unavailable, success, and retry states. No form accepts credentials, internal addresses, hardware configuration, recordings, or direct private stream URLs.

### 3.21 Profile
- Personal details
- Login methods
- Addresses
- Notifications
- Privacy
- Support
- Logout

---

## 4. Delivery Staff Screens

### 4.1 Delivery Dashboard
Shows:
- Assigned deliveries today
- Pending
- Out for delivery
- Completed
- Failed
- Replacement deliveries

### 4.2 Delivery List
Filters:
- Date
- Status
- Delivery type: All, One-time, Subscription
- Subscription slot: All, Morning, Evening
- Route/order

Paid one-time orders appear automatically as `ReadyForAssignment`. Failed or cancelled payments do not create delivery rows.

### 4.2.1 Delivery Management Operations
The shared delivery-management workspace is available to `DELIVERY_MANAGER` and `DAIRY_MANAGER` users with the existing delivery-management permissions. Dairy Manager users retain the Dairy workspace and enter this workspace through the `Delivery Management` home card; no duplicate Dairy-specific delivery screen is created.

Delivery Manager controls include:
- Date selector.
- Status filter.
- Delivery type filter: All, One-time, or Subscription.
- Subscription slot filter: All, Morning, or Evening; disabled when Delivery type is One-time.
- `Generate Subscription Deliveries` action.
- Row checkboxes for deliveries in `ReadyForAssignment`.
- Select All, Clear Selection, selected count, and Assign Selected controls.
- Employee selection followed by assignment confirmation.

`Generate Subscription Deliveries` creates only eligible subscription deliveries through the selected operational date and is bounded to the configured generation window. It does not generate an entire future subscription lifetime. The operation is subscription-only; paid one-time deliveries are created by successful payment confirmation and appear in the queue automatically.

Bulk assignment submits one backend operation. The server validates every selected delivery, branch authorization, employee eligibility, and current delivery state before mutating any row. Assignment is atomic: a validation failure leaves all selected deliveries unchanged. After success, audit, notification, and realtime effects are recorded, the list refreshes from the server, and selection is reconciled against the refreshed delivery IDs.

### 4.3 Delivery Detail
Shows only permitted customer information.

Actions:
- Navigate
- Start delivery
- Pick up
- Open requested doorstep test after arrival
- Collect payment
- Mark failed
- Mark delivered
- Start replacement

### 4.4 OTP Verification
- Enter OTP
- Verify
- Retry with rate limit

### 4.5 Milk Test

The screen is available only to the currently assigned employee and is actionable only when the delivery is `Arrived`.

Controls and content:
- Current test status and customer decision.
- Camera and gallery image-source actions.
- Uploaded-image list with upload progress/error feedback.
- Configurable reading rows with code, name, numeric value, and unit.
- Optional staff remarks.
- Complete action enabled only when at least one valid reading and one uploaded image exist.

Validation preserves the form and gives field-level feedback. Loading, no-request, offline, unauthorized, stale-assignment, terminal-delivery, and server-error states provide retry or return navigation. The screen refreshes from the server after upload and completion.

Future:
- Bluetooth/device auto-import behind the device adapter boundary.

### 4.6 Failed Delivery
Required:
- Failure reason
- Remarks optional/required by reason
- GPS captured if available

### 4.7 Replacement Delivery
Shows original complaint/order and replacement order.

---

## 5. Dairy Manager Screens

### Dashboard
- Today's production
- Available milk
- Milk batch
- Testing
- Dispatch

### Production Entry
Fields:
- Date
- Shift
- Buffalo count
- Quantity produced
- Remarks

### Batch Detail
- Batch number
- Production source
- Quantity produced
- Available quantity
- Tests

### Testing
- List tests
- Search by batch/order
- Record test
- View results

---

## 6. Owner/Admin Screens

All screens must support search, filters, pagination and export where applicable.

### Dashboard
Cards:
- Revenue
- Customers
- Orders
- Deliveries
- Active subscriptions
- Milk production
- Complaints
- Replacements
- Refunds

### Customers
Actions:
- View
- Edit
- Deactivate
- View orders
- View subscriptions
- View complaints
- View wallet
- View audit-sensitive history according to permissions

### Orders
Actions:
- View
- Reassign branch
- Reassign delivery staff
- Cancel/override where permitted
- Refund where permitted

### Branches

The Branches entry is rendered only for users holding `BRANCHES.READ` or `BRANCHES.MANAGE` (Owner and System Administrator). It is a single shared management screen — there is no duplicate branch system per role.

Branch Management list:
- Each row/card shows Branch Number, Name, Code, City/State, and Status (Active/Inactive).
- Branches are sorted by Name then Code.
- `+ Add Branch` button is shown only with `BRANCHES.MANAGE`.
- Selecting a branch opens the Branch Detail screen; without `BRANCHES.READ` the list is not reachable.
- Loading, empty, and error-with-retry states are shown for every network operation.

Add/Edit Branch screen:
- Fields: Code, Name, Address Line 1/2, Locality, City, State, Pin Code, Latitude, Longitude, and Service Radius (km).
- Branch Number is **read-only and never editable** — it is allocated by the backend from the centralized `BRANCH` numbering series on create and displayed from the server response. The client never generates or submits a branch number.
- Code is normalized to uppercase and trimmed by the server; duplicate codes (case-insensitive) and out-of-range latitude are rejected with inline server validation messages.
- On edit, the Code field is locked for branches referenced by orders, product availability, or an existing scoped `ORDER` number series.

Branch Detail screen:
- Shows the full record including the system-allocated Branch Number, address, coordinates, service radius, status, and created/updated timestamps.
- `Deactivate`/`Activate` actions are shown only with `BRANCHES.MANAGE` and require confirmation.
- State changes refresh from the server; audit events record the real authenticated actor.

Actions:
- Create
- Edit
- Activate/deactivate
- Configure service radius
- View branch dashboard

### Products
Actions:
- Create/edit/activate
- Configure availability

### Complaints
Actions:
- Review
- Approve replacement
- Approve refund
- Reject
- Override expiry with audit reason

### Employees

The Employees entry is rendered only for users holding `EMPLOYEES.READ` or `EMPLOYEES.MANAGE` (Owner and System Administrator). It is a single shared management screen — there is no duplicate employee system per role.

Employee Management list:
- Each row shows Name, Mobile, Email, Role, Branch, Status (Active/Suspended), and Invitation state (Invited, Registered, Cancelled, Expired, or none).
- Row actions: resend invitation (when an invitation is usable), cancel invitation, edit, deactivate, and reactivate — each is shown/hidden by `EMPLOYEES.MANAGE` and rendered only for an employee's invitation when it is in a state that allows the action.
- `+ Create Employee` button opens the Create Employee screen.

Create Employee screen:
- Fields: Name, Mobile, Email, Role, Branch.
- Role selector offers only assignable roles: Delivery Manager, Delivery Boy/Delivery Staff, Accountant, Dairy Manager, System Administrator. The Owner role is never offered.
- The System Administrator option is only selectable when the actor holds `IDENTITY.ADMINISTRATORS.MANAGE` (Owner-only); otherwise the backend returns a forbidden result and the UI surfaces the reason.
- Branch selector is populated from `GET /admin/employees/branches`.
- When "Send invitation" is enabled, the screen submits with `SendInvitation: true`; after create the returned invitation link (the one-time token) is surfaced in a dedicated dialog with a copy action and the expiry date, and the employee appears in the list with Invitation status "Invited".

Invitation flow screens (reached from the shared invitation link, no login required):
- Invitation verification screen calls `GET /api/v1/employee-invitations/{token}/verify` and renders a friendly state for invalid, expired, cancelled, or already-registered tokens.
- On a valid invitation it shows the invited employee's Name, Mobile, Email, Role, and Branch — all read-only; the invited user cannot edit the assigned role or branch.
- Mobile OTP verification screen (purpose `3`, employee invitation) then registration (name/password/device) and completion. On success the account is active and the user signs in and lands in their role workspace (Delivery Boy → delivery ops, Delivery Manager → delivery management, Dairy Manager → dairy + delivery management, Accountant → accounting, System Administrator → administration, Owner → owner workspace).

### Reports

The Reports entry and each report module are rendered only when the authenticated user has the corresponding report permission. The dashboard requires `REPORTS.DASHBOARD.READ`; Customers, Employees, Orders, and Subscriptions use `REPORTS.ADMINISTRATION.READ`; Payments and Wallets use `REPORTS.FINANCIAL.READ`; Deliveries, Dairy, Cameras, and Notifications use `REPORTS.OPERATIONS.READ`; Milk tests use `REPORTS.MILK_TESTS.READ`; and Audit uses `REPORTS.AUDIT.READ`. Export controls additionally require `REPORTS.EXPORT`.

Modules:
- Customers
- Employees
- Orders
- Subscriptions
- Payments
- Wallets
- Deliveries
- Dairy
- Milk tests
- Cameras
- Notifications
- Audit

Filters and navigation:
- Date range, optional branch scope, free-text search, status selection, and module-specific customer/employee/product/payment filters.
- Sort field, ascending/descending direction, page size, and server-backed pagination.
- Branch choices and dashboard metrics are limited to the server-authorized scope; global owners may select global scope.
- Desktop/tablet layouts use a responsive table; narrow layouts use compact labeled report rows/list cards.

Exports:
- CSV and XLSX are generated by the authenticated backend for the selected module and current filters.
- The client hands the returned file to the native/browser save boundary and reports the destination on success.
- Generation errors and platform save errors are distinct user-visible states; retained export bytes are cleared after handoff.

### Settings
- Complaint window
- Skip cutoff
- OTP configuration
- Delivery tracking interval
- Notification templates
- Operational limits

### Setup — Number Series

The Setup → Number Series workspace is available only when the authenticated user holds `SETUP.NUMBER_SERIES.READ`. Reads (list, detail, and preview) require `SETUP.NUMBER_SERIES.READ`; creating, editing, activating, and deactivating require `SETUP.NUMBER_SERIES.MANAGE`. Users without `MANAGE` can view the list and preview but cannot create, edit, activate, or deactivate; every mutating control is hidden or disabled and a read-only `Access denied` panel explains the missing permission on the edit screen.

#### Number Series List

- Shows every series as a card: `Code` (for example `CUSTOMER`), `Description`, `Template`, `StartingNumber`, `LastUsedNumber`, `IncrementBy`, `ResetPolicy`, and an Active/Inactive badge.
- Scoped series display a Scope badge (for example `Branch: DLH-01`) so branch-scoped counters are distinguishable from the legacy global series with the same `Code`.
- Shows the next number to be allocated (`nextNumber`) without consuming it.
- `MANAGE` holders see `Configure`, `Activate`, and `Deactivate` actions; read-only users see only the `Configure` icon, which opens the read-only config screen.
- A `New series` action creates a series; on success the list refreshes and a `Series <code> created.` confirmation banner appears.
- Required states: loading (`Loading number series`), empty (`No number series`), error with `Retry`, and the saved banner.

#### Number Series Config

- `Code` is a new-series-only field and is immutable when editing an existing series.
- Fields: `Description`, `Template` (for example `CUST/{NUMBER:0000}`), `Starting number` (minimum 1), `Increment by` (minimum 1), and `Reset policy` (`Never`, `Daily`, `Monthly`, `CalendarYear`, `FinancialYear`).
- `Preview template` renders the current template with the current counter without consuming it and shows the formatted example number.
- `Save series` is disabled until `Description` and `Template` are non-empty and `Starting number` and `Increment by` are at least 1; validation re-runs as the user types.
- On save the screen pops back to the list and the list refreshes.
- A read-only user reaching the config route sees the `Access denied` panel with no editable fields.

---

## 7. Required UI States

Every transactional screen must support:

1. Loading
2. Success
3. Empty
4. Validation error
5. Authorization error
6. Network error
7. Business-rule error
8. Offline/retry state where applicable

---

## 8. Accessibility / UX Baseline

- Minimum readable text sizes.
- High contrast.
- Touch targets suitable for mobile.
- Do not encode status using color alone.
- English/Hindi-ready localization architecture.
