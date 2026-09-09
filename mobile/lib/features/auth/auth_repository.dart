import 'dart:convert';

import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

const apiBaseUrl = String.fromEnvironment(
  'DOOHDIRECT_API_URL',
  defaultValue: 'http://localhost:5209',
);

enum UserRole { customer, delivery, dairy, owner, admin, support, accountant }

extension UserRoleLabel on UserRole {
  String get label => switch (this) {
    UserRole.customer => 'Customer',
    UserRole.delivery => 'Delivery',
    UserRole.dairy => 'Dairy',
    UserRole.owner => 'Owner',
    UserRole.admin => 'Admin',
    UserRole.support => 'Customer support',
    UserRole.accountant => 'Accountant',
  };
}

UserRole roleFromCodes(List<String> codes) {
  if (codes.contains('OWNER')) return UserRole.owner;
  if (codes.contains('SYSTEM_ADMIN')) return UserRole.admin;
  if (codes.contains('DAIRY_MANAGER')) return UserRole.dairy;
  if (codes.any(
    (code) => code == 'DELIVERY_STAFF' || code == 'DELIVERY_MANAGER',
  )) {
    return UserRole.delivery;
  }
  if (codes.contains('CUSTOMER_SUPPORT')) return UserRole.support;
  if (codes.contains('ACCOUNTANT')) return UserRole.accountant;
  return UserRole.customer;
}

class AuthUserBranchInfo {
  const AuthUserBranchInfo({required this.id, required this.code, required this.name});

  factory AuthUserBranchInfo.fromJson(Map<String, dynamic> json) =>
      AuthUserBranchInfo(
        id: (json['id'] as num).toInt(),
        code: json['code'] as String,
        name: json['name'] as String,
      );

  final int id;
  final String code;
  final String name;

  Map<String, dynamic> toJson() => {'id': id, 'code': code, 'name': name};
}

class MobileChangeRequested {
  const MobileChangeRequested({required this.reqId, required this.pendingMobile});

  final String reqId;
  final String pendingMobile;
}

class AuthUser {
  const AuthUser({
    required this.publicUserId,
    required this.displayName,
    required this.email,
    required this.mobile,
    required this.roles,
    required this.permissions,
    required this.branchIds,
    this.branchDetails = const [],
    this.emailVerified = false,
    this.hasPassword = false,
    this.pendingEmail,
    this.pendingMobile,
  });

  factory AuthUser.fromJson(Map<String, dynamic> json) => AuthUser(
    publicUserId: json['publicUserId'] as String,
    displayName: json['displayName'] as String?,
    email: json['email'] as String?,
    mobile: json['mobile'] as String?,
    roles: (json['roles'] as List<dynamic>).cast<String>(),
    permissions: (json['permissions'] as List<dynamic>).cast<String>(),
    branchIds: (json['branchIds'] as List<dynamic>)
        .cast<num>()
        .map((id) => id.toInt())
        .toList(),
    // Defensive: sessions cached before branchDetails was introduced (or
    // null/absent entries) fall back to an empty list instead of crashing.
    branchDetails: (json['branchDetails'] as List<dynamic>?)
            ?.map((e) => AuthUserBranchInfo.fromJson(e as Map<String, dynamic>))
            .toList() ??
        const [],
    emailVerified: json['emailVerified'] as bool? ?? false,
    hasPassword: json['hasPassword'] as bool? ?? false,
    pendingEmail: json['pendingEmail'] as String?,
    pendingMobile: json['pendingMobile'] as String?,
  );

  final String publicUserId;
  final String? displayName;
  final String? email;
  final String? mobile;
  final List<String> roles;
  final List<String> permissions;
  final List<int> branchIds;

  /// Resolved branch metadata (id/code/name) for every id in [branchIds],
  /// supplied by the server on authenticated sessions. Empty when the server
  /// did not enrich the payload (e.g. legacy cached sessions).
  final List<AuthUserBranchInfo> branchDetails;

  /// True when the account email has been confirmed via an OTP challenge.
  final bool emailVerified;

  /// True when the account has a password set; false for accounts created via
  /// OTP login that must create a password before using password sign in.
  final bool hasPassword;

  /// An email staged by [AuthRepository.requestEmailChange] that awaits OTP
  /// verification. Until verified, [email] keeps the current value.
  final String? pendingEmail;

  /// A mobile number staged by [AuthRepository.requestMobileChange] that awaits
  /// OTP verification. Until verified, [mobile] keeps the current value.
  final String? pendingMobile;

  UserRole get primaryRole => roleFromCodes(roles);

  /// Human-readable name for a branch id, or null when unknown (metadata not
  /// available for the id). Callers fall back to a numeric label.
  String? branchName(int id) {
    for (final branch in branchDetails) {
      if (branch.id == id) return branch.name;
    }
    return null;
  }

  Map<String, dynamic> toJson() => {
    'publicUserId': publicUserId,
    'displayName': displayName,
    'email': email,
    'mobile': mobile,
    'roles': roles,
    'permissions': permissions,
    'branchIds': branchIds,
    'branchDetails': branchDetails.map((b) => b.toJson()).toList(),
    'emailVerified': emailVerified,
    'hasPassword': hasPassword,
    'pendingEmail': pendingEmail,
    'pendingMobile': pendingMobile,
  };
}

/// Result of requesting an email change: the OTP request id that must accompany
/// the verification call plus the staged (pending) email address.
class EmailChangeRequested {
  const EmailChangeRequested({required this.reqId, required this.pendingEmail});

  final String reqId;
  final String pendingEmail;
}

class AuthSession {
  const AuthSession({
    required this.user,
    required this.accessToken,
    required this.refreshToken,
    required this.accessTokenExpiresAtUtc,
    required this.refreshTokenExpiresAtUtc,
  });

  factory AuthSession.fromJson(Map<String, dynamic> json) {
    final tokens = json['tokens'] as Map<String, dynamic>;
    return AuthSession(
      user: AuthUser.fromJson(json['user'] as Map<String, dynamic>),
      accessToken: tokens['accessToken'] as String,
      refreshToken: tokens['refreshToken'] as String,
      accessTokenExpiresAtUtc: _parseProtocolTimestamp(
        tokens,
        'accessTokenExpiresAtUtc',
        'accessTokenExpiresAt',
      ),
      refreshTokenExpiresAtUtc: _parseProtocolTimestamp(
        tokens,
        'refreshTokenExpiresAtUtc',
        'refreshTokenExpiresAt',
      ),
    );
  }

  factory AuthSession.fromStorage(Map<String, dynamic> json) => AuthSession(
    user: AuthUser.fromJson(json['user'] as Map<String, dynamic>),
    accessToken: json['accessToken'] as String,
    refreshToken: json['refreshToken'] as String,
    accessTokenExpiresAtUtc: DateTime.parse(
      json['accessTokenExpiresAtUtc'] as String,
    ).toUtc(),
    refreshTokenExpiresAtUtc: DateTime.parse(
      json['refreshTokenExpiresAtUtc'] as String,
    ).toUtc(),
  );

  final AuthUser user;
  final String accessToken;
  final String refreshToken;
  final DateTime accessTokenExpiresAtUtc;
  final DateTime refreshTokenExpiresAtUtc;

  Map<String, dynamic> toJson() => {
    'user': user.toJson(),
    'accessToken': accessToken,
    'refreshToken': refreshToken,
    'accessTokenExpiresAtUtc': accessTokenExpiresAtUtc.toIso8601String(),
    'refreshTokenExpiresAtUtc': refreshTokenExpiresAtUtc.toIso8601String(),
  };

  static DateTime _parseProtocolTimestamp(
    Map<String, dynamic> tokens,
    String preferredKey,
    String fallbackKey,
  ) {
    final value = tokens[preferredKey] ?? tokens[fallbackKey];
    if (value is! String || value.isEmpty) {
      throw FormatException('Missing authentication expiry timestamp.');
    }
    return DateTime.parse(value).toUtc();
  }
}

/// Outcome of verifying a mobile OTP. The server is authoritative on identity:
/// it either authenticates an existing active user of any role (session present
/// and persisted) or reports that the verified mobile has no account yet, in
/// which case the flow continues into customer onboarding
/// ([AuthRepository.completeOtpRegistration]).
class OtpVerificationResult {
  const OtpVerificationResult({
    required this.requiresOnboarding,
    this.session,
    this.verifiedMobile,
    this.reqId,
  });

  /// True when the verified mobile maps to no account and the user must
  /// complete onboarding (create a password) before a session exists.
  final bool requiresOnboarding;

  /// Present (and already persisted) when [requiresOnboarding] is false — the
  /// existing user authenticated via their provider-attested mobile.
  final AuthSession? session;

  /// The canonical `+91XXXXXXXXXX` mobile that the provider attested.
  final String? verifiedMobile;

  /// The consumed challenge request id, required by
  /// [AuthRepository.completeOtpRegistration] for the onboarding case.
  final String? reqId;
}

class AuthRepository {
  AuthRepository({
    ApiClient? api,
    FlutterSecureStorage? storage,
    DeviceMetadataService? deviceMetadata,
  }) : _api = api ?? ApiClient(baseUrl: apiBaseUrl),
       _storage = storage ?? const FlutterSecureStorage(),
       _deviceMetadata =
           deviceMetadata ?? DeviceMetadataService(storage: storage);

  static const _sessionKey = 'identity.session.v1';

  final ApiClient _api;
  final FlutterSecureStorage _storage;
  final DeviceMetadataService _deviceMetadata;

  Future<AuthSession> login(String login, String password) async =>
      _authenticate('/api/v1/auth/login', {
        'login': login.trim(),
        'password': password,
        'device': await _device(),
      });

  Future<AuthSession> register({
    required String displayName,
    required String? email,
    required String? mobile,
    required String password,
  }) async => _authenticate('/api/v1/auth/register', {
    'displayName': displayName.trim(),
    'email': _optional(email),
    'mobile': _optional(mobile),
    'password': password,
    'device': await _device(),
  });

  /// Sends a mobile OTP for the OTP-first sign-in. The purpose is always
  /// Registration so the backend can decide the outcome after verification:
  /// an existing active user of any role is authenticated directly, while a
  /// previously unknown verified mobile continues into customer onboarding.
  /// Returns the MSG91 request id (reqId) that must accompany the subsequent
  /// verification call. The reqId is only held in memory on the client — it is
  /// never persisted.
  Future<String> sendOtp(String mobile) async {
    final response = await _api.post(
      '/api/v1/auth/send-otp',
      body: {'mobile': mobile.trim(), 'purpose': 1},
    );
    return _readReqId(response);
  }

  /// Requests a fresh OTP for a previously sent challenge. The backend resolves
  /// the stored challenge by mobile number + purpose and returns a new reqId,
  /// which replaces the current one for the verification call.
  Future<String> retryOtp(String mobile) async {
    final response = await _api.post(
      '/api/v1/auth/retry-otp',
      body: {'mobile': mobile.trim(), 'purpose': 1},
    );
    return _readReqId(response);
  }

  /// Verifies the mobile OTP. The server is authoritative on identity: the
  /// outcome is either an authenticated session for an existing user of any
  /// role, or a `requiresOnboarding` result for a verified mobile with no
  /// account yet. No session is persisted in the onboarding case.
  Future<OtpVerificationResult> verifyOtp(
    String mobile,
    String code,
    String reqId,
  ) async {
    final response = await _api.post(
      '/api/v1/auth/verify-otp',
      body: {
        'mobile': mobile.trim(),
        'code': code.trim(),
        'purpose': 1,
        'reqId': reqId,
        'device': await _device(),
      },
    );
    final data = response['data'] as Map<String, dynamic>;
    final requiresOnboarding = data['requiresOnboarding'] as bool? ?? false;
    final session = requiresOnboarding
        ? null
        : AuthSession.fromJson(data['session'] as Map<String, dynamic>);
    if (session != null) {
      await _save(session);
    }
    return OtpVerificationResult(
      requiresOnboarding: requiresOnboarding,
      session: session,
      verifiedMobile: data['verifiedMobile'] as String?,
      reqId: data['reqId'] as String?,
    );
  }

  /// Completes onboarding for a mobile that was verified via OTP but had no
  /// account. The backend creates the Customer with the verified mobile,
  /// sets the password, and returns a fresh session (persisted here), so the
  /// user lands authenticated in their role workspace.
  Future<AuthSession> completeOtpRegistration({
    required String mobile,
    required String reqId,
    required String newPassword,
  }) async {
    final response = await _api.post(
      '/api/v1/auth/otp/complete-onboarding',
      body: {
        'mobile': mobile.trim(),
        'reqId': reqId,
        'newPassword': newPassword,
        'device': await _device(),
      },
    );
    final session = AuthSession.fromJson(
      response['data'] as Map<String, dynamic>,
    );
    await _save(session);
    return session;
  }

  /// Sets a password on an account that currently has none (e.g. created via
  /// OTP login). The endpoint returns an empty body, so the updated profile is
  /// re-fetched from `/me` and merged into the current session, which is
  /// persisted so the app immediately reflects the new `hasPassword` capability.
  Future<AuthSession> setPassword(
    AuthSession session,
    String newPassword,
  ) async {
    await _api.post(
      '/api/v1/auth/password/set',
      body: {'newPassword': newPassword},
      accessToken: session.accessToken,
    );
    return _withUser(session, await currentUser(session));
  }

  /// Changes the password after verifying the current one. The backend signs
  /// out every other session but keeps the current one active, so no session
  /// refresh is required here.
  Future<void> changePassword(
    AuthSession session, {
    required String currentPassword,
    required String newPassword,
  }) async {
    await _api.post(
      '/api/v1/auth/password/change',
      body: {
        'currentPassword': currentPassword,
        'newPassword': newPassword,
      },
      accessToken: session.accessToken,
    );
  }

  /// Requests a password-reset OTP for the account's canonical mobile number.
  Future<String> forgotPassword(String mobile) async {
    final response = await _api.post(
      '/api/v1/auth/password/forgot',
      body: {'mobile': mobile.trim()},
    );
    return _readReqId(response);
  }

  /// Verifies the password-reset OTP without creating a session.
  Future<void> verifyResetOtp({
    required String destination,
    required String code,
    required String reqId,
  }) async {
    await _api.post(
      '/api/v1/auth/password/reset/verify-otp',
      body: {
        'destination': destination.trim(),
        'code': code.trim(),
        'reqId': reqId,
        'device': await _device(),
      },
    );
  }

  /// Resets the password after the server has verified the password-reset OTP.
  /// The final request deliberately contains no OTP code.
  Future<AuthSession> resetPassword({
    required String mobile,
    required String reqId,
    required String newPassword,
  }) async => _authenticate('/api/v1/auth/password/reset', {
    'mobile': mobile.trim(),
    'reqId': reqId,
    'newPassword': newPassword,
    'device': await _device(),
  });

  /// Stages a new (or first) email for confirmation and sends an
  /// [EmailVerification] OTP to it. Returns the reqId for [verifyEmailChange].
  Future<EmailChangeRequested> requestEmailChange(
    AuthSession session,
    String newEmail,
  ) async {
    final response = await _api.post(
      '/api/v1/auth/email/request-change',
      body: {'newEmail': newEmail.trim()},
      accessToken: session.accessToken,
    );
    final data = response['data'] as Map<String, dynamic>;
    return EmailChangeRequested(
      reqId: data['reqId'] as String,
      pendingEmail: data['pendingEmail'] as String,
    );
  }

  /// Verifies the [EmailVerification] OTP and commits the staged pending email.
  /// The endpoint returns the updated [AuthUser] (not a full session), so it is
  /// merged into the current session and persisted.
  Future<AuthSession> verifyEmailChange(
    AuthSession session,
    String code,
    String reqId,
  ) async {
    final response = await _api.post(
      '/api/v1/auth/email/verify',
      body: {
        'code': code.trim(),
        'reqId': reqId,
        'device': await _device(),
      },
      accessToken: session.accessToken,
    );
    return _withUser(
      session,
      AuthUser.fromJson(response['data'] as Map<String, dynamic>),
    );
  }

  Future<MobileChangeRequested> requestMobileChange(
    AuthSession session,
    String newMobile,
  ) async {
    final response = await _api.post(
      '/api/v1/auth/mobile/request-change',
      body: {'newMobile': newMobile.trim()},
      accessToken: session.accessToken,
    );
    final data = response['data'] as Map<String, dynamic>;
    return MobileChangeRequested(
      reqId: data['reqId'] as String,
      pendingMobile: data['pendingMobile'] as String,
    );
  }

  Future<AuthSession> verifyMobileChange(
    AuthSession session,
    String mobile,
    String code,
    String reqId,
  ) async {
    final response = await _api.post(
      '/api/v1/auth/mobile/verify',
      body: {
        'mobile': mobile.trim(),
        'code': code.trim(),
        'reqId': reqId,
        'device': await _device(),
      },
      accessToken: session.accessToken,
    );
    return _withUser(
      session,
      AuthUser.fromJson(response['data'] as Map<String, dynamic>),
    );
  }

  Future<AuthSession?> restore() async {
    final encoded = await _storage.read(key: _sessionKey);
    if (encoded == null) return null;

    try {
      final session = AuthSession.fromStorage(
        jsonDecode(encoded) as Map<String, dynamic>,
      );
      if (!session.refreshTokenExpiresAtUtc.isAfter(DateTime.now().toUtc())) {
        await clear();
        return null;
      }
      return await refresh(session);
    } on Object {
      await clear();
      return null;
    }
  }

  Future<AuthSession> refresh(AuthSession session) async {
    final response = await _api.post(
      '/api/v1/auth/refresh',
      body: {'refreshToken': session.refreshToken, 'device': await _device()},
    );
    final refreshed = AuthSession.fromJson(
      response['data'] as Map<String, dynamic>,
    );
    await _save(refreshed);
    return refreshed;
  }

  Future<AuthUser> currentUser(AuthSession session) async {
    final response = await _api.get(
      '/api/v1/auth/me',
      accessToken: session.accessToken,
    );
    return AuthUser.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<void> logout(AuthSession session) async {
    try {
      await _api.post('/api/v1/auth/logout', accessToken: session.accessToken);
    } finally {
      await clear();
    }
  }

  Future<void> clear() => _storage.delete(key: _sessionKey);

  /// Persists a session produced outside the standard login/register flows — e.g. the session
  /// returned by the employee invitation completion endpoint. Used so an invited employee lands
  /// authenticated in their assigned role workspace.
  Future<void> saveSession(AuthSession session) => _save(session);

  Future<AuthSession> _authenticate(
    String path,
    Map<String, dynamic> body,
  ) async {
    final response = await _api.post(path, body: body);
    final session = AuthSession.fromJson(
      response['data'] as Map<String, dynamic>,
    );
    await _save(session);
    return session;
  }

  Future<void> _save(AuthSession session) =>
      _storage.write(key: _sessionKey, value: jsonEncode(session.toJson()));

  /// Replaces the user inside an existing session with [user] and persists the
  /// merged session. Used by endpoints that return only an updated [AuthUser]
  /// (e.g. password/set, email/verify) so the app keeps the same token pair
  /// while reflecting the new profile capabilities.
  Future<AuthSession> _withUser(AuthSession session, AuthUser user) async {
    final updated = AuthSession(
      user: user,
      accessToken: session.accessToken,
      refreshToken: session.refreshToken,
      accessTokenExpiresAtUtc: session.accessTokenExpiresAtUtc,
      refreshTokenExpiresAtUtc: session.refreshTokenExpiresAtUtc,
    );
    await _save(updated);
    return updated;
  }

  Future<Map<String, dynamic>> _device() async =>
      (await _deviceMetadata.get()).toJson();

  static String _readReqId(Map<String, dynamic> response) {
    final data = response['data'] as Map<String, dynamic>;
    return data['reqId'] as String;
  }

  static String? _optional(String? value) {
    final normalized = value?.trim();
    return normalized == null || normalized.isEmpty ? null : normalized;
  }
}
