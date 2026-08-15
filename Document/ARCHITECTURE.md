# Architecture

## Phase 0 Structure

DoodhDirect is a modular monolith with one Flutter client and one ASP.NET Core API.

```text
Flutter (Android / iOS / web)
          |
          v
ASP.NET Core API
          |
          v
Application contracts and use-case boundary
          |
          v
Domain entities and invariants
          |
          v
Infrastructure: EF Core, SQL Server, health checks, migrations
```

Dependency direction is `Domain <- Application <- Infrastructure <- API`. Flutter never connects directly to SQL Server.

## API Foundation

The API provides JWT bearer validation, a secure authenticated fallback authorization policy, correlation IDs, global exception mapping, structured Serilog request logging, OpenAPI/Scalar in development, anonymous liveness/readiness health endpoints, and controller support. No business endpoints or authentication issuance endpoints were added in Phase 0.

## Persistence Foundation

EF Core targets SQL Server Express in the verified development environment. The schema uses internal `bigint` identity keys, public GUID identifiers, UTC `datetime2` timestamps, explicit foreign keys, bounded string columns, filtered SQL Server indexes for global and branch-scoped role assignments, and restrictive deletes for retained identity/RBAC data.

Development configuration is environment-specific in `Backend/src/DoodhDirect.Api/appsettings.Development.json`; production values must be injected through environment variables or a managed secret store.

## Flutter Foundation

Flutter uses Riverpod for session state and GoRouter for role-aware navigation. The application has a login placeholder, an explicit local foundation-session picker, role workspace placeholders, sign-out behavior, a typed HTTP client, and reusable loading/empty/error/unauthorized/offline panels.

## Phase Boundary

Phase 0 establishes contracts and persistence scaffolding. Phase 1 owns real identity and authorization behavior: OTP, credential login, registration, token issuance/refresh, user administration, RBAC administration, permission administration, branch authorization enforcement, and production identity providers.
