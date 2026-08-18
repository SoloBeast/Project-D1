# Cross-Cutting Runtime Trace

## Authentication and Session

`/restore` is the initial Flutter location. [SessionController.build()](../mobile/lib/features/auth/session_controller.dart:17) returns loading and asynchronously calls `_restore`. [AuthRepository.restore()](../mobile/lib/features/auth/auth_repository.dart:190) reads secure-storage key `identity.session.v1`, rejects expired refresh tokens, and calls `/api/v1/auth/refresh`. A valid response is decoded into `AuthSession`, saved, and installed in Riverpod state. Invalid storage or refresh failure clears storage and routes to login through [app router redirect](../mobile/lib/app/app.dart:76).

Login/register/OTP call the repository, decode `data` into user plus token pair, and persist the entire session. Logout changes UI state first, calls `/api/v1/auth/logout`, and clears storage even if the network call fails. A refresh 401 invokes `expireSession()` and exposes `Your session expired. Sign in again.`.

## Transport

[ApiClient](../mobile/lib/core/network/api_client.dart:1) sends `Accept: application/json`; JSON requests send `Content-Type: application/json`; authenticated requests send `Authorization: Bearer <accessToken>`. It supports JSON, multipart, and byte responses. Non-2xx JSON errors read the first `errors[]` item and expose `ApiException(statusCode, code, message)`.

Backend controllers return `ApiResponse<T>`. [ExceptionHandlingMiddleware](../Backend/src/DoodhDirect.Api/Middleware/ExceptionHandlingMiddleware.cs:13) maps `AppException` to its status/code/field/message, unknown exceptions to `500 INTERNAL_ERROR`, and logs a correlation identifier. Development 500 responses may include `DEVELOPMENT_DETAIL`.

## Authorization

JWT claims include `user_id`, `session_id`, repeated `permission`, and repeated `branch_id`. Dynamic permission policies require authentication plus an exact permission claim. Dynamic branch policies additionally accept `ACCESS.GLOBAL` or an exact route `branchId` claim. Controllers frequently use permission policies and services perform actor-aware branch, assignment, or ownership checks.

[AuditingAuthorizationMiddlewareResultHandler](../Backend/src/DoodhDirect.Api/Authorization/AuditingAuthorizationMiddlewareResultHandler.cs:18) returns 401 `UNAUTHORIZED` or 403 `FORBIDDEN`, wrapped in `ApiResponse<object>`, and attempts an `AuditLog` record. Audit-write failure does not replace the authorization response.

Owner receives every permission, including `ACCESS.GLOBAL`; System Admin receives broad global access. This is permission-driven, not a special owner-name branch in handlers.

## State and Navigation

Riverpod controllers set loading/saving flags, call repositories, decode model responses, and retain error messages for [StatePanel](../mobile/lib/core/widgets/state_panel.dart:1). Screens disable actions while saving, show retry controls for failed reads, and use `go_router` paths listed in [app.dart](../mobile/lib/app/app.dart:1). Notification deep links are consumed by the router listener and pushed after authentication.

## Persistence and Audit

Application services use EF Core entities through [DoodhDirectDbContext](../Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs:21). `SaveChanges` and `SaveChangesAsync` apply audit timestamps. Domain transitions generally occur before save; event rows are added in the same service transaction where required. Notification event processing is asynchronous and separate from the originating request.

## Common Breakpoints

- [SessionController._restore()](../mobile/lib/features/auth/session_controller.dart:23)
- [ApiClient request path](../mobile/lib/core/network/api_client.dart:1)
- [PermissionAuthorizationHandler](../Backend/src/DoodhDirect.Api/Authorization/AuthorizationRequirements.cs:46)
- [ExceptionHandlingMiddleware.InvokeAsync()](../Backend/src/DoodhDirect.Api/Middleware/ExceptionHandlingMiddleware.cs:13)
- `SaveChangesAsync` in the responsible infrastructure service
- Riverpod controller method immediately before and after repository call
