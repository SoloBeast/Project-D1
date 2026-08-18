# Database Reverse Trace

## DbContext Surface

[ DoodhDirectDbContext ](../Backend/src/DoodhDirect.Infrastructure/Persistence/DoodhDirectDbContext.cs:21) exposes identity, audit, configuration, customer, catalogue, order/payment, wallet, subscription, delivery, dairy, milk-test, camera, notification, OTP, and location DbSets. `SaveChanges`/`SaveChangesAsync` apply audit timestamps.

| Runtime area | Entities/tables | Primary writers/readers |
|---|---|---|
| Identity | `User`, `Role`, `Permission`, `UserRole`, `RolePermission`, `UserSession`, `RefreshToken`, `OtpChallenge` | Authentication, OTP, seed, JWT claim construction |
| Audit | `AuditLog` | authorization handler, auth, delivery, payment, notification, reports |
| Customer | `CustomerProfile`, `CustomerAddress` | CustomerService, order checkout |
| Catalogue | `ProductCategory`, `Product`, `Branch`, `ProductBranch` | CatalogueService, checkout allocation |
| Orders | `Order`, `OrderItem` | OrderService, delivery materialization, payment/refund |
| Payments | `Payment`, `PaymentWebhook`, `Refund` | PaymentService, gateway/webhook |
| Wallet | `Wallet`, `WalletTransaction` | WalletService, payment/order/subscription/refund |
| Subscriptions | `Subscription`, `SubscriptionSchedule`, `SubscriptionDelivery` | SubscriptionService, payment, delivery materialization |
| Delivery | `Delivery`, `DeliveryAssignment`, `DeliveryOtp`, `DeliveryLocation` | DeliveryService, milk tests, reports |
| Dairy | `MilkProduction`, `MilkBatch`, `MilkUsage` | DairyService, reports |
| Milk testing | `MilkTest`, `MilkTestParameter`, `MilkTestImage` | MilkTestService, local media storage, reports |
| Cameras | `Camera`, `CameraStream` | CameraService, stream gateway, reports |
| Notifications | `NotificationEvent`, `Notification`, `NotificationTemplate`, `NotificationPreference`, `UserDevice`, `NotificationDelivery`, `NotificationAttempt` | event writers, processor, inbox service, template service |

## Migration Phases

Migrations progress from identity/session and customer/catalogue through orders, payment/wallet, subscriptions, delivery, dairy, doorstep testing, cameras, notifications, and report indexes. Actual table naming follows EF configuration; for example the entity is `DeliveryOtp`, not the conceptual `DeliveryOTP`.

## Query Debugging

Start at the service method, inspect actor/customer/branch predicates, then inspect `Include`/projection and `SaveChangesAsync`. For missing rows, verify public ID versus numeric key, active flags, ownership, branch membership, and status filters before inspecting the database.

## Unimplemented Persistence

`NOT FOUND IN CURRENT IMPLEMENTATION`: support tickets, accounting ledger/reconciliation tables beyond wallet/payment/report data, push delivery infrastructure beyond notification delivery/provider abstractions, and a separate customer case entity.
