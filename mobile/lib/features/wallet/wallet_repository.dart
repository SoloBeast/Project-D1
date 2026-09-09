import 'package:doodh_direct_mobile/core/network/api_client.dart';

import '../payments/payment_models.dart';
import 'wallet_models.dart';

class WalletRepository {
  WalletRepository({required this.api});

  final ApiClient api;

  Future<WalletDetails> get(String token) async {
    final response = await api.get('/api/v1/wallet', accessToken: token);
    return WalletDetails.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<List<WalletTransaction>> getTransactions(String token) async {
    final response = await api.get(
      '/api/v1/wallet/transactions',
      accessToken: token,
    );
    return (response['data'] as List<dynamic>? ?? const <dynamic>[])
        .cast<Map<String, dynamic>>()
        .map(WalletTransaction.fromJson)
        .toList(growable: false);
  }

  Future<PaymentDetails> topUp({
    required String token,
    required double amount,
    required String idempotencyKey,
  }) async {
    final response = await api.post(
      '/api/v1/wallet/top-up',
      body: <String, dynamic>{'amount': amount},
      accessToken: token,
      extraHeaders: <String, String>{'Idempotency-Key': idempotencyKey},
    );
    return PaymentDetails.fromJson(response['data'] as Map<String, dynamic>);
  }
}
