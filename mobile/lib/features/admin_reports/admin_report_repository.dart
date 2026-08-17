import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';

import 'admin_report_models.dart';

class ReportExportFile {
  const ReportExportFile({
    required this.bytes,
    required this.fileName,
    required this.contentType,
  });

  final Uint8List bytes;
  final String fileName;
  final String contentType;
}

class AdminReportRepository {
  AdminReportRepository({ApiClient? api})
    : _api = api ?? ApiClient(baseUrl: apiBaseUrl);

  final ApiClient _api;

  Future<DashboardMetrics> getDashboard(String accessToken) async {
    final response = await _api.get(
      '/api/v1/admin/reports/dashboard',
      accessToken: accessToken,
    );
    return DashboardMetrics.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<ReportPageData> getReport(
    String accessToken,
    ReportModuleDescriptor module,
    ReportFilter filter,
  ) async {
    final response = await _api.get(
      _path(module.slug, filter.toQuery()),
      accessToken: accessToken,
    );
    return ReportPageData.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<ReportExportFile> export(
    String accessToken,
    ReportModuleDescriptor module,
    ReportFilter filter,
    String format,
  ) async {
    final normalizedFormat = format.toLowerCase();
    final response = await _api.postBytes(
      '/api/v1/admin/reports/${module.slug}/export',
      accessToken: accessToken,
      body: {'filter': filter.toJson(), 'format': format},
    );
    return ReportExportFile(
      bytes: response.bytes,
      fileName: response.fileName ?? '${module.slug}.$normalizedFormat',
      contentType: response.contentType,
    );
  }

  String _path(String slug, Map<String, dynamic> query) {
    final uri = Uri(
      path: '/api/v1/admin/reports/$slug',
      queryParameters: query,
    );
    return uri.toString();
  }
}
