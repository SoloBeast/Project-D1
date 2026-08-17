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

## 3. Notification Delivery Integrations

### Durable event boundary

Business modules append provider-neutral notification events in the same persistence boundary as their business change. They never invoke Firebase Cloud Messaging, SMS, WhatsApp, or email providers directly. The background notification processor claims pending events, renders configured templates, creates an in-app notification, evaluates preferences, creates independent channel deliveries, and records every attempt and final outcome.

Supported event types cover registration, authentication, order creation, payment outcomes, wallet updates, subscription lifecycle, delivery lifecycle, and doorstep milk-test lifecycle. Templates are also seeded for complaint and replacement updates, but producer integration for those two event types remains deferred until those business modules exist.

The event key provides idempotency. Processing and retries must not duplicate the durable inbox item or channel delivery for the same event, recipient, and channel.

### Channel gateway boundary

`INotificationChannelGateway` is implemented independently for `Push`, `Sms`, `WhatsApp`, and `Email`. A provider may be replaced without changing business modules, event contracts, templates, persistence, APIs, or Flutter models.

Each channel has separate configuration and failure state:

- One unavailable or failing channel does not block the inbox or other channels.
- Disabled preferences create a suppressed delivery rather than calling a provider.
- Retryable failures are scheduled with bounded attempts and a future UTC retry time.
- Permanent destination failures are terminal. An invalid push destination also invalidates the corresponding device token.
- Unconfigured providers are recorded as `Unconfigured`; they do not report false delivery success.
- Every provider call creates an attempt record with outcome and sanitized diagnostics.

### Provider selection

Development may explicitly select `DevelopmentMock` independently for each channel. Mock gateways record deterministic success without external network calls and are prohibited outside the Development environment.

Production fails closed per channel. Every enabled channel must select an operational real provider; a missing or mock selection resolves to an unconfigured gateway and cannot synthesize success. Provider credentials and endpoint secrets are injected through environment configuration or a managed secret store.

### Firebase Cloud Messaging and devices

Push uses Firebase Cloud Messaging behind the push gateway. `POST /devices` registers or rotates the authenticated user's token only after the client reports authorized or provisional permission.

- Startup may inspect permission but must not trigger the operating-system prompt.
- The prompt follows explicit user action only.
- Token refresh is synchronized with the current authenticated session.
- The token hash supplies identity and uniqueness; the protected token supplies the provider destination.
- Plaintext tokens are not persisted, returned by the API, or logged.
- Permanent invalid-token responses deactivate the device destination.

Foreground messages refresh inbox and unread state. Opened messages and cold-start messages may navigate only through the Flutter internal deep-link allowlist.

---

## 4. SMS

Use an India-compliant SMS provider behind the shared channel gateway. Phase 11 notification SMS uses rendered templates and the durable delivery/retry lifecycle; identity OTP delivery remains a separate identity integration. SMS destination and provider diagnostics must be redacted from logs and attempts.

---

## 5. WhatsApp

Use an approved WhatsApp Business/API provider behind the shared channel gateway. WhatsApp is optional and must never be the only durable customer-notification surface; the authenticated inbox remains available. Template/provider approval failures are isolated to the WhatsApp delivery and follow retryable or permanent failure classification.

Email follows the same gateway, template, preference, attempt, and fail-closed rules even though it has no separate external-provider section in this specification.

---

## 6. Camera / CCTV

### Integration boundary

IP cameras and the camera/NVR system remain external infrastructure. DoodhDirect stores only branch-linked camera metadata and an opaque, non-secret provider reference. `ICameraStreamGateway` is the only application boundary that may translate that reference into customer playback access.

The gateway contract supports:

- Capability checks by `Hls` or `WebRtc` protocol and provider code.
- Availability checks without disclosing provider details.
- Issuance of a short-lived playback URI, UTC expiry, protocol, and development-stream marker.

The public API never returns the provider code or provider stream reference. Camera/NVR credentials, internal network addresses, hardware configuration, recordings, raw RTSP URLs, and private upstream URLs must not enter SQL Server, API DTOs, logs, analytics, or Flutter persistence.

### Adapter selection

Production fails closed when no production stream adapter is configured. The unconfigured adapter reports cameras unavailable and refuses descriptor issuance; it must not synthesize or reveal an upstream URL.

Development may explicitly select the `DevelopmentMock` adapter. It:

- Is prohibited outside the Development environment.
- Supports HLS only.
- Accepts only an absolute HTTPS playback URI as its development reference.
- Marks every descriptor with `isDevelopmentStream: true` so the client displays a visible warning.

A production HLS or WebRTC provider may replace the adapter without changing domain entities, application services, public DTOs, or Flutter models.

### Descriptor lifecycle

Playback descriptors expire after a configured short lifetime, defaulting to five minutes and constrained to 1–60 minutes. Clients keep descriptors in memory only, discard them on expiry, and request a replacement. Descriptor issuance does not grant recording playback and does not create recording metadata.

### Failure policy

- Unknown, inactive, and private camera identifiers return the same public `404` behavior.
- An unsupported provider/protocol combination or gateway outage returns `503` without exposing provider diagnostics.
- Public list availability is informational; descriptor issuance remains authoritative.
- Monitoring may record camera ID, provider code, protocol, result category, latency, and correlation ID, but never credentials or playback URIs.
- Camera outage runbooks must distinguish metadata/API health from external gateway and upstream camera health.

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

Notification provider failure:
- The core business operation and durable notification event still succeed.
- Inbox materialization and each channel delivery are processed asynchronously.
- Retryable channel failures are rescheduled with bounded attempts; permanent and unconfigured outcomes become terminal.
- A failing channel does not block other channels, and no provider failure may roll back completed business data.

Maps unavailable:
- Manual address entry fallback can be allowed.
- Geolocation can be pending until resolved.

Camera unavailable:
- Show stream offline state.
- Do not affect orders.
