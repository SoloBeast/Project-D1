# Audit — Tax & Charges Extension to Prepaid Subscriptions

**Scope:** audit only. No code was changed.
**Inputs inspected:** `ChargeEntities.cs`, `ChargeService.cs`, `ChargeContracts.cs`, `OrderServices.cs` (ResolveChargesAsync / CalculateAsync), `SubscriptionEntities.cs`, `SubscriptionService.cs`, `SubscriptionContracts.cs`, `SubscriptionsController.cs`, `PaymentService.cs` (CreateForSubscriptionAsync / RetrySubscriptionAsync / refund paths), `WalletService.cs` (DebitSubscriptionAsync / CreditSubscriptionRefundAsync), `PaymentEntities.cs`, migration `20260928211230_ChargeMasterAndOrderChargeSnapshots`, Flutter `charge_models.dart`, `subscription_models.dart`, `subscription_screens.dart`, `subscription_controller.dart`, and the existing test suites.

---

## 1. Existing subscription pricing calculation

- A subscription is **single-product, single-quantity, single-slot**: `Subscription(ProductId, Quantity, UnitPrice, TotalEntitlement, …)`. "Multiple schedules" are day-of-week + slot entries; they all deliver the same product/quantity. There is no multi-product subscription concept anywhere in domain, service, or contracts.
- `UnitPrice` is snapshotted from the server-authoritative `product.Price` at creation time (`SubscriptionService.CreateAsync`).
- **`PayableAmount` is computed inside the domain entity constructor:**
  `PayableAmount = Round(Quantity × UnitPrice × TotalEntitlement, 2, MidpointRounding.AwayFromZero)`.
  `TotalEntitlement` = prepaid delivery count; exactly `TotalEntitlement` `SubscriptionDelivery` rows are pre-generated at creation (`GenerateDates`).
- Deliveries carry **no money** (date, slot, quantity, branch/address snapshots only). No per-delivery pricing exists today.
- The payable amount is **never recalculated** after creation: `UpdateAsync` forbids quantity/address/schedule changes; retry-payment reuses the stored `PayableAmount`.

## 2. Subscription taxable base

- The only authoritative base is the existing prepaid total: `Base = Round(Quantity × UnitPrice × TotalEntitlement, 2, AwayFromZero)` (today's `PayableAmount` value).
- There is **no discount, no tip, no other component** in the subscription flow, so the base is unambiguous.
- Because a subscription is single-product, the *item-level* base and the *global* base are the **same number** — no proration or allocation logic is needed (unlike one-time checkout with a multi-line cart).

## 3. Global-charge calculation

- Global charge (`Active && ApplicableOnAll == true`) applies to the **whole subscription base**.
- `Amount = Round(Base × Percentage / 100, 2, AwayFromZero)` — same rounding rule as orders.
- Documented formula (final business rule):
  - `Base = Round(Quantity × UnitPrice × TotalEntitlement, 2, AwayFromZero)`
  - `ChargesTotal = Σ Round(Base × Pct_i / 100, 2, AwayFromZero)` over every applicable active charge
  - `PayableAmount = Round(Base + ChargesTotal, 2, AwayFromZero)`
- The one-time `OrderService.ResolveChargesAsync` **cannot be copied verbatim** (it takes a single base and applies every active charge); it must be generalized to accept per-product bases (see §4 and §9).

## 4. Item-charge calculation

- Item charge (`Active && ApplicableOnAll == false` + `ProductCharge` mapping) applies to a subscription **only when the mapped product is the subscription's product**. Base for that charge = the subscription base (single product ⇒ the mapped product's full prepaid value).
- Inactive charges never apply. In the one-time flow the same master means item charges must get a **per-line base** (`qty × price` of the mapped product's cart lines) while global charges keep the `subtotal − discount` base — this is a required consequence of the shared master (see §9) and is **not** a change to rounding semantics.
- **Critical gap:** `ApplicableOnAll` and `ProductCharge` **do not exist yet** anywhere in the backend (verified by grep). The Charge entity today is `ChargeType / ChargeCode / Description / Percentage / IsActive / IsUsed` with "every active charge applies to checkout" semantics. Applicability is **new schema + new rules**, not a flag flip.

## 5. Multiple subscription products

- **Not supported by design.** One subscription = one product. Multi-product purchasing = multiple subscriptions, each with its own base, charges snapshot, and payment. No cross-product bundling, no shared cart, no allocation.
- Consequence: the subscription charge calculation never needs multi-product allocation. If multi-product subscriptions are ever introduced, this decision must be revisited.

## 6. Historical subscription charge snapshot

- **A dedicated `SubscriptionCharge` snapshot entity is required** (mirror of `OrderCharge`: `ChargeType, ChargeCode, Description, Percentage, BaseAmount, Amount` + `SubscriptionId` FK). Reusing `OrderCharge` would force a polymorphic FK (OrderId vs SubscriptionId) — rejected.
- No FK/navigation to the `Charge` master; reference by `ChargeCode` (same as orders).
- Snapshot rows are written once inside the subscription-creation transaction; nothing ever updates or deletes them (except with the subscription's own lifecycle, which never edits money).
- `Subscription` gains `ChargesTotal` (decimal(18,2), NOT NULL default 0) and the `Charges` collection, plus a one-shot guard identical to `Order.AddCharges` so snapshots can never be appended twice.
- Old subscriptions must not change when the master changes: guaranteed because `PayableAmount` / `ChargesTotal` / snapshot rows are frozen at creation and all read paths render snapshot data only.

## 7. Wallet / Razorpay impact

- **None structurally.** Both paths already consume a single authoritative amount:
  - `Payment.CreateForSubscription(..., subscription.PayableAmount, ...)` → `payment.Amount`.
  - Wallet: `walletService.DebitSubscriptionAsync(customerId, subscriptionId, paymentId, payment.Amount, "payment:{publicId:N}")` inside the serializable transaction, then `payment.Succeed` + `ConfirmTargetAsync` (activates the subscription).
  - Razorpay: gateway order `ToMinorUnits(payment.Amount)` and `EnsureGatewayFinancials` already asserts gateway amount == payment amount.
  - Refunds cap at `payment.Amount − RefundedAmount` and credit the wallet via `CreditSubscriptionRefundAsync` — automatically charge-inclusive once `PayableAmount` is.
- Once `PayableAmount` includes charges at creation, wallet/Razorpay rules need **zero changes** — the gateway amount equals the server-authoritative payable by construction. Existing business rules (attempt evidence, expiry, reconciliation) remain untouched.

## 8. Idempotency / concurrency

- Subscription create: idempotency by unique `(CustomerId, IdempotencyKey)`; race handled by catching `DbUpdateException` and re-querying (`CompleteCreationAsync` also replays the exact original payment method and validates the request matches via `EnsureMatchingCreationRequest` → `ConflictException` on mismatch).
- **Gap:** unlike `OrderService.CreateAsync`, subscription creation runs in a plain `SaveChangesAsync` — **no serializable transaction** around the charge read. If charges are resolved for the payable, the read+snapshot+save must be wrapped in `ExecuteSerializableAsync` (order-style) so an admin charge edit cannot land between read and save.
- Payments: already serializable for the wallet path with `CaptureAttemptEvidence`/`RevalidateAttemptEvidenceAsync`, `EnsureNoActivePayment`/`EnsureNoCompletedPayment`; Razorpay does a serializable pre-check then calls the gateway between transactions (unchanged). Payment idempotency keys are `(CustomerId, IdempotencyKey)` with a conflict rule on different subscription/method.
- Retry-payment reuses the **frozen** `PayableAmount` — no recalculation on retry, so a charge edited between attempts can never change the amount mid-lifecycle. ✔
- Charge master `MarkUsed`: today set only by order creation ("applied to orders"); must be extended to subscription creation (and product-mapping existence) so used-charge guards reflect real usage.

## 9. Exact backend changes required

1. **`Charge` entity + migration:** add `ApplicableOnAll` (bit, NOT NULL, default **true** — preserves current one-time semantics for existing rows). Rules: `ApplicableOnAll = true` ⇒ must have zero `ProductCharge` mappings (enforced on create/update); switching `false → true` refused while mappings exist; `Active = false` never applies anywhere.
2. **New `ProductCharge` entity + table** (`ChargeId`, `ProductId`, unique pair) + admin endpoints under the Tax & Charges controller (`SETUP.TAX_CHARGES.MANAGE`): list/add/remove product mappings; surfaced in `ChargeResult`/admin UI.
3. **Generalize charge resolution** (extract from `OrderService.ResolveChargesAsync` into a shared helper): given (globalBase, perProductBases) produce charge lines — global charges → globalBase; item charges → the mapped product's base(s). One-time checkout passes cart-line bases; subscription passes its single base for both.
4. **`Subscription` domain:** add `ChargesTotal`, `Charges` collection (one-shot `AddCharges` guard mirroring `Order`). Decide where `BaseAmount` lives: keep `PayableAmount` as the **charge-inclusive** value (recommended — payments/wallet/gateway/refunds read it today) and expose the prepaid base from `Quantity × UnitPrice × TotalEntitlement` (or add a persisted `BaseAmount` column for display; see §13).
5. **`SubscriptionService.CreateAsync`:** wrap product/address/branch/charge-resolution + `SaveChangesAsync` in `ExecuteSerializableAsync`; resolve applicable charges for the product, build `SubscriptionCharge` snapshots, set `ChargesTotal`, construct the entity with the charge-inclusive payable; mark charges used (`MarkUsed`) in the same transaction.
6. **New `SubscriptionCharge` entity/table** (§6) + `ToResult` mapping.
7. **Contracts:** `SubscriptionResult` += `charges[]` (ChargeType/Code/Description/Percentage/BaseAmount/Amount), `chargesTotal`, and the base amount; `CreatedSubscriptionResult` inherits via `SubscriptionResult`.
8. **Optional but recommended:** `POST api/v1/subscriptions/preview` mirroring `checkout-preview` (base + per-charge lines + total, no persistence).
9. **`ChargeService`:** applicability guards (§9.1), `IsUsed` extended to "used by orders **or** subscriptions **or** product mappings" for the ChargeType-edit/delete locks.
10. Keep `OrderService` one-time behavior and rounding untouched apart from the per-line base for item charges (§4).

## 10. Exact Flutter changes required

1. `charge_models.dart`: parse `applicableOnAll` (+ product-mapping models); `ChargeConfigScreen`: applicability switch + product-mapping editor with server-guard errors surfaced (mapping removal before switching to global).
2. `SubscriptionResult` model (`subscription_models.dart`): parse-only `charges[]`, `chargesTotal`, base — display verbatim, **zero client math**.
3. Subscription setup screen (`subscription_screens.dart`): today `estimate = product.price × _quantity × _entitlement` labelled *"Estimate only; final payable amount is confirmed by the server."* — keep as-is (label already disclaims authority) or wire the preview endpoint (§13).
4. Created-subscription / payment-result screens: render the charge breakdown from the server payload (mirror the checkout `_AmountRow` pattern); payment amount already comes from the server and becomes charge-inclusive automatically.
5. Subscription detail screen: render the persisted `SubscriptionCharge` snapshot rows (historical truth) — handle legacy subscriptions with empty `charges[]` ("No charges recorded").
6. Delivery calendar / deliveries / home week strip: **no changes** — no per-delivery charging exists or is wanted.
7. Checkout charge display (Stage 2): untouched; only the shared `Charge` model gains fields.

## 11. Migration impact

- `Charge.ApplicableOnAll` bit NOT NULL DEFAULT 1 → existing charges stay global ⇒ one-time checkout behavior unchanged on deploy.
- `ProductCharge` table (unique `(ChargeId, ProductId)`, FKs to Charge/Product).
- `Subscription.ChargesTotal` decimal(18,2) NOT NULL DEFAULT 0 (+ optional `Subscription.BaseAmount` decimal(18,2) NOT NULL DEFAULT 0 — decision §13). Historical rows: `ChargesTotal = 0` ⇒ `PayableAmount` stays exactly what it was ⇒ **no backfill**, old subscriptions remain valid.
- `SubscriptionCharge` table (mirror of `OrderCharge` columns + `SubscriptionId` FK + index).
- Live dev DB note: the Stage-1 migration is applied; the new migration must be applied to the same DB before testing.

## 12. Tests required

- **Domain:** `Subscription.AddCharges` one-shot guard; payable = base + Σ snapshot; rounding invariants (AwayFromZero, 2dp).
- **ChargeServiceTests:** ApplicableOnAll guards (global with mapping refused; mapped → global blocked until mappings removed); ProductCharge CRUD + permissions; delete/type-edit guards now include subscription/mapping usage.
- **OrderServiceTests (one-time, regression + new):** global charges on `subtotal − discount`; item charge applies only to the mapped product's line base in a mixed cart; inactive excluded; ChargesTotal sum invariant (existing tests must stay green).
- **SubscriptionServiceTests:** payable = base + Σ charges; snapshot immutability (change percentage / deactivate / flip ApplicableOnAll / remove mapping ⇒ old subscription & payment unchanged); idempotent replay returns the same payable; serializable-transaction behavior on concurrent charge edit.
- **SubscriptionPaymentWalletIntegrationTests:** wallet debit amount == charge-inclusive payable; Razorpay order amount == same (EnsureGatewayFinancials); retry uses the frozen amount; refund caps at charge-inclusive amount; subscription activation on success.
- **Flutter:** charge admin applicability UI; subscription created/detail snapshot rendering (incl. legacy empty-charges state); setup-screen estimate label unchanged; existing checkout display tests stay green.

## 13. Remaining business decisions

1. **Persist `BaseAmount` on Subscription?** Recommended: yes (display/audit symmetry with OrderCharge.BaseAmount) — else recompute base = Quantity × UnitPrice × TotalEntitlement for display (safe: quantity/unitPrice frozen).
2. **Subscription preview endpoint** — recommended (mirrors checkout-preview; removes reliance on the client estimate), vs. keeping the "Estimate only" label.
3. **Show charge rows on subscription detail/history** — recommended (snapshot is already the source of truth).
4. **Item-charge per-line base in one-time checkout** — the shared master forces this; confirm the business accepts that a product-mapped charge applies to cart lines of that product only (previously every active charge hit the whole base).
5. **Cancel/refund semantics** — refunds are full `PayableAmount` (tax-inclusive). Confirm no requirement to refund tax portions differently.
6. **Vacation / skipped deliveries** — no per-delivery refund or charge proration today; charges are term-level. Confirm this remains accepted under the charge-inclusive model.
7. **`IsUsed` semantics** — extend to "referenced by orders, subscription snapshots, or product mappings" (blocks type edit/delete). Confirm.
8. Legacy subscriptions show no `charges[]` — UI copy for that state ("No charges recorded").
9. Existing open polish: trailing-zero percentage display (2.50% → 2.5%) — applies to the new surfaces too.

---

## 15. FINAL BUSINESS CLARIFICATIONS — SUBSCRIPTION DELIVERY FAILURE

**Status: design-only. No code was changed for this section.** These are the
approved final rules. Implementation is explicitly deferred.

**Freshly verified against the current tree** (this section only):
`SubscriptionEntities.cs` (ctor / `AddCharges` / `MarkFailed` / `MarkDelivered` /
`SubscriptionDelivery.Fail`), `SubscriptionService.cs` (`MarkDeliveryFailedAsync`,
`ResolveSubscriptionChargesAsync`), `DeliveryEntities.cs` (`ReferenceNumber` /
`DeliveryNumber` / `Fail`), `DeliveryService.cs` (materialization, number-series
assignment), `RefundReplacementEntities.cs` + `RefundReplacementService.cs`
(`RequestNumber`, `Deadline`, status machine), `PaymentService.cs` (`RefundAsync`:
`StartRefund` / `CompleteRefund` / `refund:{id}` wallet key), `WalletService.cs`
(`CreditSubscriptionRefundAsync`, `MutateAsync` idempotency lookup,
`ExecuteAtomicAsync` SERIALIZABLE), `NotificationEntities.cs` +
`DoodhDirectDbContext.cs` (unique `EventKey` index), `NotificationSecurityAndWorker.cs`
+ `PaymentReconciliationWorker.cs` + `DependencyInjection.cs` (worker precedent +
registration), `RefundReplacementConfigurationService.cs` (existing `WindowHours`).

### 15.1 Refundable amount (final rule)

- The refundable amount **before** Delivery Fail Deduction % is a share of the
  customer's **final charge-inclusive subscription amount actually paid** — i.e.
  the frozen `Subscription.PayableAmount` (product value + all applicable Tax
  & Charges). **Never** the bare product value.
- Frozen financial state that must be read, never recomputed (§15.8):
  `Subscription.PayableAmount`, `Subscription.ChargesTotal`, the
  `SubscriptionCharge` snapshot rows, and the original payment (`payment.Amount`).
- Single-product, fixed-entitlement model ⇒ exact per-delivery allocation:
  - `Allocation = Round(PayableAmount / TotalEntitlement, 2, AwayFromZero)`
    (project rounding convention: `decimal.Round(x, 2,
    MidpointRounding.AwayFromZero)` — verified in `SubscriptionEntities.cs`,
    `ResolveSubscriptionChargesAsync`, wallet and order paths).
  - `Deduction = Round(Allocation × DeductionPct / 100, 2, AwayFromZero)` using
    the **ticket's frozen** deduction % (§15.7).
  - `WalletCredit = Allocation − Deduction` (exact decimal subtraction; both
    inputs already 2dp).
- Worked example from the business (exact): PayableAmount ₹840,
  TotalEntitlement 5 ⇒ Allocation ₹168.00; Deduction 10% ⇒ ₹16.80;
  wallet credit **₹151.20**.
- **Rounding-drift note (accepted tolerance, no remainder ledger today):**
  `SubscriptionDelivery` rows carry no money, so each failed occurrence refunds
  the same independently-rounded share. When `PayableAmount / TotalEntitlement`
  is not exact to 2dp, the sum of all per-occurrence shares can differ from
  `PayableAmount` by at most ~1p × occurrences. No cumulative-remainder rule
  exists — either accept this bound or add a remainder rule at build time.
- **Entitlement accounting is untouched by failure:** `MarkFailed` does **not**
  increment `UsedEntitlement` (only `MarkDelivered` does — verified
  `SubscriptionEntities.cs`). The refund compensates the failed occurrence's
  share; it does not "spend" entitlement and must not adjust it.
- **Existing cap reused, not reinvented:** `Payment.RefundAsync` already caps at
  `payment.Amount − payment.RefundedAmount` and tracks `CompleteRefund(amount)`.
  Delivery-fail refunds must flow through the same `Payment.Refund` aggregate
  (partial amounts allowed: `request.Amount ?? remainder`) so cumulative
  delivery-fail refunds across a term can never exceed what was paid.
- Supersedes §13.5 for this flow: delivery-fail refunds are per-occurrence
  shares of the charge-inclusive payable, not full-payable refunds.

### 15.2 Ticket reference (verified existing numbers)

| Surface | Existing business number | Internal ids (never expose) |
|---|---|---|
| One-time delivery | `Order.OrderNumber` (also `Delivery.ReferenceNumber`) | `OrderId` |
| Subscription-occurrence delivery | **`Delivery.DeliveryNumber`** (DELIVERY series) | `SubscriptionDeliveryId`, `Delivery.Id` |
| Refund/replacement request | `RefundReplacementRequest.RequestNumber` (series-assigned once) | request `Id` |

- Verified `DeliveryService.cs`: every materialized delivery gets
  `AssignDeliveryNumber(GetNextNumberAsync("DELIVERY", …))` — one-time and
  subscription occurrences alike — and `AssignDeliveryNumber` throws if called
  twice (immutable). Staff/customer surfaces already fall back to
  `DeliveryNumber ?? ReferenceNumber`.
- **Decision:** the Delivery-Fail ticket surfaces **`Delivery.DeliveryNumber`**
  as its customer/staff reference. Do **not** use `Delivery.ReferenceNumber`
  for subscription occurrences: it is the internal composite
  `SUB-{subscriptionPublicId:N}-{yyyyMMdd}` (verified `DeliveryService.cs`),
  which leaks the subscription public id. Do not invent a new customer-facing
  source id. (The ticket itself still gets its own series number, mirroring
  the `RequestNumber` precedent — that is the *ticket's* number, not the
  *source's*.)

### 15.3 Automatic-timeout architecture (audited recommendation)

- **Audit result: no Hangfire/Quartz anywhere** (zero references in `src` and
  `tests` — adding either is rejected unless a future requirement forces it).
  Correction to the request wording: there *is* an established lightweight
  mechanism — **`BackgroundService` workers**, three of them:
  `NotificationWorker` (10s poll), `PaymentReconciliationWorker` (60s poll +
  batch), `StartupSeedWorker`, all registered in `DependencyInjection.cs` via
  `AddHostedService`. The timeout processor **must follow this exact pattern**:
  `BackgroundService.ExecuteAsync` → `while (!stoppingToken…)` →
  `CreateAsyncScope` → scoped processor call → catch-all log →
  `Task.Delay(pollInterval, stoppingToken)`; poll interval from an
  `IOptions<T>` options class (precedent: `NotificationOptions`,
  `PaymentReconciliationOptions`).
- **Recommended flow (matches the approved sequence):**
  1. Delivery marked Failed **and** the Delivery-Fail ticket created in the
     **same transaction** (one ticket per `Delivery`; unique index on
     `DeliveryId` so a double-fail attempt cannot mint two tickets).
  2. Ticket persists: `FailedAt`, `CustomerChoiceDeadlineAt =
     FailedAt + DeliveryFailWindowHours(snapshot)`, the window-hours snapshot,
     the deduction-% snapshot (§15.7), status = awaiting-customer-choice.
     Commit, then customer notification (event written in the same unit of
     work, per the `notificationEventWriter.Add` + `SaveChangesAsync` convention).
  3. Worker scans (indexed query): status awaiting-choice AND
     `CustomerChoiceDeadlineAt <= now`. Required index: `(Status,
     CustomerChoiceDeadlineAt)` — mirrors the existing
     `(Status, OccurredAt)` index on `NotificationEvent`.
  4. Per ticket, atomically: re-check still-awaiting → resolve Refund → record
     `ResolutionSource = AutomaticTimeout` → approve → compute §15.1 amounts
     from the **frozen subscription snapshot** → apply the **ticket's frozen**
     deduction % → credit wallet exactly once (deterministic key §15.4) →
     update ticket → audit (`AddAuditLog`, same as all money/config mutations)
     → notify customer (deterministic event key §15.4).
- **Hook point (design decision):** create the ticket in the *materialized*
  `Delivery.Fail` path for `SubscriptionOccurrence` deliveries (the doorstep
  event carrying `FailedAt`/`FailureReason`/employee), not in
  `SubscriptionService.MarkDeliveryFailedAsync` alone (occurrence-level, no
  doorstep evidence). Guard with the one-ticket-per-`DeliveryId` unique index.

### 15.4 Idempotency (must all hold)

1. **One ticket per delivery** — unique index on the ticket's `DeliveryId`;
   the racing second insert fails instead of double-minting.
2. **Status-gated transitions** — every state change re-checks
   `status == awaiting-choice` *inside* the serializable unit (precedent:
   materialization re-validates `Scheduled` in-transaction; domain `Fail`
   rejects terminal states; `SubscriptionDelivery.Fail` is a silent no-op when
   already `Failed`). Customer-chooses-Refund vs timeout-Refund therefore
   collapses to exactly one winner; the loser sees a non-awaiting ticket.
3. **Wallet credit exactly once** — `WalletService.MutateAsync` looks up
   `(WalletId, IdempotencyKey)` inside its SERIALIZABLE unit and returns the
   existing transaction on a hit (verified). The timeout path must use a
   deterministic key derived from the ticket (precedent:
   `$"refund:{refund.PublicId:N}"` in `PaymentService.RefundAsync`), e.g.
   `delivery-fail:{ticketPublicId:N}:refund` — never a fresh GUID per attempt.
4. **Payment-level bound** — route through `payment.StartRefund(amount, …)` +
   `CompleteRefund(amount)` so the existing `Amount − RefundedAmount` cap and
   the refund aggregate stay the single source of truth for cumulative refunds.
5. **Notifications** — `NotificationEvent.EventKey` has a **unique index**
   (verified `DoodhDirectDbContext.cs`) and writers enqueue with explicit keys
   (precedent: `$"wallet-transaction:{id}:updated"`). Timeout notifications
   must use deterministic keys (e.g.
   `delivery-fail-timeout:{ticketPublicId:N}:resolved`) so a retried cycle
   cannot duplicate the customer message; duplicate emission fails on the
   unique index instead of double-sending.

### 15.5 API-restart safety

- The deadline (`CustomerChoiceDeadlineAt`) and all snapshots live in
  **columns**, never in memory: no per-ticket timers, no in-memory queues.
  After a restart the worker resumes scanning on its poll interval and picks
  up every expired ticket — including ones that expired *during* the outage.
- Tick processing itself must be crash-safe at every step: each cycle is one
  serializable unit; a crash between cycles leaves the ticket awaiting-choice
  (reprocessed) or fully resolved (skipped by the status gate) — never half
  credited, by §15.4 rules 2–3.

### 15.6 Multi-instance safety (existing conventions, no new infra)

- No lease table, no distributed lock: the claim **is** the status transition
  inside a SERIALIZABLE transaction (`ExecuteSerializableAsync` + execution
  strategy with retry — the same shape as charge/product/payment mutations).
  Two instances racing the same ticket serialize; the loser's in-transaction
  re-check sees non-awaiting (or its write conflicts and retries into the
  same observation) and processes zero rows.
- Unique constraints are the backstop, not the mechanism: one-ticket-per-
  `DeliveryId`, wallet `(WalletId, IdempotencyKey)`, `NotificationEvent.EventKey`,
  following the existing `(CustomerId, IdempotencyKey)` payment and
  `(ChargeId, ProductId)` mapping precedents.

### 15.7 Configuration snapshot (final rule)

- Two **new** admin-config keys are required (neither exists today — verified:
  no deduction-% setting anywhere; the existing `RefundReplacementConfiguration`
  `WindowHours` (default 48) is the *customer-initiated post-delivery request*
  window and must **not** be reused): `DeliveryFailWindowHours` and
  `DeliveryFailDeductionPercentage`, managed under the existing
  refund/replacement-setup permission surface.
- The ticket snapshots **both values as columns at creation**. Later admin
  edits affect only tickets created afterwards. The timeout worker and the
  deduction math read **only** the ticket columns — never live config.

### 15.8 Subscription financial immutability (final rule)

- Never modified after creation: `Subscription.PayableAmount`,
  `Subscription.ChargesTotal`, `SubscriptionCharge` rows (one-shot `AddCharges`
  guard throws on re-entry — verified), and the original payment
  (`payment.Amount`). No setters exist on these paths by design.
- The delivery-fail refund is a **separate** `Payment.Refund` + wallet
  `RefundCredit` transaction pair against the original payment (§15.4.4 +
  `CreditSubscriptionRefundAsync`), exactly like the existing wallet-refund
  path. Nothing about the subscription row changes except delivery/occurrence
  statuses, which already have their own transitions.

### 15.9 Corrections to earlier sections of this report

- §4 stated `ApplicableOnAll` / `ProductCharge` "do not exist yet" — **stale**:
  Stage 1 has since implemented them (entity + migration
  `20261001191437_ChargeApplicabilityAndProductCharges`, `ChargeService`
  guards, concurrency tests). The §9 item 1 is therefore done, not pending.
- New since this audit: `ChargeResult.ProductCount` (list/single reads carry
  assigned-product counts; the admin UI locks the global toggle while > 0),
  and `UpsertProductApiRequest.ApplicableChargeIds` (the HTTP layer previously
  dropped the app's charge assignment — fixed with binding regression tests).
- §13.5 is superseded by §15.1 for delivery-fail; it remains true only for
  full payment-level refunds.
- §8's gap note (no serializable transaction around subscription charge
  resolution) is unchanged by this section and still stands.

### 15.10 Build-time checklist (when implementation is approved)

1. `DeliveryFailTicket` entity/table (+ unique `DeliveryId`, status enum with
   awaiting-choice, `ResolutionSource` Manual/AutomaticTimeout, snapshot
   columns, persisted deadline, `(Status, CustomerChoiceDeadlineAt)` index) —
   **not** shoehorned into `RefundReplacementRequest` (its ctor requires
   one-time `OrderId`/`DeliveryId`; different lifecycle).
2. Ticket creation inside the `Delivery.Fail` subscription-occurrence path,
   same transaction; new `DELIVERYFAIL`-family number-series ticket number
   (precedent: `DELIVERY` numbers, `RequestNumber`).
3. Customer choice endpoints (Refund / Re-Delivery) honoring the deadline and
   the §15.4 status gate; re-delivery path definition is out of scope of these
   rules and needs its own business sign-off (new occurrence vs reschedule).
4. `DeliveryFailTimeoutWorker : BackgroundService` + options + registration +
   per-ticket serializable processing per §15.3–§15.6.
5. Config keys + admin read/update (owner/admin permission surface, same as
   `RefundReplacementConfigurationService`).
6. Tests: per-occurrence math incl. the §15.1 example (840/5, 10% → 151.20)
   and non-even-split rounding; double-fail single ticket; customer-vs-timeout
   race → one resolution; restart-mid-cycle → no double credit (wallet
   transaction count = 1); concurrent workers → one refund; config change
   mid-flight → ticket keeps snapshots; notifications emitted once per
   deterministic key.
