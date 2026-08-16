import 'dart:async';

import 'package:razorpay_flutter/razorpay_flutter.dart';

import 'payment_gateway_contract.dart';
import 'payment_models.dart';

PaymentGatewayLauncher createPaymentGatewayLauncher() =>
    _RazorpayPaymentGatewayLauncher();

class _RazorpayPaymentGatewayLauncher implements PaymentGatewayLauncher {
  @override
  Future<PaymentGatewayCallback> open(PaymentDetails payment) {
    final gatewayOrderId = payment.gatewayOrderId;
    final gatewayKeyId = payment.gatewayKeyId;
    if (gatewayOrderId == null || gatewayKeyId == null) {
      return Future.error(
        const PaymentGatewayException(
          'The payment gateway configuration is incomplete.',
        ),
      );
    }

    final completer = Completer<PaymentGatewayCallback>();
    final razorpay = Razorpay();

    void dispose() {
      razorpay.clear();
    }

    razorpay.on(Razorpay.EVENT_PAYMENT_SUCCESS, (dynamic value) {
      final response = value as PaymentSuccessResponse;
      final paymentId = response.paymentId;
      final orderId = response.orderId;
      final signature = response.signature;
      dispose();
      if (paymentId == null || orderId == null || signature == null) {
        completer.completeError(
          const PaymentGatewayException(
            'Razorpay returned an incomplete payment response.',
          ),
        );
        return;
      }
      completer.complete(
        PaymentGatewayCallback(
          gatewayPaymentId: paymentId,
          gatewayOrderId: orderId,
          signature: signature,
        ),
      );
    });
    razorpay.on(Razorpay.EVENT_PAYMENT_ERROR, (dynamic value) {
      final response = value as PaymentFailureResponse;
      dispose();
      completer.completeError(
        PaymentGatewayException(
          response.message ?? 'Razorpay checkout was not completed.',
        ),
      );
    });
    razorpay.on(Razorpay.EVENT_EXTERNAL_WALLET, (dynamic value) {
      final response = value as ExternalWalletResponse;
      dispose();
      completer.completeError(
        PaymentGatewayException(
          '${response.walletName ?? 'The selected wallet'} requires external verification.',
        ),
      );
    });

    try {
      razorpay.open({
        'key': gatewayKeyId,
        'order_id': gatewayOrderId,
        'amount': (payment.amount * 100).round(),
        'currency': payment.currency,
        'name': 'DoodhDirect',
        'description': 'Order ${payment.orderNumber}',
        'retry': {'enabled': true, 'max_count': 2},
        'theme': {'color': '#087F8C'},
      });
    } on Object catch (error) {
      dispose();
      return Future.error(
        PaymentGatewayException('Unable to open Razorpay checkout: $error'),
      );
    }

    return completer.future;
  }
}
