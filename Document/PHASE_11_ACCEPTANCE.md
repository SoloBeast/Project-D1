# Phase 11 Acceptance

**Status:** PASS

This checkpoint is limited to Phase 11 notifications and regression validation. Phase 12 Admin & Reports has not been started. Complaint and replacement notification templates are included, while their producers remain deferred because those business modules do not exist.

## Durable Event and Delivery Lifecycle

- Business modules append provider-neutral notification events through `INotificationEventWriter`; they do not call push, SMS, WhatsApp, or email providers directly.
- Events, inbox notifications, per-channel deliveries, and delivery attempts are persisted with explicit processing and terminal states.
- Each channel is processed independently. Retryable failures use bounded retries, permanent failures terminate, disabled preferences suppress delivery, invalid push destinations invalidate the affected device, and unconfigured providers fail closed.
- Inbox persistence remains available when an external channel fails, and one channel outcome does not block other channels.

Evidence: [NotificationContracts.cs](../Backend/src/DoodhDirect.Application/Notifications/NotificationContracts.cs), [NotificationEntities.cs](../Backend/src/DoodhDirect.Domain/Notifications/NotificationEntities.cs), [NotificationProcessor.cs](../Backend/src/DoodhDirect.Infrastructure/Notifications/NotificationProcessor.cs), and [NotificationSecurityAndWorker.cs](../Backend/src/DoodhDirect.Infrastructure/Notifications/NotificationSecurityAndWorker.cs).

## Provider Boundary and Configuration

- Push, SMS, WhatsApp, and Email use the common `INotificationChannelGateway` boundary.
- Development mock gateways are explicit and deterministic.
- Non-Development environments reject mock provider configuration. Missing operational providers resolve to unconfigured gateways that report failure rather than simulating delivery.
- Production deployment therefore requires operational channel adapters and credentials for every enabled external channel.

Evidence: [NotificationOptions.cs](../Backend/src/DoodhDirect.Infrastructure/Notifications/NotificationOptions.cs), [DependencyInjection.cs](../Backend/src/DoodhDirect.Infrastructure/DependencyInjection.cs), [appsettings.json](../Backend/src/DoodhDirect.Api/appsettings.json), and [appsettings.Development.json](../Backend/src/DoodhDirect.Api/appsettings.Development.json).

## Device Token Security

- Device registration derives ownership from the authenticated user.
- Push-token identity and uniqueness use a deterministic hash; provider delivery uses a data-protected payload.
- Token rotation updates the protected payload, and invalid destinations are deterministically invalidated.
- API responses do not return push tokens, and the physical SQL Server schema contains no plaintext push-token column.

Evidence: [NotificationServices.cs](../Backend/src/DoodhDirect.Infrastructure/Notifications/NotificationServices.cs), [NotificationSecurityAndWorker.cs](../Backend/src/DoodhDirect.Infrastructure/Notifications/NotificationSecurityAndWorker.cs), [DoodhDirectDbContext.cs](../Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs), and [NotificationContracts.cs](../Backend/src/DoodhDirect.Application/Notifications/NotificationContracts.cs).

## Inbox, Preferences, and Administration

- Authenticated APIs provide paginated inbox reads, unread counts, ownership-scoped mark-read behavior, device registration, and notification preferences.
- Critical-event rules prevent users from disabling required notifications.
- Template reads and updates use distinct `NOTIFICATION_TEMPLATES.READ` and `NOTIFICATION_TEMPLATES.MANAGE` permissions.
- Template updates validate content and persist actor/reason audit evidence.
- English seed coverage contains 84 templates: 21 event types across four channels, exactly once per event/channel/language combination.

Evidence: [NotificationsController.cs](../Backend/src/DoodhDirect.Api/Controllers/NotificationsController.cs), [NotificationServices.cs](../Backend/src/DoodhDirect.Infrastructure/Notifications/NotificationServices.cs), [NotificationSeedServices.cs](../Backend/src/DoodhDirect.Infrastructure/Notifications/NotificationSeedServices.cs), and [NotificationServiceTests.cs](../Backend/tests/DoodhDirect.Api.IntegrationTests/NotificationServiceTests.cs).

## Flutter Workflow

- Startup inspects notification permission without prompting. Permission requests occur only after explicit user action.
- Push tokens register only for authorized or provisional permission and token refresh synchronization is bound to the current authenticated session.
- The notification inbox supports pagination, unread count, mark-read, pull and foreground refresh, loading, empty, offline, API-error, refresh-error, and pagination-error states.
- Logout or user changes clear session-scoped notification state, and stale asynchronous responses are discarded.
- Opened-message and cold-start navigation accept only allowlisted internal application routes.
- Android notification permission and iOS APS entitlement/delegate configuration are present.

Evidence: [push_notification_gateway.dart](../mobile/lib/features/notifications/push_notification_gateway.dart), [notification_controller.dart](../mobile/lib/features/notifications/notification_controller.dart), [notification_screen.dart](../mobile/lib/features/notifications/notification_screen.dart), [app.dart](../mobile/lib/app/app.dart), [AndroidManifest.xml](../mobile/android/app/src/main/AndroidManifest.xml), and [AppDelegate.swift](../mobile/ios/Runner/AppDelegate.swift).

## API and Documentation Contract

- The synchronized API contract contains eight authenticated operations for notifications, unread count, mark-read, devices, preferences, and template administration.
- Request, response, pagination, filter, UUID path-parameter, channel, preference, device, inbox, and template schemas use the standard success/error envelope conventions.
- The device request marks `pushToken` as write-only and no response schema exposes it.
- Phase 11 UI, API, integration, security/operations, architecture, roadmap, OpenAPI, and traceability documentation are synchronized.

Evidence: [11_openapi_starter.yaml](11_openapi_starter.yaml), [05_API_Specification.md](05_API_Specification.md), [07_Integration_Specification.md](07_Integration_Specification.md), [08_Security_Compliance_and_Operations.md](08_Security_Compliance_and_Operations.md), [09_Development_Roadmap.md](09_Development_Roadmap.md), [ARCHITECTURE.md](ARCHITECTURE.md), and [TRACEABILITY.md](TRACEABILITY.md).

## Migration and Physical Database Record

- The exact Phase 11 EF migration is `20260817150825_Phase11Notifications`.
- The exact idempotent Phase 10-to-Phase 11 SQL script is checked in at `Backend/Phase11Notifications.sql`; the obsolete migration identity is not used.
- SQL Server Express database `DoodhDirect` on `DESKTOP-6LU1CLD\\SQLEXPRESS` accepted the migration, and the checked-in script executed twice afterward without additional changes when quoted identifiers were enabled.
- Physical inspection verified `Notification`, `NotificationAttempt`, `NotificationDelivery`, `NotificationEvent`, `NotificationPreference`, `NotificationTemplate`, and `UserDevice` with expected keys, indexes, defaults, checks, and restrictive foreign keys.
- Catalog inspection confirmed protected token storage and no plaintext push-token column.
- Seed inspection confirmed 84 English template rows covering 21 event types and four channels exactly once per combination.

Evidence: [20260817150825_Phase11Notifications.cs](../Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260817150825_Phase11Notifications.cs), [20260817150825_Phase11Notifications.Designer.cs](../Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/20260817150825_Phase11Notifications.Designer.cs), and [Phase11Notifications.sql](../Backend/Phase11Notifications.sql).

## Focused Test Record

- Backend notification tests: PASS, 7 tests covering service behavior, protected device registration, preferences, templates, delivery processing, retries, suppression, invalid destinations, and provider outcomes.
- Flutter notification tests: PASS, 23 tests covering models/repository, permission and token lifecycle, session isolation, controller behavior, inbox widgets, and navigation behavior.

## Final Verification Record

Final Phase 11 release validation:

- Backend Release build: PASS, zero warnings and zero errors.
- Backend solution tests: PASS, 322 tests total: 57 domain and 265 API integration.
- Flutter analysis: PASS, no issues found.
- Flutter tests: PASS, 168 tests, including notification and OpenAPI contract coverage.
- Flutter web Release build: PASS; the JavaScript artifact was generated.
- OpenAPI validation: PASS; YAML parsing, every local component reference, and every templated route parameter were verified by `mobile/test/openapi_document_test.dart`.
- Migration SQL inspection: PASS for the exact idempotent `20260817150825_Phase11Notifications` script.
- SQL Server Express application and repeat execution: PASS.
- Physical schema, protected token storage, and template seed inspection: PASS.

## Production Configuration

Before production deployment, configure operational push, SMS, WhatsApp, and email gateway implementations and credentials for every enabled channel; keep Development mocks disabled; retain data-protection key material across instances and deployments; redact tokens and protected payloads from logs; monitor event, delivery, attempt, retry, permanent-failure, invalid-destination, and unconfigured-provider states; and maintain provider outage, retry backlog, token invalidation, and key-recovery runbooks.

## Scope

Phase 11 adds provider-neutral durable notification events, a persistent multi-channel delivery lifecycle, secure device registration, preferences, template administration, and the Flutter notification experience. It does not implement Phase 12 Admin & Reports. Complaint and replacement templates are seeded, but producer integration remains deferred until those modules exist.
