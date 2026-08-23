import 'dart:async';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'admin_report_models.dart';
import 'admin_report_repository.dart';

final adminReportRepositoryProvider = Provider<AdminReportRepository>(
  (ref) => AdminReportRepository(api: authenticatedApiClient(ref)),
);

final adminReportControllerProvider =
    NotifierProvider<AdminReportController, AdminReportState>(
      AdminReportController.new,
    );

class AdminReportState {
  const AdminReportState({
    this.dashboard,
    this.module,
    this.report,
    this.filter = const ReportFilter(),
    this.isDashboardLoading = false,
    this.isReportLoading = false,
    this.isExporting = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.errorMessage,
    this.exportFile,
  });

  final DashboardMetrics? dashboard;
  final ReportModuleDescriptor? module;
  final ReportPageData? report;
  final ReportFilter filter;
  final bool isDashboardLoading;
  final bool isReportLoading;
  final bool isExporting;
  final bool isOffline;
  final bool isUnauthorized;
  final String? errorMessage;
  final ReportExportFile? exportFile;

  AdminReportState copyWith({
    DashboardMetrics? dashboard,
    bool clearDashboard = false,
    ReportModuleDescriptor? module,
    bool clearModule = false,
    ReportPageData? report,
    bool clearReport = false,
    ReportFilter? filter,
    bool? isDashboardLoading,
    bool? isReportLoading,
    bool? isExporting,
    bool? isOffline,
    bool? isUnauthorized,
    String? errorMessage,
    bool clearError = false,
    ReportExportFile? exportFile,
    bool clearExport = false,
  }) => AdminReportState(
    dashboard: clearDashboard ? null : dashboard ?? this.dashboard,
    module: clearModule ? null : module ?? this.module,
    report: clearReport ? null : report ?? this.report,
    filter: filter ?? this.filter,
    isDashboardLoading: isDashboardLoading ?? this.isDashboardLoading,
    isReportLoading: isReportLoading ?? this.isReportLoading,
    isExporting: isExporting ?? this.isExporting,
    isOffline: isOffline ?? this.isOffline,
    isUnauthorized: isUnauthorized ?? this.isUnauthorized,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    exportFile: clearExport ? null : exportFile ?? this.exportFile,
  );
}

class AdminReportController extends Notifier<AdminReportState> {
  int _reportGeneration = 0;
  String? _activeUserId;

  AdminReportRepository get _repository =>
      ref.read(adminReportRepositoryProvider);

  SessionState get _session => ref.read(sessionControllerProvider);

  String? get _token => _session.session?.accessToken;

  @override
  AdminReportState build() {
    ref.listen<SessionState>(sessionControllerProvider, (previous, next) {
      final userId = next.publicUserId;
      if (!next.isAuthenticated ||
          (_activeUserId != null && _activeUserId != userId)) {
        _reportGeneration++;
        state = const AdminReportState();
      }
      _activeUserId = userId;
    }, fireImmediately: true);
    return const AdminReportState();
  }

  Future<void> loadDashboard() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(
      isDashboardLoading: true,
      isOffline: false,
      isUnauthorized: false,
      clearError: true,
    );
    try {
      final dashboard = await _repository.getDashboard(token);
      if (token != _token) return;
      state = state.copyWith(dashboard: dashboard, isDashboardLoading: false);
    } on Object catch (error) {
      if (token == _token) _setFailure(error, dashboard: true);
    }
  }

  Future<void> loadReport(
    ReportModuleDescriptor module, {
    ReportFilter? filter,
  }) async {
    final token = _token;
    if (token == null) return;

    final nextFilter =
        filter ??
        (state.module?.slug == module.slug
            ? state.filter
            : ReportFilter(sortBy: module.sorts.firstOrNull?.value));
    final generation = ++_reportGeneration;
    state = state.copyWith(
      module: module,
      filter: nextFilter,
      isReportLoading: true,
      isOffline: false,
      isUnauthorized: false,
      clearReport: state.module?.slug != module.slug,
      clearError: true,
      clearExport: true,
    );
    try {
      final report = await _repository.getReport(token, module, nextFilter);
      if (generation != _reportGeneration || token != _token) return;
      state = state.copyWith(report: report, isReportLoading: false);
    } on Object catch (error) {
      if (generation == _reportGeneration && token == _token) {
        _setFailure(error);
      }
    }
  }

  Future<void> updateFilter(ReportFilter filter) async {
    final module = state.module;
    if (module == null) return;
    await loadReport(module, filter: filter.copyWith(page: 1));
  }

  Future<void> nextPage() async {
    final module = state.module;
    if (module == null || state.report?.hasNextPage != true) return;
    await loadReport(
      module,
      filter: state.filter.copyWith(page: state.filter.page + 1),
    );
  }

  Future<void> previousPage() async {
    final module = state.module;
    if (module == null || state.filter.page <= 1) return;
    await loadReport(
      module,
      filter: state.filter.copyWith(page: state.filter.page - 1),
    );
  }

  Future<ReportExportFile?> export(String format) async {
    final token = _token;
    final module = state.module;
    if (token == null || module == null || state.isExporting) return null;

    state = state.copyWith(
      isExporting: true,
      isOffline: false,
      isUnauthorized: false,
      clearError: true,
      clearExport: true,
    );
    try {
      final file = await _repository.export(
        token,
        module,
        state.filter.copyWith(page: 1),
        format,
      );
      if (token != _token) return null;
      state = state.copyWith(exportFile: file, isExporting: false);
      return file;
    } on Object catch (error) {
      if (token == _token) _setFailure(error, exporting: true);
      return null;
    }
  }

  void clearExport() => state = state.copyWith(clearExport: true);

  void _setFailure(
    Object error, {
    bool dashboard = false,
    bool exporting = false,
  }) {
    final isApiError = error is ApiException;
    final statusCode = isApiError ? error.statusCode : null;
    state = state.copyWith(
      isDashboardLoading: dashboard ? false : state.isDashboardLoading,
      isReportLoading: dashboard || exporting ? state.isReportLoading : false,
      isExporting: exporting ? false : state.isExporting,
      isOffline: !isApiError,
      isUnauthorized: statusCode == 401 || statusCode == 403,
      errorMessage: isApiError ? error.message : _offlineMessage,
    );
  }
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
