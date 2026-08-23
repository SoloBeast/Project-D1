# Flutter-to-API Call Map

| Flutter repository | Current calls |
|---|---|
| [AuthRepository](../mobile/lib/features/auth/auth_repository.dart:136) | POST `/auth/login`, `/register`, `/send-otp`, `/verify-otp`, `/refresh`, `/logout`; GET `/auth/me` |
| CustomerRepository | GET/PATCH `/customers/me/profile`; GET/POST `/customers/me/addresses`; PATCH/DELETE address; GET `/customers/me/address-lookup/reverse` with internal map coordinates |
| CatalogueRepository | GET public products/categories/detail; admin product/category CRUD, activation, branch availability, branches |
| OrderRepository | POST `/orders/checkout-preview`, `/orders`; GET own list/detail; POST cancel; admin list/detail |
| PaymentRepository | POST create/verify/cancel; GET detail |
| WalletRepository | GET wallet/transactions; POST development top-up |
| SubscriptionRepository | POST create/retry; GET list/detail/calendar; PATCH update; POST skip/pause/resume/cancel |
| DeliveryRepository | GET own deliveries/detail; GET staff today/detail; delivery action POST/PATCH; management branch queue with date/status/source/slot filters; employees/detail; POST materialize, fetch-subscriptions, assign, and bulk-assign; OTP/location |
| MilkTestRepository | customer/staff GET; request; multipart image upload; complete; confirm/reject; protected image bytes |
| DairyRepository | dashboard, production create/history, batch list/detail, availability, usage create/history |
| CameraRepository | public list/stream; admin list/create/update |
| NotificationRepository | inbox/unread; mark read; device register; preferences/templates where exposed |
| AdminReportRepository | dashboard/module page; filters/page; byte export |

## Response Parsing

Repositories read JSON `data` into feature model factories. Controllers mutate Riverpod state after successful parse and retain/clear errors according to feature conventions. Payment/export/media calls have non-JSON paths and must be debugged separately from normal envelope parsing.

## Navigation Results

Checkout success goes to `/orders/{id}/payment`; payment result returns to an order or subscription target. Notification deep links are pushed from the app-level listener. Customer edits generally pop/reload. Route errors offer `/home`.

## Missing Calls

`NOT FOUND IN CURRENT IMPLEMENTATION`: Flutter calls for support cases, accounting/refund adjustment screens, general identity administration, and a full push notification registration workflow beyond the implemented device registration.
