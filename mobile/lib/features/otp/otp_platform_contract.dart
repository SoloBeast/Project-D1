// Platform OTP widget abstraction.
//
// The MSG91 OTP widget SDK is a web-only JavaScript library loaded from a
// <script> tag in `web/index.html`. This contract exposes a single,
// platform-independent surface so the app can ask the platform widget to
// collect the one-time code the user entered.
//
// SECURITY: the backend is ALWAYS the final authentication authority. The
// widget only assists with code entry; the app sends the entered code together
// with the MSG91 reqId to `/api/v1/auth/verify-otp`. The app never trusts a
// widget-issued access token and never holds the MSG91 AuthKey.

/// The one-time code a user entered through the platform OTP widget.
class OtpPlatformCode {
  const OtpPlatformCode(this.code);

  final String code;
}

/// Raised when the platform OTP widget cannot be used.
class OtpPlatformException implements Exception {
  const OtpPlatformException(this.message);

  final String message;

  @override
  String toString() => message;
}

/// Platform-specific MSG91 OTP widget surface.
abstract interface class OtpPlatform {
  /// Whether the current platform can render the MSG91 OTP widget.
  bool get isWidgetAvailable;

  /// Renders the MSG91 OTP widget (web) and returns the code the user
  /// entered. Throws [OtpPlatformException] on platforms where the widget is
  /// unavailable (the app's built-in OTP screen is used there instead).
  Future<OtpPlatformCode> collectOtp({
    required String mobile,
    required String widgetId,
  });
}
