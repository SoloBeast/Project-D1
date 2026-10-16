# D1-D — Approved Subscription Re-Delivery Fulfillment: Audit + Locked Design

> Status: **AUDIT + DESIGN ONLY — no code, no migration implemented.**
> Locked business decisions below are **approved for implementation**.
> D1-A (delivery-fail tickets), D1-B (customer Refund/Re-Delivery choice),
> D1-C (persisted timeout + auto-refund worker), D1-C.1 (Flutter admin config UI)
> are complete and verified. This document must not change their invariants.

## 1. Scope

- D1-D fulfills an **approved** `RefundReplacementRequest` whose customer choice
  is **ReDelivery** for a **subscription** delivery failure.
- Covers: replacement `SubscriptionDelivery` creation at approval, its journey
  through the **existing** delivery pipeline
  (materialize → assign → OTP → batches → complete), entitlement accounting,
  ticket completion on replacement delivery, and the failure/cancel/skip edges.
- Non-goals (pilot): no capacity model, no customer self-pick of date, no term
  extension, no repricing, no new payment/tax math.

## 2. Verified as-is findings (code-grounded)

All claims below were verified in the current tree before writing this design.

### 2.1 Subscription occurrences and entitlement

- `Subscription` creates exactly `TotalEntitlement` occurrences at creation
  (`GenerateDates` + `AddDelivery` in
  `Backend/src/DoodhDirect.Infrastructure/Subscriptions/SubscriptionService.cs`;
  `AddDelivery` in
  `Backend/src/DoodhDirect.Domain/Subscriptions/SubscriptionEntities.cs`).
- `EndDate` = last scheduled date.
- `SubscriptionDelivery.Status`: `Scheduled | Skipped | Failed | Delivered | Cancelled`,
  one-way out of `Scheduled` (`EnsureScheduled` guard).
- `AddDelivery` guard (today): rejects dates outside `[StartDate, EndDate]`
  (`ArgumentOutOfRangeException`) and rejects a duplicate `ScheduledDate`
  (`InvalidOperationException`). D1-D needs a **new** path that bypasses the
  EndDate guard but keeps the date-uniqueness guard (see §5).
- `MarkDelivered` is the **only** place incrementing `UsedEntitlement`
  (+ sets `Completed` at `TotalEntitlement`); it throws when
  `UsedEntitlement >= TotalEntitlement` ("prepaid entitlement is exhausted").
- `MarkFailed` (via `Subscription.MarkFailed` → `SubscriptionDelivery.Fail`)
  never touches `UsedEntitlement` → a failed delivery is structurally an
  **outstanding (already-paid, unconsumed) entitlement**.
- Invariant: `COUNT(Delivered) == UsedEntitlement <= TotalEntitlement`
  (domain guard + DB check constraint).
- `UNIQUE(SubscriptionId, ScheduledDate)` on occurrences;
  `UNIQUE(Delivery.SubscriptionDeliveryId)`; `DeliveryNumber` unique (DELIVERY series).
- `Cancel(indiaLocalNow)` cancels all `Scheduled` occurrences.
- `Skip(delivery, now, cutoff)` requires Active/Paused + cutoff
  (`Subscription.SkipPauseCutoffHours`, default 24h).

### 2.2 Delivery pipeline (must be reused, not bypassed)

- Operational `Delivery` rows are created only via
  `MaterializeEligibleAsync` / `FetchSubscriptionDeliveriesAsync`
  (`Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs`)
  for `Scheduled` + Active-subscription occurrences with no `Delivery` yet,
  re-validated inside the transaction.
- Completion requires batch allocations + no pending milk test + creates
  `MilkUsage`. D1-D replacement **must** flow through this unmodified path.
- Staff `FailAsync` and milk-test `DecideAsync(reject)` both call `MarkFailed`
  plus the same `DeliveryFailTicketCreator`
  (`Backend/src/DoodhDirect.Infrastructure/RefundReplacement/DeliveryFailTickets.cs`),
  so fault and milk-test failures converge on one ticket shape.

### 2.3 RefundReplacement approval surface (creation point)

- `ApproveAsync/RejectAsync/CompleteAsync` in
  `Backend/src/DoodhDirect.Infrastructure/RefundReplacement/RefundReplacementService.cs`
  → private `DecideAsync`: serializable txn + `RowVersion` concurrency guard,
  branch-scoped (`MANAGE_BRANCH`), lifecycle check-constraint
  (`Awaiting→Approved→Completed`), audit + customer outbox event.
- Choice is locked after approval (D1-B invariant — unchanged).
- One active remedy per delivery via filtered unique index + CreateOrReuse
  (`DeliveryFailTickets.cs`).
- Calendar today: `GetCalendarAsync` returns all occurrences ordered
  (`SubscriptionService.cs`). No per-day capacity model exists anywhere.

### 2.4 D1-A/B/C invariants D1-D must not break

- Ticket entity is `RefundReplacementRequest`; public refs are only
  `RequestNumber` / `DeliveryNumber` (DELIVERY series immutable). Never expose
  OrderId / SubDeliveryId / PKs / storage keys in API, notify, or audit.
- Choice immutable; same-choice idempotent; different-choice → Conflict.
- Deadline = persisted D1-A `Deadline` (FailedAt + snapshot), exclusive
  (`now >= Deadline` rejects). No recalculation from config.
- Notify via `INotificationEventWriter` only; JSON enums via
  `JsonStringEnumConverter`.
- D1-C worker owns expired unchosen tickets; D1-D must not touch tickets that
  are not (Approved + choice=ReDelivery).

## 3. Locked business rules (approved — implement as stated)

### Rule 1 — Replacement date: hybrid (system proposes, staff confirms)

- System proposes the next eligible replacement date; Admin / Dairy Manager /
  Support staff confirms or changes it; **customer does not select** the date.
- Proposed/confirmed date must satisfy all existing applicable rules:
  subscription weekday schedule, branch active state, delivery cutoff, no
  conflicting `SubscriptionDelivery` date, and other existing eligibility rules.

### Rule 2 — End date: replacement MAY be after EndDate; term unchanged

- A replacement `SubscriptionDelivery` MAY be created after the original
  `Subscription.EndDate`.
- Do NOT extend `Subscription.EndDate`. Purchase term remains unchanged.
- Rationale recorded: replacement is fulfillment of an already-paid outstanding
  entitlement, not a new sale.

### Rule 3 — Entitlement: fail ≠ consumed; delivered ≤ TotalEntitlement

- Failed delivery does NOT consume entitlement; approved replacement fulfills
  the outstanding entitlement.
- Final delivered count must never exceed `TotalEntitlement`.
- Example: TotalEntitlement=10, original #4 fails, replacement succeeds →
  final delivered = 10, not 11.
- Enforcement: reuse `MarkDelivered` (throws when exhausted) — no new counter.

### Rule 4 — Replacement quantity/snapshot: frozen copy, no live reads

- Replacement preserves the original failed occurrence's frozen Product,
  Quantity, Branch, Branch snapshot, Address snapshot, Slot.
- Do NOT read current `Product.Price` or current customer address.
- Do NOT recalculate Tax & Charges.

### Rule 5 — Second failure: at most ONE replacement per original failure

- If the replacement delivery fails again: do NOT offer another Re-Delivery;
  create/route into a REFUND-ONLY remedy; preserve both failed delivery
  histories; refund via the existing refund mechanism.
- No endless replacement loop. Requires an explicit test.

### Rule 6 — Pending replacement + subscription cancellation

- If the subscription is cancelled while an approved replacement is pending and
  the replacement can no longer be fulfilled: preserve original history, do not
  silently drop the entitlement, route the outstanding failed entitlement to
  refund.
- Do NOT extend the term. Do NOT reprice the subscription.
- Open item (document before implementing this edge): if the current refund
  engine has no cancellation→refund calc path for this shape, document the
  exact refund calculation path separately first (§8).

### Rule 7 — Vacation/skip on replacement date: no silent skip

- A replacement occurrence must NOT be silently skipped through ordinary
  vacation/skip behavior.
- A conflicting vacation/skip request requires an explicit operational
  decision/re-approval per the existing remedy workflow; do not silently
  consume the remedy. Requires a preventive test.

### Rule 8 — Ticket completion: Completed only on replacement delivered

- Lifecycle: Approved → replacement occurrence created → replacement Delivery
  materialized → replacement Delivery completed/delivered →
  `RefundReplacementRequest = Completed`.
- `Completed` MUST represent successful fulfillment, not a manual button press
  before delivery. Reuse the existing delivery-completion event/path.
- Consequence: the generic staff `CompleteAsync` button path must NOT be usable
  to complete a Re-Delivery ticket before its replacement is delivered
  (guard inside `CompleteAsync` or a completion-routing check — see §6 item 5).

### Rule 9 — Financial immutability

- Replacement creates: no payment, no new subscription charge, no tax
  calculation, no change to `Subscription.PayableAmount`,
  `Subscription.ChargesTotal`, or `SubscriptionCharge`.

### Rule 10 — Capacity: no new model for pilot

- Use existing schedule-weekday, date-conflict, branch-active, and cutoff
  validation. Documented gap: per-day capacity is not currently modeled.

### Rule 11 — Delivery number: NEW immutable number

- The replacement operational `Delivery` gets a NEW immutable `DeliveryNumber`.
- Never reuse the original failed `DeliveryNumber`. (Follows automatically from
  reusing the materialize path, which mints via the DELIVERY series.)

### Rule 12 — Calendar: show both rows

- Customer calendar: original date → Failed; replacement date → Upcoming /
  Delivered. Do not hide the original failure.
- Implies the calendar response must expose the replacement link so clients can
  render both rows (see §6 item 7).

## 4. Recommended shape (unchanged from audit — still recommendation, not code)

- NEW `SubscriptionDelivery` row in `Scheduled` status (not a status flip of the
  failed row — the failed row is history and must stay `Failed`).
- Link columns on `SubscriptionDelivery` (one migration, 4 nullable columns):
  `ReplacesSubscriptionDeliveryId` (FK → SubscriptionDelivery, nullable),
  `RefundReplacementRequestId` (FK → RefundReplacementRequest, nullable),
  `IsReplacement` (bool, default false), `ReplacementReason` (short string, nullable).
- Create the replacement inside the `DecideAsync(approve: true)` transaction
  (same serializable txn + `RowVersion`), so approval + occurrence creation are
  atomic.
- Replacement flows through the **unmodified** materialize → assign → OTP →
  batches → complete path; `MarkDelivered` consumes the outstanding
  entitlement; ticket flips to `Completed` on replacement delivery via the
  existing delivery-completion event/path.
- Concurrency: serializable txn + `RowVersion` + `UNIQUE(SubscriptionId,
  ScheduledDate)` + deterministic idempotency/event keys (mirror the D1-C
  `DeliveryFailTimeoutProcessor` transaction pattern for races).

## 5. Date proposal + validation (Rule 1 / Rule 2 / Rule 10)

- Proposal algorithm (server-side, staff-facing): starting the day after
  approval (or after the failed date — pick one at implementation and document
  it), scan forward for the first date satisfying: subscription weekday
  schedule; branch active on that date; cutoff still satisfiable
  (`SkipPauseCutoffHours` semantics reused); no existing `SubscriptionDelivery`
  on that date for the subscription; other existing eligibility rules.
- The scan MUST be allowed to return dates after `Subscription.EndDate`
  (Rule 2) — i.e. the new `AddReplacementDelivery` domain method bypasses the
  `[StartDate, EndDate]` guard but keeps the same-date conflict guard.
- Staff may confirm the proposal or supply a different date; a staff-supplied
  date is re-validated against the same rule set before creation.
- Bounded scan (e.g. fail closed after N days with a clear error) so an
  unsatisfiable schedule cannot hang approval.

## 6. Exact implementation checklist

1. **Domain — `Subscription.AddReplacementDelivery`** (`SubscriptionEntities.cs`):
   new method that bypasses the EndDate guard, keeps the
   `(SubscriptionId, ScheduledDate)` uniqueness guard, copies frozen
   quantity/branch/snapshots/slot from the failed occurrence (Rule 4), sets the
   link fields (`ReplacesSubscriptionDeliveryId`, `RefundReplacementRequestId`,
   `IsReplacement = true`, reason). Rejects non-Failed sources and
   already-replaced sources (Rule 5: at most one).
2. **Migration — 1 migration, 4 nullable columns** on `SubscriptionDelivery`
   (+ FKs + index on `RefundReplacementRequestId`); model snapshot + check
   constraints updated; EF no-pending-changes check passes.
3. **Approval — `DecideAsync(approve: true)`** (`RefundReplacementService.cs`):
   in the same serializable txn, verify ticket is subscription-sourced,
   choice == ReDelivery, status allows approval, no existing replacement for
   the source failure; propose/validate the staff-confirmed date (§5); create
   exactly one replacement occurrence; add approval audit + customer event
   (public refs only). Approve with choice == Refund keeps current behavior.
4. **Delivery wiring — no pipeline changes**: verify replacement materializes
   via `MaterializeEligibleAsync` (post-EndDate occurrences must not be
   filtered out by term-window logic — check and adjust the eligibility query
   minimally if it assumes `date <= EndDate`); `MarkDelivered` consumes the
   outstanding entitlement (Rule 3); new `DeliveryNumber` minted by the existing
   series (Rule 11).
5. **Ticket completion — complete-on-delivery** (Rule 8): wire the existing
   delivery-completion event/path to flip the Re-Delivery ticket to `Completed`
   when its replacement delivery completes; guard `CompleteAsync` so a
   Re-Delivery ticket cannot be manually completed before replacement delivery.
6. **Second-failure → refund-only** (Rule 5): when a replacement occurrence's
   delivery fails, route to a REFUND-ONLY remedy via the existing refund
   mechanism (no new Re-Delivery offer); preserve both failed histories; add
   the explicit loop-prevention test.
7. **Cancel + skip edges** (Rules 6–7): cancelled subscription with pending
   unfulfillable replacement → preserve history, route outstanding entitlement
   to refund, no term extension/reprice (document calc path first if the engine
   lacks it — §8); replacement occurrence rejects ordinary `Skip`/vacation, or
   conflicting skip requires explicit operational decision/re-approval; add the
   silent-skip prevention test.
8. **Financial immutability test** (Rule 9): assert `PayableAmount`,
   `ChargesTotal`, `SubscriptionCharge` rows, and payments unchanged by
   approval + replacement + delivery.
9. **Calendar** (Rule 12): original stays Failed; replacement row visible as
   Upcoming/Delivered with its link; add calendar test.
10. **Notify/audit**: customer events via `INotificationEventWriter` only,
    deterministic keys (e.g. `refund-replacement:{N}:redelivery-approved`,
    `...:redelivery-delivered`); audits with `RequestNumber`; no internal IDs
    in public surfaces.
11. **Flutter**: staff approve screen surfaces proposed date + confirm/change;
    customer calendar renders both rows; reuse D1-C.1 config patterns; no
    customer date picker.
12. **Verification**: Release build 0 warn/err; domain + integration suites
    green incl. new entitlement/replacement/financial/approval/SQL-concurrency/
    lifecycle/failure/calendar/notify tests; EF no-pending-changes; migration
    applied on `.\SQLEXPRESS`.

## 7. Test plan (§26-style, to be executed at implementation)

- Entitlement: fail consumes 0; replacement delivery consumes exactly 1;
  10-fail-#4-replace → final Delivered = 10; `MarkDelivered` exhaustion guard intact.
- Replacement creation: exactly one row per approval; idempotent retry of
  approval creates no duplicate (unique + txn).
- Financial: Payable/Charges/ChargesTotal/payments untouched end-to-end.
- Approval validation: wrong choice / wrong status / existing replacement /
  invalid staff date → Conflict/BusinessRule with no partial write.
- SQL concurrency: parallel approve + parallel complete-on-delivery races on
  `.\SQLEXPRESS`, mirror D1-C race precedent.
- Lifecycle: ticket reaches Completed only via replacement delivery; manual
  `CompleteAsync` before delivery rejected.
- Failure edges: second failure → refund-only, both histories preserved, no
  second replacement row; cancel-with-pending → refund routed, history kept.
- Silent-skip prevention: ordinary skip/vacation on replacement date rejected
  or routed to re-approval; remedy never silently consumed.
- Calendar: Failed original + Upcoming/Delivered replacement both present.
- Notify: event keys deterministic; audits carry `RequestNumber` only.

## 8. Open sub-decisions to document at implementation

1. Cancel→refund calc path: if the engine lacks it, write the exact
   calculation note (frozen per-day gross? existing refund mechanism entry
   point?) separately BEFORE coding the cancel edge (Rule 6).
2. Vacation-conflict re-approval UX: which screen/action staff uses for the
   explicit operational decision (Rule 7).
3. Ticket-Complete wiring point: exact hook in the delivery-completion path
   that flips the Re-Delivery ticket (Rule 8) — reuse, don't fork.
4. Proposal start day + scan bound (§5): day-after-approval vs day-after-failed,
   and the fail-closed horizon.
5. Documented gap (Rule 10): per-day capacity not modeled — note in release notes.

## 9. History

- D1-A/B/C/C.1 complete and verified (see summary in thread / acceptance docs).
- D1-D audit delivered; locked Rules 1–12 approved in this update; design
  updated accordingly. Implementation NOT started — checklist in §6 is the next
  continuation's work order.
