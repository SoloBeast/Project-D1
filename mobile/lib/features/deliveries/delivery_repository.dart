import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'delivery_models.dart';

class DeliveryRepository {
  DeliveryRepository({required this.api});
  final ApiClient api;

  Future<List<CustomerDelivery>> getMine(String token) async =>
      _list(await api.get('/api/v1/deliveries', accessToken: token))
          .map(CustomerDelivery.fromJson)
          .toList(growable: false);
  Future<CustomerDelivery> getMineById(String token, String id) async =>
      CustomerDelivery.fromJson(
        (await api.get('/api/v1/deliveries/$id', accessToken: token))['data']
            as Map<String, dynamic>,
      );
  Future<List<DeliveryDetails>> getToday(
    String token,
    DateTime date, {
    DeliveryStatus? status,
  }) async {
    final query = <String>['date=${formatApiDeliveryDate(date)}'];
    if (status != null && status != DeliveryStatus.unknown) {
      query.add('status=${status.name}');
    }
    return _list(
      await api.get(
        '/api/v1/delivery/my-today?${query.join('&')}',
        accessToken: token,
      ),
    ).map(DeliveryDetails.fromJson).toList(growable: false);
  }

  Future<DeliveryDetails> getStaff(String token, String id) =>
      _staffGet(token, id);
  Future<List<DeliveryDetails>> getBranch(
    String token,
    int branchId, {
    DateTime? date,
    DeliveryStatus? status,
    DeliverySourceType? sourceType,
    SubscriptionDeliverySlot? slot,
  }) async {
    final query = <String>[];
    if (date != null) query.add('date=${formatApiDeliveryDate(date)}');
    if (status != null) query.add('status=${status.name}');
    if (sourceType != null && sourceType != DeliverySourceType.unknown) {
      query.add('sourceType=${sourceType.name}');
    }
    if (slot != null && slot != SubscriptionDeliverySlot.unknown) {
      query.add('slot=${slot.apiValue}');
    }
    final suffix = query.isEmpty ? '' : '?${query.join('&')}';
    return _list(
      await api.get(
        '/api/v1/delivery-management/branches/$branchId$suffix',
        accessToken: token,
      ),
    ).map(DeliveryDetails.fromJson).toList(growable: false);
  }

  /// Past-due scheduled subscription occurrences of a branch that were
  /// never operationalized. Skipped, replacement, failed, delivered and
  /// cancelled occurrences are never listed here.
  Future<List<MissedOccurrence>> getMissedOccurrences(
    String token,
    int branchId,
  ) async => _list(
    await api.get(
      '/api/v1/delivery-management/branches/$branchId/missed-occurrences',
      accessToken: token,
    ),
  ).map(MissedOccurrence.fromJson).toList(growable: false);

  /// Records a manager-confirmed miss for a past-due occurrence with no
  /// operational delivery. A failed operational record plus the remedy
  /// ticket (frozen zero deduction: no service was provided) are created
  /// through the existing delivery-fail workflow; nothing is marked
  /// delivered here.
  Future<DeliveryDetails> failMissedOccurrence({
    required String token,
    required int branchId,
    required String occurrenceId,
    required String reason,
    String? remarks,
  }) async => DeliveryDetails.fromJson(
    (await api.post(
          '/api/v1/delivery-management/branches/$branchId/missed-occurrences/$occurrenceId/fail',
          body: {'reason': reason, 'remarks': remarks},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<List<DeliveryEmployee>> getEmployees(
    String token,
    int branchId,
  ) async => _list(
    await api.get(
      '/api/v1/delivery-management/branches/$branchId/employees',
      accessToken: token,
    ),
  ).map(DeliveryEmployee.fromJson).toList(growable: false);
  Future<DeliveryDetails> getManaged(String token, String id) async =>
      DeliveryDetails.fromJson(
        (await api.get(
              '/api/v1/delivery-management/$id',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );
  Future<DeliveryBatchAllocationsInfo> getBatchAllocations(
    String token,
    String id,
  ) async => DeliveryBatchAllocationsInfo.fromJson(
    (await api.get(
          '/api/v1/delivery-management/$id/batch-allocations',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );
  Future<DeliveryDetails> saveBatchAllocations({
    required String token,
    required String deliveryId,
    required List<DeliveryBatchAllocation> allocations,
  }) async => DeliveryDetails.fromJson(
    (await api.put(
          '/api/v1/delivery-management/$deliveryId/batch-allocations',
          body: {
            'allocations': allocations
                .map((allocation) => allocation.toSaveJson())
                .toList(growable: false),
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );
  Future<DeliveryMaterialization> materialize(
    String token,
    DateTime throughDate,
  ) async => DeliveryMaterialization.fromJson(
    (await api.post(
          '/api/v1/delivery-management/materialize?throughDate=${formatApiDeliveryDate(throughDate)}',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );
  Future<DeliveryDetails> assign({
    required String token,
    required String deliveryId,
    required String employeeId,
    String? reason,
  }) async => DeliveryDetails.fromJson(
    (await api.post(
          '/api/v1/delivery-management/$deliveryId/assign',
          body: {'employeeId': employeeId, 'reason': reason},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<BulkAssignmentResult> bulkAssign({
    required String token,
    required List<String> deliveryIds,
    required String employeeId,
    String? reason,
  }) async => BulkAssignmentResult.fromJson(
    (await api.post(
          '/api/v1/delivery-management/bulk-assign',
          body: {
            'deliveryIds': deliveryIds,
            'employeeId': employeeId,
            'reason': reason,
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<DeliveryDetails> pickup(String token, String id, {String? remarks}) =>
      _action(
        token,
        id,
        'pickup',
        body: DeliveryNotesRequest(remarks: remarks).toJson(),
      );
  Future<DeliveryDetails> start(String token, String id) =>
      _action(token, id, 'start');
  Future<DeliveryDetails> arrive(String token, String id) =>
      _action(token, id, 'arrive');

  Future<DeliveryDetails> verifyOtp(String token, String id, String code) =>
      _action(token, id, 'verify-otp', body: {'code': code});
  Future<DeliveryDetails> complete(
    String token,
    String id, {
    String? remarks,
  }) => _action(
    token,
    id,
    'complete',
    body: DeliveryNotesRequest(remarks: remarks).toJson(),
  );
  Future<DeliveryDetails> fail(
    String token,
    String id, {
    required String reason,
    String? remarks,
  }) =>
      _action(token, id, 'fail', body: {'reason': reason, 'remarks': remarks});

  /// Fails a subscription delivery with the required staff proof image
  /// (multipart `fail-with-proof`). The server validates type/size, stores
  /// the proof, and opens the remedy ticket atomically. One-time order
  /// deliveries must keep using [fail] — the server rejects proof there.
  Future<DeliveryDetails> failWithProof(
    String token,
    String id, {
    required String reason,
    String? remarks,
    required Uint8List imageBytes,
    required String fileName,
    required String contentType,
  }) async {
    final fields = <String, String>{'reason': reason};
    if (remarks != null) {
      fields['remarks'] = remarks;
    }
    return DeliveryDetails.fromJson(
      (await api.postMultipart(
            '/api/v1/delivery/$id/fail-with-proof',
            fieldName: 'proof',
            bytes: imageBytes,
            fileName: fileName,
            contentType: contentType,
            fields: fields,
            accessToken: token,
          ))['data']
          as Map<String, dynamic>,
    );
  }
  Future<DeliveryLocation> recordLocation({
    required String token,
    required String id,
    required double latitude,
    required double longitude,
    double? accuracyMetres,
    required DateTime recordedAt,
  }) async => DeliveryLocation.fromJson(
    (await api.post(
          '/api/v1/delivery/$id/location',
          body: {
            'latitude': latitude,
            'longitude': longitude,
            'accuracyMetres': accuracyMetres,
            'recordedAt': recordedAt.toIso8601String(),
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<DeliveryDetails> _staffGet(String token, String id) async =>
      DeliveryDetails.fromJson(
        (await api.get('/api/v1/delivery/$id', accessToken: token))['data']
            as Map<String, dynamic>,
      );
  Future<DeliveryDetails> _action(
    String token,
    String id,
    String action, {
    Map<String, dynamic>? body,
  }) async => DeliveryDetails.fromJson(
    (await api.post(
          '/api/v1/delivery/$id/$action',
          body: body ?? const <String, dynamic>{},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );
  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>? ?? const [])
          .cast<Map<String, dynamic>>();
}
