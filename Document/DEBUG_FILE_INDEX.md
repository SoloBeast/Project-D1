# Debug Trace Source File Index

## Flutter

| Area | Files |
|---|---|
| App/router | [app.dart](../mobile/lib/app/app.dart), [main.dart](../mobile/lib/main.dart) |
| Transport | [api_client.dart](../mobile/lib/core/network/api_client.dart), [state_panel.dart](../mobile/lib/core/widgets/state_panel.dart) |
| Auth | [auth_repository.dart](../mobile/lib/features/auth/auth_repository.dart), [session_controller.dart](../mobile/lib/features/auth/session_controller.dart), [session_state.dart](../mobile/lib/features/auth/session_state.dart), login/register/OTP screens |
| Home | [role_home_screen.dart](../mobile/lib/features/home/role_home_screen.dart) |
| Customer | customer controller/models/repository/screens and map picker |
| Catalogue | catalogue controller/models/repository/screens |
| Orders | order controller/models/repository/screens |
| Payments | payment controller/models/repository/screens/gateway launcher files |
| Wallet | wallet controller/models/repository/screens |
| Subscriptions | subscription controller/models/repository/screens |
| Deliveries | delivery controller/models/repository/screens |
| Milk tests | milk-test controller/models/repository/screens |
| Dairy | dairy controller/models/repository/screens |
| Cameras | camera controller/models/repository/screens |
| Notifications | notification controller/models/repository/screen/models/push gateway |
| Reports | admin-report controller/models/repository/screens/export saver |

## API and Application Contracts

- Controllers: `Backend/src/DoodhDirect.Api/Controllers/*Controller.cs`
- DTO/contracts: `Backend/src/DoodhDirect.Application/*/*Contracts.cs`
- Authorization: `Backend/src/DoodhDirect.Api/Authorization/*`
- Middleware: `Backend/src/DoodhDirect.Api/Middleware/*`
- Identity claims and roles: `Backend/src/DoodhDirect.Application/Identity/AuthenticationContracts.cs`, `Backend/src/DoodhDirect.Infrastructure/Identity/IdentitySeedService.cs`, `JwtTokenService.cs`

## Infrastructure Services

- Customer: `Customer/CustomerService.cs`
- Catalogue: `Catalogue/CatalogueService.cs`
- Orders: `Orders/OrderServices.cs`
- Payments: `Payments/PaymentService.cs`, `PaymentGateways.cs`
- Wallet: `Wallets/WalletService.cs`
- Subscriptions: `Subscriptions/SubscriptionService.cs`
- Delivery: `Deliveries/DeliveryService.cs`
- Dairy: `Dairy/DairyService.cs`
- Milk tests: `MilkTesting/MilkTestService.cs`, `MilkTestImageValidator.cs`, `LocalMediaStorage.cs`
- Cameras: `Cameras/CameraService.cs`, `CameraStreamGateways.cs`
- Notifications: `Notifications/NotificationServices.cs`, `NotificationProcessor.cs`, `NotificationSecurityAndWorker.cs`, `NotificationOptions.cs`
- Reports: `Reports/ReportService.cs`, `ReportTabularExporter.cs`
- Auth/OTP: `Identity/AuthenticationService.cs`, `OtpService.cs`

## Persistence and Tests

- Context: [DoodhDirectDbContext.cs](../Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs)
- Migrations: `Backend/src/DoodhDirect.Infrastructure/Persistence/Migrations/*`
- Integration tests: `Backend/tests/DoodhDirect.Api.IntegrationTests/*`
- Domain tests: `Backend/tests/DoodhDirect.Domain.Tests/*`
- Flutter tests: `mobile/test/*`

## Interpretation Rule

A file listed here establishes responsibility, not guaranteed UI exposure. For runtime order, follow the screen -> controller -> repository -> endpoint -> service -> entity path in the role and API documents. `NOT FOUND IN CURRENT IMPLEMENTATION` means no verified source path was found during this trace pass.
