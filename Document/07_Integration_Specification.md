# DoodhDirect Integration Specification

## 1. Razorpay

### Responsibilities
- Payment creation
- Payment verification
- Refund
- Supported recurring payment mechanisms for future subscriptions
- Webhooks

### Rules
- Server creates gateway-side payment/order reference.
- Client opens checkout using safe public payment details.
- Server verifies signature and final status.
- Webhooks are idempotent.
- Gateway transaction ID is unique.
- Never trust a browser/app success callback as final payment confirmation.

### Failure cases
- Payment pending
- Payment failed
- Customer closes checkout
- Gateway webhook delayed
- Duplicate webhook
- Payment success but API response lost

---

## 2. Google Maps Platform

### Uses
- Address autocomplete
- Geocoding
- Reverse geocoding
- Map rendering
- Route/navigation handoff
- Distance calculations

### Data stored
- Latitude
- Longitude
- Human-readable address
- Place identifier where useful

### Rule
The backend must own branch-allocation distance decisions; do not rely only on frontend distance calculations.

---

## 3. Firebase Cloud Messaging

### Events
- Order confirmed
- Delivery assigned
- Delivery started
- Delivery near customer
- Delivery completed
- Complaint update
- Replacement update
- Subscription reminders
- Payment result

### Device registration
Store push token against UserDevice. Tokens can rotate and become invalid.

---

## 4. SMS

Use an India-compliant SMS provider.

Initial uses:
- OTP
- Critical payment/complaint messages if configured

The SMS provider should be behind an interface so it can be changed later.

---

## 5. WhatsApp

Use a WhatsApp Business/API provider.

Potential messages:
- Order confirmation
- Delivery updates
- Complaint/replacement updates
- Subscription reminders
- Payment receipts

Do not make WhatsApp the only notification channel.

---

## 6. Camera / CCTV

Initial hardware:
- IP cameras
- Separate camera/NVR application

Application responsibility:
- Store selected public camera metadata.
- Display a secure customer-facing live stream.

Recommended customer-facing streaming format:
- HLS or WebRTC depending on camera/streaming gateway capability.

Do not expose:
- Raw RTSP credentials
- NVR admin credentials
- Internal network addresses

Camera recording stays outside the application database.

---

## 7. Doorstep Milk Testing Integrations

### MVP device boundary

Readings are entered manually from the physical device or lactometer. Device-specific data and SDK types do not enter the domain model.

### Media storage boundary

`IMediaStorage` owns save, authenticated read, and cleanup operations. Development uses a local-filesystem provider. Production may replace it with object storage without changing the milk-test domain or API contract.

- SQL Server stores the provider storage key, file name, normalized MIME type, size, authenticated uploader, and upload timestamp.
- Image bytes are never stored in SQL Server.
- The upload service validates the configured size limit and the actual JPEG, PNG, or WebP signature before storage, and rejects a declared MIME type that conflicts with the signature.
- If database persistence fails after storage succeeds, the service removes the newly stored object.
- Customer content reads remain authenticated, ownership-scoped, and unavailable before completion.

### Future device integration

Bluetooth or a vendor SDK may implement:

```text
IMilkTestingDeviceAdapter
  connect()
  disconnect()
  startTest()
  readResult()
  getDeviceInfo()
```

Vendor-specific code must remain behind the adapter and must not leak into the domain model. This future integration is not part of Phase 9.

---

## 8. Accounting

Do not build accounting software in MVP.

Create an export/integration boundary for:
- Sales
- Payments
- Refunds
- Wallet adjustments
- Expenses
- Tax information

Future adapters may connect to Tally, Zoho Books, ERP or another accounting platform.

---

## 9. GST / Invoicing Readiness

Product, customer and order models must be able to carry future tax/invoice fields.

Future invoice capabilities:
- Invoice number
- Invoice date
- Place of supply
- Taxable amount
- Tax rate
- Tax amount
- Customer billing details
- Business GSTIN where applicable

Do not assume every current milk transaction needs GST merely because an invoice module exists; tax treatment should be configured based on the business's actual registration and applicable rules.

---

## 10. Integration Failure Policy

External service failure must not corrupt core business data.

Example:

Razorpay timeout after payment initiation:
- Keep payment Pending.
- Reconcile using webhook/status lookup.

FCM failure:
- Core order still succeeds.
- Notification is retried asynchronously.

Maps unavailable:
- Manual address entry fallback can be allowed.
- Geolocation can be pending until resolved.

Camera unavailable:
- Show stream offline state.
- Do not affect orders.
