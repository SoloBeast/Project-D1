import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'branch_models.dart';

class BranchRepository {
  BranchRepository({required this.api});

  final ApiClient api;

  Future<List<Branch>> getBranches(String token) async => _list(
        await api.get('/api/v1/admin/branches', accessToken: token),
      )
      .map(Branch.fromJson)
      .toList(growable: false);

  Future<Branch> getBranch(String token, String branchId) async =>
      Branch.fromJson(
        (await api.get(
              '/api/v1/admin/branches/$branchId',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<Branch> create(
    String token,
    UpsertBranchRequest request,
  ) async => Branch.fromJson(
    (await api.post(
          '/api/v1/admin/branches',
          body: request.toJson(),
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<Branch> update(
    String token,
    String branchId,
    UpsertBranchRequest request,
  ) async => Branch.fromJson(
    (await api.put(
          '/api/v1/admin/branches/$branchId',
          body: request.toJson(),
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<Branch> activate(String token, String branchId) async =>
      Branch.fromJson(
        (await api.post(
              '/api/v1/admin/branches/$branchId/activate',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<Branch> deactivate(String token, String branchId) async =>
      Branch.fromJson(
        (await api.post(
              '/api/v1/admin/branches/$branchId/deactivate',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>? ?? const [])
          .cast<Map<String, dynamic>>();
}
