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

  Future<RefundReplacementRequest> approve(
    String token,
    String requestId, {
    String? remarks,
  }) => _decide(token, requestId, 'approve', remarks);

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

String _wireStatus(RefundReplacementStatus status) => switch (status) {
  RefundReplacementStatus.pending => 'Pending',
  RefundReplacementStatus.approved => 'Approved',
  RefundReplacementStatus.rejected => 'Rejected',
  RefundReplacementStatus.completed => 'Completed',
  RefundReplacementStatus.unknown => 'Pending',
};
