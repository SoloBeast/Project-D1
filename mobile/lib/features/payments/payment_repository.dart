import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'payment_models.dart';

class PaymentRepository {
  PaymentRepository({required this.api});

  final ApiClient api;

  Future<PaymentDetails> create({
    required String token,
    required String orderId,
    required PaymentMethod method,
    required String idempotencyKey,
  }) async {
    final response = await api.post(
      '/api/v1/payments/create',
      body: {'orderId': orderId, 'method': method.apiValue},
      accessToken: token,
      extraHeaders: {'Idempotency-Key': idempotencyKey},
    );
    return PaymentDetails.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<PaymentDetails> verify({
    required String token,
    required String paymentId,
    required String gatewayOrderId,
    required String gatewayPaymentId,
    required String signature,
  }) async {
    final response = await api.post(
      '/api/v1/payments/verify',
      body: {
        'paymentId': paymentId,
        'gatewayOrderId': gatewayOrderId,
        'gatewayPaymentId': gatewayPaymentId,
        'signature': signature,
      },
      accessToken: token,
    );
    return PaymentDetails.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<PaymentDetails> get(String token, String paymentId) async {
    final response = await api.get(
      '/api/v1/payments/$paymentId',
      accessToken: token,
    );
    return PaymentDetails.fromJson(response['data'] as Map<String, dynamic>);
  }
}
