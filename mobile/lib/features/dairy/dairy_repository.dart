import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'dairy_models.dart';

class DairyRepository {
  DairyRepository({required this.api});

  final ApiClient api;

  Future<DairyDashboard> getDashboard(
    String token,
    int branchId, {
    DateTime? productionDate,
  }) async => DairyDashboard.fromJson(
    (await api.get(
          '/api/v1/dairy/branches/$branchId/dashboard${_dateQuery(productionDate)}',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<MilkProduction> recordProduction(
    String token,
    int branchId,
    RecordMilkProductionRequest request,
  ) async => MilkProduction.fromJson(
    (await api.post(
          '/api/v1/dairy/branches/$branchId/production',
          body: request.toJson(),
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<List<MilkProduction>> getProductionHistory(
    String token,
    int branchId, {
    DateTime? fromDate,
    DateTime? toDate,
  }) async => _list(
    await api.get(
      '/api/v1/dairy/branches/$branchId/production'
      '${_rangeQuery(fromDate: fromDate, toDate: toDate)}',
      accessToken: token,
    ),
  ).map(MilkProduction.fromJson).toList(growable: false);

  Future<List<MilkBatch>> getBatches(
    String token,
    int branchId, {
    MilkBatchStatus? status,
  }) async => _list(
    await api.get(
      '/api/v1/dairy/branches/$branchId/batches'
      '${status == null ? '' : '?status=${status.apiValue.toLowerCase()}'}',
      accessToken: token,
    ),
  ).map(MilkBatch.fromJson).toList(growable: false);

  Future<MilkBatch> getBatch(String token, String batchId) async =>
      MilkBatch.fromJson(
        (await api.get(
              '/api/v1/dairy/batches/$batchId',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<MilkAvailability> getAvailability(String token, int branchId) async =>
      MilkAvailability.fromJson(
        (await api.get(
              '/api/v1/dairy/branches/$branchId/availability',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<MilkUsage> recordUsage(
    String token,
    String batchId,
    RecordMilkUsageRequest request,
  ) async => MilkUsage.fromJson(
    (await api.post(
          '/api/v1/dairy/batches/$batchId/usage',
          body: request.toJson(),
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<List<MilkUsage>> getUsageHistory(
    String token,
    int branchId, {
    DateTime? fromDate,
    DateTime? toDate,
  }) async => _list(
    await api.get(
      '/api/v1/dairy/branches/$branchId/usage'
      '${_rangeQuery(fromDate: fromDate, toDate: toDate)}',
      accessToken: token,
    ),
  ).map(MilkUsage.fromJson).toList(growable: false);

  String _dateQuery(DateTime? value) =>
      value == null ? '' : '?productionDate=${formatApiDairyDate(value)}';

  String _rangeQuery({DateTime? fromDate, DateTime? toDate}) {
    final query = <String>[];
    if (fromDate != null) query.add('fromDate=${formatApiDairyDate(fromDate)}');
    if (toDate != null) query.add('toDate=${formatApiDairyDate(toDate)}');
    return query.isEmpty ? '' : '?${query.join('&')}';
  }

  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>? ?? const [])
          .cast<Map<String, dynamic>>();
}
