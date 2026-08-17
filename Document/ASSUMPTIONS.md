# Assumptions

- The local development database is disposable only because it was absent before migration application; no existing database was deleted or recreated.
- Windows authentication is the intended local SQL Server Express authentication mode, confirmed by `LoginMode = 1`.
- The local named instance is reachable directly as `.`\\`SQLEXPRESS`; SQL Server Browser is unnecessary for this connection form.
- The database name is `DoodhDirect`, matching the checked-in EF configuration and migration tooling.
- The checked-in JWT signing key remains a non-production placeholder and must be replaced through deployment configuration before any real token issuer is implemented.
- Phase 0 does not seed users, roles, permissions, branches, or tokens; empty identity tables are expected.
- CI validates build, tests, and migration script generation. Applying a migration in CI requires a separately provisioned controlled SQL Server service and credentials.
- No real authentication or authorization workflow is inferred from the presence of JWT validation or local Flutter foundation-session state.
- Phase 8 dairy availability is the produced quantity minus append-only recorded usage. It does not include order or subscription reservations.
- Order/subscription capacity reservation is intentionally not implemented because no contract defines reservation timing, expiry/release, allocation priority, cancellation, batch selection, or reconciliation behavior. A future cross-domain design must define those semantics before orders can consume or reserve dairy capacity.
- Phase 9 doorstep tests are owned by deliveries in the MVP; they are not batch or laboratory tests.
- Development milk-test images use local filesystem storage. Production supplies another `IMediaStorage` provider without changing the domain or API contract.
- SQL Server persists milk-test image metadata and external storage keys only; no image binary content is stored in database columns.
- Customer MVP responses and screens intentionally exclude numeric milk-test readings and staff remarks.
- Phase 9 readings are entered manually from a physical device or lactometer. Bluetooth and vendor SDK integration remain future adapter implementations.
- A doorstep result is an indicative check and is not laboratory certification.
- SQL Server-specific check constraints and physical column/index properties require SQL Server verification because the SQLite integration harness cannot prove provider-specific DDL behavior.
