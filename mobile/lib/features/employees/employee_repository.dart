import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'employee_models.dart';

/// HTTP client for the Employee Management module.
///
/// Privileged operations (`list`, `get`, `create`, `update`, resend, cancel,
/// branch options) hit `/api/v1/admin/employees` with the authenticated admin's
/// access token. The invitee-facing flow (verify + complete) hits
/// `/api/v1/employee-invitations` WITHOUT authentication, exactly like the
/// public registration endpoints.
class EmployeeRepository {
  EmployeeRepository({
    required this._api,
    DeviceMetadataService? deviceMetadata,
  }) : _deviceMetadata = deviceMetadata ?? DeviceMetadataService();

  final ApiClient _api;
  final DeviceMetadataService _deviceMetadata;

  static const String _basePath = '/api/v1/admin/employees';
  static const String _invitationPath = '/api/v1/employee-invitations';

  Future<List<Employee>> list(String accessToken) async {
    final response = await _api.get(_basePath, accessToken: accessToken);
    final items = response['data'] as List<dynamic>;
    return items
        .map((item) => Employee.fromJson(item as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<Employee> get(String accessToken, int employeeId) async {
    final response = await _api.get(
      '$_basePath/$employeeId',
      accessToken: accessToken,
    );
    return Employee.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<CreateEmployeeResult> create(
    String accessToken,
    CreateEmployeeRequest request,
  ) async {
    final response = await _api.post(
      _basePath,
      accessToken: accessToken,
      body: request.toJson(),
    );
    return CreateEmployeeResult.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<Employee> update(
    String accessToken,
    int employeeId,
    UpdateEmployeeRequest request,
  ) async {
    final response = await _api.put(
      '$_basePath/$employeeId',
      accessToken: accessToken,
      body: request.toJson(),
    );
    return Employee.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<EmployeeInvitationResult> resendInvitation(
    String accessToken,
    int employeeId,
    int invitationId,
  ) async {
    final response = await _api.post(
      '$_basePath/$employeeId/invitations/$invitationId/resend',
      accessToken: accessToken,
    );
    return EmployeeInvitationResult.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<void> cancelInvitation(
    String accessToken,
    int employeeId,
    int invitationId,
  ) async {
    await _api.post(
      '$_basePath/$employeeId/invitations/$invitationId/cancel',
      accessToken: accessToken,
    );
  }

  /// Branch options for the Create Employee screen. Unlike the public catalogue
  /// endpoint, these carry the internal numeric id required by
  /// [CreateEmployeeRequest.branchId].
  Future<List<EmployeeBranchOption>> getBranchOptions(
    String accessToken,
  ) async {
    final response = await _api.get(
      '$_basePath/branches',
      accessToken: accessToken,
    );
    final items = response['data'] as List<dynamic>;
    return items
        .map((item) => EmployeeBranchOption.fromJson(item as Map<String, dynamic>))
        .toList(growable: false);
  }

  /// Sends an OTP for the employee invitation flow. Uses `purpose: 3`
  /// (EmployeeInvitation) so the OTP is bound to the invitation, not a login.
  Future<void> sendInvitationOtp(String mobile) async {
    await _api.post(
      '/api/v1/auth/send-otp',
      body: {'mobile': mobile.trim(), 'purpose': 3},
    );
  }

  /// Verifies an invitation token before the invitee completes registration.
  Future<EmployeeInvitationVerification> verifyInvitation(String token) async {
    final response = await _api.get(
      '$_invitationPath/${Uri.encodeComponent(token)}/verify',
    );
    return EmployeeInvitationVerification.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Completes registration. The returned session (created server-side from the
  /// invitation's assigned role and branch) is injected into the app via
  /// `SessionController.establishSession`.
  Future<CompleteEmployeeRegistrationResult> completeRegistration(
    CompleteEmployeeRegistrationRequest request,
  ) async {
    final response = await _api.post(
      '$_invitationPath/complete',
      body: request.toJson(),
    );
    return CompleteEmployeeRegistrationResult.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Builds the device payload required by the complete-registration endpoint.
  Future<Map<String, dynamic>> device() async =>
      (await _deviceMetadata.get()).toJson();
}
