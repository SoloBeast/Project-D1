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
- Represent application-owned business timestamps as India-local wall-clock values from the centralized `IIndiaTimeProvider`, using `DateTimeKind.Unspecified` and suffix-free API serialization.
- Retain true UTC instants for provider/external boundaries and deferred infrastructure timestamps such as payment gateway, camera descriptor expiry, notification, and shared audit values until their dedicated migration slices are completed.
- Do not bulk-convert historical rows; preserve existing physical SQL timestamp columns where practical.
- Use BIGINT internal IDs and GUID public IDs.
- DECIMAL(18,2) for money and DECIMAL(18,3) for quantities.
- Build vertically by roadmap phase; do not attempt the complete product in a single code-generation step.
