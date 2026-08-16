import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'order_models.dart';

class OrderRepository {
  OrderRepository({required this.api});

  final ApiClient api;

  Future<CheckoutPreview> preview(String token, CheckoutRequest request) async {
    final response = await api.post(
      '/api/v1/orders/checkout-preview',
      body: request.toJson(),
      accessToken: token,
    );
    return CheckoutPreview.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<OrderSummary> create(
    String token,
    CheckoutRequest request,
    String idempotencyKey,
  ) async {
    final response = await api.post(
      '/api/v1/orders',
      body: request.toJson(),
      accessToken: token,
      extraHeaders: {'Idempotency-Key': idempotencyKey},
    );
    return OrderSummary.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<List<OrderSummary>> getMine(String token) async {
    final response = await api.get('/api/v1/orders', accessToken: token);
    return _list(response).map(OrderSummary.fromJson).toList(growable: false);
  }

  Future<OrderSummary> get(String token, String orderId) async {
    final response = await api.get(
      '/api/v1/orders/$orderId',
      accessToken: token,
    );
    return OrderSummary.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<OrderSummary> cancel(String token, String orderId) async {
    final response = await api.post(
      '/api/v1/orders/$orderId/cancel',
      accessToken: token,
    );
    return OrderSummary.fromJson(response['data'] as Map<String, dynamic>);
  }

  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>? ?? const <dynamic>[])
          .cast<Map<String, dynamic>>();
}
