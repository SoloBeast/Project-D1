import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/employees/employee_controller.dart';
import 'package:doodh_direct_mobile/features/employees/employee_models.dart';
import 'package:doodh_direct_mobile/features/employees/employee_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('employee controller', () {
    test('loads employees with the session token', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      await controller.load();
      final state = container.read(employeeControllerProvider);

      expect(state.employees.single.displayName, 'Ramesh Kumar');
      expect(state.employees.single.roleCode, 'DELIVERY_STAFF');
      expect(state.isLoading, isFalse);
      expect(repository.lastToken, 'employee-token');
      expect(repository.loadCount, 1);
    });

    test('does not call the repository when unauthenticated', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _unauthenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      await controller.load();
      final state = container.read(employeeControllerProvider);

      expect(repository.loadCount, 0);
      expect(state.isLoading, isFalse);
      expect(state.employees, isEmpty);
    });

    test('loads branch options', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      await controller.loadBranchOptions();
      final state = container.read(employeeControllerProvider);

      expect(state.branchOptions.single.code, 'MAIN');
      expect(state.branchOptions.single.displayName, 'Main Branch (MAIN)');
      expect(state.isLoading, isFalse);
      expect(repository.lastToken, 'employee-token');
    });

    test('surfaces the server message when loading employees fails', () async {
      final repository = _FailingEmployeeRepository(
        ApiException(500, 'internal', 'Employee lookup failed.', field: 'Id'),
      );
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      await controller.load();
      final state = container.read(employeeControllerProvider);

      expect(state.errorMessage, 'Employee lookup failed.');
      expect(state.isLoading, isFalse);
    });

    test('surfaces a network message when loading employees fails', () async {
      final repository = _FailingEmployeeRepository(StateError('boom'));
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      await controller.load();
      final state = container.read(employeeControllerProvider);

      expect(
        state.errorMessage,
        'Unable to load employees. Check your connection and try again.',
      );
      expect(state.isLoading, isFalse);
    });

    test('creates an employee with role, branch and invitation', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final created = await controller.create(_createRequest);
      final state = container.read(employeeControllerProvider);

      expect(created?.displayName, 'Ramesh Kumar');
      expect(state.savedMessage, 'Employee Ramesh Kumar created.');
      expect(repository.lastCreateRequest?.roleCode, 'DELIVERY_STAFF');
      expect(repository.lastCreateRequest?.branchId, 7);
      expect(repository.lastCreateRequest?.sendInvitation, isTrue);
      expect(repository.createCount, 1);
      // The list is refreshed after a successful create.
      expect(repository.loadCount, 1);
      // The single-use token is surfaced exactly once via lastInvitation.
      expect(state.lastInvitation?.token, 'inv-token-9');
      expect(state.lastInvitation?.expiresAt, DateTime.utc(2026, 9, 1, 10));
      expect(state.isSaving, isFalse);
    });

    test('maps field errors on create failure', () async {
      final repository = _FailingEmployeeRepository(
        ApiException(
          422,
          'validation',
          'The name is required.',
          field: 'DisplayName',
        ),
      );
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final created = await controller.create(_createRequest);
      final state = container.read(employeeControllerProvider);

      expect(created, isNull);
      expect(state.errorMessage, 'The name is required.');
      expect(state.fieldErrors['displayName'], 'The name is required.');
      expect(state.isSaving, isFalse);
    });

    test('surfaces a network message when create fails', () async {
      final repository = _FailingEmployeeRepository(StateError('boom'));
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final created = await controller.create(_createRequest);
      final state = container.read(employeeControllerProvider);

      expect(created, isNull);
      expect(
        state.errorMessage,
        'Unable to create the employee. Check your connection and try again.',
      );
      expect(state.isSaving, isFalse);
    });

    test('updates an employee', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final updated = await controller.update(42, _updateRequest);
      final state = container.read(employeeControllerProvider);

      expect(updated?.roleCode, 'ACCOUNTANT');
      expect(state.savedMessage, 'Employee Ramesh Kumar updated.');
      expect(repository.lastEmployeeId, 42);
      expect(repository.lastUpdateRequest?.roleCode, 'ACCOUNTANT');
      expect(repository.lastUpdateRequest?.isActive, isTrue);
      expect(state.isSaving, isFalse);
    });

    test('resends an invitation', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final invitation = await controller.resendInvitation(42, 9);
      final state = container.read(employeeControllerProvider);

      expect(invitation?.token, 'inv-token-9');
      expect(state.savedMessage, 'Invitation resent.');
      expect(state.lastInvitation?.invitationId, 9);
      expect(repository.lastEmployeeId, 42);
      expect(repository.lastInvitationId, 9);
      expect(state.isSaving, isFalse);
    });

    test('cancels an invitation', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final cancelled = await controller.cancelInvitation(42, 9);
      final state = container.read(employeeControllerProvider);

      expect(cancelled, isTrue);
      expect(state.savedMessage, 'Invitation cancelled.');
      expect(repository.cancelCount, 1);
      expect(repository.lastEmployeeId, 42);
      expect(repository.lastInvitationId, 9);
    });

    test('sends the invitation OTP', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final sent = await controller.sendInvitationOtp(' 9876543210 ');
      final state = container.read(employeeControllerProvider);

      expect(sent, isTrue);
      expect(state.savedMessage, 'OTP sent to  9876543210 .');
      // The repository trims the mobile number before sending.
      expect(repository.lastOtpMobile, '9876543210');
      expect(state.isSendingOtp, isFalse);
    });

    test('verifies an invitation', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final verification = await controller.verifyInvitation('inv-token-9');
      final state = container.read(employeeControllerProvider);

      expect(verification?.isValid, isTrue);
      expect(state.invitationVerification?.roleCode, 'DELIVERY_STAFF');
      expect(state.invitationVerification?.branchId, 7);
      expect(repository.lastVerifyToken, 'inv-token-9');
      expect(state.isLoading, isFalse);
    });

    test('completes registration and establishes the returned session', () async {
      final repository = _FakeEmployeeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final completed = await controller.completeRegistration(
        token: 'inv-token-9',
        displayName: 'Ramesh Kumar',
        mobile: '9876543210',
        password: 'Password@123',
        otpCode: '123456',
      );
      final state = container.read(employeeControllerProvider);

      expect(completed, isTrue);
      expect(state.savedMessage, 'Registration complete. Welcome!');
      expect(state.isCompleting, isFalse);
      expect(repository.completeCount, 1);
      expect(repository.lastCompleteRequest?.token, 'inv-token-9');
      expect(repository.lastCompleteRequest?.otpCode, '123456');
      // The device payload comes from the overridden device() — no platform
      // channels are touched.
      expect(repository.lastCompleteRequest?.device, {'deviceId': 'test-device'});
      // The employee is authenticated with the session returned by the backend,
      // routed to their assigned role workspace.
      final sessionState = container.read(sessionControllerProvider);
      expect(sessionState.isAuthenticated, isTrue);
      expect(sessionState.session?.accessToken, 'employee-session-token');
      expect(sessionState.session?.user.roles, ['DELIVERY_STAFF']);
    });

    test('surfaces the server message when completing registration fails', () async {
      final repository = _FailingEmployeeRepository(
        ApiException(
          422,
          'validation',
          'The invitation has expired.',
          field: 'Token',
        ),
      );
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final completed = await controller.completeRegistration(
        token: 'inv-token-expired',
        displayName: 'Ramesh Kumar',
        mobile: '9876543210',
        password: 'Password@123',
        otpCode: '123456',
      );
      final state = container.read(employeeControllerProvider);

      expect(completed, isFalse);
      expect(state.errorMessage, 'The invitation has expired.');
      expect(state.fieldErrors['token'], 'The invitation has expired.');
      expect(state.isCompleting, isFalse);
    });

    test('surfaces a network message when completing registration fails', () async {
      final repository = _FailingEmployeeRepository(StateError('boom'));
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(employeeControllerProvider.notifier);

      final completed = await controller.completeRegistration(
        token: 'inv-token-9',
        displayName: 'Ramesh Kumar',
        mobile: '9876543210',
        password: 'Password@123',
        otpCode: '123456',
      );
      final state = container.read(employeeControllerProvider);

      expect(completed, isFalse);
      expect(
        state.errorMessage,
        'Unable to complete registration. Check your connection and try again.',
      );
      expect(state.isCompleting, isFalse);
    });
  });
}

Future<ProviderContainer> _authenticatedContainer(
  EmployeeRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      employeeRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

Future<ProviderContainer> _unauthenticatedContainer(
  EmployeeRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_UnauthenticatedRepository()),
      employeeRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;

  @override
  Future<void> saveSession(AuthSession session) async {
    // No-op — avoids FlutterSecureStorage platform channels in tests.
  }
}

class _UnauthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;
}

class _FakeEmployeeRepository extends EmployeeRepository {
  _FakeEmployeeRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int callCount = 0;
  int loadCount = 0;
  int branchCount = 0;
  int createCount = 0;
  int updateCount = 0;
  int resendCount = 0;
  int cancelCount = 0;
  int otpCount = 0;
  int verifyCount = 0;
  int completeCount = 0;

  String? lastToken;
  int? lastEmployeeId;
  int? lastInvitationId;
  String? lastOtpMobile;
  String? lastVerifyToken;
  CreateEmployeeRequest? lastCreateRequest;
  UpdateEmployeeRequest? lastUpdateRequest;
  CompleteEmployeeRegistrationRequest? lastCompleteRequest;

  @override
  Future<List<Employee>> list(String token) async {
    callCount++;
    loadCount++;
    lastToken = token;
    return [_employee()];
  }

  @override
  Future<Employee> get(String token, int employeeId) async {
    callCount++;
    lastToken = token;
    lastEmployeeId = employeeId;
    return _employee(id: employeeId);
  }

  @override
  Future<CreateEmployeeResult> create(
    String token,
    CreateEmployeeRequest request,
  ) async {
    callCount++;
    createCount++;
    lastToken = token;
    lastCreateRequest = request;
    return CreateEmployeeResult(
      employee: _employee(
        displayName: request.displayName,
        roleCode: request.roleCode,
        branchId: request.branchId,
      ),
      invitation: _invitation(),
    );
  }

  @override
  Future<Employee> update(
    String token,
    int employeeId,
    UpdateEmployeeRequest request,
  ) async {
    callCount++;
    updateCount++;
    lastToken = token;
    lastEmployeeId = employeeId;
    lastUpdateRequest = request;
    return _employee(
      id: employeeId,
      displayName: request.displayName,
      roleCode: request.roleCode ?? 'DELIVERY_STAFF',
      branchId: request.branchId,
      isActive: request.isActive,
    );
  }

  @override
  Future<EmployeeInvitationResult> resendInvitation(
    String token,
    int employeeId,
    int invitationId,
  ) async {
    callCount++;
    resendCount++;
    lastToken = token;
    lastEmployeeId = employeeId;
    lastInvitationId = invitationId;
    return _invitation(invitationId: invitationId, employeeId: employeeId);
  }

  @override
  Future<void> cancelInvitation(
    String token,
    int employeeId,
    int invitationId,
  ) async {
    callCount++;
    cancelCount++;
    lastToken = token;
    lastEmployeeId = employeeId;
    lastInvitationId = invitationId;
  }

  @override
  Future<List<EmployeeBranchOption>> getBranchOptions(String token) async {
    callCount++;
    branchCount++;
    lastToken = token;
    return [_branchOption()];
  }

  @override
  Future<void> sendInvitationOtp(String mobile) async {
    callCount++;
    otpCount++;
    // Mirror the real repository, which trims the mobile number before
    // sending (employee_repository.dart).
    lastOtpMobile = mobile.trim();
  }

  @override
  Future<EmployeeInvitationVerification> verifyInvitation(String token) async {
    callCount++;
    verifyCount++;
    lastVerifyToken = token;
    return _verification();
  }

  @override
  Future<CompleteEmployeeRegistrationResult> completeRegistration(
    CompleteEmployeeRegistrationRequest request,
  ) async {
    callCount++;
    completeCount++;
    lastCompleteRequest = request;
    return _completeResult();
  }

  @override
  Future<Map<String, dynamic>> device() async => {'deviceId': 'test-device'};
}

class _FailingEmployeeRepository extends EmployeeRepository {
  _FailingEmployeeRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<List<Employee>> list(String token) async => throw failure;

  @override
  Future<Employee> get(String token, int employeeId) async => throw failure;

  @override
  Future<CreateEmployeeResult> create(
    String token,
    CreateEmployeeRequest request,
  ) async => throw failure;

  @override
  Future<Employee> update(
    String token,
    int employeeId,
    UpdateEmployeeRequest request,
  ) async => throw failure;

  @override
  Future<EmployeeInvitationResult> resendInvitation(
    String token,
    int employeeId,
    int invitationId,
  ) async => throw failure;

  @override
  Future<void> cancelInvitation(
    String token,
    int employeeId,
    int invitationId,
  ) async => throw failure;

  @override
  Future<List<EmployeeBranchOption>> getBranchOptions(String token) async =>
      throw failure;

  @override
  Future<void> sendInvitationOtp(String mobile) async => throw failure;

  @override
  Future<EmployeeInvitationVerification> verifyInvitation(String token) async =>
      throw failure;

  @override
  Future<CompleteEmployeeRegistrationResult> completeRegistration(
    CompleteEmployeeRegistrationRequest request,
  ) async => throw failure;

  @override
  Future<Map<String, dynamic>> device() async => throw failure;
}

Employee _employee({
  int id = 42,
  String displayName = 'Ramesh Kumar',
  String roleCode = 'DELIVERY_STAFF',
  String roleName = 'Delivery Boy',
  int? branchId = 7,
  String? branchName = 'Main Branch',
  bool isActive = true,
  int? invitationId = 9,
  EmployeeInvitationStatus? invitationStatus = EmployeeInvitationStatus.invited,
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
  invitationExpiresAt: DateTime.utc(2026, 9, 1, 10),
  registeredAt: null,
  createdAt: DateTime.utc(2026, 8, 1, 10),
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

EmployeeInvitationResult _invitation({int invitationId = 9, int employeeId = 42}) =>
    EmployeeInvitationResult(
      invitationId: invitationId,
      invitationPublicId: 'inv-$invitationId',
      employeeId: employeeId,
      token: 'inv-token-$invitationId',
      expiresAt: DateTime.utc(2026, 9, 1, 10),
    );

EmployeeInvitationVerification _verification({bool isValid = true}) =>
    EmployeeInvitationVerification(
      isValid: isValid,
      displayName: 'Ramesh Kumar',
      mobile: '9876543210',
      email: 'ramesh@example.test',
      roleCode: 'DELIVERY_STAFF',
      branchId: 7,
      reason: isValid ? null : 'This invitation has expired.',
    );

CompleteEmployeeRegistrationResult _completeResult() =>
    CompleteEmployeeRegistrationResult(
      sessionJson: {
        'user': {
          'publicUserId': 'emp-user-1',
          'displayName': 'Ramesh Kumar',
          'email': 'ramesh@example.test',
          'mobile': '9876543210',
          'roles': ['DELIVERY_STAFF'],
          'permissions': <String>[],
          'branchIds': [7],
        },
        'tokens': {
          'accessToken': 'employee-session-token',
          'refreshToken': 'refresh-token',
          'accessTokenExpiresAtUtc': '2099-01-01T00:00:00.000Z',
          'refreshTokenExpiresAtUtc': '2099-02-01T00:00:00.000Z',
        },
      },
      invitationStatus: EmployeeInvitationStatus.registered,
    );

const _createRequest = CreateEmployeeRequest(
  displayName: 'Ramesh Kumar',
  mobile: '9876543210',
  roleCode: 'DELIVERY_STAFF',
  branchId: 7,
  sendInvitation: true,
);

const _updateRequest = UpdateEmployeeRequest(
  displayName: 'Ramesh Kumar',
  isActive: true,
  roleCode: 'ACCOUNTANT',
  branchId: 7,
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'owner-1',
    displayName: 'Owner',
    email: 'owner@example.test',
    mobile: null,
    roles: ['OWNER'],
    permissions: ['EMPLOYEES.READ', 'EMPLOYEES.MANAGE'],
    branchIds: [7],
  ),
  accessToken: 'employee-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
