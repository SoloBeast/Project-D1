# Assumptions

- The local development database is disposable only because it was absent before migration application; no existing database was deleted or recreated.
- Windows authentication is the intended local SQL Server Express authentication mode, confirmed by `LoginMode = 1`.
- The local named instance is reachable directly as `.`\\`SQLEXPRESS`; SQL Server Browser is unnecessary for this connection form.
- The database name is `DoodhDirect`, matching the checked-in EF configuration and migration tooling.
- The checked-in JWT signing key remains a non-production placeholder and must be replaced through deployment configuration before any real token issuer is implemented.
- Phase 0 does not seed users, roles, permissions, branches, or tokens; empty identity tables are expected.
- CI validates build, tests, and migration script generation. Applying a migration in CI requires a separately provisioned controlled SQL Server service and credentials.
- No real authentication or authorization workflow is inferred from the presence of JWT validation or local Flutter foundation-session state.
