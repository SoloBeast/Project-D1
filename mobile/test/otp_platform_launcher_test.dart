import 'package:doodh_direct_mobile/features/otp/otp_platform_launcher.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('OtpPlatform abstraction', () {
    test('factory resolves to a platform implementation', () {
      expect(createOtpPlatform(), isA<OtpPlatform>());
    });

    test('native builds report the MSG91 widget as unavailable', () {
      // flutter test runs in the VM where the native implementation is
      // selected by the conditional import. The MSG91 OTP widget is a
      // web-only SDK; mobile keeps the built-in OTP screen.
      expect(createOtpPlatform().isWidgetAvailable, isFalse);
    });

    test('collectOtp rejects with OtpPlatformException on native', () async {
      await expectLater(
        createOtpPlatform().collectOtp(
          mobile: '9876543210',
          widgetId: '6f2c4a1b9e3d',
        ),
        throwsA(isA<OtpPlatformException>()),
      );
    });

    test('OtpPlatformCode carries the entered code', () {
      expect(const OtpPlatformCode('123456').code, '123456');
    });

    test('OtpPlatformException surfaces its message', () {
      const error = OtpPlatformException('Widget unavailable.');
      expect(error.toString(), 'Widget unavailable.');
    });
  });
}
