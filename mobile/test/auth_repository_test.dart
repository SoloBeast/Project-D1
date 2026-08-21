import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('AuthSession response parsing', () {
    test('parses the captured nested UTC-suffixed token contract', () {
      final session = AuthSession.fromJson(_loginData(
        accessTokenExpiresKey: 'accessTokenExpiresAtUtc',
        refreshTokenExpiresKey: 'refreshTokenExpiresAtUtc',
      ));

      expect(session.user.primaryRole, UserRole.customer);
      expect(session.accessTokenExpiresAtUtc.isUtc, isTrue);
      expect(session.refreshTokenExpiresAtUtc.isUtc, isTrue);
      expect(
        session.accessTokenExpiresAtUtc,
        DateTime.utc(2026, 8, 19, 22, 2),
      );
      expect(
        session.refreshTokenExpiresAtUtc,
        DateTime.utc(2026, 9, 18, 22, 29, 37, 962, 953),
      );
    });

    test('accepts the current unsuffixed backend expiry names', () {
      final session = AuthSession.fromJson(_loginData(
        accessTokenExpiresKey: 'accessTokenExpiresAt',
        refreshTokenExpiresKey: 'refreshTokenExpiresAt',
      ));

      expect(session.accessTokenExpiresAtUtc.isUtc, isTrue);
      expect(session.refreshTokenExpiresAtUtc.isUtc, isTrue);
    });

    test('serializes protocol expiry values with explicit UTC keys', () {
      final session = AuthSession.fromJson(_loginData(
        accessTokenExpiresKey: 'accessTokenExpiresAt',
        refreshTokenExpiresKey: 'refreshTokenExpiresAt',
      ));

      final storage = session.toJson();
      expect(storage.keys, containsAll([
        'accessTokenExpiresAtUtc',
        'refreshTokenExpiresAtUtc',
      ]));
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

class _SuccessfulAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;

  @override
  Future<AuthSession> login(String login, String password) async =>
      AuthSession.fromJson(_loginData(
        accessTokenExpiresKey: 'accessTokenExpiresAtUtc',
        refreshTokenExpiresKey: 'refreshTokenExpiresAtUtc',
      ));
}
