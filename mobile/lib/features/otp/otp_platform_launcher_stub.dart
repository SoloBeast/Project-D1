import 'otp_platform_contract.dart';

OtpPlatform createOtpPlatform() => const _UnsupportedOtpPlatform();

class _UnsupportedOtpPlatform implements OtpPlatform {
  const _UnsupportedOtpPlatform();

  @override
  bool get isWidgetAvailable => false;

  @override
  Future<OtpPlatformCode> collectOtp({
    required String mobile,
    required String widgetId,
  }) => Future.error(
    const OtpPlatformException(
      'The MSG91 OTP widget is available in the web app.',
    ),
  );
}
