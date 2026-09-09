import 'dart:async';
import 'dart:js_interop';
import 'dart:js_interop_unsafe';

import 'otp_platform_contract.dart';

OtpPlatform createOtpPlatform() => const _Msg91WebOtpPlatform();

class _Msg91WebOtpPlatform implements OtpPlatform {
  const _Msg91WebOtpPlatform();

  @override
  bool get isWidgetAvailable =>
      globalContext.hasProperty('Msg91Otp'.toJS).toDart;

  @override
  Future<OtpPlatformCode> collectOtp({
    required String mobile,
    required String widgetId,
  }) {
    if (!isWidgetAvailable) {
      return Future.error(
        const OtpPlatformException(
          'The MSG91 OTP widget script is not loaded. Please contact support.',
        ),
      );
    }

    final completer = Completer<OtpPlatformCode>();

    void fail(String message) {
      if (!completer.isCompleted) {
        completer.completeError(OtpPlatformException(message));
      }
    }

    try {
      final options = <String, Object?>{
        'widgetId': widgetId,
        'mobile': mobile,
      }.jsify() as JSObject;
      final widget = Msg91Otp(options);
      widget.on(
        'verified',
        ((JSObject response) {
          final otp = _stringProperty(response, 'otp');
          if (otp == null) {
            fail('MSG91 OTP widget did not return the entered code.');
            return;
          }
          if (!completer.isCompleted) {
            completer.complete(OtpPlatformCode(otp));
          }
        }).toJS,
      );
      widget.sendOtp();
    } on Object {
      fail('Unable to open the MSG91 OTP widget. Please try again.');
    }

    return completer.future;
  }
}

String? _stringProperty(JSObject object, String name) {
  final value = object.getProperty<JSAny?>(name.toJS);
  if (value == null || !value.isA<JSString>()) return null;
  final text = (value as JSString).toDart;
  return text.isNotEmpty ? text : null;
}

@JS('Msg91Otp')
extension type Msg91Otp._(JSObject _) implements JSObject {
  external factory Msg91Otp(JSObject options);

  external void on(String event, JSFunction callback);

  external void sendOtp();

  /// Reserved for widget-driven flows. The app never calls this: the backend
  /// performs the authoritative MSG91 verification with the AuthKey.
  external void verifyOtp(JSObject request);

  /// Reserved for widget-driven flows. The app never calls this: the backend
  /// validates MSG91 access tokens server-side.
  external void validateAccessToken(JSObject request);
}
