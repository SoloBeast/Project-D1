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

### 3.13 Test Request
Initial screen explains:
"Doorstep quality check is an indicative check performed using the available dairy testing device."

Actions:
- Request test
- Cancel request

After request:
- Waiting for delivery staff
- Test performed
- Test outcome recorded

The MVP does not need to display the numeric test result to the customer.

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
Shows only cameras configured as public.

Rules:
- Login required.
- 24x7 when stream is available.
- No customer playback of recordings.

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
- Request test
- Perform test
- Collect payment
- Mark failed
- Mark delivered
- Start replacement

### 4.4 OTP Verification
- Enter OTP
- Verify
- Retry with rate limit

### 4.5 Milk Test
Fields:
- Test type
- Readings relevant to configured device
- Remarks

Future:
- Bluetooth/device auto-import.

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
Filters:
- Date range
- Branch
- Customer
- Status

Exports:
- CSV
- XLSX

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
