import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_controller.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_models.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('number series list screen', () {
    testWidgets('shows loading state while fetching', (tester) async {
      await _pump(
        tester,
        const NumberSeriesListScreen(),
        _SeededNumberSeriesController(const NumberSeriesState(isLoading: true)),
      );

      expect(find.bySemanticsLabel('Loading number series'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows empty state with create action for managers', (
      tester,
    ) async {
      await _pump(
        tester,
        const NumberSeriesListScreen(),
        _SeededNumberSeriesController(const NumberSeriesState()),
      );

      expect(find.text('No number series'), findsOneWidget);
      expect(find.text('New series'), findsOneWidget);
    });

    testWidgets('hides create action for read-only users', (tester) async {
      await _pump(
        tester,
        const NumberSeriesListScreen(),
        _SeededNumberSeriesController(const NumberSeriesState()),
        permissions: const [kNumberSeriesReadPermission],
      );

      expect(find.text('No number series'), findsOneWidget);
      expect(find.text('New series'), findsNothing);
    });

    testWidgets('shows error state and retries loading', (tester) async {
      final controller = _SeededNumberSeriesController(
        const NumberSeriesState(
          errorMessage: 'Unable to load number series.',
        ),
      );
      await _pump(tester, const NumberSeriesListScreen(), controller);

      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.text('Unable to load number series.'), findsOneWidget);

      await tester.tap(find.text('Retry'));
      await tester.pump();

      expect(controller.loadCount, 2);
    });

    testWidgets('renders series cards and hides actions for read-only users', (
      tester,
    ) async {
      await _pump(
        tester,
        const NumberSeriesListScreen(),
        _SeededNumberSeriesController(
          NumberSeriesState(series: [_series()]),
        ),
        permissions: const [kNumberSeriesReadPermission],
      );

      expect(find.text('CUST'), findsOneWidget);
      expect(find.text('Customer account numbers'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      expect(find.text('Template: CUST/{NUMBER:0000}'), findsOneWidget);
      expect(find.textContaining('Reset: Never'), findsOneWidget);
      expect(find.byTooltip('Configure CUST'), findsNothing);
      expect(find.text('Deactivate'), findsNothing);
    });

    testWidgets('shows manage actions and deactivates an active series', (
      tester,
    ) async {
      final controller = _SeededNumberSeriesController(
        NumberSeriesState(series: [_series()]),
      );
      await _pump(tester, const NumberSeriesListScreen(), controller);

      expect(find.byTooltip('Configure CUST'), findsOneWidget);
      expect(find.text('Deactivate'), findsOneWidget);

      await tester.tap(find.text('Deactivate'));
      await tester.pump();

      expect(controller.lastCode, 'CUST');
      expect(controller.lastIsActive, isFalse);
      expect(controller.lastScope, isNull);
    });

    testWidgets('shows the scope badge and passes scope for scoped series', (
      tester,
    ) async {
      final controller = _SeededNumberSeriesController(
        NumberSeriesState(series: [_scopedSeries()]),
      );
      await _pump(tester, const NumberSeriesListScreen(), controller);

      expect(find.text('ORD'), findsOneWidget);
      expect(find.text('MAIN'), findsOneWidget);
      expect(find.text('Global'), findsNothing);

      await tester.tap(find.text('Deactivate'));
      await tester.pump();

      expect(controller.lastCode, 'ORD');
      expect(controller.lastScope, 'MAIN');
      expect(controller.lastIsActive, isFalse);
    });

    testWidgets('shows Global badge for unscoped series', (tester) async {
      await _pump(
        tester,
        const NumberSeriesListScreen(),
        _SeededNumberSeriesController(
          NumberSeriesState(series: [_series()]),
        ),
      );

      expect(find.text('Global'), findsOneWidget);
    });

    testWidgets('activates an inactive series', (tester) async {
      final controller = _SeededNumberSeriesController(
        NumberSeriesState(series: [_series(isActive: false)]),
      );
      await _pump(tester, const NumberSeriesListScreen(), controller);

      expect(find.text('Inactive'), findsOneWidget);

      await tester.tap(find.text('Activate'));
      await tester.pump();

      expect(controller.lastCode, 'CUST');
      expect(controller.lastIsActive, isTrue);
    });

    testWidgets('shows saved banner after a save', (tester) async {
      await _pump(
        tester,
        const NumberSeriesListScreen(),
        _SeededNumberSeriesController(
          NumberSeriesState(
            series: [_series()],
            savedMessage: 'Series CUST created.',
          ),
        ),
      );

      expect(find.text('Series CUST created.'), findsOneWidget);
    });
  });

  group('number series config screen', () {
    testWidgets('blocks users without manage permission', (tester) async {
      await _pump(
        tester,
        const NumberSeriesConfigScreen(code: '', series: null),
        _SeededNumberSeriesController(const NumberSeriesState()),
        permissions: const [kNumberSeriesReadPermission],
      );

      expect(find.text('Access denied'), findsOneWidget);
      expect(find.text('Save series'), findsNothing);
    });

    testWidgets('prefills a new series form with defaults', (tester) async {
      await _pumpConfig(
        tester,
        const NumberSeriesConfigScreen(code: '', series: null),
        _SeededNumberSeriesController(const NumberSeriesState()),
      );

      expect(find.text('New series'), findsOneWidget);
      expect(find.widgetWithText(TextField, '1'), findsNWidgets(2));
      expect(find.text('Preview template'), findsOneWidget);
      expect(find.text('Save series'), findsOneWidget);
    });

    testWidgets('disables save until valid input is entered', (tester) async {
      final controller = _SeededNumberSeriesController(
        const NumberSeriesState(),
      );
      await _pumpConfig(
        tester,
        const NumberSeriesConfigScreen(code: '', series: null),
        controller,
      );

      await tester.tap(find.text('Save series'));
      await tester.pump();
      expect(controller.createCount, 0);

      await tester.enterText(find.byType(TextField).at(0), 'Customer account numbers');
      await tester.enterText(find.byType(TextField).at(1), 'CUST/{NUMBER:0000}');
      await tester.pump();

      await tester.tap(find.text('Save series'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 1);
      expect(find.text('Number series list'), findsOneWidget);
    });

    testWidgets('requires a scope key when the template uses {SCOPE}', (
      tester,
    ) async {
      final controller = _SeededNumberSeriesController(
        const NumberSeriesState(),
      );
      await _pumpConfig(
        tester,
        const NumberSeriesConfigScreen(code: '', series: null),
        controller,
      );

      await tester.enterText(find.byType(TextField).at(0), 'Order numbers');
      await tester.enterText(
        find.byType(TextField).at(1),
        'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
      );
      await tester.pump();

      expect(
        find.text('Enter a scope key — the template uses {SCOPE}.'),
        findsOneWidget,
      );

      await tester.tap(find.text('Save series'));
      await tester.pump();
      expect(controller.createCount, 0);

      await tester.enterText(find.byType(TextField).at(2), 'MAIN');
      await tester.pump();

      await tester.tap(find.text('Save series'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 1);
      expect(controller.lastCreateRequest?.scopeKey, 'MAIN');
    });

    testWidgets('previews the template without consuming', (tester) async {
      final controller = _SeededNumberSeriesController(
        const NumberSeriesState(),
      );
      await _pumpConfig(
        tester,
        const NumberSeriesConfigScreen(code: '', series: null),
        controller,
      );

      await tester.enterText(find.byType(TextField).at(0), 'Customer account numbers');
      await tester.enterText(find.byType(TextField).at(1), 'CUST/{NUMBER:0000}');
      await tester.tap(find.text('Preview template'));
      await tester.pump();

      expect(controller.previewCount, 1);
    });

    testWidgets('prefills an edit form from the selected series', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        NumberSeriesConfigScreen(code: 'CUST', series: _series()),
        _SeededNumberSeriesController(const NumberSeriesState()),
        initialPath: '/admin/setup/number-series/CUST/edit',
      );

      expect(find.text('Configure CUST'), findsOneWidget);
      expect(find.text('Code: CUST'), findsOneWidget);
      expect(
        find.widgetWithText(TextField, 'Customer account numbers'),
        findsOneWidget,
      );
      expect(
        find.widgetWithText(TextField, 'CUST/{NUMBER:0000}'),
        findsOneWidget,
      );
      expect(find.textContaining('Fixed for this series'), findsOneWidget);
    });

    testWidgets('prefills a scoped edit form with a locked scope key', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        NumberSeriesConfigScreen(code: 'ORD', series: _scopedSeries()),
        _SeededNumberSeriesController(const NumberSeriesState()),
        initialPath: '/admin/setup/number-series/ORD/edit',
      );

      expect(find.text('Configure ORD'), findsOneWidget);
      expect(find.widgetWithText(TextField, 'MAIN'), findsOneWidget);
      expect(find.textContaining('Fixed for this series'), findsOneWidget);
    });

    testWidgets('updates an existing series and pops back', (tester) async {
      final controller = _SeededNumberSeriesController(
        const NumberSeriesState(),
      );
      await _pumpConfig(
        tester,
        NumberSeriesConfigScreen(code: 'CUST', series: _series()),
        controller,
        initialPath: '/admin/setup/number-series/CUST/edit',
      );

      await tester.tap(find.text('Save series'));
      await tester.pumpAndSettle();

      expect(controller.updateCount, 1);
      expect(controller.lastCode, 'CUST');
      expect(find.text('Number series list'), findsOneWidget);
    });
  });
}

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededNumberSeriesController controller, {
  List<String> permissions = const [
    kNumberSeriesReadPermission,
    kNumberSeriesManagePermission,
  ],
  List<int> branchIds = const [7],
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        numberSeriesControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(
            permissions: permissions,
            branchIds: branchIds,
          ),
        ),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pump();
  await tester.pump();
}

Future<void> _pumpConfig(
  WidgetTester tester,
  NumberSeriesConfigScreen screen,
  _SeededNumberSeriesController controller, {
  List<String> permissions = const [
    kNumberSeriesReadPermission,
    kNumberSeriesManagePermission,
  ],
  List<int> branchIds = const [7],
  String initialPath = '/admin/setup/number-series/new',
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final router = GoRouter(
    initialLocation: '/admin/setup/number-series',
    routes: [
      GoRoute(
        path: '/admin/setup/number-series',
        builder: (context, state) => const Scaffold(
          body: Center(child: Text('Number series list')),
        ),
      ),
      GoRoute(
        path: '/admin/setup/number-series/new',
        builder: (context, state) => screen,
      ),
      GoRoute(
        path: '/admin/setup/number-series/:code/edit',
        builder: (context, state) => screen,
      ),
    ],
  );
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        numberSeriesControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(
            permissions: permissions,
            branchIds: branchIds,
          ),
        ),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  router.push(initialPath);
  await tester.pumpAndSettle();
}

class _SeededNumberSeriesController extends NumberSeriesController {
  _SeededNumberSeriesController(this.initialState);

  final NumberSeriesState initialState;
  int loadCount = 0;
  int previewCount = 0;
  int createCount = 0;
  int updateCount = 0;
  int setActiveCount = 0;
  String? lastCode;
  String? lastScope;
  bool? lastIsActive;
  CreateNumberSeriesRequest? lastCreateRequest;

  @override
  NumberSeriesState build() => initialState;

  @override
  Future<void> load() async {
    loadCount++;
  }

  @override
  Future<void> previewTemplate(
    String code,
    String template, {
    int? nextNumber,
    String? scope,
  }) async {
    previewCount++;
  }

  @override
  Future<NumberSeries?> create(CreateNumberSeriesRequest request) async {
    createCount++;
    lastCreateRequest = request;
    return _series(code: request.code, scope: request.scopeKey);
  }

  @override
  Future<NumberSeries?> update(
    String code,
    UpdateNumberSeriesRequest request, {
    String? scope,
  }) async {
    updateCount++;
    lastCode = code;
    lastScope = scope;
    return _series(code: code, scope: scope);
  }

  @override
  Future<NumberSeries?> setActive(
    String code,
    bool isActive, {
    String? scope,
  }) async {
    setActiveCount++;
    lastCode = code;
    lastScope = scope;
    lastIsActive = isActive;
    return _series(code: code, isActive: isActive, scope: scope);
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({
    required this.permissions,
    required this.branchIds,
  });

  final List<String> permissions;
  final List<int> branchIds;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'number-series-user-1',
        displayName: 'Setup Manager',
        email: null,
        mobile: '9999999999',
        roles: const ['OWNER'],
        permissions: permissions,
        branchIds: branchIds,
      ),
      accessToken: 'number-series-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

NumberSeries _series({
  String code = 'CUST',
  bool isActive = true,
  String? scope,
}) => NumberSeries(
  code: code,
  description: scope == null
      ? 'Customer account numbers'
      : 'Main branch order numbers',
  template: scope == null
      ? 'CUST/{NUMBER:0000}'
      : 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
  startingNumber: 1,
  lastUsedNumber: 1000,
  incrementBy: 1,
  resetPolicy: scope == null
      ? NumberSeriesResetPolicy.never
      : NumberSeriesResetPolicy.financialYear,
  isActive: isActive,
  scopeKey: scope,
  nextNumber: 'CUST/1001',
);

NumberSeries _scopedSeries({bool isActive = true}) => _series(
  code: 'ORD',
  isActive: isActive,
  scope: 'MAIN',
);
