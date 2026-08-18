# Customer Runtime Debug Trace

## Entry and Home

Role code `CUSTOMER` maps to `UserRole.customer` in [roleFromCodes()](../mobile/lib/features/auth/auth_repository.dart:26). [RoleHomeScreen](../mobile/lib/features/home/role_home_screen.dart:9) exposes profile/addresses, wallet, catalogue, orders, deliveries, subscriptions, cameras, notifications, and logout.

## Main Click Traces

| Click | Runtime chain | Result |
|---|---|---|
| Profile and addresses | `/customer/account` -> customer controller/repository -> `GET /customers/me/profile` and `GET /customers/me/addresses` -> Customer controller/service -> `CustomerProfile`, `CustomerAddress` | Overview state renders; edit/add/deactivate reloads state |
| Browse products | `/catalogue` -> catalogue controller -> public category/product GETs -> catalogue service -> `ProductCategory`, `Product`, `ProductBranch` | Product list; tap opens `/catalogue/products/{id}` |
| Buy product | product detail validates positive quantity -> `/checkout` -> checkout preview/create -> order service -> order entities | Success navigates `/orders/{id}/payment` |
| Pay | payment method -> create payment -> wallet or gateway flow -> payment result route | Poll/verify updates result; valid target goes to order/subscription |
| Wallet | `/wallet` -> wallet and transaction GETs; development top-up POST | Balance and ledger refresh |
| Orders | `/orders` -> own list; detail -> GET; cancel -> POST | Ownership enforced; state reloads after cancel |
| Subscriptions | list/setup/detail/calendar -> create/update/action/retry/skip calls | Payment result and subscription/calendar reloads |
| Deliveries | `/deliveries` -> own deliveries; detail -> tracking snapshot | Eligible delivery opens milk-test route |
| Milk test | request/read, customer evidence access, confirm/reject | Delivery ownership and legal test state enforced |
| Live Dairy | public camera list -> stream descriptor | Viewer handles availability/expiry/retry |
| Notifications | inbox -> mark read -> optional deep link | Controller decrements unread and router pushes link |

## Ownership and Validation

Customer IDs are derived from JWT `user_id`; client-supplied customer ownership is not trusted. Order/payment services use `bypassOwnership: false`. Address and customer service queries filter by user. Delivery and milk-test services resolve the customer’s own delivery. Subscription methods use owned lookup.

Common failures: missing/inactive address or product, unavailable branch product, invalid quantity/schedule, duplicate or illegal state transition, insufficient wallet balance, expired payment, ineligible delivery/milk-test state, and 401/403 permission failures. See [DEBUG_VALIDATION_MATRIX.md](DEBUG_VALIDATION_MATRIX.md).

## Breakpoints

- [CustomerOverviewScreen](../mobile/lib/features/customer/customer_screens.dart:10)
- [CheckoutScreen](../mobile/lib/features/orders/order_screens.dart:11)
- [PaymentMethodScreen](../mobile/lib/features/payments/payment_screens.dart:11)
- [SubscriptionDetailScreen](../mobile/lib/features/subscriptions/subscription_screens.dart:420)
- [CustomerDeliveryDetailScreen](../mobile/lib/features/deliveries/delivery_screens.dart:55)
- [MilkTestService.RequestAsync()](../Backend/src/DoodhDirect.Infrastructure/MilkTesting/MilkTestService.cs:24)
