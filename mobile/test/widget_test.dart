import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:flutter/material.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('unauthenticated session has no role', () {
    const session = SessionState.unauthenticated();

    expect(session.isAuthenticated, isFalse);
    expect(session.role, isNull);
  });

  test('canonical role codes map to role-aware navigation', () {
    expect(roleFromCodes(['CUSTOMER']).label, 'Customer');
    expect(roleFromCodes(['DELIVERY_STAFF']).label, 'Delivery');
    expect(roleFromCodes(['SYSTEM_ADMIN']).label, 'Admin');
    expect(roleFromCodes(['OWNER', 'CUSTOMER']).label, 'Owner');
  });

  testWidgets(
    'password login routes to server-derived workspace and logs out',
    (tester) async {
      final repository = _FakeAuthRepository();
      await tester.pumpWidget(
        ProviderScope(
          overrides: [authRepositoryProvider.overrideWithValue(repository)],
          child: const DoodhDirectApp(),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Sign in to your account'), findsOneWidget);
      await tester.enterText(
        find.byType(EditableText).at(0),
        'delivery@example.test',
      );
      await tester.enterText(
        find.byType(EditableText).at(1),
        'correct-password',
      );
      await tester.tap(find.text('Sign in'));
      await tester.pumpAndSettle();

      expect(repository.lastLogin, 'delivery@example.test');
      expect(find.text('Delivery workspace'), findsOneWidget);
      expect(find.text('Delivery workspace ready'), findsOneWidget);

      await tester.tap(find.byTooltip('Sign out'));
      await tester.pumpAndSettle();

      expect(repository.loggedOut, isTrue);
      expect(find.text('Sign in to your account'), findsOneWidget);
    },
  );
}

class _FakeAuthRepository extends AuthRepository {
  String? lastLogin;
  bool loggedOut = false;

  @override
  Future<AuthSession?> restore() async => null;

  @override
  Future<AuthSession> login(String login, String password) async {
    lastLogin = login;
    return _session;
  }

  @override
  Future<void> logout(AuthSession session) async {
    loggedOut = true;
  }

  static final _session = AuthSession(
    user: const AuthUser(
      publicUserId: '00000000-0000-0000-0000-000000000001',
      displayName: 'Delivery User',
      email: 'delivery@example.test',
      mobile: null,
      roles: ['DELIVERY_STAFF'],
      permissions: ['IDENTITY.BRANCH.ACCESS'],
      branchIds: [1],
    ),
    accessToken: 'access-token',
    refreshToken: 'refresh-token',
    accessTokenExpiresAtUtc: DateTime.utc(2099),
    refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
  );
}
