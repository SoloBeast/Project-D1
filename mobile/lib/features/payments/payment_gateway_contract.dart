import 'payment_models.dart';

class PaymentGatewayCallback {
  const PaymentGatewayCallback({
    required this.gatewayPaymentId,
    required this.gatewayOrderId,
    required this.signature,
  });

  final String gatewayPaymentId;
  final String gatewayOrderId;
  final String signature;
}

abstract interface class PaymentGatewayLauncher {
  Future<PaymentGatewayCallback> open(PaymentDetails payment);
}

class PaymentGatewayException implements Exception {
  const PaymentGatewayException(this.message);

  final String message;

  @override
  String toString() => message;
}
