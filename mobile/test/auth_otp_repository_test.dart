import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('AuthRepository OTP reqId plumbing', () {
    test(
      'sendOtp posts the mobile with the registration purpose and returns the MSG91 reqId',
      () async {
        late http.Request captured;
        final repository = _repository((request) async {
          captured = request;
          return http.Response(
            jsonEncode(_otpResponse(reqId: 'req-send-1')),
            200,
            headers: _jsonHeaders,
          );
        });

        final reqId = await repository.sendOtp(' 9876543210 ');

        expect(reqId, 'req-send-1');
        expect(captured.method, 'POST');
        expect(captured.url.path, '/api/v1/auth/send-otp');
        expect(jsonDecode(captured.body), {
          'mobile': '9876543210',
          'purpose': 1,
        });
      },
    );

    test(
      'retryOtp posts to retry-otp with the registration purpose and returns a fresh reqId',
      () async {
        late http.Request captured;
        final repository = _repository((request) async {
          captured = request;
          return http.Response(
            jsonEncode(_otpResponse(reqId: 'req-retry-9')),
            200,
            headers: _jsonHeaders,
          );
        });

        final reqId = await repository.retryOtp('9876543210');

        expect(reqId, 'req-retry-9');
        expect(captured.method, 'POST');
        expect(captured.url.path, '/api/v1/auth/retry-otp');
        expect(jsonDecode(captured.body), {
          'mobile': '9876543210',
          'purpose': 1,
        });
      },
    );

    test(
      'verifyOtp sends the reqId and authenticates an existing user of any role',
      () async {
        late http.Request captured;
        final repository = _repository((request) async {
          captured = request;
          return http.Response(
            jsonEncode(_verifyResponse()),
            200,
            headers: _jsonHeaders,
          );
        });

        final result = await repository.verifyOtp(
          '9876543210',
          '123456',
          'req-send-1',
        );

        expect(result.requiresOnboarding, isFalse);
        expect(result.session, isNotNull);
        expect(result.session!.accessToken, 'access-token-test-value');
        expect(result.verifiedMobile, '+919876543210');
        expect(result.reqId, 'req-send-1');
        expect(captured.method, 'POST');
        expect(captured.url.path, '/api/v1/auth/verify-otp');
        final body = jsonDecode(captured.body) as Map<String, dynamic>;
        expect(body['mobile'], '9876543210');
        expect(body['code'], '123456');
        expect(body['purpose'], 1);
        expect(body['reqId'], 'req-send-1');
        expect(body['device'], isA<Map<String, dynamic>>());
      },
    );

    test('verifyOtp persists the session to secure storage', () async {
      final storage = _FakeFlutterSecureStorage();
      final repository = _repository(
        (request) async => http.Response(
          jsonEncode(_verifyResponse()),
          200,
          headers: _jsonHeaders,
        ),
        storage: storage,
      );

      final result = await repository.verifyOtp(
        '9876543210',
        '123456',
        'req-send-1',
      );

      expect(result.requiresOnboarding, isFalse);
      expect(result.session, isNotNull);
      final saved = storage.values['identity.session.v1'];
      expect(saved, isNotNull);
      final stored = jsonDecode(saved!) as Map<String, dynamic>;
      expect(stored['accessToken'], 'access-token-test-value');
    });

    test(
      'verifyOtp reports onboarding for a verified mobile with no account and does not persist a session',
      () async {
        final storage = _FakeFlutterSecureStorage();
        final repository = _repository(
          (request) async => http.Response(
            jsonEncode(
              _verifyResponse(requiresOnboarding: true, reqId: 'req-onboard-7'),
            ),
            200,
            headers: _jsonHeaders,
          ),
          storage: storage,
        );

        final result = await repository.verifyOtp(
          '9876543210',
          '123456',
          'req-onboard-7',
        );

        expect(result.requiresOnboarding, isTrue);
        expect(result.session, isNull);
        expect(result.verifiedMobile, '+919876543210');
        expect(result.reqId, 'req-onboard-7');
        expect(storage.values['identity.session.v1'], isNull);
      },
    );

    test(
      'completeOtpRegistration posts the verified mobile and creates a persisted customer session',
      () async {
        late http.Request captured;
        final storage = _FakeFlutterSecureStorage();
        final repository = _repository(
          (request) async {
            captured = request;
            return http.Response(
              jsonEncode(_onboardingResponse()),
              200,
              headers: _jsonHeaders,
            );
          },
          storage: storage,
        );

        final session = await repository.completeOtpRegistration(
          mobile: '+919876543210',
          reqId: 'req-onboard-7',
          newPassword: 'new-password',
        );

        expect(session.accessToken, 'access-token-test-value');
        expect(captured.method, 'POST');
        expect(captured.url.path, '/api/v1/auth/otp/complete-onboarding');
        final body = jsonDecode(captured.body) as Map<String, dynamic>;
        expect(body['mobile'], '+919876543210');
        expect(body['reqId'], 'req-onboard-7');
        expect(body['newPassword'], 'new-password');
        expect(body['device'], isA<Map<String, dynamic>>());
        final saved = storage.values['identity.session.v1'];
        expect(saved, isNotNull);
        expect(
          (jsonDecode(saved!) as Map<String, dynamic>)['accessToken'],
          'access-token-test-value',
        );
      },
    );
  });
}

AuthRepository _repository(
  MockClientHandler handler, {
  FlutterSecureStorage? storage,
}) {
  final fakeStorage = storage ?? _FakeFlutterSecureStorage();
  return AuthRepository(
    api: ApiClient(client: MockClient(handler), baseUrl: 'https://api.example.test'),
    storage: fakeStorage,
  );
}

const _jsonHeaders = {'content-type': 'application/json'};

Map<String, dynamic> _otpResponse({required String reqId}) => {
  'success': true,
  'data': {'reqId': reqId},
  'errors': <dynamic>[],
};

/// Envelope the verify-otp endpoint returns in `data`: a server-authoritative
/// decision on whether the verified mobile maps to an existing account
/// (session nested) or to onboarding for a previously unknown mobile.
Map<String, dynamic> _verifyResponse({
  bool requiresOnboarding = false,
  String reqId = 'req-send-1',
}) =>
    {
      'success': true,
      'data': {
        'requiresOnboarding': requiresOnboarding,
        if (!requiresOnboarding) 'session': _sessionData(),
        'verifiedMobile': '+919876543210',
        'reqId': reqId,
      },
      'errors': <dynamic>[],
    };

/// Envelope returned by the onboarding-completion endpoint: an authenticated
/// session under `data` once the Customer account is created.
Map<String, dynamic> _onboardingResponse() => {
  'success': true,
  'data': _sessionData(),
  'errors': <dynamic>[],
};

Map<String, dynamic> _sessionData() => {
  'user': {
    'publicUserId': 'customer-1',
    'displayName': 'Customer User',
    'email': 'customer@example.test',
    'mobile': '+919876543210',
    'roles': ['CUSTOMER'],
    'permissions': <dynamic>[],
    'branchIds': <dynamic>[],
  },
  'tokens': {
    'accessToken': 'access-token-test-value',
    'refreshToken': 'refresh-token-test-value',
    'accessTokenExpiresAtUtc': '2026-08-20T03:32:00+05:30',
    'refreshTokenExpiresAtUtc': '2026-09-18T22:29:37.9629532Z',
  },
};

/// In-memory secure storage sharing v9.2.4 write/read/delete semantics
/// (writing null deletes the key).
class _FakeFlutterSecureStorage extends FlutterSecureStorage {
  final Map<String, String> values = {};

  @override
  Future<void> write({
    required String key,
    required String? value,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    if (value == null) {
      values.remove(key);
    } else {
      values[key] = value;
    }
  }

  @override
  Future<String?> read({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => values[key];

  @override
  Future<bool> containsKey({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => values.containsKey(key);

  @override
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    values.remove(key);
  }
}
