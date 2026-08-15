# Decisions

## D-001: SQL Server Express for Development

The installed and verified development database is SQL Server Express named instance `DESKTOP-6LU1CLD\\SQLEXPRESS`. The application uses `Server=.\\SQLEXPRESS` with Windows authentication. LocalDB is not required.

## D-002: Environment-Specific Connection Configuration

The shared settings file contains no machine-specific database connection. Development uses `appsettings.Development.json`; deployments override `ConnectionStrings__DoodhDirect` through environment configuration or a secret manager.

## D-003: EF Core Migration Authority

The EF Core migration and model snapshot are authoritative for the implemented Phase 0 schema. The SQL starter document is treated as conceptual reference material.

## D-004: Secure-by-Default API

The API uses a fallback policy requiring authenticated users. Only health and development documentation endpoints are explicitly anonymous. This establishes the authorization boundary without pretending that authentication issuance already exists.

## D-005: Defer Production Identity

OTP, login, registration, token issuance, refresh, user management, RBAC management, permission management, branch authorization, and identity providers are Phase 1 concerns. The Flutter foundation session is local demonstration state only.

## D-006: Retain Identity and Audit Records

Identity/RBAC relationships use restrictive deletes. Refresh-token hashes are bounded and indexed; public IDs use sequential SQL Server GUID defaults and unique indexes.
