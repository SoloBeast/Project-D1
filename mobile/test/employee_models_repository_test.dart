import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/employees/employee_models.dart';
import 'package:doodh_direct_mobile/features/employees/employee_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('employee models', () {
    test('parses an employee from API JSON', () {
      final employee = Employee.fromJson(_employeeJson());

      expect(employee.id, 42);
      expect(employee.publicId, 'emp-0001');
      expect(employee.displayName, 'Ramesh Kumar');
      expect(employee.mobile, '9876543210');
      expect(employee.email, 'ramesh@example.com');
      expect(employee.roleCode, 'DELIVERY_STAFF');
      expect(employee.roleName, 'Delivery Boy');
      expect(employee.invitationId, 9);
      expect(employee.branchId, 7);
      expect(employee.branchName, 'Main Branch');
      expect(employee.isActive, isTrue);
      expect(employee.invitationStatus, EmployeeInvitationStatus.invited);
      expect(
        employee.invitationExpiresAt,
        DateTime.parse('2026-09-01T10:00:00Z'),
      );
      expect(employee.registeredAt, isNull);
      expect(employee.assignableRole, EmployeeRole.deliveryStaff);
    });

    test('parses an employee without optional fields', () {
      final employee = Employee.fromJson({
        'id': 3,
        'publicId': 'emp-0003',
        'displayName': 'Suresh',
        'mobile': null,
        'email': null,
        'roleCode': 'SYSTEM_ADMIN',
        'roleName': null,
        'invitationId': null,
        'branchId': null,
        'branchName': null,
        'isActive': true,
        'invitationStatus': null,
        'invitationExpiresAt': null,
        'registeredAt': '2026-08-28T10:00:00Z',
        'createdAt': '2026-08-28T10:00:00Z',
      });

      expect(employee.invitationId, isNull);
      expect(employee.branchId, isNull);
      expect(employee.branchName, isNull);
      expect(employee.invitationStatus, isNull);
      expect(employee.invitationExpiresAt, isNull);
      expect(employee.assignableRole, EmployeeRole.systemAdmin);
      expect(employee.registeredAt, DateTime.parse('2026-08-28T10:00:00Z'));
    });

    test('does not map OWNER or unknown codes to an assignable role', () {
      final owner = Employee.fromJson({
        ..._employeeJson(),
        'roleCode': 'OWNER',
        'roleName': 'Owner',
      });
      expect(owner.assignableRole, isNull);

      final unknown = Employee.fromJson({
        ..._employeeJson(),
        'roleCode': 'CUSTOMER',
        'roleName': 'Customer',
      });
      expect(unknown.assignableRole, isNull);
    });

    test('parses invitation status values with case-insensitive fallback', () {
      expect(
        EmployeeInvitationStatus.fromJson('Invited'),
        EmployeeInvitationStatus.invited,
      );
      expect(
        EmployeeInvitationStatus.fromJson('invited'),
        EmployeeInvitationStatus.invited,
      );
      expect(
        EmployeeInvitationStatus.fromJson('Registered'),
        EmployeeInvitationStatus.registered,
      );
      expect(
        EmployeeInvitationStatus.fromJson('cancelled'),
        EmployeeInvitationStatus.cancelled,
      );
      expect(
        EmployeeInvitationStatus.fromJson('Expired'),
        EmployeeInvitationStatus.expired,
      );
      expect(
        EmployeeInvitationStatus.fromJson('Unknown'),
        EmployeeInvitationStatus.invited,
      );
      expect(
        EmployeeInvitationStatus.fromJson(null),
        EmployeeInvitationStatus.invited,
      );
    });

    test('maps invitation status to display labels', () {
      expect(EmployeeInvitationStatus.invited.label, 'Invited');
      expect(EmployeeInvitationStatus.registered.label, 'Registered');
      expect(EmployeeInvitationStatus.cancelled.label, 'Cancelled');
      expect(EmployeeInvitationStatus.expired.label, 'Expired');
    });

    test('employee roles round-trip API codes and exclude OWNER', () {
      expect(EmployeeRole.values, hasLength(5));
      expect(EmployeeRole.values.map((role) => role.apiCode), [
        'DELIVERY_MANAGER',
        'DELIVERY_STAFF',
        'ACCOUNTANT',
        'DAIRY_MANAGER',
        'SYSTEM_ADMIN',
      ]);
      for (final role in EmployeeRole.values) {
        expect(EmployeeRole.fromApiCode(role.apiCode), role);
      }
      expect(EmployeeRole.fromApiCode('OWNER'), isNull);
      expect(EmployeeRole.fromApiCode('CUSTOMER'), isNull);
    });

    test('employee role labels match the administrator role selector', () {
      expect(EmployeeRole.deliveryManager.label, 'Delivery Manager');
      expect(
        EmployeeRole.deliveryStaff.label,
        'Delivery Boy / Delivery Staff',
      );
      expect(EmployeeRole.accountant.label, 'Accountant');
      expect(EmployeeRole.dairyManager.label, 'Dairy Manager');
      expect(EmployeeRole.systemAdmin.label, 'System Administrator');
    });

    test('parses branch options and formats display names', () {
      final branch = EmployeeBranchOption.fromJson({
        'id': 7,
        'publicId': 'branch-0007',
        'code': 'MAIN',
        'name': 'Main Branch',
        'city': 'Pune',
        'state': 'Maharashtra',
        'isActive': true,
      });
      expect(branch.id, 7);
      expect(branch.displayName, 'Main Branch (MAIN)');

      final noCode = EmployeeBranchOption.fromJson({
        'id': 8,
        'publicId': 'branch-0008',
        'code': '',
        'name': 'Second Branch',
        'city': null,
        'state': null,
        'isActive': false,
      });
      expect(noCode.displayName, 'Second Branch (—)');
    });

    test('serializes create employee requests', () {
      final request = CreateEmployeeRequest(
        displayName: 'Ramesh Kumar',
        mobile: '9876543210',
        email: '  ramesh@example.com  ',
        roleCode: 'DELIVERY_STAFF',
        branchId: 7,
      );
      expect(request.toJson(), {
        'displayName': 'Ramesh Kumar',
        'mobile': '9876543210',
        'email': 'ramesh@example.com',
        'roleCode': 'DELIVERY_STAFF',
        'branchId': 7,
        'sendInvitation': true,
      });

      const noExtras = CreateEmployeeRequest(
        displayName: 'Suresh',
        mobile: '9876500000',
        roleCode: 'SYSTEM_ADMIN',
        branchId: null,
        sendInvitation: false,
      );
      expect(noExtras.toJson(), {
        'displayName': 'Suresh',
        'mobile': '9876500000',
        'roleCode': 'SYSTEM_ADMIN',
        'sendInvitation': false,
      });
    });

    test('serializes update employee requests', () {
      final request = UpdateEmployeeRequest(
        displayName: 'Ramesh Kumar',
        isActive: false,
        roleCode: 'DAIRY_MANAGER',
        branchId: 8,
      );
      expect(request.toJson(), {
        'displayName': 'Ramesh Kumar',
        'roleCode': 'DAIRY_MANAGER',
        'branchId': 8,
        'isActive': false,
      });

      const noOptionals = UpdateEmployeeRequest(
        displayName: 'Ramesh Kumar',
        isActive: true,
      );
      expect(noOptionals.toJson(), {
        'displayName': 'Ramesh Kumar',
        'isActive': true,
      });
    });

    test('parses create result with and without an invitation', () {
      final withInvitation = CreateEmployeeResult.fromJson({
        'employee': _employeeJson(),
        'invitation': _invitationJson(),
      });
      expect(withInvitation.employee.displayName, 'Ramesh Kumar');
      expect(withInvitation.invitation!.token, 'inv-token-42');
      expect(
        withInvitation.invitation!.expiresAt,
        DateTime.parse('2026-09-01T10:00:00Z'),
      );

      final withoutInvitation = CreateEmployeeResult.fromJson({
        'employee': _employeeJson(),
        'invitation': null,
      });
      expect(withoutInvitation.invitation, isNull);
    });

    test('parses invitation verification with and without a branch', () {
      final verification = EmployeeInvitationVerification.fromJson({
        'isValid': true,
        'displayName': 'Ramesh Kumar',
        'mobile': '9876543210',
        'email': null,
        'roleCode': 'DELIVERY_STAFF',
        'branchId': 7,
        'reason': null,
      });
      expect(verification.isValid, isTrue);
      expect(verification.displayName, 'Ramesh Kumar');
      expect(verification.mobile, '9876543210');
      expect(verification.roleCode, 'DELIVERY_STAFF');
      expect(verification.branchId, 7);

      final unbound = EmployeeInvitationVerification.fromJson({
        'isValid': true,
        'displayName': 'Admin',
        'mobile': '9876500000',
        'email': null,
        'roleCode': 'SYSTEM_ADMIN',
        'branchId': null,
        'reason': null,
      });
      expect(unbound.branchId, isNull);
    });

    test('serializes complete registration requests', () {
      final request = CompleteEmployeeRegistrationRequest(
        token: 'invitation-token-42',
        displayName: 'Ramesh Kumar',
        mobile: '9876543210',
        password: 'secret123',
        otpCode: '123456',
        device: {'deviceId': 'dev-1'},
      );
      expect(request.toJson(), {
        'token': 'invitation-token-42',
        'displayName': 'Ramesh Kumar',
        'mobile': '9876543210',
        'password': 'secret123',
        'otpCode': '123456',
        'device': {'deviceId': 'dev-1'},
      });
    });

    test('parses complete registration result', () {
      final result = CompleteEmployeeRegistrationResult.fromJson({
        'session': _sessionJson(),
        'invitationStatus': 'Registered',
      });
      expect(result.invitationStatus, EmployeeInvitationStatus.registered);

      final session = AuthSession.fromJson(result.sessionJson);
      expect(session.accessToken, 'employee-token');
      expect(session.user.publicUserId, 'owner-1');
      expect(session.user.roles, ['DELIVERY_STAFF']);
    });
  });

  group('employee repository', () {
    test('lists employees with the admin access token', () async {
      final repository = _repository((request) async {
        _expectRequest(request, method: 'GET', path: '/api/v1/admin/employees');
        return _response([_employeeJson()]);
      });

      final employees = await repository.list('employee-token');

      expect(employees.single.displayName, 'Ramesh Kumar');
      expect(employees.single.assignableRole, EmployeeRole.deliveryStaff);
    });

    test('gets a single employee', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/employees/42',
        );
        return _response(_employeeJson());
      });

      final employee = await repository.get('employee-token', 42);

      expect(employee.id, 42);
      expect(employee.branchName, 'Main Branch');
      expect(employee.invitationId, 9);
    });

    test('creates an employee and returns the invitation token once', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/employees',
        );
        expect(jsonDecode(request.body), {
          'displayName': 'Ramesh Kumar',
          'mobile': '9876543210',
          'email': 'ramesh@example.com',
          'roleCode': 'DELIVERY_STAFF',
          'branchId': 7,
          'sendInvitation': true,
        });
        return _response({
          'employee': _employeeJson(),
          'invitation': _invitationJson(),
        });
      });

      final result = await repository.create(
        'employee-token',
        CreateEmployeeRequest(
          displayName: 'Ramesh Kumar',
          mobile: '9876543210',
          email: '  ramesh@example.com  ',
          roleCode: 'DELIVERY_STAFF',
          branchId: 7,
        ),
      );

      expect(result.employee.displayName, 'Ramesh Kumar');
      expect(result.invitation!.token, 'inv-token-42');
      expect(
        result.invitation!.expiresAt,
        DateTime.parse('2026-09-01T10:00:00Z'),
      );
    });

    test('updates an employee', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/employees/42',
        );
        expect(jsonDecode(request.body), {
          'displayName': 'Ramesh Kumar Updated',
          'roleCode': 'DAIRY_MANAGER',
          'branchId': 8,
          'isActive': true,
        });
        return _response(
          _employeeJson(
            displayName: 'Ramesh Kumar Updated',
            roleCode: 'DAIRY_MANAGER',
            roleName: 'Dairy Manager',
          ),
        );
      });

      final employee = await repository.update(
        'employee-token',
        42,
        UpdateEmployeeRequest(
          displayName: 'Ramesh Kumar Updated',
          roleCode: 'DAIRY_MANAGER',
          branchId: 8,
          isActive: true,
        ),
      );

      expect(employee.displayName, 'Ramesh Kumar Updated');
      expect(employee.assignableRole, EmployeeRole.dairyManager);
    });

    test('resends an invitation', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/employees/42/invitations/9/resend',
        );
        return _response(_invitationJson());
      });

      final invitation = await repository.resendInvitation(
        'employee-token',
        42,
        9,
      );

      expect(invitation.token, 'inv-token-42');
      expect(invitation.invitationId, 9);
    });

    test('cancels an invitation', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/employees/42/invitations/9/cancel',
        );
        return _response(<String, dynamic>{});
      });

      await repository.cancelInvitation('employee-token', 42, 9);
    });

    test('loads branch options with internal numeric ids', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/employees/branches',
        );
        return _response([
          {
            'id': 7,
            'publicId': 'branch-0007',
            'code': 'MAIN',
            'name': 'Main Branch',
            'city': 'Pune',
            'state': 'Maharashtra',
            'isActive': true,
          },
        ]);
      });

      final options = await repository.getBranchOptions('employee-token');

      expect(options.single.displayName, 'Main Branch (MAIN)');
      expect(options.single.id, 7);
    });

    test('sends the invitation OTP without an auth token', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/auth/send-otp',
          authorization: null,
        );
        expect(jsonDecode(request.body), {'mobile': '9876543210', 'purpose': 3});
        return _response(<String, dynamic>{});
      });

      await repository.sendInvitationOtp('  9876543210  ');
    });

    test('verifies an invitation without an auth token', () async {
      const token = 'invitation-token-42';
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/employee-invitations/${Uri.encodeComponent(token)}/verify',
          authorization: null,
        );
        return _response({
          'isValid': true,
          'displayName': 'Ramesh Kumar',
          'mobile': '9876543210',
          'email': null,
          'roleCode': 'DELIVERY_STAFF',
          'branchId': 7,
          'reason': null,
        });
      });

      final verification = await repository.verifyInvitation(token);

      expect(verification.isValid, isTrue);
      expect(verification.roleCode, 'DELIVERY_STAFF');
      expect(verification.branchId, 7);
    });

    test('completes registration with device payload and no auth token', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/employee-invitations/complete',
          authorization: null,
        );
        expect(jsonDecode(request.body), {
          'token': 'invitation-token-42',
          'displayName': 'Ramesh Kumar',
          'mobile': '9876543210',
          'password': 'secret123',
          'otpCode': '123456',
          'device': {'deviceId': 'dev-1'},
        });
        return _response({
          'session': _sessionJson(),
          'invitationStatus': 'Registered',
        });
      });

      final result = await repository.completeRegistration(
        CompleteEmployeeRegistrationRequest(
          token: 'invitation-token-42',
          displayName: 'Ramesh Kumar',
          mobile: '9876543210',
          password: 'secret123',
          otpCode: '123456',
          device: {'deviceId': 'dev-1'},
        ),
      );

      expect(result.invitationStatus, EmployeeInvitationStatus.registered);
      final session = AuthSession.fromJson(result.sessionJson);
      expect(session.accessToken, 'employee-token');
    });
  });
}

EmployeeRepository _repository(
  Future<http.Response> Function(http.Request request) handler,
) => EmployeeRepository(
  api: ApiClient(
    client: MockClient(handler),
    baseUrl: 'https://api.example.test',
  ),
);

void _expectRequest(
  http.Request request, {
  required String method,
  required String path,
  String? authorization = 'Bearer employee-token',
}) {
  expect(request.method, method);
  expect(request.url.path, path);
  expect(request.headers['Authorization'], authorization);
  expect(request.headers['Accept'], 'application/json');
  expect(request.headers['Content-Type'], 'application/json');
}

http.Response _response(Object data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': <Object>[]}),
  200,
  headers: {'content-type': 'application/json'},
);

Map<String, dynamic> _employeeJson({
  String displayName = 'Ramesh Kumar',
  String roleCode = 'DELIVERY_STAFF',
  String? roleName = 'Delivery Boy',
}) => {
  'id': 42,
  'publicId': 'emp-0001',
  'displayName': displayName,
  'mobile': '9876543210',
  'email': 'ramesh@example.com',
  'roleCode': roleCode,
  'roleName': roleName,
  'invitationId': 9,
  'branchId': 7,
  'branchName': 'Main Branch',
  'isActive': true,
  'invitationStatus': 'Invited',
  'invitationExpiresAt': '2026-09-01T10:00:00Z',
  'registeredAt': null,
  'createdAt': '2026-08-28T10:00:00Z',
};

Map<String, dynamic> _invitationJson() => {
  'invitationId': 9,
  'invitationPublicId': 'inv-0009',
  'employeeId': 42,
  'token': 'inv-token-42',
  'expiresAt': '2026-09-01T10:00:00Z',
};

Map<String, dynamic> _sessionJson() => {
  'user': {
    'publicUserId': 'owner-1',
    'displayName': 'Ramesh Kumar',
    'email': 'ramesh@example.com',
    'mobile': '9876543210',
    'roles': ['DELIVERY_STAFF'],
    'permissions': <String>[],
    'branchIds': [7],
  },
  'tokens': {
    'accessToken': 'employee-token',
    'refreshToken': 'refresh-token',
    'accessTokenExpiresAtUtc': '2099-01-01T00:00:00.000Z',
    'refreshTokenExpiresAtUtc': '2099-02-01T00:00:00.000Z',
  },
};
