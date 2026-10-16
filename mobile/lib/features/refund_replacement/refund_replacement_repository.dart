import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'refund_replacement_models.dart';

class RefundReplacementRepository {
  RefundReplacementRepository({required this.api});

  final ApiClient api;

  Future<RefundReplacementEligibility> getEligibility(
    String token,
    String deliveryId,
  ) async => RefundReplacementEligibility.fromJson(
    (await api.get(
          '/api/v1/deliveries/$deliveryId/refund-replacement/eligibility',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<RefundReplacementRequest> submit(
    String token,
    String deliveryId, {
    required RefundReplacementType type,
    required String reason,
    String? remarks,
    String? milkTestId,
  }) async => RefundReplacementRequest.fromJson(
    (await api.post(
          '/api/v1/deliveries/$deliveryId/refund-replacement',
          body: {
            'type': _wireType(type),
            'reason': reason,
            'remarks': remarks,
            'milkTestId': milkTestId,
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<List<RefundReplacementRequest>> listForCustomer(String token) async =>
      ((await api.get(
                    '/api/v1/customer/refund-replacements',
                    accessToken: token,
                  ))['data']
                  as List<dynamic>? ??
              const [])
          .cast<Map<String, dynamic>>()
          .map(RefundReplacementRequest.fromJson)
          .toList(growable: false);

  Future<RefundReplacementRequest?> getForCustomer(
    String token,
    String requestId,
  ) async {
    final response = await api.get(
      '/api/v1/customer/refund-replacements/$requestId',
      accessToken: token,
    );
    final data = response['data'];
    return data is Map<String, dynamic>
        ? RefundReplacementRequest.fromJson(data)
        : null;
  }

  /// Fetches the raw bytes of a protected proof image over the authenticated
  /// channel. The browser cannot send the JWT with [Image.network], so content
  /// is loaded here and rendered from bytes.
  Future<ApiByteResponse> getImageContent(
    String token,
    String requestId,
    String imageId,
  ) => api.getBytes(
    '/api/v1/refund-replacements/$requestId/images/$imageId/content',
    accessToken: token,
  );

  Future<RefundReplacementImage> uploadImage(
    String token,
    String requestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async => RefundReplacementImage.fromJson(
    (await api.postMultipart(
          '/api/v1/refund-replacements/$requestId/images',
          fieldName: 'image',
          bytes: bytes,
          fileName: fileName,
          contentType: contentType,
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<RefundReplacementPage> listForStaff(
    String token, {
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) async {
    final query = <String, String>{
      if (branchId != null) 'branchId': '$branchId',
      if (status != null) 'status': _wireStatus(status),
      'page': '$page',
      'pageSize': '$pageSize',
    };
    return RefundReplacementPage.fromJson(
      (await api.get(
            '/api/v1/refund-replacements?${Uri(queryParameters: query).query}',
            accessToken: token,
          ))['data']
          as Map<String, dynamic>,
    );
  }

  Future<RefundReplacementRequest> getForStaff(
    String token,
    String requestId,
  ) async => RefundReplacementRequest.fromJson(
    (await api.get(
          '/api/v1/refund-replacements/$requestId',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  /// D1-B: records the owning customer's explicit Refund / Re-Delivery choice
  /// on a delivery-fail ticket awaiting choice. The UI never computes money,
  /// deductions, wallet credit, or replacement deliveries from this call.
  Future<RefundReplacementRequest> choose(
    String token,
    String requestId,
    CustomerChoice choice,
  ) async => RefundReplacementRequest.fromJson(
    (await api.post(
          '/api/v1/customer/refund-replacements/$requestId/choose',
          body: {'choice': choice.wireValue},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  /// D1-D: approves a request. For re-delivery tickets [replacementDate] (a
  /// calendar day, no time) carries the staff-confirmed date and is sent as
  /// the approval request's `replacementDate` (`yyyy-MM-dd`); the backend
  /// remains authoritative for weekday, cutoff, branch, uniqueness,
  /// vacation/skip, and concurrency validation. Null preserves the existing
  /// refund/one-time approval behavior, letting the server propose the date.
  Future<RefundReplacementRequest> approve(
    String token,
    String requestId, {
    String? remarks,
    DateTime? replacementDate,
  }) async => RefundReplacementRequest.fromJson(
    (await api.post(
          '/api/v1/refund-replacements/$requestId/approve',
          body: {
            'remarks': remarks,
            if (replacementDate != null)
              'replacementDate': _wireDateOnly(replacementDate),
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  /// D1-D: fetches the server-proposed next eligible replacement date for a
  /// re-delivery ticket awaiting staff approval. Confirmed route + staff-only
  /// authorization: `GET /api/v1/refund-replacements/{id}/replacement-date-proposal`
  /// (requires `REFUND_REPLACEMENT.MANAGE_BRANCH`; there is no
  /// `/api/v1/customer/...` proposal route). Surfaces backend validation
  /// failures to the caller for display; never computed locally.
  Future<ReplacementDateProposal> getReplacementDateProposal(
    String token,
    String requestId,
  ) async => ReplacementDateProposal.fromJson(
    (await api.get(
          '/api/v1/refund-replacements/$requestId/replacement-date-proposal',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<RefundReplacementRequest> reject(
    String token,
    String requestId, {
    String? remarks,
  }) => _decide(token, requestId, 'reject', remarks);

  Future<RefundReplacementRequest> complete(
    String token,
    String requestId, {
    String? remarks,
  }) => _decide(token, requestId, 'complete', remarks);

  Future<RefundReplacementRequest> _decide(
    String token,
    String requestId,
    String decision,
    String? remarks,
  ) async => RefundReplacementRequest.fromJson(
    (await api.post(
          '/api/v1/refund-replacements/$requestId/$decision',
          body: {'remarks': remarks},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );
}

String _wireType(RefundReplacementType type) => switch (type) {
  RefundReplacementType.refund => 'Refund',
  RefundReplacementType.replacement => 'Replacement',
  RefundReplacementType.unknown => 'Refund',
};

String _wireDateOnly(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';

String _wireStatus(RefundReplacementStatus status) => switch (status) {
  RefundReplacementStatus.pending => 'Pending',
  RefundReplacementStatus.awaitingCustomerChoice => 'AwaitingCustomerChoice',
  RefundReplacementStatus.approved => 'Approved',
  RefundReplacementStatus.rejected => 'Rejected',
  RefundReplacementStatus.completed => 'Completed',
  RefundReplacementStatus.failed => 'Failed',
  RefundReplacementStatus.cancelled => 'Cancelled',
  RefundReplacementStatus.unknown => 'Pending',
};
