import 'dart:async';
import 'dart:js_interop';
import 'dart:js_interop_unsafe';

import 'payment_gateway_contract.dart';
import 'payment_models.dart';

PaymentGatewayLauncher createPaymentGatewayLauncher() =>
    const _RazorpayWebPaymentGatewayLauncher();

class _RazorpayWebPaymentGatewayLauncher
    implements PaymentGatewayLauncher {
  const _RazorpayWebPaymentGatewayLauncher();

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

    void fail(String message) {
      if (!completer.isCompleted) {
        completer.completeError(PaymentGatewayException(message));
      }
    }

    final options = <String, Object?>{
      'key': gatewayKeyId,
      'order_id': gatewayOrderId,
      'amount': (payment.amount * 100).round(),
      'currency': payment.currency,
      'name': 'DoodhDirect',
      'description': 'Order ${payment.orderNumber}',
      'retry': {'enabled': true, 'max_count': 2},
      'theme': {'color': '#087F8C'},
      'handler': ((JSObject response) {
        final paymentId = _stringProperty(response, 'razorpay_payment_id');
        final orderId = _stringProperty(response, 'razorpay_order_id');
        final signature = _stringProperty(response, 'razorpay_signature');
        if (paymentId == null || orderId == null || signature == null) {
          fail('Razorpay returned an incomplete payment response.');
          return;
        }
        if (!completer.isCompleted) {
          completer.complete(
            PaymentGatewayCallback(
              gatewayPaymentId: paymentId,
              gatewayOrderId: orderId,
              signature: signature,
            ),
          );
        }
      }).toJS,
      'modal': {
        'ondismiss': (() {
          fail('Razorpay checkout was cancelled.');
        }).toJS,
      },
    }.jsify() as JSObject;

    try {
      final razorpay = Razorpay(options);
      razorpay.on(
        'payment.failed',
        ((JSObject _) {
          fail('Razorpay could not complete the payment. Please try again.');
        }).toJS,
      );
      razorpay.open();
    } on Object {
      fail(
        'Unable to open Razorpay checkout. Refresh the page and try again.',
      );
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

@JS('Razorpay')
extension type Razorpay._(JSObject _) implements JSObject {
  external factory Razorpay(JSObject options);

  external void on(String event, JSFunction callback);

  external void open();
}
