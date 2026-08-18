# API Runtime Index

Base URL is `DOOHDIRECT_API_URL`, defaulting to `http://localhost:5209` in [auth_repository.dart](../mobile/lib/features/auth/auth_repository.dart:7). All paths are under `/api/v1`.

| Area | Main paths | Auth/scope |
|---|---|---|
| Auth | `/auth/register`, `/auth/login`, `/auth/send-otp`, `/auth/verify-otp`, `/auth/refresh`, `/auth/logout`, `/auth/me` | Login/register/OTP/refresh anonymous as configured; logout/me authenticated |
| Catalogue | `/products`, `/product-categories` | Public reads; admin mutation paths require catalogue permissions |
| Customer | `/customers/me/profile`, `/customers/me/addresses` | Own profile/address permissions |
| Orders | `/orders/checkout-preview`, `/orders`, `/orders/{id}`, `/orders/{id}/cancel`; `/admin/orders` | Own versus administrative read |
| Payments | `/payments/create`, `/payments/verify`, `/payments/{id}`, `/payments/{id}/refund`, `/webhooks/razorpay` | Own payment, refund permission, webhook signature/config |
| Wallet | `/wallet`, `/wallet/transactions`, `/wallet/topup`, `/admin/customers/{id}/wallet/adjust` | Own wallet versus adjustment permission |
| Subscriptions | `/subscriptions`, `/subscriptions/{id}`, `/subscriptions/{id}/retry-payment`, `/subscriptions/{id}/skip`, `/subscriptions/{id}/calendar`, `/pause`, `/resume`, `/cancel` | Own subscription permissions |
| Deliveries | `/deliveries`, `/deliveries/{id}`, `/delivery/today`, `/delivery/{id}`, `/delivery-management/branches/{branchId}`, `/delivery-management/{id}` | Own, assigned, or branch scope |
| Milk tests | `/deliveries/{id}/milk-test`, `/delivery/{id}/milk-test`, `/milk-tests/{id}/images`, `/complete`, `/confirm`, `/reject` | Customer ownership or assigned staff |
| Dairy | `/dairy/branches/{branchId}/dashboard`, `production`, `batches`, `availability`, `usage` | Branch/global actor scope |
| Cameras | `/cameras/public`, `/cameras/public/{id}/stream`, `/admin/cameras` | Public read or camera permissions/branch |
| Notifications | `/notifications`, `/notifications/unread-count`, `/notifications/{id}/read`, `/devices`, `/notification-preferences`, `/admin/notification-templates` | Own inbox/device/preferences; admin templates |
| Reports | `/admin/reports/dashboard`, module pages, `/export` | Report permission and actor branch scope |

## Envelope

Success is read from `response.data`. Errors are read from `errors[0].code`, `errors[0].field`, and `errors[0].message`; fallback code is `HTTP_ERROR`. Binary exports are handled as bytes rather than JSON.

## Headers

JSON: `Accept: application/json`, `Content-Type: application/json`. Authenticated calls add `Authorization: Bearer`. Uploads use multipart; exports use byte response handling. Payment webhook verification uses the incoming Razorpay payload/signature path, not the mobile bearer flow.

## Exact Endpoint Source

See [DEBUG_API_CALL_MAP.md](DEBUG_API_CALL_MAP.md) for repository-to-controller mappings and [DEBUG_FILE_INDEX.md](DEBUG_FILE_INDEX.md) for source responsibility.
