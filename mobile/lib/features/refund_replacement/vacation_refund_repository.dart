import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'vacation_refund_models.dart';

final vacationRefundRepositoryProvider = Provider<VacationRefundRepository>(
  (ref) => VacationRefundRepository(api: authenticatedApiClient(ref)),
);

/// Server-backed vacation-refund history. Money is always rendered from the
/// frozen server snapshot; the client never computes amounts or deductions.
class VacationRefundRepository {
  VacationRefundRepository({required this.api});

  final ApiClient api;

  Future<List<VacationRefundRequest>> listForCustomer(String token) async =>
      ((await api.get(
                    '/api/v1/subscriptions/vacation-refunds',
                    accessToken: token,
                  ))['data']
                  as List<dynamic>?)
              ?.cast<Map<String, dynamic>>()
              .map(VacationRefundRequest.fromJson)
              .toList(growable: false) ??
          const [];

  Future<VacationRefundRequest?> getForCustomer(
    String token,
    String requestId,
  ) async {
    final response = await api.get(
      '/api/v1/subscriptions/vacation-refunds/$requestId',
      accessToken: token,
    );
    final data = response['data'];
    return data is Map<String, dynamic>
        ? VacationRefundRequest.fromJson(data)
        : null;
  }

  /// Customer-facing state for one owned subscription: the created request
  /// when present, otherwise the pending/blocked posture so an end-of-term
  /// vacation refund that has not appeared yet is explained. Read-only:
  /// never processes or mutates.
  Future<VacationRefundSubscriptionStatus?> getStatusForCustomer(
    String token,
    String subscriptionId,
  ) async {
    final response = await api.get(
      '/api/v1/subscriptions/vacation-refunds/$subscriptionId/vacation-refund-status',
      accessToken: token,
    );
    final data = response['data'];
    return data is Map<String, dynamic>
        ? VacationRefundSubscriptionStatus.fromJson(data)
        : null;
  }

  Future<List<VacationRefundRequest>> listForStaff(String token) async =>
      ((await api.get(
                    '/api/v1/vacation-refunds',
                    accessToken: token,
                  ))['data']
                  as List<dynamic>?)
              ?.cast<Map<String, dynamic>>()
              .map(VacationRefundRequest.fromJson)
              .toList(growable: false) ??
          const [];

  Future<VacationRefundRequest?> getForStaff(
    String token,
    String requestId,
  ) async {
    final response = await api.get(
      '/api/v1/vacation-refunds/$requestId',
      accessToken: token,
    );
    final data = response['data'];
    return data is Map<String, dynamic>
        ? VacationRefundRequest.fromJson(data)
        : null;
  }

  Future<VacationRefundRequest> _decide(
    String token,
    String requestId,
    String action,
    String? remarks,
  ) async =>
      VacationRefundRequest.fromJson(
        (await api.post(
              '/api/v1/vacation-refunds/$requestId/$action',
              body: {'remarks': remarks},
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<VacationRefundRequest> approve(
    String token,
    String requestId, {
    String? remarks,
  }) async =>
      _decide(token, requestId, 'approve', remarks);

  Future<VacationRefundRequest> reject(
    String token,
    String requestId, {
    String? remarks,
  }) async =>
      _decide(token, requestId, 'reject', remarks);

  Future<VacationRefundRequest> complete(
    String token,
    String requestId, {
    String? remarks,
  }) async =>
      _decide(token, requestId, 'complete', remarks);
}
