import 'dart:async';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_controller.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_models.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_repository.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('admin report controller', () {
    test('loads dashboard and report with authenticated token', () async {
      final repository = _FakeReportRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(adminReportControllerProvider.notifier);

      await controller.loadDashboard();
      await controller.loadReport(reportModuleBySlug('orders')!);

      final state = container.read(adminReportControllerProvider);
      expect(state.dashboard?.customers, 12);
      expect(state.report?.items.single['orderNumber'], 'ORD-1001');
      expect(state.module?.slug, 'orders');
      expect(state.filter.sortBy, 'createdAtUtc');
      expect(state.isDashboardLoading, isFalse);
      expect(state.isReportLoading, isFalse);
      expect(repository.lastToken, 'report-token');
    });

    test(
      'updates filters at page one and paginates within page bounds',
      () async {
        final repository = _FakeReportRepository();
        final container = await _authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(
          adminReportControllerProvider.notifier,
        );
        final module = reportModuleBySlug('orders')!;

        await controller.loadReport(module);
        await controller.updateFilter(
          const ReportFilter(search: 'ORD', page: 5, pageSize: 50),
        );
        expect(repository.filters.last.page, 1);
        expect(repository.filters.last.search, 'ORD');

        await controller.nextPage();
        expect(repository.filters.last.page, 2);
        expect(container.read(adminReportControllerProvider).filter.page, 2);

        await controller.previousPage();
        expect(repository.filters.last.page, 1);
        final callsAtFirstPage = repository.reportCalls;
        await controller.previousPage();
        expect(repository.reportCalls, callsAtFirstPage);
      },
    );

    test('does not request next page when the page is terminal', () async {
      final repository = _FakeReportRepository(hasNextPage: false);
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(adminReportControllerProvider.notifier);

      await controller.loadReport(reportModuleBySlug('orders')!);
      final calls = repository.reportCalls;
      await controller.nextPage();

      expect(repository.reportCalls, calls);
      expect(container.read(adminReportControllerProvider).filter.page, 1);
    });

    test(
      'exports the complete filter from page one and can clear it',
      () async {
        final repository = _FakeReportRepository();
        final container = await _authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(
          adminReportControllerProvider.notifier,
        );
        final module = reportModuleBySlug('payments')!;

        await controller.loadReport(
          module,
          filter: const ReportFilter(
            statuses: ['Succeeded'],
            page: 4,
            sortBy: 'amount',
          ),
        );
        final file = await controller.export('Csv');

        expect(file?.fileName, 'payments.csv');
        expect(repository.exportFilter?.page, 1);
        expect(repository.exportFilter?.statuses, ['Succeeded']);
        expect(repository.exportFormat, 'Csv');
        expect(
          container.read(adminReportControllerProvider).isExporting,
          isFalse,
        );
        expect(
          container.read(adminReportControllerProvider).exportFile,
          same(file),
        );

        controller.clearExport();
        expect(
          container.read(adminReportControllerProvider).exportFile,
          isNull,
        );
      },
    );

    for (final statusCode in [401, 403]) {
      test('maps $statusCode API failures to unauthorized state', () async {
        final repository = _FailingReportRepository(
          ApiException(statusCode, 'REPORT_ACCESS_DENIED', 'Report denied.'),
        );
        final container = await _authenticatedContainer(repository);
        addTearDown(container.dispose);

        await container
            .read(adminReportControllerProvider.notifier)
            .loadReport(reportModuleBySlug('audit')!);
        final state = container.read(adminReportControllerProvider);

        expect(state.isUnauthorized, isTrue);
        expect(state.isOffline, isFalse);
        expect(state.isReportLoading, isFalse);
        expect(state.errorMessage, 'Report denied.');
      });
    }

    test(
      'retains API error messages without classifying them offline',
      () async {
        final container = await _authenticatedContainer(
          _FailingReportRepository(
            const ApiException(422, 'INVALID_FILTER', 'Invalid report filter.'),
          ),
        );
        addTearDown(container.dispose);

        await container
            .read(adminReportControllerProvider.notifier)
            .loadDashboard();
        final state = container.read(adminReportControllerProvider);

        expect(state.isDashboardLoading, isFalse);
        expect(state.isUnauthorized, isFalse);
        expect(state.isOffline, isFalse);
        expect(state.errorMessage, 'Invalid report filter.');
      },
    );

    test('maps transport and export failures to offline state', () async {
      final repository = _FailingReportRepository(Exception('socket closed'));
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(adminReportControllerProvider.notifier);

      await controller.loadReport(reportModuleBySlug('orders')!);
      var state = container.read(adminReportControllerProvider);
      expect(state.isOffline, isTrue);
      expect(state.errorMessage, contains('Check your connection'));

      expect(await controller.export('Csv'), isNull);
      state = container.read(adminReportControllerProvider);
      expect(state.isExporting, isFalse);
      expect(state.isOffline, isTrue);
    });

    test('ignores an older report response after a newer request', () async {
      final repository = _DelayedReportRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(adminReportControllerProvider.notifier);
      final module = reportModuleBySlug('orders')!;

      final older = controller.loadReport(
        module,
        filter: const ReportFilter(search: 'old'),
      );
      final newer = controller.loadReport(
        module,
        filter: const ReportFilter(search: 'new'),
      );
      repository.complete(1, _page(orderNumber: 'ORD-NEW', hasNextPage: false));
      await newer;
      repository.complete(0, _page(orderNumber: 'ORD-OLD', hasNextPage: false));
      await older;

      final state = container.read(adminReportControllerProvider);
      expect(state.filter.search, 'new');
      expect(state.report?.items.single['orderNumber'], 'ORD-NEW');
    });

    test('clears report state when the session signs out', () async {
      final auth = _AuthenticatedRepository();
      final repository = _FakeReportRepository();
      final container = await _authenticatedContainer(
        repository,
        authRepository: auth,
      );
      addTearDown(container.dispose);
      final controller = container.read(adminReportControllerProvider.notifier);

      await controller.loadDashboard();
      await controller.loadReport(reportModuleBySlug('orders')!);
      await container.read(sessionControllerProvider.notifier).signOut();
      await Future<void>.delayed(Duration.zero);

      final state = container.read(adminReportControllerProvider);
      expect(state.dashboard, isNull);
      expect(state.module, isNull);
      expect(state.report, isNull);
      expect(auth.logoutCalls, 1);
    });

    test('does not call repository without an authenticated session', () async {
      final repository = _FakeReportRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(
            _UnauthenticatedRepository(),
          ),
          adminReportRepositoryProvider.overrideWithValue(repository),
        ],
      );
      addTearDown(container.dispose);
      container.read(sessionControllerProvider);
      container.read(adminReportControllerProvider);
      await Future<void>.delayed(Duration.zero);
      final controller = container.read(adminReportControllerProvider.notifier);

      await controller.loadDashboard();
      await controller.loadReport(reportModuleBySlug('orders')!);
      expect(await controller.export('Csv'), isNull);

      expect(repository.dashboardCalls, 0);
      expect(repository.reportCalls, 0);
      expect(repository.exportCalls, 0);
    });
  });
}

Future<ProviderContainer> _authenticatedContainer(
  AdminReportRepository repository, {
  _AuthenticatedRepository? authRepository,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(
        authRepository ?? _AuthenticatedRepository(),
      ),
      adminReportRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  container.read(adminReportControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

class _AuthenticatedRepository extends AuthRepository {
  int logoutCalls = 0;

  @override
  Future<AuthSession?> restore() async => _session;

  @override
  Future<void> logout(AuthSession session) async {
    logoutCalls++;
  }

  @override
  Future<void> clear() async {}
}

class _UnauthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;
}

class _FakeReportRepository extends AdminReportRepository {
  _FakeReportRepository({this.hasNextPage = true})
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final bool hasNextPage;
  String? lastToken;
  final List<ReportFilter> filters = [];
  ReportFilter? exportFilter;
  String? exportFormat;
  int dashboardCalls = 0;
  int reportCalls = 0;
  int exportCalls = 0;

  @override
  Future<DashboardMetrics> getDashboard(String accessToken) async {
    dashboardCalls++;
    lastToken = accessToken;
    return _dashboard;
  }

  @override
  Future<ReportPageData> getReport(
    String accessToken,
    ReportModuleDescriptor module,
    ReportFilter filter,
  ) async {
    reportCalls++;
    lastToken = accessToken;
    filters.add(filter);
    return _page(
      page: filter.page,
      orderNumber: 'ORD-1001',
      hasNextPage: hasNextPage,
    );
  }

  @override
  Future<ReportExportFile> export(
    String accessToken,
    ReportModuleDescriptor module,
    ReportFilter filter,
    String format,
  ) async {
    exportCalls++;
    lastToken = accessToken;
    exportFilter = filter;
    exportFormat = format;
    return ReportExportFile(
      bytes: Uint8List.fromList([1, 2, 3]),
      fileName: '${module.slug}.${format.toLowerCase()}',
      contentType: 'text/csv',
    );
  }
}

class _FailingReportRepository extends AdminReportRepository {
  _FailingReportRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<DashboardMetrics> getDashboard(String accessToken) async =>
      throw failure;

  @override
  Future<ReportPageData> getReport(
    String accessToken,
    ReportModuleDescriptor module,
    ReportFilter filter,
  ) async => throw failure;

  @override
  Future<ReportExportFile> export(
    String accessToken,
    ReportModuleDescriptor module,
    ReportFilter filter,
    String format,
  ) async => throw failure;
}

class _DelayedReportRepository extends AdminReportRepository {
  _DelayedReportRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<Completer<ReportPageData>> _requests = [];

  @override
  Future<ReportPageData> getReport(
    String accessToken,
    ReportModuleDescriptor module,
    ReportFilter filter,
  ) {
    final completer = Completer<ReportPageData>();
    _requests.add(completer);
    return completer.future;
  }

  void complete(int index, ReportPageData page) {
    _requests[index].complete(page);
  }
}

ReportPageData _page({
  int page = 1,
  required String orderNumber,
  required bool hasNextPage,
}) => ReportPageData(
  items: [
    {'orderNumber': orderNumber, 'status': 'Pending'},
  ],
  page: page,
  pageSize: 25,
  totalCount: hasNextPage ? 30 : 1,
  hasNextPage: hasNextPage,
);

const _dashboard = DashboardMetrics(
  customers: 12,
  activeCustomers: 10,
  employees: 4,
  orders: 7,
  oneTimeOrderRevenue: 1450.75,
  activeSubscriptions: 3,
  successfulPayments: 1200,
  pendingPayments: 250.25,
  refunds: 20,
  walletBalances: 420.5,
  deliveries: 8,
  successfulDeliveries: 6,
  failedDeliveries: 2,
  milkProduced: 94.5,
  milkUsed: 88,
  pendingMilkTests: 1,
  availableCameras: 3,
  notificationFailures: 2,
);

final _session = AuthSession(
  user: AuthUser(
    publicUserId: 'owner-1',
    displayName: 'Owner',
    email: 'owner@example.test',
    mobile: null,
    roles: ['OWNER'],
    permissions: [
      'REPORTS.ADMINISTRATION.READ',
      'REPORTS.FINANCIAL.READ',
      'REPORTS.OPERATIONS.READ',
      'REPORTS.MILK_TESTS.READ',
      'REPORTS.AUDIT.READ',
      'REPORTS.EXPORT',
    ],
    branchIds: [7],
  ),
  accessToken: 'report-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
