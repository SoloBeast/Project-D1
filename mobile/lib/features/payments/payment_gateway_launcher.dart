import 'payment_gateway_contract.dart';
import 'payment_gateway_launcher_stub.dart'
    if (dart.library.io) 'payment_gateway_launcher_native.dart'
    as platform;

export 'payment_gateway_contract.dart';

PaymentGatewayLauncher createPaymentGatewayLauncher() =>
    platform.createPaymentGatewayLauncher();
