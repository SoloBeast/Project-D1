# D1-D Slice 5 Discovery — Cancellation With Pending Re-Delivery

> Status: **DISCOVERY ONLY — no code, migration, test, config, or UI changed.**
> Answers design doc §8 open item 1 (cancel→refund calc path) far enough to
> specify implementation. Implements nothing; Rule 6 stays open pending the
> business decisions in §7.

## 1. Verified current behavior (code-grounded)

### 1.1 What cancel does — `Subscription.Cancel` + `SubscriptionService.CancelAsync`

- Domain: `Backend/src/DoodhDirect.Domain/Subscriptions/SubscriptionEntities.cs:334`
  (`Subscription.Cancel`). Refuses `Completed`/`PaymentFailed`; allows
  `PaymentPending`/`Active`/`Paused`. Sets `Cancelled` + `CancelledAt`, then
  cancels **every** `Scheduled` occurrence — no `IsReplacement` distinction,
  so an approved pending replacement becomes `Cancelled` like any other row.
- Service: `Backend/src/DoodhDirect.Infrastructure/Subscriptions/SubscriptionService.cs:296`
  (`CancelAsync`). Loads owned subscription, calls `Cancel`, invalidates OTPs
  of materialized operational deliveries
  (`InvalidateMaterializedOtpsAsync`), single `SaveChanges`, returns. It does
  **not** run in a serializable transaction (unlike Skip/Vacation/Delivery
  paths), sends **no** customer notification, queries **no** ticket, and moves
  **no** money. Ordinary prepaid cancellation therefore issues **no automatic
  refund** today.
- Occurrence cancel: `SubscriptionDelivery.Cancel` (same file) requires
  `Scheduled` (`EnsureScheduled`) — `Failed`/`Delivered`/`Skipped` rows
  (original failure included) are untouched, so history is preserved.

### 1.2 Guards on replacement occurrences (pause / skip / materialize)

- Skip/vacation of a replacement is rejected (Slice 2.1): domain
  `SubscriptionDelivery.Skip` guard + `SkipAsync` `BusinessRuleException` +
  vacation `replacementDelivery` ineligibility
  (`SubscriptionService.cs:306-436`).
- Pause is subscription-level only; nothing occurrence-specific. A `Paused`
  subscription blocks materialization (see below) transiently; `Resume`
  restores it. No ticket impact.
- Materialization (`DeliveryService.MaterializeEligibleAsync` /
  `FetchSubscriptionDeliveriesAsync`) only picks `Scheduled` occurrences of
  `Active` subscriptions. After cancel, a still-`Scheduled` replacement can
  never exist (cancel flips it), and a `Cancelled` subscription yields
  nothing — a pending remedy is permanently unfulfillable past this point.

### 1.3 The orphaned-operational-delivery gap (verified)

- `CancelAsync` leaves materialized operational `Delivery` rows in place (only
  their OTPs are invalidated). The delivery pipeline has **no**
  subscription-status check: `EnsureOperationalAccess`
  (`DeliveryService.cs:1319`) validates assignment/branch only.
- Those rows can never finish: `CompleteAsync`/`VerifyOtpAsync` reach
  `occurrence.Subscription.MarkDelivered`, which throws on the `Cancelled`
  occurrence (`EnsureScheduled`); `FailAsync` fails the same way at
  `MarkFailed`. They sit frozen in non-terminal status with dead OTPs.
- Calendar (`GetCalendarAsync`) returns all rows, so customer UI would show a
  `Cancelled` replacement with a possibly still-`Assigned` operational
  delivery — inconsistent but visible, not silent.

### 1.4 Ticket fate on cancel — nothing happens (verified)

- Zero references to cancellation in
  `Backend/src/DoodhDirect.Infrastructure/RefundReplacement/` (grep) and no
  ticket query in `CancelAsync`. The linked `RefundReplacementRequest` stays
  exactly as it was — almost always `Approved` (replacement exists only after
  approval).
- That ticket is then **stranded**: Slice 3 auto-completion needs a
  `Delivered` replacement (impossible — §1.2/1.3); manual `CompleteAsync`
  rejects Re-Delivery tickets whose replacement is not `Delivered`
  (`RefundReplacementService.cs` Slice 3 guard); the D1-C worker only takes
  `AwaitingCustomerChoice` + choiceless + expired tickets
  (`DeliveryFailTimeoutProcessor.cs:71-75,153-154`), so an `Approved` ticket
  never times out. This is the Rule 6 violation the slice must fix: an
  approved remedy that can never be fulfilled and never resolves.
- Benign case: cancel while the ticket is still `AwaitingCustomerChoice`
  (replacement never approved). The original `Failed` row survives cancel, the
  ticket still expires normally, and the D1-C worker refunds it — the worker
  performs **no** subscription-status check (`ProcessTicketAsync`
  reads subscription snapshots + payment only), so cancellation does not block
  the timeout refund. No slice needed for this state, but a test should lock
  it in.

### 1.5 Existing refund calculation + idempotency paths (verified)

- Failed-occurrence refund (D1-C), `DeliveryFailTimeoutProcessor.cs:213-258`:
  frozen `Gross = Round(Payable/TotalEntitlement, 2)`,
  `Deduction = Round(Gross * snapshot% / 100, 2)`, `final` capped by
  `payment.Amount - payment.RefundedAmount`; wallet credit keyed
  `delivery-fail:{N}:refund` with ledger reuse check (`CreditWalletAsync`);
  `Refund` row + `payment.CompleteRefund` via `RecordPaymentRefund` (key
  reuse, in-progress conflict); ticket Approve→Complete; per-ticket
  transaction with `RowVersion` conflict mapping and unique-EventKey /
  idempotency race adoption (`:340-378`). Financial fields never recalculated
  — only read as frozen snapshots.
- Approved-but-undelivered replacement: **no path exists** (§1.4). Same for
  materialized/assigned-then-cancelled replacements.
- Generic staff refund: `PaymentService.RefundAsync` (`PaymentService.cs:932`;
  explicit amount or remaining balance, idempotent by caller key, wallet
  credit for Wallet-method payments, gateway flow otherwise). Nothing invokes
  it on subscription cancel today; usable as a manual fallback, not a policy.
- Protections available for reuse: serializable transactions +
  `EnableRetryOnFailure(5)` pattern, ticket `RowVersion`
  (`RefundReplacementEntities.cs:207`, `DbContext:774-781`), unique
  `NotificationEvents.EventKey` (`DbContext:1437`), wallet/payment
  idempotency-key reuse, deterministic audit + outbox event buffering in-txn.

## 2. Cancellation states × outcomes

| # | State at cancel | Replacement row | Operational delivery | Ticket | Outcome today |
|---|---|---|---|---|---|
| A | Approved, replacement `Scheduled`, not materialized | → `Cancelled` | none | stays `Approved` | **Stranded** (never fulfillable, never resolves) |
| B | Approved, replacement materialized/assigned, undelivered | → `Cancelled` | orphaned, OTPs dead, can never complete/fail | stays `Approved` | **Stranded + orphan row** |
| C | Replacement already `Delivered` | untouched (`Delivered`) | terminal | already `Completed` (Slice 3, same txn) | Healthy — no action needed |
| D | Ticket `AwaitingCustomerChoice`, no replacement yet | n/a | n/a | stays `Awaiting` → D1-C auto-refund at deadline works (no subscription check) | Healthy — lock in with test |
| E | Paused (not cancelled) with pending replacement | stays `Scheduled` | materialization deferred | stays `Approved` | Transient — `Resume` restores; no action |

## 3. Recommendations (not decisions)

- **Recommended (Option A): cancel routes the outstanding entitlement to
  refund.** In `CancelAsync` (upgraded to the serializable pattern), when an
  `Approved` Re-Delivery ticket's replacement is still unfulfilled
  (`Scheduled`, or `Cancelled`-by-this-cancel / orphaned operational row):
  preserve both failed histories, credit the frozen one-share refund to the
  wallet (no gateway, no repricing), complete the ticket with cancel remarks,
  audit + deterministic outbox event. Reuse D1-C math, the payment-remaining
  cap, and key-reuse helpers with a **distinct idempotency namespace**
  (e.g. `cancel-redelivery:{N}:refund`) so a prior timeout credit can never
  duplicate and the remaining cap stays exact.
- **Option B (not recommended): block cancel while an Approved replacement
  pends.** Simpler code, but traps the customer and merely relocates the
  explicit-decision burden with no workflow to discharge it.
- **Option C (fallback only): manual staff `RefundAsync`.** Available today
  but amount is operator-chosen and ticket linkage is unaudited; acceptable as
  an interim runbook, not the policy.
- Opportunistic cleanup inside the same slice: mark orphaned operational
  deliveries from state B terminally failed/voided via the existing fail
  plumbing (no new statuses), so no frozen assigned rows remain. Flagged as
  optional — it touches delivery history presentation.
- Financial example (actual code math, harness-scale numbers): Payable
  160.00, entitlement 2 → Gross 80.00; snapshot 10% → deduction 8.00 →
  **72.00** wallet credit, capped by `payment.Amount - RefundedAmount`.
  Fails ≠ consumed, cancel consumes nothing, so exactly one share is
  outstanding — refunding it cannot exceed the frozen paid total when the cap
  is applied.

## 4. Decisions requiring explicit business approval

1. Cancel-refund deduction: reuse the ticket's snapshot % (consistent with
   timeout), 0% (full share), or a separate cancel policy? (Do not assume the
   cancellation policy determines the failed-delivery amount.)
2. Wallet-only credit vs. gateway/original-method refund for this path.
3. Auto-refund on cancel (Option A) vs. blocking cancel (Option B) vs. manual
   fallback (Option C).
4. Whether orphaned operational rows (state B) are voided in-slice or left
   frozen with calendar-only visibility.
5. Audit action name + completer identity for system-driven cancel completion
   (e.g. `REFUND_REPLACEMENT.CANCEL_REFUND`, completer = cancelling actor).

## 5. Proposed implementation sequence + test matrix

1. Domain: none expected (reuse `Cancel`, `Complete`, frozen math; no new
   statuses). If voiding state-B rows needs it, extend `Fail` reasons only.
2. `CancelAsync`: serializable txn; detect unfulfilled Approved-ticket
   replacements; credit + complete ticket same-txn (Option A);
   audit/event with distinct keys; keep histories, term, financials frozen.
3. Tests: A→refunded+Completed, single credit on retry/concurrent cancel,
   B→no orphan completable rows, C untouched, D→timeout refund still fires
   post-cancel, first-failure flows unchanged, cap respected when payment
   partially refunded, no second replacement creatable, manual `RefundAsync`
   interplay (no double credit across namespaces).
4. Verification: focused + full Domain/Integration Release, EF validation,
   Release build (same bar as Slices 2–4).

## 6. Verified vs. assumed

- **Verified in code:** §1.1–§1.5, the state table, D1-C math/caps/keys,
  orphan-row terminality (`EnsureScheduled`), worker's lack of subscription
  check, `CancelAsync` having no txn/notification/ticket/money handling.
- **Recommendations (§3):** engineering judgment, not approved policy.
- **Unresolved assumptions:** the five §4 decisions; exact customer-facing
  messaging; whether gateway refunds are in scope for cancel; backfill policy
  for already-stranded tickets (none known in production data — assumed).
