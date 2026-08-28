import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/employees/employee_controller.dart';
import 'package:doodh_direct_mobile/features/employees/employee_models.dart';
import 'package:doodh_direct_mobile/features/employees/employee_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('employee list screen', () {
    testWidgets('shows loading state while fetching', (tester) async {
      await _pumpConfig(
        tester,
        const EmployeeListScreen(),
        _SeededEmployeeController(const EmployeeState(isLoading: true)),
      );

      expect(find.bySemanticsLabel('Loading employees'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows empty state with create action for managers', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const EmployeeListScreen(),
        _SeededEmployeeController(const EmployeeState()),
      );

      expect(find.text('No employees'), findsOneWidget);
      expect(
        find.text(
          'Create an employee to start onboarding them with a secure '
          'invitation.',
        ),
        findsOneWidget,
      );
      expect(find.text('Create employee'), findsOneWidget);
      expect(find.byTooltip('Create employee'), findsOneWidget);
    });

    testWidgets('hides create action without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        const EmployeeListScreen(),
        _SeededEmployeeController(const EmployeeState()),
        permissions: const [kEmployeesReadPermission],
      );

      expect(find.text('No employees'), findsOneWidget);
      expect(
        find.text('Employees managed for this account are not available.'),
        findsOneWidget,
      );
      expect(find.text('Create employee'), findsNothing);
      expect(find.byTooltip('Create employee'), findsNothing);
    });

    testWidgets('shows error state with retry', (tester) async {
      final controller = _SeededEmployeeController(
        const EmployeeState(errorMessage: 'Employee lookup failed.'),
      );
      await _pumpConfig(tester, const EmployeeListScreen(), controller);

      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.text('Employee lookup failed.'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);

      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();

      // initState schedules one load() via Future.microtask and Retry fires a
      // second one.
      expect(controller.loadCount, 2);
    });

    testWidgets('renders employee card details', (tester) async {
      await _pumpConfig(
        tester,
        const EmployeeListScreen(),
        _SeededEmployeeController(EmployeeState(employees: [_employee()])),
      );

      expect(find.text('Ramesh Kumar'), findsOneWidget);
      expect(find.text('Delivery Boy / Delivery Staff'), findsOneWidget);
      expect(find.text('Branch: Main Branch'), findsOneWidget);
      expect(find.text('Mobile: 9876543210'), findsOneWidget);
      expect(find.text('Email: ramesh@example.test'), findsOneWidget);
      expect(find.text('Invitation sent · expires 01-09-2026'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);

      // Manage actions visible when the account has EMPLOYEES.MANAGE.
      expect(find.text('Resend'), findsOneWidget);
      expect(find.text('Cancel'), findsOneWidget);
      expect(find.text('Deactivate'), findsOneWidget);
      expect(find.byTooltip('Edit Ramesh Kumar'), findsOneWidget);
    });

    testWidgets('hides manage actions without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        const EmployeeListScreen(),
        _SeededEmployeeController(EmployeeState(employees: [_employee()])),
        permissions: const [kEmployeesReadPermission],
      );

      expect(find.text('Ramesh Kumar'), findsOneWidget);
      expect(find.text('Resend'), findsNothing);
      expect(find.text('Cancel'), findsNothing);
      expect(find.text('Deactivate'), findsNothing);
      expect(find.byTooltip('Edit Ramesh Kumar'), findsNothing);
    });

    testWidgets('shows registered status without resend/cancel actions', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const EmployeeListScreen(),
        _SeededEmployeeController(
          EmployeeState(
            employees: [
              _employee(
                invitationStatus: EmployeeInvitationStatus.registered,
              ),
            ],
          ),
        ),
      );

      expect(find.text('Registered · expires 01-09-2026'), findsOneWidget);
      expect(find.text('Resend'), findsNothing);
      expect(find.text('Cancel'), findsNothing);
      // Registered employees can still be deactivated/edited.
      expect(find.text('Deactivate'), findsOneWidget);
    });

    testWidgets('resend surfaces the new invitation link', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(employees: [_employee()]),
      );
      await _pumpConfig(tester, const EmployeeListScreen(), controller);

      await tester.tap(find.text('Resend'));
      await tester.pumpAndSettle();

      expect(controller.resendCount, 1);
      expect(find.text('Invitation link'), findsOneWidget);
      expect(find.text('/invite/inv-token-9'), findsOneWidget);
      expect(find.text('Expires: 01-09-2026'), findsOneWidget);

      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();
    });

    testWidgets('cancel invitation confirms before cancelling', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(employees: [_employee()]),
      );
      await _pumpConfig(tester, const EmployeeListScreen(), controller);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(find.text('Cancel invitation?'), findsOneWidget);
      expect(
        find.text(
          'The invitation for Ramesh Kumar will be invalidated. '
          'This cannot be undone.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Cancel invitation'));
      await tester.pumpAndSettle();

      expect(controller.cancelCount, 1);
    });
  });

  group('employee create screen', () {
    testWidgets('shows access denied without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        const CreateEmployeeScreen(),
        _SeededEmployeeController(const EmployeeState()),
        permissions: const [kEmployeesReadPermission],
        routePath: '/admin/employees/new',
      );

      expect(find.text('Access denied'), findsOneWidget);
      expect(
        find.text('Your account does not have permission to view this content.'),
        findsOneWidget,
      );
    });

    testWidgets('role selector lists assignable roles and excludes Owner', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const CreateEmployeeScreen(),
        _SeededEmployeeController(
          EmployeeState(branchOptions: [_branchOption()]),
        ),
        routePath: '/admin/employees/new',
      );

      await tester.tap(find.byType(DropdownButtonFormField<EmployeeRole>));
      await tester.pumpAndSettle();

      expect(find.text('Delivery Manager'), findsOneWidget);
      expect(find.text('Delivery Boy / Delivery Staff'), findsOneWidget);
      expect(find.text('Accountant'), findsOneWidget);
      expect(find.text('Dairy Manager'), findsOneWidget);
      expect(find.text('System Administrator'), findsOneWidget);
      expect(find.text('Owner'), findsNothing);
    });

    testWidgets('system administrator is not bound to a branch', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(branchOptions: [_branchOption()]),
      );
      await _pumpConfig(
        tester,
        const CreateEmployeeScreen(),
        controller,
        routePath: '/admin/employees/new',
      );

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Full name *'),
        'Suresh Kumar',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Mobile number *'),
        '9876543211',
      );

      await tester.tap(find.byType(DropdownButtonFormField<EmployeeRole>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('System Administrator').last);
      await tester.pumpAndSettle();

      expect(
        find.text('System Administrators are not bound to a branch.'),
        findsOneWidget,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Create'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 1);
      expect(controller.lastCreateRequest?.roleCode, 'SYSTEM_ADMIN');
      expect(controller.lastCreateRequest?.branchId, isNull);
      expect(controller.lastCreateRequest?.displayName, 'Suresh Kumar');
    });

    testWidgets('create sends role and branch for delivery staff', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(branchOptions: [_branchOption()]),
      );
      await _pumpConfig(
        tester,
        const CreateEmployeeScreen(),
        controller,
        routePath: '/admin/employees/new',
      );

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Full name *'),
        'Suresh Kumar',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Mobile number *'),
        '9876543211',
      );

      await tester.tap(find.byType(DropdownButtonFormField<EmployeeRole>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Delivery Boy / Delivery Staff').last);
      await tester.pumpAndSettle();

      await tester.tap(find.byType(DropdownButtonFormField<EmployeeBranchOption>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Main Branch (MAIN)').last);
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(FilledButton, 'Create'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 1);
      final request = controller.lastCreateRequest;
      expect(request?.roleCode, 'DELIVERY_STAFF');
      expect(request?.branchId, 7);
      expect(request?.sendInvitation, isTrue);
      expect(request?.mobile, '9876543211');
    });

    testWidgets('surfaces the invitation link after creating', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(branchOptions: [_branchOption()]),
        surfaceInvitation: true,
      );
      await _pumpConfig(
        tester,
        const CreateEmployeeScreen(),
        controller,
        routePath: '/admin/employees/new',
      );

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Full name *'),
        'Suresh Kumar',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Mobile number *'),
        '9876543211',
      );
      await tester.tap(find.byType(DropdownButtonFormField<EmployeeRole>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Dairy Manager').last);
      await tester.pumpAndSettle();
      await tester.tap(find.byType(DropdownButtonFormField<EmployeeBranchOption>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Main Branch (MAIN)').last);
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(FilledButton, 'Create'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 1);
      expect(find.text('Invitation link'), findsOneWidget);
      expect(find.text('/invite/inv-token-9'), findsOneWidget);
      expect(find.text('Expires: 01-09-2026'), findsOneWidget);

      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();
    });
  });

  group('employee edit screen', () {
    testWidgets('shows access denied without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        EmployeeEditScreen(employeeId: 42, employee: _employee()),
        _SeededEmployeeController(const EmployeeState()),
        permissions: const [kEmployeesReadPermission],
        routePath: '/admin/employees/:id',
        initialPath: '/admin/employees/42',
      );

      expect(find.text('Access denied'), findsOneWidget);
      expect(
        find.text('Your account does not have permission to view this content.'),
        findsOneWidget,
      );
    });

    testWidgets('prefills employee details and saves role changes', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(branchOptions: [_branchOption()]),
      );
      await _pumpConfig(
        tester,
        EmployeeEditScreen(employeeId: 42, employee: _employee()),
        controller,
        routePath: '/admin/employees/:id',
        initialPath: '/admin/employees/42',
      );

      expect(find.text('Edit Ramesh Kumar'), findsOneWidget);
      expect(find.text('Invitation sent · expires 01-09-2026'), findsOneWidget);
      expect(
        find.text(
          'Changing the role reassigns access immediately and '
          'is recorded in the audit trail.',
        ),
        findsOneWidget,
      );
      expect(
        find.text(
          'Changing the branch updates the employee’s working branch and '
          'is recorded in the audit trail.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.byType(DropdownButtonFormField<EmployeeRole>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Accountant').last);
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(FilledButton, 'Save changes'));
      await tester.pumpAndSettle();

      expect(controller.updateCount, 1);
      expect(controller.lastUpdateRequest?.roleCode, 'ACCOUNTANT');
      expect(controller.lastUpdateRequest?.displayName, 'Ramesh Kumar');
    });

    testWidgets('loads branch options when the store is empty', (tester) async {
      final controller = _SeededEmployeeController(const EmployeeState());
      await _pumpConfig(
        tester,
        EmployeeEditScreen(employeeId: 42, employee: _employee()),
        controller,
        routePath: '/admin/employees/:id',
        initialPath: '/admin/employees/42',
      );

      expect(controller.branchLoadCount, 1);
    });
  });

  group('employee invitation screen', () {
    testWidgets('shows checking state while verifying', (tester) async {
      await _pumpConfig(
        tester,
        const EmployeeInvitationScreen(token: 'inv-token-9'),
        _SeededEmployeeController(const EmployeeState(isLoading: true)),
        routePath: '/invite/:token',
        initialPath: '/invite/inv-token-9',
        // The infinite progress indicator never settles, so navigate with
        // fixed pumps instead of pumpAndSettle.
        settleOnNavigate: false,
      );

      expect(find.bySemanticsLabel('Checking invitation'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows the assigned profile with role and branch read-only', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const EmployeeInvitationScreen(token: 'inv-token-9'),
        _SeededEmployeeController(
          EmployeeState(
            invitationVerification: _verification(),
            branchOptions: [_branchOption()],
          ),
        ),
        routePath: '/invite/:token',
        initialPath: '/invite/inv-token-9',
      );

      expect(find.text('Your assigned profile'), findsOneWidget);
      expect(find.text('Role: Delivery Boy / Delivery Staff'), findsOneWidget);
      expect(find.text('Branch: Main Branch (MAIN)'), findsOneWidget);
      expect(
        find.text(
          'Your role and branch were assigned by your administrator. '
          'They cannot be changed here.',
        ),
        findsOneWidget,
      );
      // Prefilled from the invitation.
      expect(find.text('9876543210'), findsOneWidget);
      expect(find.text('Ramesh Kumar'), findsOneWidget);

      // Role and branch are NOT editable on this screen.
      expect(find.byType(DropdownButtonFormField<EmployeeRole>), findsNothing);
      expect(
        find.byType(DropdownButtonFormField<EmployeeBranchOption>),
        findsNothing,
      );
      expect(find.text('Send one-time code'), findsOneWidget);
      expect(find.text('Complete registration'), findsOneWidget);
    });

    testWidgets('shows invitation unavailable for an invalid token', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const EmployeeInvitationScreen(token: 'inv-token-9'),
        _SeededEmployeeController(
          EmployeeState(
            invitationVerification: _verification(
              isValid: false,
              reason: 'This invitation has expired.',
            ),
          ),
        ),
        routePath: '/invite/:token',
        initialPath: '/invite/inv-token-9',
      );

      expect(find.text('Invitation unavailable'), findsOneWidget);
      expect(find.text('This invitation has expired.'), findsOneWidget);

      await tester.tap(find.text('Back to login'));
      await tester.pumpAndSettle();

      expect(find.text('Login'), findsOneWidget);
    });

    testWidgets('send one-time code calls the controller', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(
          invitationVerification: _verification(),
          branchOptions: [_branchOption()],
        ),
      );
      await _pumpConfig(
        tester,
        const EmployeeInvitationScreen(token: 'inv-token-9'),
        controller,
        routePath: '/invite/:token',
        initialPath: '/invite/inv-token-9',
      );

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Create password'),
        'password123',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'One-time code'),
        '123456',
      );

      await tester.tap(find.text('Send one-time code'));
      await tester.pumpAndSettle();

      expect(controller.sendOtpCount, 1);
      expect(controller.lastOtpMobile, '9876543210');
    });

    testWidgets('complete registration navigates to home', (tester) async {
      final controller = _SeededEmployeeController(
        EmployeeState(
          invitationVerification: _verification(),
          branchOptions: [_branchOption()],
        ),
      );
      await _pumpConfig(
        tester,
        const EmployeeInvitationScreen(token: 'inv-token-9'),
        controller,
        routePath: '/invite/:token',
        initialPath: '/invite/inv-token-9',
      );

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Create password'),
        'password123',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'One-time code'),
        '123456',
      );

      await tester.tap(find.text('Complete registration'));
      await tester.pumpAndSettle();

      expect(controller.completeCount, 1);
      expect(find.text('Home'), findsOneWidget);
    });
  });
}

/// Pumps a screen inside a GoRouter shell that also exposes the shared
/// employee routes ([routePath] renders [screen]; every other route renders a
/// placeholder). The session carries the supplied permissions.
Future<void> _pumpConfig(
  WidgetTester tester,
  Widget screen,
  _SeededEmployeeController controller, {
  List<String> permissions = const [
    kEmployeesReadPermission,
    kEmployeesManagePermission,
  ],
  List<int> branchIds = const [7],
  String routePath = '/admin/employees',
  String? initialPath,
  bool settleOnNavigate = true,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  Widget placeholder(BuildContext context, GoRouterState state) =>
      const Scaffold(body: Center(child: Text('Placeholder')));

  final router = GoRouter(
    initialLocation: '/admin/employees',
    routes: [
      GoRoute(
        path: '/admin/employees',
        builder: routePath == '/admin/employees'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/employees/new',
        builder: routePath == '/admin/employees/new'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/employees/:id',
        builder: routePath == '/admin/employees/:id'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/invite/:token',
        builder: routePath == '/invite/:token'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/login',
        builder: (context, state) =>
            const Scaffold(body: Center(child: Text('Login'))),
      ),
      GoRoute(
        path: '/home',
        builder: (context, state) =>
            const Scaffold(body: Center(child: Text('Home'))),
      ),
    ],
  );

  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        employeeControllerProvider.overrideWith(() => controller),
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

  final target = initialPath ?? routePath;
  if (target != '/admin/employees') {
    router.push(target);
    if (settleOnNavigate) {
      await tester.pumpAndSettle();
    } else {
      // Used when the target route renders an infinite progress indicator
      // (e.g. 'Checking invitation'), which would make pumpAndSettle time out.
      // Pump fixed durations instead so the GoRouter push transition (~300 ms)
      // completes and the target screen is actually built.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      await tester.pump(const Duration(milliseconds: 300));
      await tester.pump(const Duration(milliseconds: 100));
    }
  }
}

class _SeededEmployeeController extends EmployeeController {
  _SeededEmployeeController(this.initialState, {this.surfaceInvitation = false});

  final EmployeeState initialState;
  final bool surfaceInvitation;

  int loadCount = 0;
  int branchLoadCount = 0;
  int createCount = 0;
  int updateCount = 0;
  int resendCount = 0;
  int cancelCount = 0;
  int sendOtpCount = 0;
  int verifyCount = 0;
  int completeCount = 0;

  CreateEmployeeRequest? lastCreateRequest;
  UpdateEmployeeRequest? lastUpdateRequest;
  String? lastOtpMobile;
  String? lastVerifyToken;

  @override
  EmployeeState build() => initialState;

  @override
  Future<void> load() async {
    loadCount++;
  }

  @override
  Future<void> loadBranchOptions() async {
    branchLoadCount++;
  }

  @override
  Future<Employee?> create(CreateEmployeeRequest request) async {
    createCount++;
    lastCreateRequest = request;
    if (surfaceInvitation) {
      state = state.copyWith(lastInvitation: _invitation());
    }
    return _employee(
      displayName: request.displayName,
      roleCode: request.roleCode,
      branchId: request.branchId,
    );
  }

  @override
  Future<Employee?> update(
    int employeeId,
    UpdateEmployeeRequest request,
  ) async {
    updateCount++;
    lastUpdateRequest = request;
    return _employee(
      id: employeeId,
      displayName: request.displayName,
      roleCode: request.roleCode ?? 'DELIVERY_STAFF',
      branchId: request.branchId,
    );
  }

  @override
  Future<EmployeeInvitationResult?> resendInvitation(
    int employeeId,
    int invitationId,
  ) async {
    resendCount++;
    return _invitation(invitationId: invitationId, employeeId: employeeId);
  }

  @override
  Future<bool> cancelInvitation(int employeeId, int invitationId) async {
    cancelCount++;
    return true;
  }

  @override
  Future<bool> sendInvitationOtp(String mobile) async {
    sendOtpCount++;
    lastOtpMobile = mobile;
    return true;
  }

  @override
  Future<EmployeeInvitationVerification?> verifyInvitation(String token) async {
    verifyCount++;
    lastVerifyToken = token;
    return null;
  }

  @override
  Future<bool> completeRegistration({
    required String token,
    required String displayName,
    required String mobile,
    required String password,
    required String otpCode,
    String? email,
  }) async {
    completeCount++;
    return true;
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
        publicUserId: 'employee-user-1',
        displayName: 'Employee Manager',
        email: null,
        mobile: '9999999999',
        roles: const ['OWNER'],
        permissions: permissions,
        branchIds: branchIds,
      ),
      accessToken: 'employee-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

Employee _employee({
  int id = 42,
  String displayName = 'Ramesh Kumar',
  String roleCode = 'DELIVERY_STAFF',
  String? roleName = 'Delivery Boy',
  int? branchId = 7,
  String? branchName = 'Main Branch',
  bool isActive = true,
  int? invitationId = 9,
  EmployeeInvitationStatus? invitationStatus = EmployeeInvitationStatus.invited,
  DateTime? invitationExpiresAt,
  DateTime? registeredAt,
}) => Employee(
  id: id,
  publicId: 'emp-$id',
  displayName: displayName,
  mobile: '9876543210',
  email: 'ramesh@example.test',
  roleCode: roleCode,
  roleName: roleName,
  branchId: branchId,
  branchName: branchName,
  isActive: isActive,
  invitationId: invitationId,
  invitationStatus: invitationStatus,
  invitationExpiresAt: invitationExpiresAt ?? DateTime(2026, 9, 1, 10),
  registeredAt: registeredAt,
  createdAt: DateTime(2026, 8, 1, 10),
);

EmployeeBranchOption _branchOption({int id = 7}) => EmployeeBranchOption(
  id: id,
  publicId: 'branch-$id',
  code: 'MAIN',
  name: 'Main Branch',
  city: 'Mumbai',
  state: 'MH',
  isActive: true,
);

EmployeeInvitationResult _invitation({
  int invitationId = 9,
  int employeeId = 42,
}) => EmployeeInvitationResult(
  invitationId: invitationId,
  invitationPublicId: 'inv-$invitationId',
  employeeId: employeeId,
  token: 'inv-token-$invitationId',
  expiresAt: DateTime(2026, 9, 1, 10),
);

EmployeeInvitationVerification _verification({
  bool isValid = true,
  String? reason,
}) => EmployeeInvitationVerification(
  isValid: isValid,
  displayName: 'Ramesh Kumar',
  mobile: '9876543210',
  email: 'ramesh@example.test',
  roleCode: 'DELIVERY_STAFF',
  branchId: 7,
  reason: reason,
);
