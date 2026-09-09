# DoodhDirect Flutter application

The `mobile` project is the DoodhDirect client for Android, iOS, and web. It implements authenticated customer ordering, payments, wallet access, delivery operations, and role-aware workspaces.

## Implemented client behavior

- Email/mobile password registration and login
- OTP request and verification flows
- Secure session persistence and refresh-based restoration
- Customer catalogue, address, checkout, and one-time order flows
- Guest (deferred login) browsing: public storefront home, catalogue, product details, cart, and checkout review without an account; the device-local cart is persisted under `identity.guest.cart.v1` and survives sign-in and app/browser restarts
- Wallet balance and transaction history
- Wallet and Razorpay payment initiation with server verification
- Payment result refresh, failure handling, and terminal-payment retry
- Customer delivery status and active-location tracking
- Delivery staff pickup, start, arrival, OTP, completion, failure, and location updates
- Delivery manager materialization, branch queue, employee assignment, and detail views
- Role-aware navigation based on server role codes
- Standard API success/error envelope handling

## Guest customer / deferred login

A browsing-only guest state lets a visitor open the public storefront without an account:

- **Allowed while a guest:** home, catalogue, product details, cart, checkout review.
- **Login required before:** checkout submission, delivery addresses, payment, orders, subscriptions, wallet, delivery tracking, milk tests, profile, notifications, and cameras.
- The cart lives **only on this device** under storage key `identity.guest.cart.v1` (see [`lib/features/orders/guest_cart_storage.dart`](lib/features/orders/guest_cart_storage.dart)). It is scoped to the owning user so a cart built by one identity never leaks into another identity or into guest mode.
- The guest home (`GuestHomeScreen`) exposes only public storefront actions; every account-dependent quick action routes to sign-in with a `redirectTo` return-intent.
- At the checkout boundary a guest sees a login/register prompt that preserves the in-memory cart; after sign-in the cart is re-scoped to the account and the user is returned straight to checkout.
- Guests hold **no session and no token**; the app makes no protected API calls in guest mode, and the backend keeps every protected endpoint 401 for unauthenticated requests (covered by `Backend/tests/DoodhDirect.Api.IntegrationTests/GuestAuthorizationTests.cs`).

## Configuration

The centralized Development web API base URL defaults to `http://localhost:5209`. The `DOOHDIRECT_API_URL` Dart define can override it for Production and other deployment-specific endpoints.

Development tools are not part of the normal application UI. The quick customer login, wallet top-up, mock/development payment, and the "Development payment" subscription payment method were removed from the client, and the backend endpoints they called (`POST /payments/{paymentId}/complete-development` and `POST /wallet/topup`) are removed too. A documented `devToolsEnabled` flag in [`lib/core/config/app_config.dart`](../mobile/lib/core/config/app_config.dart) remains purely as a deliberate opt-in switch (`DOOHDIRECT_ENABLE_DEV_TOOLS=true`) for tooling. No normal application screen consumes it, it is never auto-enabled by `kDebugMode`, and it is hard-disabled in release builds:

```powershell
--dart-define=DOOHDIRECT_ENABLE_DEV_TOOLS=true
```

Never set this define for a production distributable build.

### Google Maps — local Development

Add the browser key to the untracked repository-root `.env` alongside the existing local Razorpay settings:

```dotenv
DOOHDIRECT_GOOGLE_MAPS_API_KEY=your_local_browser_key
```

The repository `.gitignore` excludes `.env` and `.env.*` while allowing only `.env.example`; never commit the real key. The Development launcher reads only `DOOHDIRECT_GOOGLE_MAPS_API_KEY` from that file. It does not load or expose Razorpay values or any other backend secret. It passes the key to the unchanged Flutter Maps configuration as `--dart-define=DOOHDIRECT_GOOGLE_MAPS_API_KEY=<value>` without printing it.

From the repository root, install packages once and launch Web Development on the stable restricted origin:

```powershell
cd mobile
flutter pub get
cd ..
.\scripts\run-flutter-web-development.ps1
```

Verify configuration without launching Flutter or revealing the value:

```powershell
.\scripts\run-flutter-web-development.ps1 -CheckConfiguration
```

A successful check states only that the variable is configured. If `.env` or the variable is absent, the normal launcher omits the Maps Dart define and Flutter preserves the clear `Google Maps API key is not configured for this Web build.` state. The default Development origin is `http://localhost:51482/`.

The Development Google Cloud key must use **Websites / HTTP referrers** application restrictions, allow the exact local origin used by the launcher, and use an API restriction for **Maps JavaScript API**. Add other Google Maps APIs only if this Web client actually starts using them; the current reverse lookup remains a backend endpoint and does not require exposing a Geocoding API credential to Flutter.

### Google Maps — Production

Production builds and deployments must not read the repository `.env` or use the Development launcher. Supply a separately managed Production browser key directly through the build/deployment environment:

```powershell
flutter build web --release `
  --dart-define=DOOHDIRECT_API_URL=https://api.example.com `
  --dart-define=DOOHDIRECT_GOOGLE_MAPS_API_KEY=$env:PRODUCTION_GOOGLE_MAPS_API_KEY
```

Use a distinct Production key restricted to **Websites / HTTP referrers** for only the deployed HTTPS origins. Restrict it to **Maps JavaScript API**, adding only Google Maps APIs actually used by this Web client. A browser key is visible to users by design; referrer and API restrictions are therefore mandatory. Keep backend secrets, including Razorpay key secrets and webhook secrets, out of all Flutter Dart defines.

For an Android emulator connecting to the Development HTTP profile, use an API hostname reachable from the emulator, commonly `http://10.0.2.2:5209`. A physical device must use the development machine's reachable LAN hostname or address.

## Local payment and wallet workflow

1. Start SQL Server Express and apply migrations from the repository root:

   ```powershell
   dotnet ef database update --project Backend/src/DoodhDirect.Infrastructure --startup-project Backend/src/DoodhDirect.Api
   ```

2. Start the API with its Development HTTP profile at `http://localhost:5209`. `appsettings.Development.json` configures the same real provider implementations as UAT/Production — Razorpay payments (sandbox/test credentials) — and, when `SeedOptions:EnableDevelopmentSeeds` is `true`, startup creates the checkout-ready development customer, default address, branch, and product:

   ```powershell
   dotnet run --project Backend/src/DoodhDirect.Api --launch-profile http
   ```

3. Run Flutter and sign in as the development customer (`customer@doodhdirect.local` / `DoodhDirect@123`, created when `SeedOptions:EnableDevelopmentSeeds` is enabled). There is no one-click development login in the app.
4. Open the catalogue, select Fresh Buffalo Milk, use the seeded Home address, review checkout, and create the order.
5. For Razorpay, select Razorpay and complete the payment through the configured gateway (sandbox/test credentials in Development). There is no "Complete development payment" shortcut.
6. For Wallet, fund the wallet through the permission-protected admin wallet adjustment endpoint (no free development top-up exists in the normal runtime), then create another order and select Wallet. The backend debits the ledger and confirms the order atomically.
7. Open Orders and the order detail. Verify the final status is Confirmed and that Wallet shows the matching debit for wallet-paid orders.

Development seed users are created only when `SeedOptions:EnableDevelopmentSeeds` is explicitly enabled (default `false`); seeds are never activated merely because the ASP.NET environment is Development. Development uses the same normal application/provider implementations as UAT/Production.

## Development UAT accounts

When `SeedOptions:EnableDevelopmentSeeds` is enabled, Development startup creates these local-only accounts with the normal password hasher and JWT authentication flow. Every account uses the password `DoodhDirect@123` and is never seeded unless the seed option is explicitly enabled.

| Role | Email | Scope |
| --- | --- | --- |
| `OWNER` | `owner@doodhdirect.local` | Global |
| `SYSTEM_ADMIN` | `system.admin@doodhdirect.local` | Global |
| `DELIVERY_MANAGER` | `delivery.manager@doodhdirect.local` | `MAIN` branch |
| `CUSTOMER_SUPPORT` | `support@doodhdirect.local` | `MAIN` branch |
| `ACCOUNTANT` | `accountant@doodhdirect.local` | `MAIN` branch |

## Local dairy operations workflow

When `SeedOptions:EnableDevelopmentSeeds` is enabled, Development startup creates a dairy manager scoped only to the `MAIN` branch: `dairy.manager@doodhdirect.local` / `DoodhDirect@123`. Sign in with this account to record production, inspect the automatically-created batch, review operational availability, and append batch usage. The fixture uses the normal password hasher and JWT authentication flow and is never seeded unless the seed option is explicitly enabled.

## Local delivery workflow

1. Complete the local payment and wallet workflow through a confirmed customer order. With `SeedOptions:EnableDevelopmentSeeds` enabled, Development startup also creates a branch-scoped delivery staff account for `MAIN`: `delivery@doodhdirect.local` / `DoodhDirect@123`.
2. Sign in as `delivery.manager@doodhdirect.local` to open the manager workspace for the `MAIN` branch. The `OWNER` and `SYSTEM_ADMIN` accounts have global access for workflows that require it.
3. In the manager workspace, materialize eligible deliveries through the order's scheduled date, open the `MAIN` branch queue, and assign the order's delivery to Development Delivery Staff.
4. Sign out and sign in as `delivery@doodhdirect.local`. The staff workspace shows assigned deliveries for the selected date. Open the assigned delivery and perform Pickup, Start delivery, and Arrive in that order.
5. With a configured server-side OTP delivery provider, issue the OTP and verify the customer-provided code, then complete the delivery. Alternatively, exercise the failed-delivery action with a supported failure reason and optional coordinates.
6. Sign back in as the customer and open Orders. The delivery status is available from the delivery details while tracking is active. Location updates can also be posted to the staff location endpoint using device coordinates and an explicitly UTC timestamp, because this endpoint retains provider/device instant semantics.

The Flutter client does not currently acquire device location from a platform location plugin. The delivery location API and client repository are available for an approved platform integration or API-driven local verification. The default backend OTP provider is unconfigured, so real OTP completion requires a configured server-side delivery integration.

## Validation

Run these commands from the `mobile` directory:

```powershell
flutter analyze
flutter test
flutter build web --release
```

For Maps UAT, use the Development launcher, open `http://localhost:51482/`, sign in as the Development customer, and open Customer account > Addresses > Add address. Confirm that the map loads near Faridabad, a marker is visible, tapping the map updates the internal coordinates, no manual coordinate fields are shown, reverse lookup still uses the backend, and saving continues through the existing address API.

The client expects the API routes documented in [`Document/05_API_Specification.md`](../Document/05_API_Specification.md), including authentication, customer, catalogue, order, payment, wallet, and delivery routes.
