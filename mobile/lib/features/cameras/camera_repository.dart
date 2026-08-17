import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'camera_models.dart';

class CameraRepository {
  CameraRepository({required this.api});

  final ApiClient api;

  Future<List<PublicCamera>> getPublic(String token) async =>
      _list(await api.get('/api/v1/cameras/public', accessToken: token))
          .map(PublicCamera.fromJson)
          .toList(growable: false);

  Future<PublicCameraStream> getPublicStream(
    String token,
    String cameraId,
  ) async => PublicCameraStream.fromJson(
    (await api.get(
          '/api/v1/cameras/public/$cameraId/stream',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<List<ManagedCamera>> getManaged(
    String token, {
    int? branchId,
  }) async {
    final query = branchId == null ? '' : '?branchId=$branchId';
    return _list(
          await api.get('/api/v1/admin/cameras$query', accessToken: token),
        )
        .map(ManagedCamera.fromJson)
        .toList(growable: false);
  }

  Future<ManagedCamera> create(
    String token,
    SaveCameraRequest request,
  ) async => ManagedCamera.fromJson(
    (await api.post(
          '/api/v1/admin/cameras',
          body: request.toCreateJson(),
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<ManagedCamera> update(
    String token,
    String cameraId,
    SaveCameraRequest request,
  ) async => ManagedCamera.fromJson(
    (await api.patch(
          '/api/v1/admin/cameras/$cameraId',
          body: request.toUpdateJson(),
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>? ?? const [])
          .cast<Map<String, dynamic>>();
}
