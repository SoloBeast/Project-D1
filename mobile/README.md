# DoodhDirect Flutter application

The `mobile` project is the DoodhDirect client for Android, iOS, and web. It implements authenticated customer ordering, payments, wallet access, delivery operations, and role-aware workspaces.

## Implemented client behavior

- Email/mobile password registration and login
- OTP request and verification flows
- Secure session persistence and refresh-based restoration
- Customer catalogue, address, checkout, and one-time order flows
- Wallet balance, transaction history, and Development-only top-up
- Wallet and Razorpay payment initiation with server verification
- Payment result refresh, failure handling, and terminal-payment retry
- Customer delivery status and active-location tracking
- Delivery staff pickup, start, arrival, OTP, completion, failure, and location updates
- Delivery manager materialization, branch queue, employee assignment, and detail views
- Role-aware navigation based on server role codes
- Standard API success/error envelope handling

## Configuration

The centralized Development web API base URL defaults to `http://localhost:5209`. The `DOOHDIRECT_API_URL` Dart define can override it for Production and other deployment-specific endpoints. `DOOHDIRECT_ENABLE_DEV_TOOLS=true` exposes the quick customer login, wallet top-up, and mock payment controls; do not set it for distributable builds.

Flutter Web Maps uses the existing local Dart-define configuration pattern. Supply the Google Maps JavaScript API key through `DOOHDIRECT_GOOGLE_MAPS_API_KEY`; it is injected only into the local Web build at runtime. Do not hard-code it in Dart or `web/index.html`, commit it to the repository, print it in logs, or copy it from `Project D Credentials.txt`. The Google Cloud key must allow the exact origin `http://localhost:51482/` and be restricted to the Maps JavaScript API. Places API and Geocoding API are not required by this feature.

Run the Web app on port `51482` so the configured website restriction matches the browser origin. In PowerShell, store the key only in the current local shell session before starting Flutter:

```powershell
$env:DOOHDIRECT_GOOGLE_MAPS_API_KEY = '<local key value>'
flutter pub get
flutter run -d chrome --web-port 51482 `
  --dart-define=DOOHDIRECT_API_URL=http://localhost:5209 `
  --dart-define=DOOHDIRECT_ENABLE_DEV_TOOLS=true `
  --dart-define=DOOHDIRECT_GOOGLE_MAPS_API_KEY=$env:DOOHDIRECT_GOOGLE_MAPS_API_KEY
```

For an Android emulator connecting to the Development HTTP profile, use an API hostname reachable from the emulator, commonly `http://10.0.2.2:5209`. A physical device must use the development machine's reachable LAN hostname or address.

## Local payment and wallet workflow

1. Start SQL Server Express and apply migrations from the repository root:

   ```powershell
   dotnet ef database update --project Backend/src/DoodhDirect.Infrastructure --startup-project Backend/src/DoodhDirect.Api
   ```

2. Start the API with its Development HTTP profile at `http://localhost:5209`. `appsettings.Development.json` selects the Mock payment provider, and startup creates the checkout-ready development customer, default address, branch, and product:

   ```powershell
   dotnet run --project Backend/src/DoodhDirect.Api --launch-profile http
   ```

3. Run Flutter with development tools enabled using the command above, then choose **Sign in as development customer**. Manual credentials are `customer@doodhdirect.local` and `DoodhDirect@123`.
4. Open the catalogue, select Fresh Buffalo Milk, use the seeded Home address, review checkout, and create the order.
5. For Razorpay, select Razorpay and choose **Complete development payment** on the result screen. The client sends the Mock provider callback to the backend, and only the server-confirmed success state confirms the order.
6. For Wallet, open Wallet and add a Development top-up large enough for the order, then create another order and select Wallet. The backend debits the ledger and confirms the order atomically.
7. Open Orders and the order detail. Verify the final status is Confirmed and that Wallet shows the matching debit for wallet-paid orders.

The backend fixture is activated only by the ASP.NET Development environment. The visual shortcuts are compiled out unless `DOOHDIRECT_ENABLE_DEV_TOOLS` is explicitly enabled.

## Development UAT accounts

Development startup creates these local-only accounts with the normal password hasher and JWT authentication flow. Every account uses the password `DoodhDirect@123` and is never seeded outside the ASP.NET Development environment.

| Role | Email | Scope |
| --- | --- | --- |
| `OWNER` | `owner@doodhdirect.local` | Global |
| `SYSTEM_ADMIN` | `system.admin@doodhdirect.local` | Global |
| `DELIVERY_MANAGER` | `delivery.manager@doodhdirect.local` | `MAIN` branch |
| `CUSTOMER_SUPPORT` | `support@doodhdirect.local` | `MAIN` branch |
| `ACCOUNTANT` | `accountant@doodhdirect.local` | `MAIN` branch |

## Local dairy operations workflow

Development startup creates a dairy manager scoped only to the `MAIN` branch: `dairy.manager@doodhdirect.local` / `DoodhDirect@123`. Sign in with this account to record production, inspect the automatically-created batch, review operational availability, and append batch usage. The fixture uses the normal password hasher and JWT authentication flow and is never seeded outside the ASP.NET Development environment.

## Local delivery workflow

1. Complete the local payment and wallet workflow through a confirmed customer order. Development startup also creates a branch-scoped delivery staff account for `MAIN`: `delivery@doodhdirect.local` / `DoodhDirect@123`.
2. Sign in as `delivery.manager@doodhdirect.local` to open the manager workspace for the `MAIN` branch. The `OWNER` and `SYSTEM_ADMIN` accounts have global access for workflows that require it.
3. In the manager workspace, materialize eligible deliveries through the order's scheduled date, open the `MAIN` branch queue, and assign the order's delivery to Development Delivery Staff.
4. Sign out and sign in as `delivery@doodhdirect.local`. The staff workspace shows assigned deliveries for the selected date. Open the assigned delivery and perform Pickup, Start delivery, and Arrive in that order.
5. With a configured server-side OTP delivery provider, issue the OTP and verify the customer-provided code, then complete the delivery. Alternatively, exercise the failed-delivery action with a supported failure reason and optional coordinates.
6. Sign back in as the customer and open Orders. The delivery status is available from the delivery details while tracking is active. Location updates can also be posted to the staff location endpoint using device coordinates and a UTC timestamp.

The Flutter client does not currently acquire device location from a platform location plugin. The delivery location API and client repository are available for an approved platform integration or API-driven local verification. The default backend OTP provider is unconfigured, so real OTP completion requires a configured server-side delivery integration.

## Validation

Run these commands from the `mobile` directory:

```powershell
flutter analyze
flutter test
flutter build web --release
```

For Maps UAT, start the Web server with `--web-port 51482`, open `http://localhost:51482/`, sign in as the Development customer, and open Customer account > Addresses > Add address. Confirm that the map loads near Faridabad, a marker is visible, tapping the map updates latitude and longitude, manual coordinate entry still works, reverse lookup still uses the backend, and saving continues through the existing address API.

The client expects the API routes documented in [`Document/05_API_Specification.md`](../Document/05_API_Specification.md), including authentication, customer, catalogue, order, payment, wallet, and delivery routes.
