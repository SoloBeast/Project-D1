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

  /// Stage 2.1: single atomic submission binding validated proof evidence to
  /// the ticket. The server re-derives the evidence requirement from its own
  /// records; field values here are advisory only.
  Future<RefundReplacementRequest> submitWithImage(
    String token,
    String deliveryId, {
    required RefundReplacementType type,
    required String reason,
    String? remarks,
    String? milkTestId,
    required Uint8List imageBytes,
    required String fileName,
    required String contentType,
  }) async => RefundReplacementRequest.fromJson(
    (await api.postMultipart(
          '/api/v1/deliveries/$deliveryId/refund-replacement',
          fieldName: 'image',
          bytes: imageBytes,
          fileName: fileName,
          contentType: contentType,
          fields: {
            'type': _wireType(type),
            'reason': reason,
            'remarks': ?remarks,
            'milkTestId': ?milkTestId,
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
  ///
  /// Stage 2: for order-backed Refund tickets [applyDeduction] is required —
  /// true applies the shared Admin Centre percentage (snapshotted by the
  /// backend), false waives it to zero and requires [waiverReason]. Null
  /// preserves the existing subscription approval behavior. The keys are
  /// omitted when null so legacy payloads stay byte-identical.
  Future<RefundReplacementRequest> approve(
    String token,
    String requestId, {
    String? remarks,
    DateTime? replacementDate,
    bool? applyDeduction,
    String? waiverReason,
    String? settlementMethod,
  }) async => RefundReplacementRequest.fromJson(
    (await api.post(
          '/api/v1/refund-replacements/$requestId/approve',
          body: {
            'remarks': remarks,
            if (replacementDate != null)
              'replacementDate': _wireDateOnly(replacementDate),
            if (applyDeduction case final bool apply)
              'applyDeduction': apply,
            if (waiverReason case final String reason) 'waiverReason': reason,
            if (settlementMethod case final String method)
              'settlementMethod': method,
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  /// Stage 2: read-only preview of an order-backed Refund ticket's proposed
  /// wallet settlement. The UI renders the response verbatim and never
  /// computes money itself. Never mutates server state.
  Future<OrderRefundPreview> getOrderRefundPreview(
    String token,
    String requestId, {
    required bool applyDeduction,
  }) async => OrderRefundPreview.fromJson(
    (await api.get(
          '/api/v1/refund-replacements/$requestId/order-refund-preview'
          '?applyDeduction=${applyDeduction ? 'true' : 'false'}',
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

  /// Legacy remediation: atomically supersedes an approved, order-backed
  /// Replacement ticket and creates exactly one linked Pending Refund
  /// ticket. Requires `REFUND_REPLACEMENT.MANAGE_BRANCH`; the backend
  /// enforces branch scope, the Failed-delivery precondition, and
  /// idempotency (409 when the ticket no longer qualifies). Issues no
  /// wallet credit and creates no financial refund record.
  Future<RemediationPair> remediate(
    String token,
    String requestId, {
    required String reason,
  }) async => RemediationPair.fromJson(
    (await api.post(
          '/api/v1/refund-replacements/$requestId/remediate',
          body: {'reason': reason},
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
  RefundReplacementStatus.superseded => 'Superseded',
  RefundReplacementStatus.unknown => 'Pending',
};
