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

```powershell
flutter pub get
flutter run -d chrome --dart-define=DOOHDIRECT_API_URL=http://localhost:5209 --dart-define=DOOHDIRECT_ENABLE_DEV_TOOLS=true
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

## Local delivery workflow

1. Complete the local payment and wallet workflow through a confirmed customer order. Development startup now also creates a branch-scoped delivery staff account for `MAIN`: `delivery@doodhdirect.local` / `DoodhDirect@123`.
2. Use an account with `DELIVERIES.ASSIGN_BRANCH` and `DELIVERIES.READ_BRANCH` access for `MAIN` to open the manager workspace. The Development fixture intentionally does not create a manager or owner account; assign those permissions and the `MAIN` branch scope to an existing local employee before this step.
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

The client expects the API routes documented in [`Document/05_API_Specification.md`](../Document/05_API_Specification.md), including authentication, customer, catalogue, order, payment, wallet, and delivery routes.
