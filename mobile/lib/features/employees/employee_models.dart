/// Invitation lifecycle state for an employee invitation.
///
/// Mirrors the backend `EmployeeInvitationStatus` enum. The backend is the
/// single source of truth — the client only renders the value it receives.
enum EmployeeInvitationStatus {
  invited,
  registered,
  cancelled,
  expired;

  static EmployeeInvitationStatus fromJson(Object? value) {
    switch (value) {
      case 'Invited':
      case 'invited':
        return EmployeeInvitationStatus.invited;
      case 'Registered':
      case 'registered':
        return EmployeeInvitationStatus.registered;
      case 'Cancelled':
      case 'cancelled':
        return EmployeeInvitationStatus.cancelled;
      case 'Expired':
      case 'expired':
        return EmployeeInvitationStatus.expired;
      default:
        return EmployeeInvitationStatus.invited;
    }
  }

  String get label {
    switch (this) {
      case EmployeeInvitationStatus.invited:
        return 'Invited';
      case EmployeeInvitationStatus.registered:
        return 'Registered';
      case EmployeeInvitationStatus.cancelled:
        return 'Cancelled';
      case EmployeeInvitationStatus.expired:
        return 'Expired';
    }
  }
}

/// Employee roles assignable by an administrator.
///
/// OWNER is intentionally excluded — the Owner is the application's highest
/// authority and is never created through the employee onboarding flow.
enum EmployeeRole {
  deliveryManager,
  deliveryStaff,
  accountant,
  dairyManager,
  systemAdmin;

  static EmployeeRole? fromApiCode(Object? value) {
    switch (value) {
      case 'DELIVERY_MANAGER':
        return EmployeeRole.deliveryManager;
      case 'DELIVERY_STAFF':
        return EmployeeRole.deliveryStaff;
      case 'ACCOUNTANT':
        return EmployeeRole.accountant;
      case 'DAIRY_MANAGER':
        return EmployeeRole.dairyManager;
      case 'SYSTEM_ADMIN':
        return EmployeeRole.systemAdmin;
      default:
        return null;
    }
  }

  /// Backend role code as serialized over the wire.
  String get apiCode {
    switch (this) {
      case EmployeeRole.deliveryManager:
        return 'DELIVERY_MANAGER';
      case EmployeeRole.deliveryStaff:
        return 'DELIVERY_STAFF';
      case EmployeeRole.accountant:
        return 'ACCOUNTANT';
      case EmployeeRole.dairyManager:
        return 'DAIRY_MANAGER';
      case EmployeeRole.systemAdmin:
        return 'SYSTEM_ADMIN';
    }
  }

  String get label {
    switch (this) {
      case EmployeeRole.deliveryManager:
        return 'Delivery Manager';
      case EmployeeRole.deliveryStaff:
        return 'Delivery Boy / Delivery Staff';
      case EmployeeRole.accountant:
        return 'Accountant';
      case EmployeeRole.dairyManager:
        return 'Dairy Manager';
      case EmployeeRole.systemAdmin:
        return 'System Administrator';
    }
  }

  String get description {
    switch (this) {
      case EmployeeRole.deliveryManager:
        return 'Plans and manages delivery operations.';
      case EmployeeRole.deliveryStaff:
        return 'Executes deliveries and confirms handoffs.';
      case EmployeeRole.accountant:
        return 'Manages payments, wallets and financial reports.';
      case EmployeeRole.dairyManager:
        return 'Oversees dairy production and delivery management.';
      case EmployeeRole.systemAdmin:
        return 'High-privilege operational administrator.';
    }
  }
}

/// A single employee (or pending invite) visible to Owner / System Administrator.
class Employee {
  const Employee({
    required this.id,
    required this.publicId,
    required this.displayName,
    required this.roleCode,
    required this.isActive,
    this.invitationId,
    this.mobile,
    this.email,
    this.roleName,
    this.branchId,
    this.branchName,
    this.invitationStatus,
    this.invitationExpiresAt,
    this.registeredAt,
    this.createdAt,
  });

  factory Employee.fromJson(Map<String, dynamic> json) => Employee(
    id: _long(json['id']),
    publicId: json['publicId'] as String,
    displayName: json['displayName'] as String,
    mobile: json['mobile'] as String?,
    email: json['email'] as String?,
    roleCode: json['roleCode'] as String,
    roleName: json['roleName'] as String?,
    invitationId: _nullableLong(json['invitationId']),
    branchId: _nullableLong(json['branchId']),
    branchName: json['branchName'] as String?,
    isActive: json['isActive'] as bool,
    invitationStatus: json['invitationStatus'] == null
        ? null
        : EmployeeInvitationStatus.fromJson(json['invitationStatus']),
    invitationExpiresAt: json['invitationExpiresAt'] == null
        ? null
        : DateTime.tryParse(json['invitationExpiresAt'] as String),
    registeredAt: json['registeredAt'] == null
        ? null
        : DateTime.tryParse(json['registeredAt'] as String),
    createdAt: json['createdAt'] == null
        ? null
        : DateTime.tryParse(json['createdAt'] as String),
  );

  final int id;
  final String publicId;
  final String displayName;
  final int? invitationId;
  final String? mobile;
  final String? email;
  final String roleCode;
  final String? roleName;
  final int? branchId;
  final String? branchName;
  final bool isActive;
  final EmployeeInvitationStatus? invitationStatus;
  final DateTime? invitationExpiresAt;
  final DateTime? registeredAt;
  final DateTime? createdAt;

  EmployeeRole? get assignableRole => EmployeeRole.fromApiCode(roleCode);

  static int _long(Object? value) => (value as num).toInt();

  static int? _nullableLong(Object? value) => value == null
      ? null
      : value is num
          ? value.toInt()
          : int.tryParse(value.toString());
}

/// A branch option for the Create Employee screen. Unlike the public catalogue
/// endpoint (which only exposes the Guid public id), this carries the internal
/// numeric id required by `CreateEmployeeRequest.BranchId`.
class EmployeeBranchOption {
  const EmployeeBranchOption({
    required this.id,
    required this.publicId,
    required this.code,
    required this.name,
    required this.city,
    required this.state,
    required this.isActive,
  });

  factory EmployeeBranchOption.fromJson(Map<String, dynamic> json) =>
      EmployeeBranchOption(
        id: (json['id'] as num).toInt(),
        publicId: json['publicId'] as String,
        code: json['code'] as String,
        name: json['name'] as String,
        city: json['city'] as String? ?? '',
        state: json['state'] as String? ?? '',
        isActive: json['isActive'] as bool,
      );

  final int id;
  final String publicId;
  final String code;
  final String name;
  final String city;
  final String state;
  final bool isActive;

  String get displayName => '$name (${code.isEmpty ? '—' : code})';
}

/// Payload for creating an employee and, by default, a secure invitation.
class CreateEmployeeRequest {
  const CreateEmployeeRequest({
    required this.displayName,
    required this.mobile,
    required this.roleCode,
    required this.branchId,
    this.email,
    this.sendInvitation = true,
  });

  final String displayName;
  final String mobile;
  final String? email;
  final String roleCode;
  final int? branchId;
  final bool sendInvitation;

  Map<String, dynamic> toJson() => {
    'displayName': displayName,
    'mobile': mobile,
    if (email != null && email!.trim().isNotEmpty) 'email': email!.trim(),
    'roleCode': roleCode,
    if (branchId != null) 'branchId': branchId,
    'sendInvitation': sendInvitation,
  };
}

/// Payload for updating permitted employee attributes.
class UpdateEmployeeRequest {
  const UpdateEmployeeRequest({
    required this.displayName,
    required this.isActive,
    this.email,
    this.roleCode,
    this.branchId,
  });

  final String displayName;
  final String? email;
  final String? roleCode;
  final int? branchId;
  final bool isActive;

  Map<String, dynamic> toJson() => {
    'displayName': displayName,
    if (email != null && email!.trim().isNotEmpty) 'email': email!.trim(),
    if (roleCode != null) 'roleCode': roleCode,
    if (branchId != null) 'branchId': branchId,
    'isActive': isActive,
  };
}

/// The outcome of creating an employee. When the administrator opted to send an
/// invitation, [invitation] carries the raw token so the invitation link can be
/// surfaced exactly once — the token is never included on list/get results.
class CreateEmployeeResult {
  const CreateEmployeeResult({
    required this.employee,
    this.invitation,
  });

  factory CreateEmployeeResult.fromJson(Map<String, dynamic> json) =>
      CreateEmployeeResult(
        employee: Employee.fromJson(json['employee'] as Map<String, dynamic>),
        invitation: json['invitation'] == null
            ? null
            : EmployeeInvitationResult.fromJson(
                json['invitation'] as Map<String, dynamic>,
              ),
      );

  final Employee employee;
  final EmployeeInvitationResult? invitation;
}

/// A fresh invitation token. Returned exactly once by the backend.
class EmployeeInvitationResult {
  const EmployeeInvitationResult({
    required this.invitationId,
    required this.invitationPublicId,
    required this.employeeId,
    required this.token,
    required this.expiresAt,
  });

  factory EmployeeInvitationResult.fromJson(Map<String, dynamic> json) =>
      EmployeeInvitationResult(
        invitationId: (json['invitationId'] as num).toInt(),
        invitationPublicId: json['invitationPublicId'] as String,
        employeeId: (json['employeeId'] as num).toInt(),
        token: json['token'] as String,
        expiresAt: DateTime.tryParse(json['expiresAt'] as String) ??
            DateTime.fromMillisecondsSinceEpoch(0),
      );

  final int invitationId;
  final String invitationPublicId;
  final int employeeId;
  final String token;
  final DateTime expiresAt;
}

/// Result of verifying an invitation token before completing registration.
class EmployeeInvitationVerification {
  const EmployeeInvitationVerification({
    required this.isValid,
    required this.roleCode,
    this.displayName,
    this.mobile,
    this.email,
    this.branchId,
    this.reason,
  });

  factory EmployeeInvitationVerification.fromJson(Map<String, dynamic> json) =>
      EmployeeInvitationVerification(
        isValid: json['isValid'] as bool,
        displayName: json['displayName'] as String?,
        mobile: json['mobile'] as String?,
        email: json['email'] as String?,
        roleCode: json['roleCode'] as String,
        branchId: (json['branchId'] as num?)?.toInt(),
        reason: json['reason'] as String?,
      );

  final bool isValid;
  final String? displayName;
  final String? mobile;
  final String? email;
  final String roleCode;
  final int? branchId;
  final String? reason;
}

/// Payload for completing an employee registration using the OTP verified
/// against the invitation.
class CompleteEmployeeRegistrationRequest {
  const CompleteEmployeeRegistrationRequest({
    required this.token,
    required this.displayName,
    required this.mobile,
    required this.password,
    required this.otpCode,
    required this.device,
    this.email,
  });

  final String token;
  final String displayName;
  final String? email;
  final String mobile;
  final String password;
  final String otpCode;
  final Map<String, dynamic> device;

  Map<String, dynamic> toJson() => {
    'token': token,
    'displayName': displayName,
    if (email != null && email!.trim().isNotEmpty) 'email': email!.trim(),
    'mobile': mobile,
    'password': password,
    'otpCode': otpCode,
    'device': device,
  };
}

/// Result of completing an employee registration. Contains the session that
/// lands the employee directly in their assigned role workspace, and the
/// invitation's terminal state.
class CompleteEmployeeRegistrationResult {
  const CompleteEmployeeRegistrationResult({
    required this.sessionJson,
    required this.invitationStatus,
  });

  factory CompleteEmployeeRegistrationResult.fromJson(
    Map<String, dynamic> json,
  ) => CompleteEmployeeRegistrationResult(
    sessionJson: json['session'] as Map<String, dynamic>,
    invitationStatus: EmployeeInvitationStatus.fromJson(
      json['invitationStatus'],
    ),
  );

  final Map<String, dynamic> sessionJson;
  final EmployeeInvitationStatus invitationStatus;
}
