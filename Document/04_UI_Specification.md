# DoodhDirect UI / Screen Specification

## 1. Common Application Shell

All role-based experiences use one Flutter application with role-aware navigation.

### Customer navigation
Home | Orders | Subscription | Wallet | Live Dairy | Profile

### Employee navigation
Today | Deliveries | Test | Replacement | Profile

### Dairy Manager navigation
Dashboard | Production | Batches | Testing | Reports

### Delivery Manager navigation
Dashboard | Live Deliveries | Assignments | Staff | Reports

### Support navigation
Dashboard | Complaints | Replacements | Customers | Reports

### Owner/Admin navigation
Dashboard | Customers | Orders | Subscriptions | Delivery | Dairy | Products | Payments | Complaints | Branches | Cameras | Employees | Reports | Settings | Audit

---

## 2. Global UI Rules

- Show loading state for every network operation.
- Show empty state where a list has no records.
- Show retry action for recoverable API/network errors.
- Never trust client-side business validation alone; repeat critical validation on the backend.
- Currency: INR.
- Quantity: litres for milk, decimal with up to 3 fractional digits.
- Display dates/times in India local time for current deployment; backend stores UTC.
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

Location must be geocoded and stored as latitude/longitude.

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
- Start date
- Entitlement period / prepaid amount
- Address
- Payment method

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
- Status
- Route/order

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
Actions:
- Create
- Assign role
- Assign branch scope
- Deactivate

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
