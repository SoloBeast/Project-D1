import 'dart:async';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('AuthSession response parsing', () {
    test('parses the captured nested UTC-suffixed token contract', () {
      final session = AuthSession.fromJson(
        _loginData(
          accessTokenExpiresKey: 'accessTokenExpiresAtUtc',
          refreshTokenExpiresKey: 'refreshTokenExpiresAtUtc',
        ),
      );

      expect(session.user.primaryRole, UserRole.customer);
      expect(session.accessTokenExpiresAtUtc.isUtc, isTrue);
      expect(session.refreshTokenExpiresAtUtc.isUtc, isTrue);
      expect(session.accessTokenExpiresAtUtc, DateTime.utc(2026, 8, 19, 22, 2));
      expect(
        session.refreshTokenExpiresAtUtc,
        DateTime.utc(2026, 9, 18, 22, 29, 37, 962, 953),
      );
    });

    test('accepts the current unsuffixed backend expiry names', () {
      final session = AuthSession.fromJson(
        _loginData(
          accessTokenExpiresKey: 'accessTokenExpiresAt',
          refreshTokenExpiresKey: 'refreshTokenExpiresAt',
        ),
      );

      expect(session.accessTokenExpiresAtUtc.isUtc, isTrue);
      expect(session.refreshTokenExpiresAtUtc.isUtc, isTrue);
    });

    test('serializes protocol expiry values with explicit UTC keys', () {
      final session = AuthSession.fromJson(
        _loginData(
          accessTokenExpiresKey: 'accessTokenExpiresAt',
          refreshTokenExpiresKey: 'refreshTokenExpiresAt',
        ),
      );

      final storage = session.toJson();
      expect(
        storage.keys,
        containsAll(['accessTokenExpiresAtUtc', 'refreshTokenExpiresAtUtc']),
      );
      expect(storage['accessTokenExpiresAtUtc'], endsWith('Z'));
      expect(storage['refreshTokenExpiresAtUtc'], endsWith('Z'));
    });
  });

  test('login success transitions session state to authenticated', () async {
    final container = ProviderContainer(
      overrides: [
        authRepositoryProvider.overrideWithValue(_SuccessfulAuthRepository()),
      ],
    );
    addTearDown(container.dispose);

    final controller = container.read(sessionControllerProvider.notifier);
    final result = await controller.login('customer@example.test', 'password');

    expect(result, isTrue);
    final state = container.read(sessionControllerProvider);
    expect(state.isAuthenticated, isTrue);
    expect(state.role, UserRole.customer);
    expect(state.session?.accessToken, isNotNull);
  });
  test(
    'successful refresh updates the active session and deduplicates callers',
    () async {
      final repository = _RefreshAuthRepository();
      final container = ProviderContainer(
        overrides: [authRepositoryProvider.overrideWithValue(repository)],
      );
      addTearDown(container.dispose);

      final controller = container.read(sessionControllerProvider.notifier);
      await controller.login('customer@example.test', 'password');
      final first = controller.refreshAccessToken();
      final second = controller.refreshAccessToken();
      repository.completeRefresh(_refreshedSession);

      expect(await Future.wait([first, second]), [
        'refreshed-access-token',
        'refreshed-access-token',
      ]);
      expect(repository.refreshCalls, 1);
      expect(
        container.read(sessionControllerProvider).session?.accessToken,
        'refreshed-access-token',
      );
    },
  );

  test(
    'refresh 401 clears credentials and transitions to unauthenticated',
    () async {
      final repository = _RefreshAuthRepository(
        refreshError: const ApiException(
          401,
          'AUTHENTICATION_REQUIRED',
          'Refresh token is invalid.',
        ),
      );
      final container = ProviderContainer(
        overrides: [authRepositoryProvider.overrideWithValue(repository)],
      );
      addTearDown(container.dispose);

      final controller = container.read(sessionControllerProvider.notifier);
      await controller.login('customer@example.test', 'password');
      expect(await controller.refreshAccessToken(), isNull);

      final state = container.read(sessionControllerProvider);
      expect(state.isAuthenticated, isFalse);
      expect(state.errorMessage, 'Your session expired. Sign in again.');
      expect(repository.clearCalls, 1);
    },
  );
}

Map<String, dynamic> _loginData({
  required String accessTokenExpiresKey,
  required String refreshTokenExpiresKey,
}) => {
  'user': {
    'publicUserId': 'customer-1',
    'displayName': 'Customer User',
    'email': 'customer@example.test',
    'mobile': null,
    'roles': ['CUSTOMER'],
    'permissions': [],
    'branchIds': [],
  },
  'tokens': {
    'accessToken': 'access-token-test-value',
    'refreshToken': 'refresh-token-test-value',
    accessTokenExpiresKey: '2026-08-20T03:32:00+05:30',
    refreshTokenExpiresKey: '2026-09-18T22:29:37.9629532Z',
  },
};

class _RefreshAuthRepository extends AuthRepository {
  _RefreshAuthRepository({this.refreshError});

  final ApiException? refreshError;
  final _refreshCompleter = Completer<AuthSession>();
  int refreshCalls = 0;
  int clearCalls = 0;

  @override
  Future<AuthSession?> restore() async => null;

  @override
  Future<AuthSession> login(String login, String password) async => _session;

  @override
  Future<AuthSession> refresh(AuthSession session) {
    refreshCalls++;
    if (refreshError != null) return Future<AuthSession>.error(refreshError!);
    return _refreshCompleter.future;
  }

  @override
  Future<void> clear() async {
    clearCalls++;
  }

  void completeRefresh(AuthSession session) =>
      _refreshCompleter.complete(session);
}

final _session = AuthSession.fromJson(
  _loginData(
    accessTokenExpiresKey: 'accessTokenExpiresAtUtc',
    refreshTokenExpiresKey: 'refreshTokenExpiresAtUtc',
  ),
);

final _refreshedSession = AuthSession(
  user: _session.user,
  accessToken: 'refreshed-access-token',
  refreshToken: 'refreshed-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2026, 8, 20, 4),
  refreshTokenExpiresAtUtc: DateTime.utc(2026, 9, 19, 4),
);

class _SuccessfulAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;

  @override
  Future<AuthSession> login(String login, String password) async =>
      AuthSession.fromJson(
        _loginData(
          accessTokenExpiresKey: 'accessTokenExpiresAtUtc',
          refreshTokenExpiresKey: 'refreshTokenExpiresAtUtc',
        ),
      );
}
