# Traceability

| Requirement area | Phase 0 implementation | Evidence |
|---|---|---|
| Layered backend | Domain, Application, Infrastructure, API projects with one-way dependencies | `Backend/DoodhDirect.slnx`; `README.md` |
| SQL Server persistence | EF Core SQL Server context, migration, health check | `Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs` |
| Identity foundation | User, role, permission, assignment, refresh-token persistence | `Backend/src/DoodhDirect.Domain/Identity/IdentityEntities.cs` |
| Audit/configuration foundation | Audit log and system configuration entities | `Backend/src/DoodhDirect.Domain/Auditing/AuditEntities.cs`; `Backend/src/DoodhDirect.Domain/Configuration/SystemConfiguration.cs` |
| API security baseline | JWT validation and authenticated fallback policy | `Backend/src/DoodhDirect.Api/Program.cs` |
| API operations | Correlation IDs, errors, logging, health, OpenAPI | `Backend/src/DoodhDirect.Api/Middleware`; `Backend/src/DoodhDirect.Api/Program.cs` |
| Flutter foundation | Riverpod state, GoRouter, typed client, role workspaces | `mobile/lib` |
| Required UI states | Loading, empty, error, unauthorized, offline panels | `mobile/lib/core/widgets/state_panel.dart` |
| Verification | Domain/API tests, Flutter tests, analysis, web build, migration script | `.github/workflows/ci.yml`; acceptance commands in `README.md` |
| Development database | SQL Server Express `DESKTOP-6LU1CLD\\SQLEXPRESS`, database `DoodhDirect` | `Document/PHASE_0_DOCUMENT_REVIEW.md` |
| Deferred identity behavior | OTP, login, registration, token issuance, RBAC administration, branch authorization, providers | `Document/DECISIONS.md`; `README.md` |

## Acceptance Evidence

- SQL Server connection test: PASS.
- EF migration application: PASS.
- Migration history and schema inspection: PASS.
- Backend Release build: PASS, zero warnings/errors.
- Backend tests: PASS, 8 tests.
- Flutter analyze: PASS.
- Flutter tests: PASS, 5 tests.
- Flutter web release build: PASS.
