import 'otp_platform_contract.dart';

OtpPlatform createOtpPlatform() => const _MobileOtpPlatform();

class _MobileOtpPlatform implements OtpPlatform {
  const _MobileOtpPlatform();

  @override
  bool get isWidgetAvailable => false;

  @override
  Future<OtpPlatformCode> collectOtp({
    required String mobile,
    required String widgetId,
  }) => Future.error(
    const OtpPlatformException(
      'Mobile uses the built-in OTP screen; the MSG91 widget is a web-only flow.',
    ),
  );
}
