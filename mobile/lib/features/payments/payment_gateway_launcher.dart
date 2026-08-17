import 'payment_gateway_contract.dart';
import 'payment_gateway_launcher_stub.dart'
    if (dart.library.io) 'payment_gateway_launcher_native.dart'
    if (dart.library.js_interop) 'payment_gateway_launcher_web.dart'
    as platform;

export 'payment_gateway_contract.dart';

PaymentGatewayLauncher createPaymentGatewayLauncher() =>
    platform.createPaymentGatewayLauncher();
