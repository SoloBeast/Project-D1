import 'otp_platform_contract.dart';
import 'otp_platform_launcher_stub.dart'
    if (dart.library.io) 'otp_platform_launcher_native.dart'
    if (dart.library.js_interop) 'otp_platform_launcher_web.dart'
    as platform;

export 'otp_platform_contract.dart';

OtpPlatform createOtpPlatform() => platform.createOtpPlatform();
