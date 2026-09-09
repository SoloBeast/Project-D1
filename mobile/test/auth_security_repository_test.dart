import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('AuthRepository password flows', () {
    test('setPassword posts the new password and refreshes the profile', () async {
      final requests = <http.Request>[];
      final storage = _FakeFlutterSecureStorage();
      final repository = _repository(
        (request) async {
          requests.add(request);
          if (request.url.path == '/api/v1/auth/password/set') {
            return http.Response('', 200, headers: _jsonHeaders);
          }
          if (request.url.path == '/api/v1/auth/me') {
            return http.Response(
              jsonEncode(_meResponse(hasPassword: true)),
              200,
              headers: _jsonHeaders,
            );
          }
          return http.Response('{"success":false}', 500);
        },
        storage: storage,
      );

      final updated = await repository.setPassword(_session, 'NewPassw0rd!');

      expect(requests, hasLength(2));
      expect(requests[0].method, 'POST');
      expect(requests[0].url.path, '/api/v1/auth/password/set');
      expect(requests[0].headers['authorization'], contains(_session.accessToken));
      expect(jsonDecode(requests[0].body), {'newPassword': 'NewPassw0rd!'});
      expect(requests[1].url.path, '/api/v1/auth/me');
      expect(updated.user.hasPassword, isTrue);
      expect(updated.accessToken, _session.accessToken);
      expect(
        jsonDecode(storage.values['identity.session.v1']!)['user']['hasPassword'],
        isTrue,
      );
    });

    test('changePassword posts current+new password with the access token', () async {
      late http.Request captured;
      final repository = _repository((request) async {
        captured = request;
        return http.Response('', 200, headers: _jsonHeaders);
      });

      await repository.changePassword(
        _session,
        currentPassword: 'OldPassw0rd',
        newPassword: 'NewPassw0rd!',
      );

      expect(captured.method, 'POST');
      expect(captured.url.path, '/api/v1/auth/password/change');
      expect(captured.headers['authorization'], contains(_session.accessToken));
      expect(jsonDecode(captured.body), {
        'currentPassword': 'OldPassw0rd',
        'newPassword': 'NewPassw0rd!',
      });
    });

    test('forgotPassword trims the mobile and returns the reqId', () async {
      late http.Request captured;
      final repository = _repository((request) async {
        captured = request;
        return http.Response(
          jsonEncode(_reqIdResponse('req-reset-1')),
          200,
          headers: _jsonHeaders,
        );
      });

      final reqId = await repository.forgotPassword('  +919999000021  ');

      expect(reqId, 'req-reset-1');
      expect(captured.method, 'POST');
      expect(captured.url.path, '/api/v1/auth/password/forgot');
      expect(jsonDecode(captured.body), {'mobile': '+919999000021'});
    });

    test('verifyResetOtp posts destination, code, reqId and device', () async {
      late http.Request captured;
      final repository = _repository((request) async {
        captured = request;
        return http.Response('', 200, headers: _jsonHeaders);
      });

      await repository.verifyResetOtp(
        destination: ' user@example.test ',
        code: ' 123456 ',
        reqId: 'req-reset-1',
      );

      expect(captured.method, 'POST');
      expect(captured.url.path, '/api/v1/auth/password/reset/verify-otp');
      final body = jsonDecode(captured.body) as Map<String, dynamic>;
      expect(body['destination'], 'user@example.test');
      expect(body['code'], '123456');
      expect(body['reqId'], 'req-reset-1');
      expect(body['device'], isA<Map<String, dynamic>>());
    });

    test('resetPassword sends no OTP code after server verification',
        () async {
      late http.Request captured;
      final storage = _FakeFlutterSecureStorage();
      final repository = _repository(
        (request) async {
          captured = request;
          return http.Response(
            jsonEncode(_sessionResponse()),
            200,
            headers: _jsonHeaders,
          );
        },
        storage: storage,
      );

      final session = await repository.resetPassword(
        mobile: ' +919999000021 ',
        reqId: 'req-reset-1',
        newPassword: 'NewPassw0rd!',
      );

      expect(session.accessToken, 'access-token-test-value');
      expect(captured.method, 'POST');
      expect(captured.url.path, '/api/v1/auth/password/reset');
      final body = jsonDecode(captured.body) as Map<String, dynamic>;
      expect(body['mobile'], '+919999000021');
      expect(body.containsKey('code'), isFalse);
      expect(body['reqId'], 'req-reset-1');
      expect(body['newPassword'], 'NewPassw0rd!');
      expect(body['device'], isA<Map<String, dynamic>>());
      expect(storage.values['identity.session.v1'], isNotNull);
    });
  });

  group('AuthRepository email flows', () {
    test('requestEmailChange posts the new email and returns reqId + pending',
        () async {
      late http.Request captured;
      final repository = _repository((request) async {
        captured = request;
        return http.Response(
          jsonEncode(_emailChangeResponse('req-email-1')),
          200,
          headers: _jsonHeaders,
        );
      });

      final result = await repository.requestEmailChange(
        _session,
        ' new@example.test ',
      );

      expect(result.reqId, 'req-email-1');
      expect(result.pendingEmail, 'new@example.test');
      expect(captured.method, 'POST');
      expect(captured.url.path, '/api/v1/auth/email/request-change');
      expect(captured.headers['authorization'], contains(_session.accessToken));
      expect(jsonDecode(captured.body), {'newEmail': 'new@example.test'});
    });

    test('verifyEmailChange posts code+reqId and merges the returned user',
        () async {
      final requests = <http.Request>[];
      final storage = _FakeFlutterSecureStorage();
      final repository = _repository(
        (request) async {
          requests.add(request);
          return http.Response(
            jsonEncode(_meResponse(email: 'new@example.test', emailVerified: true)),
            200,
            headers: _jsonHeaders,
          );
        },
        storage: storage,
      );

      final updated = await repository.verifyEmailChange(
        _session,
        '123456',
        'req-email-1',
      );

      expect(requests, hasLength(1));
      final captured = requests.single;
      expect(captured.method, 'POST');
      expect(captured.url.path, '/api/v1/auth/email/verify');
      expect(captured.headers['authorization'], contains(_session.accessToken));
      final body = jsonDecode(captured.body) as Map<String, dynamic>;
      expect(body['code'], '123456');
      expect(body['reqId'], 'req-email-1');
      expect(body['device'], isA<Map<String, dynamic>>());
      expect(updated.user.email, 'new@example.test');
      expect(updated.user.emailVerified, isTrue);
      expect(updated.accessToken, _session.accessToken);
      final stored =
          jsonDecode(storage.values['identity.session.v1']!)
              as Map<String, dynamic>;
      expect(stored['user']['email'], 'new@example.test');
    });
  });
}

AuthRepository _repository(
  MockClientHandler handler, {
  FlutterSecureStorage? storage,
}) {
  final fakeStorage = storage ?? _FakeFlutterSecureStorage();
  return AuthRepository(
    api: ApiClient(
      client: MockClient(handler),
      baseUrl: 'https://api.example.test',
    ),
    storage: fakeStorage,
  );
}

const _jsonHeaders = {'content-type': 'application/json'};

Map<String, dynamic> _reqIdResponse(String reqId) => {
  'success': true,
  'data': {'reqId': reqId},
  'errors': <dynamic>[],
};

Map<String, dynamic> _emailChangeResponse(String reqId) => {
  'success': true,
  'data': {'reqId': reqId, 'pendingEmail': 'new@example.test'},
  'errors': <dynamic>[],
};

Map<String, dynamic> _sessionResponse() => {
  'success': true,
  'data': {
    'user': _userJson(),
    'tokens': {
      'accessToken': 'access-token-test-value',
      'refreshToken': 'refresh-token-test-value',
      'accessTokenExpiresAtUtc': '2026-08-20T03:32:00+05:30',
      'refreshTokenExpiresAtUtc': '2026-09-18T22:29:37.9629532Z',
    },
  },
  'errors': <dynamic>[],
};

Map<String, dynamic> _meResponse({
  String? email,
  bool emailVerified = false,
  bool hasPassword = false,
}) => {
  'success': true,
  'data': _userJson(
    email: email,
    emailVerified: emailVerified,
    hasPassword: hasPassword,
  ),
  'errors': <dynamic>[],
};

Map<String, dynamic> _userJson({
  String? email,
  bool emailVerified = false,
  bool hasPassword = false,
}) => {
  'publicUserId': 'customer-1',
  'displayName': 'Customer User',
  'email': email ?? 'customer@example.test',
  'mobile': '9876543210',
  'roles': ['CUSTOMER'],
  'permissions': <dynamic>[],
  'branchIds': <dynamic>[],
  'emailVerified': emailVerified,
  'hasPassword': hasPassword,
};

final _session = AuthSession.fromJson(
  _sessionResponse()['data'] as Map<String, dynamic>,
);

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
