# DoodhDirect Flutter application

The `mobile` project is the Phase 1 DoodhDirect client for Android, iOS, and web. It implements the identity session workflow and derives navigation from roles returned by the API.

## Implemented client behavior

- Email/mobile password registration and login
- OTP request and verification flows
- Secure session persistence and refresh-based restoration
- Device identifier persistence for device-bound sessions
- Current-user lookup
- Logout with local session cleanup even when the API call fails
- Expired refresh-session cleanup
- Role-aware navigation based on server role codes
- Standard API success/error envelope handling

## Configuration

The API base URL is supplied with the `DOOHDIRECT_API_URL` Dart define. The default is `https://localhost:7213`.

```powershell
flutter pub get
flutter run -d chrome --dart-define=DOOHDIRECT_API_URL=https://localhost:7213
```

For an Android emulator or physical device, use an API hostname reachable from that device. Host-only `localhost` usually points to the device itself rather than the development machine.

## Validation

Run these commands from the `mobile` directory:

```powershell
flutter analyze
flutter test
flutter build web --release
```

The client expects the API routes documented in [`Document/05_API_Specification.md`](../Document/05_API_Specification.md), including `/api/v1/auth/register`, `/api/v1/auth/login`, `/api/v1/auth/send-otp`, `/api/v1/auth/verify-otp`, `/api/v1/auth/refresh`, `/api/v1/auth/logout`, and `/api/v1/auth/me`.

OTP delivery is not configured by the backend default provider, so end-to-end OTP use requires a configured server-side delivery integration.
