import 'dart:typed_data';

import 'package:doodh_direct_mobile/features/admin_reports/admin_report_controller.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_models.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_repository.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_screens.dart';
import 'package:doodh_direct_mobile/features/admin_reports/report_export_saver.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('admin dashboard screen', () {
    testWidgets('shows only permitted modules and loaded dashboard metrics', (
      tester,
    ) async {
      await _pump(
        tester,
        const AdminDashboardScreen(),
        _SeededReportController(const AdminReportState(dashboard: _dashboard)),
        permissions: const ['REPORTS.ADMINISTRATION.READ'],
      );

      expect(find.text('Administration'), findsOneWidget);
      expect(find.text('Overview'), findsOneWidget);
      expect(find.text('Customers'), findsWidgets);
      expect(find.text('Employees'), findsWidgets);
      expect(find.text('Orders'), findsWidgets);
      expect(find.text('Subscriptions'), findsWidgets);
      expect(find.text('Payments'), findsNothing);
      expect(find.text('42'), findsOneWidget);
      expect(find.text('INR 1250.50'), findsOneWidget);
    });

    testWidgets('shows access denied when no report module is permitted', (
      tester,
    ) async {
      await _pump(
        tester,
        const AdminDashboardScreen(),
        _SeededReportController(const AdminReportState()),
        permissions: const [],
      );

      expect(find.text('Access denied'), findsOneWidget);
      expect(
        find.text(
          'Your account does not have permission to view this content.',
        ),
        findsOneWidget,
      );
    });

    testWidgets('shows dashboard loading, offline, and error states', (
      tester,
    ) async {
      await _pump(
        tester,
        const AdminDashboardScreen(),
        _SeededReportController(
          const AdminReportState(isDashboardLoading: true),
        ),
      );
      expect(
        find.bySemanticsLabel('Loading administration data'),
        findsOneWidget,
      );

      await _pump(
        tester,
        const AdminDashboardScreen(),
        _SeededReportController(const AdminReportState(isOffline: true)),
      );
      expect(find.text('You are offline'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);

      await _pump(
        tester,
        const AdminDashboardScreen(),
        _SeededReportController(
          const AdminReportState(errorMessage: 'Dashboard failed'),
        ),
      );
      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.text('Dashboard failed'), findsOneWidget);
    });
  });

  group('admin report screen', () {
    testWidgets('shows direct unauthorized report route and hides export', (
      tester,
    ) async {
      final controller = _SeededReportController(
        AdminReportState(module: reportModules.first),
      );
      await _pump(
        tester,
        const AdminReportScreen(moduleSlug: 'customers'),
        controller,
        permissions: const [],
      );

      expect(find.text('Customers report'), findsOneWidget);
      expect(find.text('Access denied'), findsOneWidget);
      expect(find.byTooltip('Export report'), findsNothing);
      expect(controller.loadReportCount, 0);
    });

    testWidgets(
      'shows loading, empty, offline, unauthorized, and error states',
      (tester) async {
        const screen = AdminReportScreen(moduleSlug: 'customers');
        const permissions = ['REPORTS.ADMINISTRATION.READ'];

        await _pump(
          tester,
          screen,
          _SeededReportController(
            AdminReportState(
              module: reportModules.first,
              isReportLoading: true,
            ),
          ),
          permissions: permissions,
        );
        expect(find.bySemanticsLabel('Loading report'), findsOneWidget);

        await _pump(
          tester,
          screen,
          _SeededReportController(
            AdminReportState(module: reportModules.first, report: _emptyPage),
          ),
          permissions: permissions,
        );
        expect(find.text('No results'), findsOneWidget);

        await _pump(
          tester,
          screen,
          _SeededReportController(
            AdminReportState(module: reportModules.first, isOffline: true),
          ),
          permissions: permissions,
        );
        expect(find.text('You are offline'), findsOneWidget);

        await _pump(
          tester,
          screen,
          _SeededReportController(
            AdminReportState(module: reportModules.first, isUnauthorized: true),
          ),
          permissions: permissions,
        );
        expect(find.text('Access denied'), findsOneWidget);

        await _pump(
          tester,
          screen,
          _SeededReportController(
            AdminReportState(
              module: reportModules.first,
              errorMessage: 'Report failed',
            ),
          ),
          permissions: permissions,
        );
        expect(find.text('Something went wrong'), findsOneWidget);
        expect(find.text('Report failed'), findsOneWidget);
      },
    );

    testWidgets('renders a desktop report as a DataTable', (tester) async {
      await _pump(
        tester,
        const AdminReportScreen(moduleSlug: 'orders'),
        _SeededReportController(
          AdminReportState(module: reportModules[2], report: _ordersPage),
        ),
        permissions: const ['REPORTS.ADMINISTRATION.READ'],
        size: Size(800, 900),
      );

      expect(find.byType(DataTable), findsOneWidget);
      expect(find.text('Order'), findsOneWidget);
      expect(find.text('ORD-1001'), findsOneWidget);
      expect(find.text('Customer'), findsOneWidget);
    });

    testWidgets('renders a narrow report as labeled mobile rows', (
      tester,
    ) async {
      await _pump(
        tester,
        const AdminReportScreen(moduleSlug: 'orders'),
        _SeededReportController(
          AdminReportState(module: reportModules[2], report: _ordersPage),
        ),
        permissions: const ['REPORTS.ADMINISTRATION.READ'],
        size: Size(600, 900),
      );

      expect(find.byType(DataTable), findsNothing);
      expect(find.text('Order'), findsOneWidget);
      expect(find.text('ORD-1001'), findsOneWidget);
      expect(find.text('Status'), findsOneWidget);
    });

    testWidgets('exposes export only with export permission', (tester) async {
      final controller = _SeededReportController(
        AdminReportState(module: reportModules.first, report: _customerPage),
      );
      await _pump(
        tester,
        const AdminReportScreen(moduleSlug: 'customers'),
        controller,
        permissions: const ['REPORTS.ADMINISTRATION.READ', 'REPORTS.EXPORT'],
      );

      expect(find.byTooltip('Export report'), findsOneWidget);
      await tester.tap(find.byTooltip('Export report'));
      await tester.pumpAndSettle();
      expect(find.text('Export CSV'), findsOneWidget);
      expect(find.text('Export Excel'), findsOneWidget);
    });

    testWidgets('saves an exported report and clears its in-memory payload', (
      tester,
    ) async {
      final file = ReportExportFile(
        bytes: Uint8List.fromList([1, 2, 3]),
        fileName: 'customers.csv',
        contentType: 'text/csv',
      );
      final controller = _SeededReportController(
        AdminReportState(module: reportModules.first, report: _customerPage),
        exportFile: file,
      );
      final saver = _FakeReportExportSaver(destination: 'Downloads');
      await _pump(
        tester,
        const AdminReportScreen(moduleSlug: 'customers'),
        controller,
        saver: saver,
      );

      await tester.tap(find.byTooltip('Export report'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Export CSV'));
      await tester.pumpAndSettle();

      expect(controller.lastExportFormat, 'Csv');
      expect(saver.savedFile, same(file));
      expect(find.text('customers.csv saved to Downloads.'), findsOneWidget);
      expect(controller.state.exportFile, isNull);
    });

    testWidgets('reports a platform save failure and clears export payload', (
      tester,
    ) async {
      final file = ReportExportFile(
        bytes: Uint8List.fromList([4, 5, 6]),
        fileName: 'customers.xlsx',
        contentType:
            'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      );
      final controller = _SeededReportController(
        AdminReportState(module: reportModules.first, report: _customerPage),
        exportFile: file,
      );
      final saver = _FakeReportExportSaver(error: StateError('Save failed'));
      await _pump(
        tester,
        const AdminReportScreen(moduleSlug: 'customers'),
        controller,
        saver: saver,
      );

      await tester.tap(find.byTooltip('Export report'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Export Excel'));
      await tester.pumpAndSettle();

      expect(controller.lastExportFormat, 'Xlsx');
      expect(saver.savedFile, same(file));
      expect(
        find.text('The report was generated but could not be saved.'),
        findsOneWidget,
      );
      expect(controller.state.exportFile, isNull);
    });

    testWidgets('submits status filters and invokes pagination callbacks', (
      tester,
    ) async {
      final controller = _SeededReportController(
        AdminReportState(
          module: reportModules[2],
          report: _ordersPage,
          filter: const ReportFilter(page: 2),
        ),
      );
      await _pump(
        tester,
        const AdminReportScreen(moduleSlug: 'orders'),
        controller,
        permissions: const ['REPORTS.ADMINISTRATION.READ'],
      );

      final statusField = find.widgetWithText(TextField, 'Statuses');
      await tester.enterText(statusField, 'Pending, Failed');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pump();
      expect(controller.lastFilter?.statuses, ['Pending', 'Failed']);
      expect(controller.lastFilter?.page, 1);

      await tester.tap(find.byTooltip('Next page'));
      await tester.pump();
      expect(controller.nextPageCount, 1);

      await tester.tap(find.byTooltip('Previous page'));
      await tester.pump();
      expect(controller.previousPageCount, 1);
    });
  });
}

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededReportController controller, {
  List<String> permissions = const [
    'REPORTS.ADMINISTRATION.READ',
    'REPORTS.EXPORT',
  ],
  Size size = const Size(800, 1200),
  ReportExportSaver? saver,
}) async {
  await tester.binding.setSurfaceSize(size);
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        adminReportControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(permissions),
        ),
        if (saver != null) reportExportSaverProvider.overrideWithValue(saver),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pump();
  await tester.pump();
}

class _SeededReportController extends AdminReportController {
  _SeededReportController(this.initialState, {this.exportFile});

  final AdminReportState initialState;
  final ReportExportFile? exportFile;
  int loadDashboardCount = 0;
  int loadReportCount = 0;
  int nextPageCount = 0;
  int previousPageCount = 0;
  ReportFilter? lastFilter;
  String? lastExportFormat;

  @override
  AdminReportState build() => initialState;

  @override
  Future<void> loadDashboard() async {
    loadDashboardCount++;
  }

  @override
  Future<void> loadReport(
    ReportModuleDescriptor module, {
    ReportFilter? filter,
  }) async {
    loadReportCount++;
  }

  @override
  Future<void> updateFilter(ReportFilter filter) async {
    lastFilter = filter.copyWith(page: 1);
  }

  @override
  Future<ReportExportFile?> export(String format) async {
    lastExportFormat = format;
    final file = exportFile;
    if (file != null) {
      state = state.copyWith(exportFile: file);
    }
    return file;
  }

  @override
  Future<void> nextPage() async {
    nextPageCount++;
  }

  @override
  Future<void> previousPage() async {
    previousPageCount++;
  }
}

class _FakeReportExportSaver implements ReportExportSaver {
  _FakeReportExportSaver({this.destination = 'Downloads', this.error});

  final String destination;
  final Object? error;
  ReportExportFile? savedFile;

  @override
  Future<String> save(ReportExportFile file) async {
    savedFile = file;
    final failure = error;
    if (failure != null) throw failure;
    return destination;
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.permissions);

  final List<String> permissions;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'report-user-1',
        displayName: 'Report User',
        email: null,
        mobile: null,
        roles: const ['OWNER'],
        permissions: permissions,
        branchIds: const [7],
      ),
      accessToken: 'report-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

const _dashboard = DashboardMetrics(
  customers: 42,
  activeCustomers: 40,
  employees: 8,
  orders: 12,
  oneTimeOrderRevenue: 1250.5,
  activeSubscriptions: 6,
  successfulPayments: 1100,
  pendingPayments: 150.5,
  refunds: 20,
  walletBalances: 500,
  deliveries: 18,
  successfulDeliveries: 16,
  failedDeliveries: 2,
  milkProduced: 300,
  milkUsed: 250,
  pendingMilkTests: 3,
  availableCameras: 4,
  notificationFailures: 1,
);

const _emptyPage = ReportPageData(
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  hasNextPage: false,
);

const _customerPage = ReportPageData(
  items: [
    {
      'id': 'customer-1',
      'displayName': 'Anita Sharma',
      'mobile': '9999999999',
      'isActive': true,
    },
  ],
  page: 1,
  pageSize: 25,
  totalCount: 1,
  hasNextPage: false,
);

const _ordersPage = ReportPageData(
  items: [
    {
      'orderNumber': 'ORD-1001',
      'customerName': 'Anita Sharma',
      'status': 'Pending',
      'payableAmount': 250.0,
    },
  ],
  page: 2,
  pageSize: 25,
  totalCount: 50,
  hasNextPage: true,
);
