import 'dart:convert';
import 'dart:math';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:flutter/foundation.dart';
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

class AuthUser {
  const AuthUser({
    required this.publicUserId,
    required this.displayName,
    required this.email,
    required this.mobile,
    required this.roles,
    required this.permissions,
    required this.branchIds,
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
  );

  final String publicUserId;
  final String? displayName;
  final String? email;
  final String? mobile;
  final List<String> roles;
  final List<String> permissions;
  final List<int> branchIds;

  UserRole get primaryRole => roleFromCodes(roles);

  Map<String, dynamic> toJson() => {
    'publicUserId': publicUserId,
    'displayName': displayName,
    'email': email,
    'mobile': mobile,
    'roles': roles,
    'permissions': permissions,
    'branchIds': branchIds,
  };
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
      accessTokenExpiresAtUtc: DateTime.parse(
        tokens['accessTokenExpiresAtUtc'] as String,
      ).toUtc(),
      refreshTokenExpiresAtUtc: DateTime.parse(
        tokens['refreshTokenExpiresAtUtc'] as String,
      ).toUtc(),
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
}

class AuthRepository {
  AuthRepository({ApiClient? api, FlutterSecureStorage? storage})
    : _api = api ?? ApiClient(baseUrl: apiBaseUrl),
      _storage = storage ?? const FlutterSecureStorage();

  static const _sessionKey = 'identity.session.v1';
  static const _deviceKey = 'identity.device-id.v1';

  final ApiClient _api;
  final FlutterSecureStorage _storage;

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

  Future<void> sendOtp(String mobile, {required bool registration}) async {
    await _api.post(
      '/api/v1/auth/send-otp',
      body: {'mobile': mobile.trim(), 'purpose': registration ? 1 : 0},
    );
  }

  Future<AuthSession> verifyOtp(
    String mobile,
    String code, {
    required bool registration,
  }) async => _authenticate('/api/v1/auth/verify-otp', {
    'mobile': mobile.trim(),
    'code': code.trim(),
    'purpose': registration ? 1 : 0,
    'device': await _device(),
  });

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

  Future<Map<String, dynamic>> _device() async {
    var identifier = await _storage.read(key: _deviceKey);
    if (identifier == null) {
      final random = Random.secure();
      identifier = List.generate(
        32,
        (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
      ).join();
      await _storage.write(key: _deviceKey, value: identifier);
    }
    return {
      'deviceIdentifier': identifier,
      'deviceName': 'DoodhDirect Flutter',
      'platform': kIsWeb ? 'web' : defaultTargetPlatform.name,
    };
  }

  static String? _optional(String? value) {
    final normalized = value?.trim();
    return normalized == null || normalized.isEmpty ? null : normalized;
  }
}
