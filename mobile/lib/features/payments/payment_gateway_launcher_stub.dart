import 'payment_gateway_contract.dart';
import 'payment_models.dart';

PaymentGatewayLauncher createPaymentGatewayLauncher() =>
    const _UnsupportedPaymentGatewayLauncher();

class _UnsupportedPaymentGatewayLauncher implements PaymentGatewayLauncher {
  const _UnsupportedPaymentGatewayLauncher();

  @override
  Future<PaymentGatewayCallback> open(PaymentDetails payment) => Future.error(
    const PaymentGatewayException(
      'Razorpay checkout is available in the Android and iOS apps.',
    ),
  );
}
