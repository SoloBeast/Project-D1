# Payment Finalization & Recovery — Design Review

**Status:** Design review only. No code, database, or configuration has been modified.
**Scope:** Razorpay capture succeeded but DoodhDirect `Payment` remained `Pending` / `Expired`; `dbo.PaymentWebhook` has 0 rows; reconciliation is manual-only; there is no automatic stale-payment safety net.
**Diagnostic evidence:** Payment 88 and the four historical payments (INR 2,800 + INR 80) are treated as evidence only. They are not modified, retried, refunded, or manually marked successful here.

---

## 1. Current payment confirmation architecture

Three server-side entry points exist today, and **all three converge on one authoritative finalization routine**.

### 1.1 Client verify (primary path)

[`PaymentService.VerifyAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:484):

1. Loads the payment by `PublicId` + `CustomerId` via `PaymentQuery()` (which eagerly loads `Order` and `Subscription`) — [`PaymentService.cs`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:1171).
2. Requires `Method == Razorpay`.
3. If already `Success`, returns idempotently only when `GatewayPaymentId` matches, else throws `ConflictException`.
4. Allows `Pending` or `Expired`.
5. Validates `payment.GatewayOrderId == request.GatewayOrderId` **and** verifies the Razorpay payment signature `orderId|paymentId` via [`RazorpayPaymentGateway.VerifyPaymentSignatureAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentGateway.cs:227) using `MockPaymentGateway.VerifyHmac()` (HMAC-SHA256, fixed-time compare) — [`PaymentGateways.cs`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentGateways.cs:117).
6. Resolves the authoritative gateway state via `ResolveGatewayPaymentAsync()`; rejects `Ambiguous` and `Pending` outcomes.
7. Inside `ExecuteSerializableAsync` (SQL Server `Serializable`): reloads payment+target, validates identity/financials, then `RecoverCaptured` (if Expired) or `Succeed`, then `ConfirmTargetAsync`, then `AddPaymentOutcomeEvents`, then `SaveChangesAsync`.

### 1.2 Razorpay webhook (already implemented)

- Endpoint: [`RazorpayWebhooksController`](Backend/src/DoodhDirect.Api/Controllers/PaymentsWalletController.cs:117) → `POST /api/v1/webhooks/razorpay`, `[AllowAnonymous]`, requires `X-Razorpay-Signature`; reads the raw body (≤ 1 MiB) and calls `IPaymentService.ProcessWebhookAsync(byte[], signature, ct)` — [`PaymentsWalletController.cs`](Backend/src/DoodhDirect.Api/Controllers/PaymentsWalletController.cs:122).
- [`PaymentService.ProcessWebhookAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:979):
  1. Verifies the HMAC-SHA256 signature over the **raw body** using the webhook secret ([`VerifyWebhookSignatureCoreAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentGateways.cs:290)); throws `UnauthorizedAppException` on failure.
  2. Parses the event (`event` id/type, entity, order/payment/refund ids, status, amount, currency).
  3. **Durable idempotency:** if a `PaymentWebhook` row already exists for `(Provider, EventId)`, it returns early.
  4. Persists a `PaymentWebhook` row (unique index `(Provider, EventId)`), handling a `DbUpdateException` race.
  5. Inside `ExecuteSerializableAsync`: `StartProcessing()` → dispatch by event type → `Complete()` / `Reject()`; on any exception it clears the change tracker, reloads the row, marks `Fail("WEBHOOK_PROCESSING_FAILED", …)`, and rethrows.
- Dispatch: `payment.*` → [`ProcessPaymentWebhookAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:1069) (finds payment by `GatewayOrderId` **or** `GatewayPaymentId`; `Succeed`/`RecoverCaptured` + `ConfirmTargetAsync`); `refund.*` → `ProcessRefundWebhookAsync()`; anything else → `Reject("UNSUPPORTED_EVENT")`.

### 1.3 Reconciliation (manual only)

[`PaymentService.ReconcileAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:918) is invoked only by `POST /api/v1/payments/{paymentId}/reconcile`, which requires the `PaymentsRefund` permission and passes `bypassOwnership: true` — [`PaymentsWalletController.cs`](Backend/src/DoodhDirect.Api/Controllers/PaymentsWalletController.cs:77). It resolves the gateway state and, when `Captured`, runs the **same** `Succeed`/`RecoverCaptured` + `ConfirmTargetAsync` path. It is the only recovery mechanism, and nothing calls it automatically.

### 1.4 The single authoritative finalization routine

[`PaymentService.ConfirmTargetAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:1267) is shared by verify, webhook, and reconcile:

- `Order` target → `Order.ConfirmPayment()` (or `Order.RecoverCapturedPayment()` when recovering) → `IOneTimeDeliveryCreator.AddIfMissing()` → `SaveChangesAsync()` → `IOneTimeDeliveryCreator.IssuePendingOtpsAsync()` — [`PaymentService.cs`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:1273).
- `Subscription` target → `Activate(now)` / `RecoverCapturedPayment(now)`.
- No target → `IWalletService.CreditWalletTopUpAsync(...)`.

`AddIfMissing` is itself idempotent (skips when a delivery already exists for the order, or one was added locally) — [`DeliveryService.cs`](Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:43). `IssuePendingOtpsAsync` is idempotent via the durable notification event key `delivery:{PublicId}:otp-issued` — [`DeliveryService.cs`](Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:489).

### 1.5 Payment state machine

[`Payment.CompleteSuccess()`](Backend/src/DoodhDirect.Domain/Payments/PaymentEntities.cs:171) is the shared implementation of `Succeed` (`allowExpired: false`) and `RecoverCaptured` (`allowExpired: true`):

- Already `Success` → returns when the gateway payment id matches (idempotent); throws when it differs.
- Allows `Initiated` / `Pending`, plus `Expired` **only** when `allowExpired && Method == Razorpay`.
- Requires a gateway payment id for Razorpay/Development.
- [`Payment.Expire()`](Backend/src/DoodhDirect.Domain/Payments/PaymentEntities.cs:235) is idempotent and only moves `Initiated`/`Pending` → `Expired` with `FailureCode = "PAYMENT_EXPIRED"`.
- [`Payment.Fail()`](Backend/src/DoodhDirect.Domain/Payments/PaymentEntities.cs:210) is idempotent and only moves `Initiated`/`Pending` → `Failed`.

Target coupling (this is the critical constraint for recovery):

- [`Order.ConfirmPayment()`](Backend/src/DoodhDirect.Domain/Orders/OrderEntities.cs:125) requires `PendingPayment` (idempotent when already `Confirmed`).
- [`Order.RecoverCapturedPayment()`](Backend/src/DoodhDirect.Domain/Orders/OrderEntities.cs:141) requires `PaymentFailed` (idempotent when already `Confirmed`).
- [`Order.FailPayment()`](Backend/src/DoodhDirect.Domain/Orders/OrderEntities.cs:157) only moves `PendingPayment` → `PaymentFailed`.

**Invariant implied by the code:** an `Expired` payment is only recoverable because `ConfirmTargetAsync(recover: true)` calls `RecoverCapturedPayment()`, which requires the order to be in `PaymentFailed`. Therefore *any* local expiry that does not also move the target to `PaymentFailed` would make later capture-recovery throw and permanently strand the payment.

### 1.6 Idempotency and transactions

- `ExecuteSerializableAsync` begins a `Serializable` transaction under an execution strategy — [`PaymentService.cs`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:1499). The delivery service's equivalent detects an ambient `CurrentTransaction` and runs inline (no nested begin) — [`DeliveryService.cs`](Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:1217).
- Unique indexes: `Payment.GatewayOrderId`, `Payment.GatewayPaymentId`, `PaymentWebhook(Provider, EventId)`, `Delivery.DeliveryNumber` (filtered), `Delivery.OrderId` (filtered). See the model snapshot around [`DoodhDirectDbContextModelSnapshot.cs`](Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/DoodhDirectDbContextModelSnapshot.cs:2785) and [`…:2886`](Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/DoodhDirectDbContextModelSnapshot.cs:2886).
- Domain methods are idempotent (see §1.5), `AddIfMissing` is idempotent, and OTP issuance uses a durable event key.

---

## 2. The exact gap

The confirmation machinery is substantially built. The gap is **operational and supervisory**, not a missing finalization path:

1. **No automatic reconciliation.** `ReconcileAsync` exists but is only reachable via an authenticated admin endpoint. If the client verify call never arrives and no webhook is delivered, a captured payment stays `Pending` indefinitely.
2. **No stale-payment expiry.** There is no hosted service that marks overdue `Pending` payments `Expired`. The only hosted services are `NotificationWorker` and `StartupSeedWorker` — [`DependencyInjection.cs`](Backend/src/DoodhDirect.Infrastructure/DependencyInjection.cs:159) and [`…:192`](Backend/src/DoodhDirect.Infrastructure/DependencyInjection.cs:192). So there is no "no Pending forever" safety net.
3. **`PaymentWebhook` has 0 rows.** The endpoint is implemented and anonymous, but if the webhook URL/secret is not configured at Razorpay (or the endpoint is not publicly reachable behind the Cloudflare tunnel), nothing arrives. This is consistent with the incident report's finding that the table is empty.
4. **Failed webhooks are permanently stuck.** [`ProcessWebhookAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:1001) short-circuits on an existing `(Provider, EventId)` row **without inspecting its status**. Because Razorpay redelivers the same event id, a row left in `Failed` (e.g., a transient finalization error, or a `ConflictException` from a still-`Pending` gateway state) will never be re-driven — every redelivery is treated as a duplicate and returns HTTP 200.
5. **No persisted webhook payload / no payment FK.** [`PaymentWebhook`](Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs:616) stores only a SHA-256 `PayloadHash`, with no raw payload and no `PaymentId` FK. This limits post-incident forensics for exactly the Payment 88 class of issue.
6. **Non-atomic outbound side effect.** `ConfirmTargetAsync` calls `IssuePendingOtpsAsync` while still inside the payment transaction; the OTP transport loop runs before the outer commit — [`DeliveryService.cs`](Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:579). An SMS can therefore be sent for a transaction that subsequently rolls back.

The net effect: a captured Razorpay payment can remain non-finalized forever unless an operator manually reconciles it.

---

## 3. Recommended architecture (smallest architecture-compatible solution)

Extend the existing path; do not add a parallel one.

1. **Add one hosted `PaymentReconciliationWorker`** (`BackgroundService`) that periodically:
   - **Reconciles** candidate `Pending`/`Expired` Razorpay payments that have a `GatewayOrderId` and are not finalized, by invoking the **same** authoritative routine used by verify/webhook/reconcile.
   - **Expires** candidates that remain `Pending` past `ExpiresAt` and are not captured, applying the §1.5 invariant (also move the target to `PaymentFailed`) so that a later captured-payment recovery still works.
   - Never assumes the payment failed at Razorpay.
2. **Add a single public service method** (e.g. `ReconcileStalePaymentsAsync`) reusing `ResolveGatewayPaymentAsync` + `Succeed`/`RecoverCaptured`/`Fail`/`Expire` + `ConfirmTargetAsync`. **No finalization logic is duplicated.**
3. **Preserve** verify and webhook exactly as they are, including signature verification.
4. **Harden** the webhook duplicate rule so failed/unprocessed events can be re-driven (§5).
5. Optionally persist a bounded webhook payload and/or a `PaymentId` FK for audit (migration; not required for correctness).

This keeps the deterministic, already-tested finalization path as the single source of truth and adds only a supervisory loop plus its configuration.

---

## 4. Should the webhook be enabled?

**Yes.** The webhook is the only push-based, provider-authoritative signal that survives a client that crashes or closes the browser. It is fully implemented (signature-verified, idempotent, durable), so enabling it is configuration + exposure work, not development.

Required Razorpay-side configuration:

- **Endpoint URL:** `POST https://<public-host>/api/v1/webhooks/razorpay`
- **Secret:** set `RAZORPAY_WEBHOOK_SECRET` (mapped by `LocalDotEnvLoader` to `Payments:RazorpayWebhookSecret`) — [`Program.cs`](Backend/src/DoodhDirect.Api/Program.cs:343). In non-development the same value must be supplied via the integration settings store / configuration.
- **Events (minimum):** `payment.captured`, `payment.failed`. Also subscribe `refund.processed`, `refund.failed` to drive refund finalization.
- Avoid subscribing to the intermediate `payment.authorized`: it resolves to a `Pending` outcome, which `ProcessPaymentWebhookAsync` treats as unsafe and rejects with `ConflictException`, producing noisy failed rows.
- **Signature:** HMAC-SHA256 over the raw request body in `X-Razorpay-Signature`. The controller reads the exact bytes, so no middleware may rewrite the body.
- **Cloudflare/UAT:** the tunnel must expose the path publicly without a Cloudflare Access challenge (the endpoint itself is `[AllowAnonymous]`, so no JWT/CORS is involved for a server-to-server call). Never log the secret or the signature.

**Critical companion fix (gap #4):** when a duplicate `(Provider, EventId)` is found, treat only `Processed`/`Rejected` as terminal; re-drive rows in `Received`/`Failed`/stale-`Processing`. Otherwise provider redelivery cannot repair a transient failure.

---

## 5. How reconciliation should work

- **Trigger:** scheduled background scan (`PaymentReconciliationWorker`), plus the existing manual endpoint.
- **Candidates:** `Method == Razorpay`, `Status in (Pending, Expired)`, not finalized, `GatewayOrderId` present, `ExpiresAt <= now - grace` (and, for `Expired`, not yet recovered). Batch-limited.
- **Per payment (its own `Serializable` transaction):**
  1. Reload payment + target (`Order`/`Subscription`) to avoid stale state.
  2. `ResolveGatewayPaymentAsync`:
     - `Captured` → `RecoverCaptured` (if `Expired`) or `Succeed` (if `Pending`), then `ConfirmTargetAsync(recover)`, then `AddPaymentOutcomeEvents`, then `SaveChangesAsync`.
     - `DefinitivelyNotCaptured` → `Fail` + `FailTarget`, then events, then save.
     - `Pending`/`Ambiguous` → do **not** invent a terminal state; leave for the next scan (and let the expiry rule apply only when the local expiry has passed).
  3. Any exception is caught and logged at the payment level so one failure cannot abort the batch.
- **Reuse:** the worker must call the same `Succeed`/`RecoverCaptured` + `ConfirmTargetAsync` routine (ideally via the shared service method). It must **not** re-implement finalization.
- **Idempotency:** safe to run repeatedly — `CompleteSuccess`/`ConfirmPayment`/`RecoverCapturedPayment`/`AddIfMissing`/OTP event-key guards already make repeats no-ops.
- **Concurrency:** with multiple API instances, `Serializable` isolation plus unique indexes prevent duplicate side effects. Optionally add a short-lived DB lease (or `sp_getapplock`) so only one instance scans at a time.

---

## 6. How stale payment expiry should work

- **Rule:** a payment is stale when `Status == Pending` and `ExpiresAt <= now`.
- **Action:** `Payment.Expire(now)` (existing idempotent rule; `FailureCode = "PAYMENT_EXPIRED"`), and **also** `FailTarget(payment)` so the target moves to `PaymentFailed`.
- **Why the target must fail:** [`Order.RecoverCapturedPayment()`](Backend/src/DoodhDirect.Domain/Orders/OrderEntities.cs:141) requires `PaymentFailed`. Because `ConfirmTargetAsync(recover: true)` is only selected when `payment.Status == Expired`, an expired payment whose order remains `PendingPayment` can never recover a later capture — it would throw. This mirrors the existing pattern in [`CompleteDevelopmentAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:618), which expires **and** fails the target together.
- **Guard:** only call `FailPayment()` when the target is still `PendingPayment`; if the target is already `Confirmed` (e.g., a duplicate payment attempt for an already-paid order), expire the payment without touching the target.
- **Never assume gateway failure:** expiry is a *local* timeout. It does not assert the Razorpay payment failed, and it intentionally leaves the door open for a later `payment.captured` webhook or reconciliation to call `RecoverCaptured` → `ConfirmTargetAsync(recover: true)` → `RecoverCapturedPayment()`.
- **Wallet/no-target payments:** `FailTarget` is a no-op; expiry alone is correct.
- **Expiry of payments with no `GatewayOrderId`:** safe to expire directly (no gateway order can be captured).

**Implementation note:** this is why a naive "expire only" worker would be unsafe. The recommendation is architecture-compatible only if expiry and target-fail happen in the same transaction, matching the existing domain rules.

---

## 7. Transaction & idempotency strategy

### Atomicity of finalization (requirement 8)

Finalization is already atomic under normal EF behaviour:

- Verify, webhook, and reconcile each wrap `Succeed`/`RecoverCaptured` + `ConfirmTargetAsync` + `AddPaymentOutcomeEvents` + `SaveChangesAsync` in one `Serializable` transaction.
- `DeliveryService.ExecuteSerializableAsync` detects the ambient transaction and runs inline — [`DeliveryService.cs`](Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:1219) — so payment success, order confirmation, delivery creation, and OTP issuance share the caller's transaction.
- Consequently a delivery/number-series failure (e.g., the `Delivery.DeliveryNumber` unique index) rolls back payment success too. The historical "Payment Success while order finalization fails" state cannot occur structurally; the payment stays `Pending`/`Expired` and becomes a reconciliation candidate.

### One hardening item

`IssuePendingOtpsAsync` sends the OTP over the transport **before** the outer transaction commits — [`DeliveryService.cs`](Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:579), called from [`ConfirmTargetAsync()`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:1287). On rollback this can emit an orphan SMS. Recommended: keep the DB write in-transaction but move transport after commit (e.g., publish the issuance event and let `NotificationWorker` deliver it). This is a hardening change, proposed — not required to close the primary gap.

### Idempotency guarantees to preserve

| Mechanism | Guard |
|---|---|
| Payment success | `CompleteSuccess` returns early when already `Success` with the same gateway id; throws on a different id |
| Order confirmation | `ConfirmPayment`/`RecoverCapturedPayment` return early when already `Confirmed` |
| Webhook event | unique `(Provider, EventId)` + durable row (needs status-aware re-drive fix) |
| Delivery | `AddIfMissing` skips existing delivery (DB or local) |
| Delivery OTP | durable event key `delivery:{PublicId}:otp-issued` |
| Wallet credit | idempotency key `payment:{PublicId:N}` |
| Refund | unique `GatewayRefundId` + `(PaymentId, IdempotencyKey)` |

Exactly-once is achieved by the combination of `Serializable` transactions and these idempotent domain guards.

---

## 8. Exact files/components that would change (future implementation — not now)

| # | File | Change |
|---|---|---|
| 1 | [`PaymentContracts.cs`](Backend/src/DoodhDirect.Application/Payments/PaymentContracts.cs:159) | Add `ReconcileStalePaymentsAsync(...)` (and result type) to `IPaymentService` |
| 2 | [`PaymentService.cs`](Backend/src/DoodhDirect.Infrastructure/Payments/PaymentService.cs:918) | Add stale scan reusing `ResolveGatewayPaymentAsync` + `ConfirmTargetAsync`; add expiry with the `FailTarget` invariant; make webhook duplicate check status-aware |
| 3 | `Backend/src/DoodhDirect.Infrastructure/Payments/PaymentReconciliationWorker.cs` | **New** `BackgroundService` (periodic scan, batching, per-payment isolation, logging) |
| 4 | `Backend/src/DoodhDirect.Infrastructure/Payments/PaymentReconciliationOptions.cs` | **New** options (enabled, interval, batch size, grace, max attempts) |
| 5 | [`DependencyInjection.cs`](Backend/src/DoodhDirect.Infrastructure/DependencyInjection.cs:159) | Register options + `AddHostedService<PaymentReconciliationWorker>()` |
| 6 | [`appsettings.json`](Backend/src/DoodhDirect.Api/appsettings.json:21) | Add a `Payments:Reconciliation` section (no secrets) |
| 7 | [`DeliveryService.cs`](Backend/src/DoodhDirect.Infrastructure/Deliveries/DeliveryService.cs:489) | Optional hardening: move OTP transport outside the transaction |
| 8 | [`DoodhDirectDbContext.cs`](Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs:616) + migration | Optional: webhook raw-payload column and/or `PaymentId` FK for forensics |
| 9 | `Backend/tests/DoodhDirect.Api.IntegrationTests/` | New reconciliation tests (see §9) |

No change is proposed to the Razorpay signature verification, the client verify contract, or the domain state machine.

---

## 9. Testing plan

Integration tests (against the SQL Server test database) should assert the invariant **"a payment can never be `Success` while a one-time order is left `PendingPayment`, and duplicate side effects are never created."**

| # | Scenario | Assertion |
|---|---|---|
| 1 | Client verify success | Payment `Success`, Order `Confirmed`, exactly one Delivery, exactly one OTP event, wallet credited once |
| 2 | Webhook `payment.captured` success | Same finalization as (1) |
| 3 | Duplicate webhook (same event id) | No second finalization; no duplicate Delivery/OTP/wallet credit |
| 4 | Webhook + client verify race | Exactly one finalization; second call is an idempotent no-op |
| 5 | Reconciliation of a captured `Pending` payment | `Success` + target confirmed; delivery/OTP once |
| 6 | Reconciliation after local expiry (`Expired` + order `PaymentFailed`) | `RecoverCaptured` succeeds; order `Confirmed`; no duplicate delivery |
| 7 | Razorpay `payment.failed` | Payment `Failed`, Order `PaymentFailed`, no delivery |
| 8 | Invalid webhook signature | `UnauthorizedAppException`; no `PaymentWebhook` row; no finalization |
| 9 | Wrong amount | Rejected by `EnsureGatewayFinancials`; payment stays un-finalized |
| 10 | Wrong currency | Rejected by `EnsureGatewayFinancials` |
| 11 | Wrong Razorpay order id (client verify) | `ValidationAppException`; no finalization |
| 12 | Duplicate finalization (repeat verify/webhook/reconcile) | No duplicate Payment success/Order confirm/Delivery/OTP/wallet credit |
| 13 | Delivery creation failure (forced `DeliveryNumber` collision) | Transaction rolls back; payment not `Success`; order not `Confirmed`; no partial state |
| 14 | Retry after transient finalization failure | Later reconciliation finalizes exactly once |
| 15 | Stale `Pending` past expiry, gateway not captured | Payment `Expired` and target `PaymentFailed`, in one transaction |
| 16 | Stale `Pending` past expiry, **then** `payment.captured` | `RecoverCaptured` → order `Confirmed`; delivery/OTP once |
| 17 | Failed webhook redelivery (same event id) | Re-driven (after the status-aware duplicate fix) and finalized |
| 18 | Ambiguous gateway state | No state invented; retried on next scan; no duplicate side effects |
| 19 | Wallet top-up verify/webhook race | Credited exactly once (idempotency key) |
| 20 | Multi-instance concurrent reconciliation | Unique indexes + Serializable ensure exactly-once |

Extend existing suites in [`PaymentsWalletControllerTests.cs`](Backend/tests/DoodhDirect.Api.IntegrationTests/PaymentsWalletControllerTests.cs) and [`PaymentWalletServiceTests.cs`](Backend/tests/DoodhDirect.Api.IntegrationTests/PaymentWalletServiceTests.cs); add a dedicated `PaymentReconciliationTests.cs`.

---

## 10. Payment 88 and historical captured payments

These remain **diagnostic evidence only**. This review does not modify, retry, refund, or mark them successful.

The safe future procedure, once the fixes above are implemented and tested, is:

1. Deploy the status-aware webhook re-drive and the reconciliation worker.
2. Run a targeted, audited reconciliation on the affected payments through the **existing** authoritative path (which will `RecoverCaptured` + confirm target + create delivery/OTP exactly once).
3. Confirm each repair against Razorpay's captured-payment record and the audit trail.

This is a separate, explicitly approved operational action — not part of this design review.

---

## 11. Summary of the recommended architecture

- **Keep** the existing verify, webhook, and reconciliation mechanisms and the single `ConfirmTargetAsync` finalization path.
- **Enable** the Razorpay webhook (endpoint `POST /api/v1/webhooks/razorpay`, secret `RAZORPAY_WEBHOOK_SECRET`, events `payment.captured`/`payment.failed`/`refund.*`) and expose it through the Cloudflare tunnel without interception.
- **Fix** the webhook duplicate rule so `Failed`/`Received`/stale-`Processing` events can be re-driven.
- **Add** a periodic `PaymentReconciliationWorker` that reuses the authoritative path to (a) recover captured payments and (b) expire overdue payments **together with** moving the target to `PaymentFailed`, preserving capture-after-expiry recovery.
- **Rely** on `Serializable` transactions plus the existing idempotent domain guards (and unique indexes) for exactly-once finalization.
- **Harden** OTP transport so it occurs after commit.
- **Optionally** persist a bounded webhook payload / `PaymentId` FK for forensics.

No code, database, or configuration has been modified. This review stops here.
