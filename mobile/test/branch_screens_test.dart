import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/branches/branch_controller.dart';
import 'package:doodh_direct_mobile/features/branches/branch_models.dart';
import 'package:doodh_direct_mobile/features/branches/branch_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('branch list screen', () {
    testWidgets('shows loading state while fetching', (tester) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(const BranchState(isLoading: true)),
      );

      expect(find.bySemanticsLabel('Loading branches...'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows empty state with create action for managers', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(const BranchState()),
      );

      expect(find.text('No branches yet'), findsOneWidget);
      expect(find.text('Add branch'), findsOneWidget);
      expect(find.byTooltip('Add branch'), findsOneWidget);
    });

    testWidgets('hides create action without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(const BranchState()),
        permissions: const [kBranchesReadPermission],
      );

      expect(find.text('No branches yet'), findsOneWidget);
      expect(find.text('Add branch'), findsNothing);
      expect(find.byTooltip('Add branch'), findsNothing);
    });

    testWidgets('shows error state with retry', (tester) async {
      final controller = _SeededBranchController(
        const BranchState(errorMessage: 'Branch lookup failed.'),
      );
      await _pumpConfig(tester, const BranchListScreen(), controller);

      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.text('Branch lookup failed.'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);

      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();

      // initState schedules one load() via Future.microtask and Retry fires a
      // second one.
      expect(controller.loadCount, 2);
    });

    testWidgets('renders branch cards sorted by name with number and code', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(
          BranchState(
            branches: [
              _branch(
                publicId: 'b-zebra',
                name: 'Zebra Branch',
                code: 'ZEB',
                branchNumber: 'BR-0002',
              ),
              _branch(
                publicId: 'b-main',
                name: 'Main Branch',
                code: 'MAIN',
                branchNumber: 'BR-0001',
                isActive: false,
              ),
            ],
          ),
        ),
      );

      expect(find.text('Main Branch'), findsOneWidget);
      expect(find.text('Zebra Branch'), findsOneWidget);
      expect(find.text('No. BR-0001 · Code MAIN · Mumbai'), findsOneWidget);
      expect(find.text('No. BR-0002 · Code ZEB · Mumbai'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      expect(find.text('Inactive'), findsOneWidget);

      // Sorted alphabetically by name: Main Branch appears above Zebra Branch.
      final mainY = tester.getTopLeft(find.text('Main Branch')).dy;
      final zebraY = tester.getTopLeft(find.text('Zebra Branch')).dy;
      expect(mainY, lessThan(zebraY));
    });
  });

  group('branch form screen', () {
    testWidgets('blocks access without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        const BranchFormScreen(),
        _SeededBranchController(const BranchState()),
        permissions: const [kBranchesReadPermission],
        routePath: '/admin/branches/new',
      );

      expect(find.text('Add branch'), findsNothing);
      expect(find.text('Access denied'), findsOneWidget);
    });

    testWidgets('validates required fields before saving', (tester) async {
      final controller = _SeededBranchController(const BranchState());
      await _pumpConfig(
        tester,
        const BranchFormScreen(),
        controller,
        routePath: '/admin/branches/new',
      );

      await tester.tap(find.text('Create branch'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 0);
      expect(find.text('Enter a branch code.'), findsOneWidget);
      expect(find.text('Enter a branch name.'), findsOneWidget);
      expect(find.text('Enter a city.'), findsOneWidget);
      expect(find.text('Enter a state.'), findsOneWidget);
      expect(find.text('Required'), findsNWidgets(2));
    });

    testWidgets('creates a branch and pops back to the list', (tester) async {
      final controller = _SeededBranchController(const BranchState());
      await _pumpConfig(
        tester,
        const BranchFormScreen(),
        controller,
        routePath: '/admin/branches/new',
      );

      await _enterField(tester, 'Branch code *', 'MAIN');
      await _enterField(tester, 'Branch name *', 'Main Branch');
      await _enterField(tester, 'City *', 'Mumbai');
      await _enterField(tester, 'State *', 'MH');
      await _enterField(tester, 'Latitude *', '19.07');
      await _enterField(tester, 'Longitude *', '72.87');
      await _enterField(tester, 'Service radius (km)', '5');

      await tester.tap(find.text('Create branch'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 1);
      expect(controller.lastCreateRequest?.code, 'MAIN');
      expect(controller.lastCreateRequest?.name, 'Main Branch');
      expect(controller.lastCreateRequest?.city, 'Mumbai');
      expect(controller.lastCreateRequest?.state, 'MH');
      expect(controller.lastCreateRequest?.latitude, 19.07);
      expect(controller.lastCreateRequest?.longitude, 72.87);
      expect(controller.lastCreateRequest?.serviceRadiusKm, 5);
      // Branch number is never part of the client request.
      expect(controller.lastCreateRequest?.toJson().containsKey('branchNumber'), isFalse);
      expect(find.text('Branch saved'), findsOneWidget);
      // Popped back to the list placeholder.
      expect(find.text('Placeholder'), findsOneWidget);
    });

    testWidgets('shows read-only branch number when editing', (tester) async {
      final branch = _branch(branchNumber: 'BR-0007');
      final controller = _SeededBranchController(BranchState(branches: [branch]));
      await _pumpConfig(
        tester,
        BranchFormScreen(branch: branch),
        controller,
        routePath: '/admin/branches/:id/edit',
        initialPath: '/admin/branches/b-main/edit',
      );

      expect(find.text('Edit branch'), findsOneWidget);
      expect(find.text('Branch number'), findsOneWidget);
      expect(
        find.text(
          'BR-0007 — allocated from the BRANCH numbering series. It is '
          'assigned by the system and cannot be edited.',
        ),
        findsOneWidget,
      );
      // No branch-number input exists anywhere in the form.
      expect(find.text('Branch code *'), findsOneWidget);

      await tester.tap(find.text('Save changes'));
      await tester.pumpAndSettle();

      expect(controller.updateCount, 1);
      expect(controller.lastUpdateRequest?.code, 'MAIN');
      expect(find.text('Branch saved'), findsOneWidget);
    });
  });

  group('branch detail screen', () {
    testWidgets('shows loading state while fetching by id', (tester) async {
      await _pumpConfig(
        tester,
        const BranchDetailScreen(branchId: 'b-missing'),
        _SeededBranchController(const BranchState(isLoading: true)),
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-missing',
        settle: false,
      );

      expect(find.bySemanticsLabel('Loading branch...'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('renders branch details with system-allocated number', (
      tester,
    ) async {
      final branch = _branch(branchNumber: 'BR-0001');
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        _SeededBranchController(BranchState(branches: [branch])),
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      expect(find.text('Branch details'), findsOneWidget);
      expect(find.text('Main Branch'), findsOneWidget);
      expect(find.text('Code MAIN'), findsOneWidget);
      expect(find.text('Branch number'), findsOneWidget);
      expect(find.text('BR-0001'), findsOneWidget);
      expect(
        find.text(
          'Allocated from the BRANCH numbering series by the system. '
          'It cannot be edited or generated on the client.',
        ),
        findsOneWidget,
      );
      expect(
        find.text('12 Main Road, Andheri, Mumbai, MH, 400001'),
        findsOneWidget,
      );
      expect(find.byTooltip('Edit branch'), findsOneWidget);
      expect(find.text('Deactivate branch'), findsOneWidget);
    });

    testWidgets('deactivates a branch after confirmation', (tester) async {
      final branch = _branch(branchNumber: 'BR-0001');
      final controller = _SeededBranchController(
        BranchState(branches: [branch]),
      );
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        controller,
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      await tester.tap(find.text('Deactivate branch'));
      await tester.pumpAndSettle();

      expect(find.text('Deactivate branch?'), findsOneWidget);

      await tester.tap(find.widgetWithText(FilledButton, 'Deactivate'));
      await tester.pumpAndSettle();

      expect(controller.setActiveCount, 1);
      expect(controller.lastSetActiveBranchId, 'b-main');
      expect(controller.lastSetActiveValue, isFalse);
      expect(find.text('Branch deactivated'), findsOneWidget);
      // The branch is now inactive, so the button flips to Activate.
      expect(find.text('Activate branch'), findsOneWidget);
    });

    testWidgets('activates a branch after confirmation', (tester) async {
      final branch = _branch(branchNumber: 'BR-0001', isActive: false);
      final controller = _SeededBranchController(
        BranchState(branches: [branch]),
      );
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        controller,
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      expect(find.text('Activate branch'), findsOneWidget);

      await tester.tap(find.text('Activate branch'));
      await tester.pumpAndSettle();

      expect(find.text('Activate branch?'), findsOneWidget);

      await tester.tap(find.widgetWithText(FilledButton, 'Activate'));
      await tester.pumpAndSettle();

      expect(controller.setActiveCount, 1);
      expect(controller.lastSetActiveBranchId, 'b-main');
      expect(controller.lastSetActiveValue, isTrue);
      expect(find.text('Branch activated'), findsOneWidget);
      expect(find.text('Deactivate branch'), findsOneWidget);
    });

    testWidgets('hides manage actions without manage permission', (tester) async {
      final branch = _branch(branchNumber: 'BR-0001');
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        _SeededBranchController(BranchState(branches: [branch])),
        permissions: const [kBranchesReadPermission],
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      expect(find.text('Main Branch'), findsOneWidget);
      expect(find.text('BR-0001'), findsOneWidget);
      expect(find.byTooltip('Edit branch'), findsNothing);
      expect(find.text('Deactivate branch'), findsNothing);
    });
  });
}

/// Pumps a branch screen inside a GoRouter shell ([routePath] renders [screen];
/// every other route renders a placeholder). The session carries the supplied
/// permissions.
Future<void> _pumpConfig(
  WidgetTester tester,
  Widget screen,
  _SeededBranchController controller, {
  List<String> permissions = const [
    kBranchesReadPermission,
    kBranchesManagePermission,
  ],
  String routePath = '/admin/branches',
  String? initialPath,
  bool settle = true,
}) async {
  await tester.binding.setSurfaceSize(const Size(900, 1400));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  Widget placeholder(BuildContext context, GoRouterState state) =>
      const Scaffold(body: Center(child: Text('Placeholder')));

  final router = GoRouter(
    initialLocation: '/admin/branches',
    routes: [
      GoRoute(
        path: '/admin/branches',
        builder: routePath == '/admin/branches'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/branches/new',
        builder: routePath == '/admin/branches/new'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/branches/:id',
        builder: routePath == '/admin/branches/:id'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/branches/:id/edit',
        builder: routePath == '/admin/branches/:id/edit'
            ? (context, state) => screen
            : placeholder,
      ),
    ],
  );

  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        branchControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(permissions: permissions),
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

  final target = initialPath ?? routePath;
  if (target != '/admin/branches') {
    router.push(target);
    if (settle) {
      await tester.pumpAndSettle();
    } else {
      // A perpetual animation (e.g. a loading spinner) would make
      // pumpAndSettle hang, so pump the route transition manually instead.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
    }
  }
}

/// Enters text into a [TextFormField] identified by its label text.
Future<void> _enterField(WidgetTester tester, String label, String value) async {
  await tester.enterText(find.widgetWithText(TextFormField, label), value);
}

class _SeededBranchController extends BranchController {
  _SeededBranchController(this.initialState);

  final BranchState initialState;

  int loadCount = 0;
  int loadByIdCount = 0;
  int createCount = 0;
  int updateCount = 0;
  int setActiveCount = 0;

  UpsertBranchRequest? lastCreateRequest;
  UpsertBranchRequest? lastUpdateRequest;
  String? lastSetActiveBranchId;
  bool? lastSetActiveValue;

  @override
  BranchState build() => initialState;

  @override
  Future<void> load() async {
    loadCount++;
  }

  @override
  Future<void> loadById(String branchId) async {
    loadByIdCount++;
  }

  @override
  Future<bool> create(UpsertBranchRequest request) async {
    createCount++;
    lastCreateRequest = request;
    state = state.copyWith(savedMessage: 'Branch saved');
    return true;
  }

  @override
  Future<bool> update(String branchId, UpsertBranchRequest request) async {
    updateCount++;
    lastUpdateRequest = request;
    state = state.copyWith(savedMessage: 'Branch saved');
    return true;
  }

  @override
  Future<bool> setActive(String branchId, bool isActive) async {
    setActiveCount++;
    lastSetActiveBranchId = branchId;
    lastSetActiveValue = isActive;
    final updated = _branch(
      publicId: branchId,
      isActive: isActive,
      branchNumber: 'BR-0001',
    );
    state = state.copyWith(
      branches: [updated],
      selectedBranch: updated,
      savedMessage: isActive ? 'Branch activated' : 'Branch deactivated',
    );
    return true;
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({required this.permissions});

  final List<String> permissions;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'branch-admin-user',
        displayName: 'Branch Administrator',
        email: null,
        mobile: '9999999999',
        roles: const ['OWNER'],
        permissions: permissions,
        branchIds: const [7],
      ),
      accessToken: 'branch-admin-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

Branch _branch({
  String publicId = 'b-main',
  String code = 'MAIN',
  String name = 'Main Branch',
  String city = 'Mumbai',
  String state = 'MH',
  double latitude = 19.07,
  double longitude = 72.87,
  double? serviceRadiusKm = 5,
  bool isActive = true,
  String? branchNumber = 'BR-0001',
}) => Branch(
  publicId: publicId,
  code: code,
  name: name,
  addressLine1: '12 Main Road',
  addressLine2: null,
  locality: 'Andheri',
  city: city,
  state: state,
  pinCode: '400001',
  latitude: latitude,
  longitude: longitude,
  serviceRadiusKm: serviceRadiusKm,
  isActive: isActive,
  branchNumber: branchNumber,
  createdAt: DateTime(2026, 8, 1, 10),
  updatedAt: DateTime(2026, 8, 2, 10),
);
