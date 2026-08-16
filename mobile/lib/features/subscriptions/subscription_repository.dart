import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';

import 'subscription_models.dart';

class SubscriptionRepository {
  SubscriptionRepository({required this.api});

  final ApiClient api;

  Future<CreatedSubscription> create({
    required String token,
    required CreateSubscriptionRequest request,
    required String idempotencyKey,
  }) async {
    final response = await api.post(
      '/api/v1/subscriptions',
      body: request.toJson(),
      accessToken: token,
      extraHeaders: {'Idempotency-Key': idempotencyKey},
    );
    return CreatedSubscription.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<CreatedSubscription> retryPayment({
    required String token,
    required String subscriptionId,
    required PaymentMethod paymentMethod,
    required String idempotencyKey,
  }) async {
    final response = await api.post(
      '/api/v1/subscriptions/$subscriptionId/retry-payment',
      body: {'paymentMethod': paymentMethod.apiValue},
      accessToken: token,
      extraHeaders: {'Idempotency-Key': idempotencyKey},
    );
    return CreatedSubscription.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<List<SubscriptionDetails>> getMine(String token) async {
    final response = await api.get('/api/v1/subscriptions', accessToken: token);
    return _list(response)
        .map(SubscriptionDetails.fromJson)
        .toList(growable: false);
  }

  Future<SubscriptionDetails> get(String token, String subscriptionId) async {
    final response = await api.get(
      '/api/v1/subscriptions/$subscriptionId',
      accessToken: token,
    );
    return SubscriptionDetails.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<SubscriptionDetails> update({
    required String token,
    required String subscriptionId,
    required UpdateSubscriptionRequest request,
  }) async {
    final response = await api.patch(
      '/api/v1/subscriptions/$subscriptionId',
      body: request.toJson(),
      accessToken: token,
    );
    return SubscriptionDetails.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<SubscriptionDetails> pause(String token, String subscriptionId) =>
      _postAction(token, subscriptionId, 'pause');

  Future<SubscriptionDetails> resume(String token, String subscriptionId) =>
      _postAction(token, subscriptionId, 'resume');

  Future<SubscriptionDetails> cancel(String token, String subscriptionId) =>
      _postAction(token, subscriptionId, 'cancel');

  Future<SubscriptionDelivery> skip({
    required String token,
    required String subscriptionId,
    required String deliveryId,
  }) async {
    final response = await api.post(
      '/api/v1/subscriptions/$subscriptionId/skip',
      body: {'deliveryId': deliveryId},
      accessToken: token,
    );
    return SubscriptionDelivery.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<List<SubscriptionDelivery>> getCalendar(
    String token,
    String subscriptionId,
  ) async {
    final response = await api.get(
      '/api/v1/subscriptions/$subscriptionId/calendar',
      accessToken: token,
    );
    return _list(response)
        .map(SubscriptionDelivery.fromJson)
        .toList(growable: false);
  }

  Future<SubscriptionDetails> _postAction(
    String token,
    String subscriptionId,
    String action,
  ) async {
    final response = await api.post(
      '/api/v1/subscriptions/$subscriptionId/$action',
      accessToken: token,
    );
    return SubscriptionDetails.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>? ?? const <dynamic>[])
          .cast<Map<String, dynamic>>();
}
