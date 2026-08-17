# DoodhDirect Security, Compliance and Operations

## 1. Security Baseline

- TLS for all external traffic.
- Hash passwords using a modern password hashing algorithm.
- Short-lived access tokens.
- Rotating/revocable refresh tokens.
- OTP rate limiting.
- Login attempt protection.
- Role + permission authorization on every protected endpoint.
- Input validation.
- Parameterized database access.
- Secure file upload validation.
- Secrets stored outside source control.
- Signed/expiring media URLs.
- Audit logs for sensitive actions.

---

## 2. Role/Permission Matrix

### Owner
Full global access.

### System Administrator
Global configuration and technical administration; financial privileges should still be explicitly assigned if desired.

### Dairy Manager
Branch-scoped dairy/production/testing access.

### Delivery Manager
Branch-scoped delivery/customer operational data.

### Delivery Staff
Only assigned/current delivery information, operational customer data and delivery/test functions.

### Customer Support
Customer and complaint information needed for support; no unrestricted financial or system configuration by default.

### Accountant
Financial/reporting data; no need for delivery/GPS details by default.

---

## 3. Customer Data

Data categories:
- Identity
- Contact
- Address
- Location
- Order history
- Complaint history
- Payment references

Access should be role-restricted.

The owner's requested global access is implemented through permission, not by bypassing audit/security layers.

---

## 4. Location Privacy

Customer address coordinates are operationally sensitive.

Employee location should be exposed:
- To the customer only while the relevant delivery is active.
- To authorized operational staff.

Do not provide a permanent public employee tracking feed.

---

## 5. Financial Security

The application should never store raw card details.

Use Razorpay-hosted/tokenized mechanisms.

Store gateway IDs and status, not sensitive payment credentials.

Manual refund/wallet operations require audit records.

---

## 6. File Upload Security

Complaint attachments:
- Allow configured image/video MIME types.
- Maximum file size.
- Virus/malware scanning where available.
- Randomized storage object key.
- Never execute uploaded content.
- Generate short-lived download URLs.

---

## 7. Audit Requirements

Audit:
- Old value
- New value
- Actor
- Timestamp
- Entity
- Action
- Reason for override

Mandatory audited actions:
- Refund
- Wallet adjustment
- Price change
- Role change
- Permission change
- Complaint expiry override
- Subscription entitlement override
- Branch override
- Order status override

---

## 8. Backup

SQL Server:
- Automated backups.
- Point-in-time recovery according to chosen service/tier.
- Periodic restore testing.

Object storage:
- Versioning where practical.
- Lifecycle management.

---

## 9. Disaster Recovery

Define:
- RPO target
- RTO target

Suggested pilot targets:
- RPO: 1 hour or better for core transaction data.
- RTO: 4 hours or better.

Targets should be re-evaluated before scaling to larger operations.

---

## 10. Monitoring

Monitor:
- API latency
- 5xx errors
- Payment failure
- Webhook failure
- Database errors
- Background job failures
- Push/SMS/WhatsApp failures
- GPS ingestion issues
- Camera stream health

Create alert thresholds and on-call/escalation procedures.

---

## 11. FSSAI / Food Compliance

The business should complete the applicable FSSAI registration/licensing and food-safety process before commercial launch. The platform should store compliance details for the business/branch.

Do not present a lactometer/doorstep reading as definitive laboratory certification or a guarantee of complete purity.

The business should define written SOPs for:
- Milking hygiene
- Equipment cleaning
- Storage
- Handling
- Delivery
- Complaint handling
- Replacement
- Test-device calibration/usage

---

## 12. Privacy / Data Protection

The application should support a privacy notice and appropriate consent/notice flows as required by applicable Indian law at launch.

Data minimization should be applied.

Provide processes for:
- Access/correction requests
- Account deactivation
- Support/grievance handling
- Retention/deletion according to policy

Before launch, obtain legal review for the actual business, data flows and applicable obligations.

---

## 13. GST / Financial Compliance Readiness

The system should be tax-ready without hardcoding today's tax outcome.

Admin can configure future tax/invoice attributes.

The business should obtain professional tax/accounting advice before invoicing and GST implementation.

---

## 14. Production Environment Rules

- Production secrets never committed to Git.
- Separate database credentials by environment.
- Database migrations version-controlled.
- Production deployment requires CI/CD approval.
- Admin accounts use strong authentication.
- Critical admin actions are logged.
- Database write access restricted to backend services.
- A production camera stream gateway must be explicitly configured and operational; the system fails closed otherwise.
- The `DevelopmentMock` camera adapter is prohibited outside the Development environment.
- Each enabled notification channel must select an explicitly configured operational provider. Missing providers fail closed and cannot report synthetic success.
- Notification `DevelopmentMock` providers are prohibited outside the Development environment.
- Notification provider credentials, API keys, signing material, endpoints containing secrets, and encryption/data-protection keys must be injected through environment configuration or a managed secret/key store.

### Notification security and privacy

- Inbox reads, unread count, mark-read, device registration, and preferences derive the user from the authenticated token and are ownership-scoped. Unknown and non-owned notification IDs share the same `404` behavior.
- Template reads require `NOTIFICATION_TEMPLATES.READ`; template mutations require `NOTIFICATION_TEMPLATES.MANAGE`. Every mutation records the actor, template identity, reason, and UTC timestamp without copying secrets or rendered destination data into the audit record.
- Push permission is inspected without prompting at startup. The operating-system prompt may be triggered only by an explicit user action, and tokens may be registered only while permission is authorized or provisional.
- Push tokens are never stored in plaintext. A cryptographic hash is used for equality and uniqueness; the provider-deliverable value is protected with the application data-protection key ring. Tokens are never returned by APIs or written to logs, metrics, traces, audit events, exception messages, or analytics.
- Device identifiers, mobile numbers, email addresses, and WhatsApp destinations are personal data. Logs and delivery-attempt diagnostics must redact them and retain only the minimum operational category required for diagnosis.
- Permanent invalid-destination responses invalidate the associated device or destination. Token rotation replaces protected material while preserving user/device ownership.
- Notification deep links are untrusted input even when produced internally. Flutter accepts only allowlisted internal application paths, rejects external schemes/hosts and malformed routes, and rechecks authorization at the destination screen.
- Preferences cannot bypass protected critical-delivery rules. Provider suppression and unconfigured outcomes are persisted explicitly and must not be represented as successful delivery.
- Notification events, deliveries, and attempts must use bounded diagnostic fields, restrictive foreign keys, and retention controls appropriate to their operational and personal-data content.

### Live dairy camera security

- SQL Server stores camera identity, branch/display/visibility metadata, protocol, a non-secret provider code, and an opaque non-secret provider reference only.
- Camera/NVR credentials, internal network addresses, hardware configuration, recordings, raw RTSP URLs, private upstream URLs, and issued playback URIs are prohibited from persistence.
- Public DTOs omit branch and provider metadata. Unknown, private, and inactive cameras share the same public not-found behavior to prevent enumeration.
- Playback descriptors are gateway-issued, short-lived, and retained in Flutter memory only. Clients discard expired descriptors and request replacements.
- `CAMERAS.VIEW_PUBLIC`, `CAMERAS.READ`, and `CAMERAS.MANAGE` are enforced by the API. Branch-scoped administration requires an assigned branch; reassignment requires both source and destination scope unless `ACCESS.GLOBAL` is present.
- Camera creation and update are audit events. Audit data may identify the camera, actor, action, and branch but must not include playback URIs or prohibited secrets.
- Operational logs and metrics may include camera ID, provider code, protocol, availability/result category, latency, and correlation ID. They must redact provider references where those references could reveal deployment details and must never include descriptor URIs or credentials.
- Development descriptors must be visibly marked in the client and must use absolute HTTPS HLS URLs.

---

## 15. Operational Runbooks

Create runbooks for:
- Payment outage
- Notification outage, including pending-event age, retry backlog, per-channel unconfigured/failure rates, invalid-token spikes, provider credential/configuration checks, worker health, and safe replay/idempotency verification
- GPS outage
- Camera outage, including metadata API, stream gateway, descriptor issuance, upstream camera/NVR, expiry, and protocol-specific diagnosis
- Database outage
- Milk shortage
- Mass complaint event
- Replacement backlog
- Security incident
- Data restore
