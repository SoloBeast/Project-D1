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
  Future<List<DeliveryDetails>> getToday(String token, DateTime date) async =>
      _list(
        await api.get(
          '/api/v1/delivery/my-today?date=${formatApiDeliveryDate(date)}',
          accessToken: token,
        ),
      ).map(DeliveryDetails.fromJson).toList(growable: false);
  Future<DeliveryDetails> getStaff(String token, String id) =>
      _staffGet(token, id);
  Future<List<DeliveryDetails>> getBranch(
    String token,
    int branchId, {
    DateTime? date,
    DeliveryStatus? status,
  }) async {
    final query = <String>[];
    if (date != null) query.add('date=${formatApiDeliveryDate(date)}');
    if (status != null) query.add('status=${status.name}');
    final suffix = query.isEmpty ? '' : '?${query.join('&')}';
    return _list(
      await api.get(
        '/api/v1/delivery-management/branches/$branchId$suffix',
        accessToken: token,
      ),
    ).map(DeliveryDetails.fromJson).toList(growable: false);
  }

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
  Future<void> issueOtp(String token, String id) async {
    await api.post('/api/v1/delivery/$id/issue-otp', accessToken: token);
  }

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
