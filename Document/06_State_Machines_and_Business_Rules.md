# DoodhDirect State Machines and Business Rules

## 1. Order State Machine

```text
PENDING_PAYMENT -> CONFIRMED -> ASSIGNED -> OUT_FOR_DELIVERY -> DELIVERED
                                      |                         |
                                      v                         v
                                   FAILED                 COMPLAINT/REPLACEMENT

CONFIRMED -> CANCELLED
PENDING_PAYMENT -> PAYMENT_FAILED
OUT_FOR_DELIVERY -> REJECTED_BY_CUSTOMER
```

### Rules
- Order cannot become Delivered without valid OTP verification.
- DeliveredAtUtc is written exactly once for the first successful delivery completion.
- Complaint eligibility starts only after successful delivery.
- Payment confirmation must be server verified.
- Replacement orders reference the original order.
- Historical branch on an order is immutable except through an explicit audited admin override.

---

## 2. Subscription State Machine

```text
DRAFT -> PAYMENT_PENDING -> ACTIVE
ACTIVE -> PAUSED
PAUSED -> ACTIVE
ACTIVE -> COMPLETED
ACTIVE -> CANCELLED
ACTIVE -> PAYMENT_FAILED
```

### Subscription delivery state

```text
SCHEDULED -> ASSIGNED -> OUT_FOR_DELIVERY -> DELIVERED
SCHEDULED -> SKIPPED
ASSIGNED -> FAILED
OUT_FOR_DELIVERY -> FAILED
OUT_FOR_DELIVERY -> CUSTOMER_REJECTED
```

### Entitlement rule
Only `DELIVERED` consumes one prepaid delivery entitlement.

Skipped, failed and customer-rejected deliveries do not consume entitlement unless an authorized administrative policy explicitly overrides it.

---

## 3. Complaint State Machine

```text
DRAFT -> SUBMITTED -> UNDER_REVIEW -> APPROVED_REPLACEMENT -> RESOLVED
                         |
                         -> APPROVED_REFUND -> RESOLVED
                         |
                         -> REJECTED
```

### Complaint eligibility

`EligibleUntilUtc = DeliveredAtUtc + ComplaintWindow`.

Default `ComplaintWindow = 5 hours`, configurable by admin.

Backend must calculate eligibility. Client calculations are informational only.

---

## 4. Replacement State Machine

```text
REQUESTED -> APPROVED -> ASSIGNED -> OUT_FOR_DELIVERY -> COMPLETED
     |
     -> REJECTED
```

If the customer is not available during replacement delivery, follow the same failed-delivery rules as an original delivery.

---

## 5. Payment State Machine

```text
INITIATED -> PENDING -> SUCCESS
                  |
                  -> FAILED
                  |
                  -> EXPIRED
SUCCESS -> REFUND_PENDING -> REFUNDED
```

Payment webhook processing must be idempotent.

---

## 6. Wallet Rules

Every wallet balance change creates a WalletTransaction.

Never directly mutate CurrentBalance without an auditable transaction.

Supported transaction types:

- TopUp
- SubscriptionDebit
- OrderDebit
- RefundCredit
- PromoCredit
- AdminAdjustment

Negative wallet balances should be prohibited unless a future finance policy explicitly enables them.

---

## 7. Branch Allocation Rules

1. Customer address must have valid latitude/longitude.
2. Find active branches.
3. Filter by branch service eligibility.
4. Calculate distance.
5. Filter branches with sufficient fulfillment capacity.
6. Choose closest eligible branch.
7. Save branch on order/subscription.
8. If no branch qualifies, do not confirm the order; present a serviceability message.
9. Admin may override the allocation.
10. Overrides must be audited.

---

## 8. Address Change Rules

Customer may maintain multiple addresses.

Changing the default address must not change historical orders.

For an active subscription, address changes should create a new effective address from the chosen date rather than rewriting past subscription deliveries.

---

## 9. Quantity Rules

Milk quantity is decimal litres.

Recommended technical precision: 3 decimal places.

Examples accepted:

- 0.500
- 1.000
- 1.250
- 2.000

Admin may configure minimum order quantity and increment later.

---

## 10. Production Capacity Rule

A future order-capacity integration may check available milk capacity for a near-term delivery, but Phase 8 does not implement that reservation or allocation workflow.

The Phase 8 operational inventory quantity is:

`Produced - Recorded Usage = Available`.

The current dairy service rejects usage above a batch's derived available quantity and does not create a usage row when that rule fails. It does not create `Reserved` quantities for orders or subscriptions. Reservation timing, expiry/release, allocation priority, cancellation, batch selection, and reconciliation must be specified before the conceptual formula below becomes an implemented cross-domain rule:

`Produced - Reserved - Dispatched = Available`.

---

## 11. Delivery GPS Rules

- Capture delivery completion coordinates when available.
- Live employee tracking begins only for active delivery.
- Location interval is configurable.
- Tracking stops at completion/failure.
- GPS data retention should be limited.
- Lack of GPS must not make successful delivery impossible if the business intentionally permits manual fallback; manual fallback should be audited.

---

## 12. Doorstep Test Rules

Lifecycle:

`Not requested -> Requested -> Completed -> Customer Confirmed | Customer Rejected`

- A customer may request at most one test for an active delivery they own. A unique delivery index and serializable request transaction protect this rule.
- `Delivered` and `Failed` deliveries cannot accept a request or image upload.
- Only the delivery's currently assigned employee may read or operate the staff workflow.
- Assigned staff must also hold the delivery branch claim unless granted `ACCESS.GLOBAL`.
- Completion is permitted only while the delivery is `Arrived`.
- Completion requires at least one validated external image and one valid reading.
- Reading code, name, and unit are required; codes are unique case-insensitively within a test.
- Reading values support at most six decimal places and must fit `decimal(18,6)`.
- Customer DTOs and UI never expose numeric readings or staff remarks in the MVP.
- Customer image metadata and content remain hidden until completion. Image content additionally requires customer ownership.
- Confirmation and rejection require a completed test. The first decision is terminal; repeated or conflicting decisions are rejected.
- Request, record creation, image upload, completion, confirmation, and rejection are audited with the authenticated actor.
- Testing is free to the customer. Physical readings may be shown by staff at the doorstep, but the result is an indicative check and must not be represented as laboratory certification.
- Phase 9 is delivery-owned. Future batch/lab testing may extend the abstraction only through a separately approved model.

---

## 13. Refund Rules

Refund may result from:

- Failed payment/order correction
- Approved complaint
- Customer rejection where refund policy applies
- Replacement escalation
- Admin decision

Every refund needs:
- Reason
- Amount
- Approver if manual
- Payment reference
- Audit record

---

## 14. Complaint Escalation

Admin-configurable parameters:

- Repeated complaint count threshold
- Monitoring period

Example:

3 complaints in 30 days → Admin review alert.

Do not automatically block the customer.

---

## 15. Coupon Rules

Backend validates:
- Coupon active
- Valid date
- Usage limit
- Customer usage limit
- Minimum order value
- Product/branch restrictions

Never trust discount value supplied by the client.

---

## 16. Referral Rules

Reward only after the referred customer completes the qualifying action defined by configuration.

Referral reward must be recorded as a wallet transaction.

---

## 17. Admin Override Rule

Owner/admin may override most operational rules, but the following should remain auditable:

- Refund
- Wallet adjustment
- Complaint expiry override
- Order status override
- Subscription entitlement adjustment
- Branch override
- Price change
- Role/permission change

Every override requires a reason.

---

## 18. Idempotency Rules

The following operations must be idempotent:

- Order creation from a client retry
- Payment callback
- Payment webhook
- Refund request
- Wallet credit
- Subscription delivery generation
- Replacement order creation

Use an idempotency key/reference unique within the operation scope.
