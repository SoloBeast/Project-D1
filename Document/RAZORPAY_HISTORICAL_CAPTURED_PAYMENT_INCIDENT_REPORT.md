# Razorpay Historical Captured-Payment Reconciliation Report

**Report type:** Read-only incident evidence report  
**Database:** Development SQL Server database `DoodhDirect` on `.`\\`SQLEXPRESS`  
**Scope:** Four requested historical captured-payment incidents totaling INR 2,800  
**Generated:** 2026-08-23  
**Mutation status:** No application reconciliation, cancellation, expiry, replacement-payment creation, refund, webhook processing, or database update was invoked.

## 1. Executive finding

Read-only Razorpay order-payment discovery identified four recent locally non-successful Razorpay attempts whose gateway payment records were captured:

| Local payment public ID | Local payment state | Local amount | Razorpay order ID | Razorpay payment ID | Razorpay evidence | Local target state |
|---|---:|---:|---|---|---|---|
| `B818B208-110B-4C8B-BAF5-4A078DDF2CF3` | Pending | INR 930 | `order_TT8RqpLYoBJoAq` | `pay_TT8S36LJS0HXRI` | `captured=true`, `status=captured` | Order `PendingPayment` |
| `654CDB8B-229D-46B6-AA66-691487F7960E` | Pending | INR 370 | `order_TTA9DYjf4N2imh` | `pay_TTA9VassBfJPZS` | `captured=true`, `status=captured` | Order `PendingPayment` |
| `CD1A5DCF-9981-44BB-8B35-FD67D2E8A48D` | Pending | INR 900 | `order_TTAEUmc153VmDO` | `pay_TTAEeGj6BpnUf3` | `captured=true`, `status=captured` | Order `PendingPayment` |
| `90F8B59D-5476-4A13-AEEC-CAB359A03CDB` | Expired | INR 600 | `order_TTAGJH5OlDz19M` | `pay_TTAGNRhs9NIfyC` | `captured=true`, `status=captured` | Order `PaymentFailed` |
| **Total** |  | **INR 2,800** |  |  |  |  |

The gateway evidence was obtained through Razorpay’s read-only order-payments endpoint. The four records passed the requested incident amount total exactly, but the local rows do **not** contain a gateway payment ID and still show gateway order status `created`. This is a material evidence discrepancy: the local database alone cannot establish the captured-payment outcome for these incidents.

## 2. Local before/after evidence

The query in [`scripts/incident-snapshot.sql`](../scripts/incident-snapshot.sql) was executed twice against the development SQL Server database. Each execution opened an explicit transaction and ended with `ROLLBACK TRANSACTION`.

The query captured:

- payment public ID, status, method, amount, currency, refunded amount
- locally stored gateway order/payment/status fields
- target order public ID, order number, status, and payable amount
- delivery count and delivery OTP count
- refund count and refund amount
- notification-event token matches for payment/order/gateway identifiers
- deterministic SHA-256 evidence hash over the relevant local state
- total Razorpay webhook rows and the webhook-linkage limitation

The complete command outputs are retained in:

- [`scripts/incident-snapshot-before.txt`](../scripts/incident-snapshot-before.txt)
- [`scripts/incident-snapshot-after.txt`](../scripts/incident-snapshot-after.txt)

Both output files had the identical SHA-256 hash:

```text
E533CD9396C744B8999090DA9E48B037292E98A80431EFA0D6F35058995F3DA8
```

This establishes that the two read-only observations were byte-identical. It does not claim that no unrelated process could ever mutate the database between observations; it proves that this investigation performed no mutation and observed no change in the scoped evidence.

### 2.1 Local evidence rows

| Payment public ID | Payment row ID | Local payment state | Gateway order ID | Local gateway payment ID | Local gateway status | Order number | Order state | Delivery count | OTP count | Refund count | Refund amount | Notification token matches | Evidence hash |
|---|---:|---|---|---|---|---|---|---:|---:|---:|---:|---:|---|
| `654CDB8B-229D-46B6-AA66-691487F7960E` | 60 | Pending | `order_TTA9DYjf4N2imh` | NULL | created | `DD-20260823150149-BB12912` | PendingPayment | 0 | 0 | 0 | INR 0 | 1 | `075E2D336F405D1D2C9DFE2DDAC3F6E18F8E1F8AD2CD99E02062E79B011F8C68` |
| `90F8B59D-5476-4A13-AEEC-CAB359A03CDB` | 62 | Expired | `order_TTAGJH5OlDz19M` | NULL | created | `DD-20260823150832-E385307` | PaymentFailed | 0 | 0 | 0 | INR 0 | 2 | `45F713A233A6E1664DD78396B89177330BB1801A0A19F1FD8F2A75A828AAD0C1` |
| `B818B208-110B-4C8B-BAF5-4A078DDF2CF3` | 58 | Pending | `order_TT8RqpLYoBJoAq` | NULL | created | `DD-20260823132200-54B78F7` | PendingPayment | 0 | 0 | 0 | INR 0 | 1 | `F10578EEA44C9ACC507EA5B44C1E92FAC8376D3CC036E78C9637F68BA62A9031` |
| `CD1A5DCF-9981-44BB-8B35-FD67D2E8A48D` | 61 | Pending | `order_TTAEUmc153VmDO` | NULL | created | `DD-20260823150650-6396A0E` | PendingPayment | 0 | 0 | 0 | INR 0 | 1 | `57EF209670C4650CAB165EAC43FF6A0EB24FDF132C213EBE63EFB45AA10F0BE8` |

The local query returned **zero Razorpay rows** from `PaymentWebhook`. `PaymentWebhook` has no payment foreign key or persisted payload column in the current model, so it cannot provide deterministic incident-to-webhook linkage for this report.

## 3. Incident-by-incident assessment

### 3.1 INR 930 — `B818B208-110B-4C8B-BAF5-4A078DDF2CF3`

- Razorpay order: `order_TT8RqpLYoBJoAq`
- Razorpay payment: `pay_TT8S36LJS0HXRI`
- Gateway result: captured
- Local payment: Pending, INR 930, INR currency
- Local order: `DD-20260823132200-54B78F7`, `PendingPayment`
- Local side effects: zero deliveries, zero OTPs, zero refunds
- Notification token matches: 1
- Local evidence hash: `F10578EEA44C9ACC507EA5B44C1E92FAC8376D3CC036E78C9637F68BA62A9031`

**Assessment:** A validated remote capture exists while the local payment and order remained pending. The local row lacks the gateway payment ID needed for a direct lookup, so any repair must first validate order identity, amount, currency, payment status, and capture flags through the gateway-authoritative reconciliation path.

### 3.2 INR 370 — `654CDB8B-229D-46B6-AA66-691487F7960E`

- Razorpay order: `order_TTA9DYjf4N2imh`
- Razorpay payment: `pay_TTA9VassBfJPZS`
- Gateway result: captured
- Local payment: Pending, INR 370, INR currency
- Local order: `DD-20260823150149-BB12912`, `PendingPayment`
- Local side effects: zero deliveries, zero OTPs, zero refunds
- Notification token matches: 1
- Local evidence hash: `075E2D336F405D1D2C9DFE2DDAC3F6E18F8E1F8AD2CD99E02062E79B011F8C68`

**Assessment:** A validated remote capture exists while the local payment and order remained pending. The same missing-local-payment-ID discrepancy applies.

### 3.3 INR 900 — `CD1A5DCF-9981-44BB-8B35-FD67D2E8A48D`

- Razorpay order: `order_TTAEUmc153VmDO`
- Razorpay payment: `pay_TTAEeGj6BpnUf3`
- Gateway result: captured
- Local payment: Pending, INR 900, INR currency
- Local order: `DD-20260823150650-6396A0E`, `PendingPayment`
- Local side effects: zero deliveries, zero OTPs, zero refunds
- Notification token matches: 1
- Local evidence hash: `57EF209670C4650CAB165EAC43FF6A0EB24FDF132C213EBE63EFB45AA10F0BE8`

**Assessment:** A validated remote capture exists while the local payment and order remained pending. The same missing-local-payment-ID discrepancy applies.

### 3.4 INR 600 — `90F8B59D-5476-4A13-AEEC-CAB359A03CDB`

- Razorpay order: `order_TTAGJH5OlDz19M`
- Razorpay payment: `pay_TTAGNRhs9NIfyC`
- Gateway result: captured
- Local payment: Expired, INR 600, INR currency
- Local order: `DD-20260823150832-E385307`, `PaymentFailed`
- Local side effects: zero deliveries, zero OTPs, zero refunds
- Notification token matches: 2
- Local evidence hash: `45F713A233A6E1664DD78396B89177330BB1801A0A19F1FD8F2A75A828AAD0C1`

**Assessment:** A validated remote capture exists after the local payment expired and the order entered `PaymentFailed`. This is the expired-payment recovery case. It must be repaired only through the guarded reconciliation flow, which revalidates gateway evidence and performs exact-once target confirmation under the backend transaction boundary.

## 4. Separate INR 80 anomaly

A fifth, older captured/local-non-success attempt was found during the broader read-only Razorpay discovery:

- Local payment public ID: `B49402BD-C679-42E6-A35E-2192AEC5DCA2`
- Local state: Pending
- Amount: INR 80
- Razorpay order: `order_TRNVMj8F2uZq5H`
- Razorpay payment: `pay_TRNX7OVCrPcLzV`
- Gateway result: captured

This row is **excluded** from the requested INR 2,800 incident set. It is reported separately because the broader remote/local comparison found five captured discrepancies totaling INR 2,880. It must not be silently merged into, discarded from, or repaired as part of the four-incident report.

## 5. Safety conclusion and recommended handling

1. The requested four incidents are real gateway/local convergence failures, not four locally captured rows that can be safely inferred from SQL alone.
2. The development database shows no local delivery, OTP, refund, or deterministic webhook side effect for the four scoped rows.
3. The local gateway-payment-ID fields are NULL, despite the remote order-payment lookup returning captured payment IDs. This should be treated as an identity/evidence-integrity finding and not bypassed with a blind state update.
4. No automatic refund is recommended by this report.
5. Any production remediation must use the hardened reconciliation path and validate:
   - Razorpay order identity
   - Razorpay payment identity
   - amount and currency
   - captured status and terminal flags
   - absence of conflicting payments
   - current local payment and target state under serialization
   - exact-once target and notification side effects
6. The INR 80 anomaly requires a separate review decision and must remain outside this four-incident remediation scope.

## 6. Evidence limitations

- Razorpay evidence was read-only and obtained from order-payment discovery. No gateway mutation endpoint was called.
- The local `PaymentWebhook` table contained zero Razorpay rows in this development database and does not persist a direct payment relationship in the current schema.
- Notification token matches are forensic indicators only; they are not treated as proof of payment-side-effect execution because notification payloads are not modeled with a payment foreign key.
- This report does not claim that the remote captured payments were refunded or delivered; it records only the authoritative gateway capture result and the local database state observed during the rollback-only snapshots.
