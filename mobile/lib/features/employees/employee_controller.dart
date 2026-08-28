import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'employee_models.dart';
import 'employee_repository.dart';

final employeeRepositoryProvider = Provider<EmployeeRepository>(
  (ref) => EmployeeRepository(api: authenticatedApiClient(ref)),
);

final employeeControllerProvider =
    NotifierProvider<EmployeeController, EmployeeState>(
      EmployeeController.new,
    );

class EmployeeState {
  const EmployeeState({
    this.employees = const <Employee>[],
    this.branchOptions = const <EmployeeBranchOption>[],
    this.isLoading = false,
    this.isSaving = false,
    this.isSendingOtp = false,
    this.isCompleting = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
    this.savedMessage,
    this.invitationVerification,
    this.lastInvitation,
  });

  final List<Employee> employees;
  final List<EmployeeBranchOption> branchOptions;
  final bool isLoading;
  final bool isSaving;
  final bool isSendingOtp;
  final bool isCompleting;
  final String? errorMessage;
  final Map<String, String> fieldErrors;
  final String? savedMessage;
  final EmployeeInvitationVerification? invitationVerification;
  final EmployeeInvitationResult? lastInvitation;

  EmployeeState copyWith({
    List<Employee>? employees,
    List<EmployeeBranchOption>? branchOptions,
    bool? isLoading,
    bool? isSaving,
    bool? isSendingOtp,
    bool? isCompleting,
    String? errorMessage,
    Map<String, String>? fieldErrors,
    bool clearError = false,
    String? savedMessage,
    bool clearSaved = false,
    EmployeeInvitationVerification? invitationVerification,
    bool clearVerification = false,
    EmployeeInvitationResult? lastInvitation,
    bool clearLastInvitation = false,
  }) => EmployeeState(
    employees: employees ?? this.employees,
    branchOptions: branchOptions ?? this.branchOptions,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isSendingOtp: isSendingOtp ?? this.isSendingOtp,
    isCompleting: isCompleting ?? this.isCompleting,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    fieldErrors: clearError
        ? const <String, String>{}
        : fieldErrors ?? this.fieldErrors,
    savedMessage: clearSaved ? null : savedMessage ?? this.savedMessage,
    invitationVerification: clearVerification
        ? null
        : invitationVerification ?? this.invitationVerification,
    lastInvitation: clearLastInvitation
        ? null
        : lastInvitation ?? this.lastInvitation,
  );
}

class EmployeeController extends Notifier<EmployeeState> {
  String? _activeUserId;

  EmployeeRepository get _repository =>
      ref.read(employeeRepositoryProvider);

  SessionState get _session => ref.read(sessionControllerProvider);

  String? get _token => _session.session?.accessToken;

  @override
  EmployeeState build() {
    ref.listen<SessionState>(sessionControllerProvider, (previous, next) {
      final userId = next.publicUserId;
      if (!next.isAuthenticated ||
          (_activeUserId != null && _activeUserId != userId)) {
        state = const EmployeeState();
      }
      _activeUserId = userId;
    }, fireImmediately: true);
    return const EmployeeState();
  }

  Future<void> load() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final employees = await _repository.list(token);
      if (token != _token) return;
      state = state.copyWith(employees: employees, isLoading: false);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage: 'Unable to load employees. Check your connection and try again.',
      );
    }
  }

  /// Loads the branch options used by the Create Employee screen.
  Future<void> loadBranchOptions() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final options = await _repository.getBranchOptions(token);
      if (token != _token) return;
      state = state.copyWith(branchOptions: options, isLoading: false);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage: 'Unable to load branches. Check your connection and try again.',
      );
    }
  }

  /// Creates an employee (with an invitation) and refreshes the list. When an
  /// invitation was issued, [EmployeeState.lastInvitation] carries the raw token
  /// so the caller can surface the invitation link exactly once.
  Future<Employee?> create(CreateEmployeeRequest request) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true, clearSaved: true);
    try {
      final result = await _repository.create(token, request);
      if (token != _token) return null;
      final employees = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        employees: employees,
        isSaving: false,
        savedMessage: 'Employee ${result.employee.displayName} created.',
        lastInvitation: result.invitation,
      );
      return result.employee;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return null;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'Unable to create the employee. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Updates permitted employee attributes (including role/branch changes).
  Future<Employee?> update(
    int employeeId,
    UpdateEmployeeRequest request,
  ) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true, clearSaved: true);
    try {
      final updated = await _repository.update(token, employeeId, request);
      if (token != _token) return null;
      final employees = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        employees: employees,
        isSaving: false,
        savedMessage: 'Employee ${updated.displayName} updated.',
      );
      return updated;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return null;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'Unable to update the employee. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Resends an invitation, invalidating the previously delivered link.
  Future<EmployeeInvitationResult?> resendInvitation(
    int employeeId,
    int invitationId,
  ) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true, clearSaved: true);
    try {
      final invitation = await _repository.resendInvitation(
        token,
        employeeId,
        invitationId,
      );
      if (token != _token) return null;
      final employees = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        employees: employees,
        isSaving: false,
        savedMessage: 'Invitation resent.',
        lastInvitation: invitation,
      );
      return invitation;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return null;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'Unable to resend the invitation. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Cancels a pending invitation.
  Future<bool> cancelInvitation(int employeeId, int invitationId) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true, clearSaved: true);
    try {
      await _repository.cancelInvitation(token, employeeId, invitationId);
      if (token != _token) return false;
      final employees = await _repository.list(token);
      if (token != _token) return false;
      state = state.copyWith(
        employees: employees,
        isSaving: false,
        savedMessage: 'Invitation cancelled.',
      );
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return false;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'Unable to cancel the invitation. Check your connection and try again.',
      );
      return false;
    }
  }

  // ---- Invitee-facing flow (unauthenticated) ----

  /// Sends the OTP for the invitation flow (`purpose: 3`). This is the only
  /// step the invitee performs before completing registration.
  Future<bool> sendInvitationOtp(String mobile) async {
    state = state.copyWith(
      isSendingOtp: true,
      clearError: true,
      clearSaved: true,
      clearVerification: true,
    );
    try {
      await _repository.sendInvitationOtp(mobile);
      state = state.copyWith(
        isSendingOtp: false,
        savedMessage: 'OTP sent to $mobile.',
      );
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSendingOtp: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return false;
    } on Object {
      state = state.copyWith(
        isSendingOtp: false,
        errorMessage: 'Unable to send the OTP. Check your connection and try again.',
      );
      return false;
    }
  }

  /// Verifies the invitation token before the invitee completes registration.
  Future<EmployeeInvitationVerification?> verifyInvitation(String token) async {
    state = state.copyWith(isLoading: true, clearError: true, clearSaved: true);
    try {
      final verification = await _repository.verifyInvitation(token);
      state = state.copyWith(
        isLoading: false,
        invitationVerification: verification,
      );
      return verification;
    } on ApiException catch (error) {
      state = state.copyWith(
        isLoading: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return null;
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage: 'Unable to verify the invitation. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Completes registration and injects the returned session so the employee
  /// lands authenticated in their assigned role workspace.
  Future<bool> completeRegistration({
    required String token,
    required String displayName,
    required String mobile,
    required String password,
    required String otpCode,
    String? email,
  }) async {
    state = state.copyWith(isCompleting: true, clearError: true, clearSaved: true);
    try {
      final device = await _repository.device();
      final result = await _repository.completeRegistration(
        CompleteEmployeeRegistrationRequest(
          token: token,
          displayName: displayName,
          mobile: mobile,
          password: password,
          otpCode: otpCode,
          device: device,
          email: email,
        ),
      );
      final session = AuthSession.fromJson(result.sessionJson);
      await ref
          .read(sessionControllerProvider.notifier)
          .establishSession(session);
      state = state.copyWith(
        isCompleting: false,
        savedMessage: 'Registration complete. Welcome!',
        clearVerification: true,
      );
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isCompleting: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return false;
    } on Object {
      state = state.copyWith(
        isCompleting: false,
        errorMessage: 'Unable to complete registration. Check your connection and try again.',
      );
      return false;
    }
  }

  Map<String, String> _fieldErrors(ApiException error) {
    final field = error.field;
    if (field == null || field.trim().isEmpty) {
      return const <String, String>{};
    }
    final normalized = field[0].toLowerCase() + field.substring(1);
    return <String, String>{normalized: error.message};
  }
}
