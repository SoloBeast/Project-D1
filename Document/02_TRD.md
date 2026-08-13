# DoodhDirect TRD

## Approved stack

- Flutter/Dart for Android, iOS and Web.
- ASP.NET Core 10 / C# Web API.
- Entity Framework Core.
- SQL Server 2025.
- API-first modular monolith.
- Azure-ready cloud architecture.
- Razorpay payments.
- Google Maps Platform.
- Firebase Cloud Messaging.
- SMS/WhatsApp provider abstractions.
- SignalR for real-time delivery tracking/operations.
- Object storage for complaint media.

## Architectural requirements

- No direct client-to-database access.
- Backend owns price, branch, payment, entitlement and complaint eligibility logic.
- Financial operations are transactional and idempotent.
- All protected endpoints use authorization.
- All critical overrides are audited.
- Store UTC internally.
- Use BIGINT internal IDs and GUID public IDs.
- DECIMAL(18,2) for money and DECIMAL(18,3) for quantities.
- Build vertically by roadmap phase; do not attempt the complete product in a single code-generation step.
