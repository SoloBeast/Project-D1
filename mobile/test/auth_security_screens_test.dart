import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/security_screens.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('LoginSecurityScreen', () {
    testWidgets('shows email, verification status, and password state', (
      tester,
    ) async {
      await _pump(tester, _user(hasPassword: true));

      expect(find.text('Login & security'), findsOneWidget);
      expect(find.text('customer@example.test'), findsOneWidget);
      expect(find.text('Verified'), findsOneWidget);
      expect(find.text('Configure Password'), findsOneWidget);
      expect(find.text('Password is set'), findsNothing);
      expect(
        find.text('Change it any time to keep your account secure.'),
        findsOneWidget,
      );
      // No separate trailing button on the card.
      expect(find.widgetWithText(FilledButton, 'Change password'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Set password'), findsNothing);
      expect(find.text('Add email'), findsNothing);
    });

    testWidgets('tapping the Configure Password card opens the change dialog', (
      tester,
    ) async {
      await _pump(tester, _user(hasPassword: true));

      await tester.tap(find.text('Configure Password'));
      await tester.pumpAndSettle();

      expect(find.byType(AlertDialog), findsOneWidget);
      expect(find.text('Current password'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Change password'), findsOneWidget);
    });

    testWidgets('shows unverified email status', (tester) async {
      await _pump(
        tester,
        _user(email: 'customer@example.test', emailVerified: false),
      );

      expect(
        find.text('Not yet verified — verify to secure your account.'),
        findsOneWidget,
      );
      expect(find.text('Verified'), findsNothing);
    });

    testWidgets('shows pending email verification card', (tester) async {
      await _pump(
        tester,
        _user(
          email: 'customer@example.test',
          pendingEmail: 'new@example.test',
        ),
      );

      expect(find.text('Verify pending email'), findsOneWidget);
      expect(find.text('Pending confirmation: new@example.test'), findsOneWidget);
    });

    testWidgets('shows add-email state when no email exists', (tester) async {
      await _pump(tester, _user(email: null));

      expect(find.text('No email address'), findsOneWidget);
      expect(
        find.text('Add an email to receive account notifications.'),
        findsOneWidget,
      );
      expect(find.text('No password set'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Set password'), findsNothing);
      expect(find.text('Change password'), findsNothing);
    });
  });

  group('EmailChangeScreen', () {
    testWidgets('starts in address mode for a verified email and requests a code',
        (tester) async {
      await _pump(
        tester,
        _user(email: 'customer@example.test'),
        screen: _Screen.emailChange,
      );

      expect(find.text('Change email'), findsOneWidget);
      expect(find.text('Send verification code'), findsOneWidget);

      await tester.enterText(find.byType(TextFormField), 'new@example.test');
      await tester.pump();
      await tester.tap(find.text('Send verification code'));
      await tester.pumpAndSettle();

      expect(find.text('Verify your email'), findsOneWidget);
      expect(
        find.text('Enter the 6-digit code sent to new@example.test.'),
        findsOneWidget,
      );
    });

    testWidgets('starts in verification mode for an unverified email', (
      tester,
    ) async {
      await _pump(
        tester,
        _user(email: 'customer@example.test', emailVerified: false),
        screen: _Screen.emailChange,
      );

      expect(find.text('Verify your email'), findsOneWidget);
      expect(find.text('Send a new code'), findsOneWidget);
    });

    testWidgets('verifies the code and pops back', (tester) async {
      await _pump(
        tester,
        _user(email: 'customer@example.test', emailVerified: false),
        screen: _Screen.emailChange,
      );

      // Verification mode starts with no in-memory reqId, so a fresh code must
      // be requested before verifying.
      await tester.tap(find.text('Send a new code'));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '123456');
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Verify email'));
      await tester.pumpAndSettle();

      // The resend SnackBar is still on screen; let it expire so the success
      // SnackBar queued behind it can display.
      await tester.pump(const Duration(seconds: 5));
      await tester.pumpAndSettle();

      expect(find.text('Email verified.'), findsOneWidget);
      expect(find.text('Change email'), findsNothing);
      expect(find.text('Login & security'), findsOneWidget);
    });
  });

  group('ForgotPasswordScreen', () {
    testWidgets('verifies OTP before showing password fields and resets', (
      tester,
    ) async {
      await _pump(
        tester,
        _user(),
        screen: _Screen.forgotPassword,
      );

      await tester.enterText(find.byType(TextFormField).first, '9999000021');
      expect(find.text('New password'), findsNothing);
      await tester.tap(find.text('Send OTP'));
      await tester.pumpAndSettle();

      expect(find.text('Verify your mobile'), findsOneWidget);
      expect(find.text('New password'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Verify OTP'), findsOneWidget);

      await tester.enterText(
        find.widgetWithText(TextFormField, '6-digit verification code'),
        '123456',
      );
      await tester.tap(find.widgetWithText(FilledButton, 'Verify OTP'));
      await tester.pumpAndSettle();

      expect(find.text('Choose a new password'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Reset password'), findsOneWidget);
      expect(find.text('6-digit verification code'), findsNothing);

      await tester.enterText(
        find.widgetWithText(TextFormField, 'New password'),
        'NewPassw0rd!',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Confirm password'),
        'NewPassw0rd!',
      );
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Reset password'));
      await tester.pumpAndSettle();

      // A successful reset creates a fresh session and navigates to home.
      expect(find.text('Password reset. You are signed in.'), findsNothing);
      expect(find.text('Home placeholder'), findsOneWidget);
    });
  });

  group('CreatePasswordScreen', () {
    testWidgets('opens from Login & security when no password is set', (
      tester,
    ) async {
      await _pump(tester, _user());

      expect(find.text('No password set'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Set password'), findsNothing);
      expect(find.text('Configure Password'), findsNothing);

      // The whole card is tappable — no separate button.
      await tester.tap(find.text('No password set'));
      await tester.pumpAndSettle();

      // The action opens the Create password screen instead of redirecting.
      expect(find.text('Create password'), findsOneWidget);
      expect(find.text('Secure your account'), findsOneWidget);
    });

    testWidgets('validates password length and match before submitting', (
      tester,
    ) async {
      await _pump(tester, _user(), screen: _Screen.createPassword);

      await tester.enterText(
        find.widgetWithText(TextFormField, 'New password'),
        'short',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Confirm password'),
        'different',
      );
      await tester.tap(find.text('Set password'));
      await tester.pump();

      expect(find.text('Use at least 8 characters.'), findsOneWidget);
      expect(find.text('Passwords do not match.'), findsOneWidget);
      expect(find.text('Secure your account'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
      'sets a password and returns to Login & security with hasPassword true',
      (tester) async {
        await _pump(tester, _user(), screen: _Screen.createPassword);

        await tester.enterText(
          find.widgetWithText(TextFormField, 'New password'),
          'NewPassw0rd!',
        );
        await tester.enterText(
          find.widgetWithText(TextFormField, 'Confirm password'),
          'NewPassw0rd!',
        );
        await tester.pump();
        await tester.tap(find.text('Set password'));
        await tester.pumpAndSettle();

        expect(
          find.text('Password set. You can sign in with it.'),
          findsOneWidget,
        );
        // The flow pops back to Login & security (never /home) and the
        // refreshed session reports hasPassword = true.
        expect(find.text('Login & security'), findsOneWidget);
        expect(find.text('Configure Password'), findsOneWidget);
        expect(find.text('Password is set'), findsNothing);
        expect(
          find.text('Change it any time to keep your account secure.'),
          findsOneWidget,
        );
        expect(find.widgetWithText(FilledButton, 'Set password'), findsNothing);
        expect(find.text('No password set'), findsNothing);
        expect(find.text('Home placeholder'), findsNothing);
      },
    );
  });

  group('ChangePasswordScreen', () {
    testWidgets('shows only password fields, no profile fields', (tester) async {
      await _pump(
        tester,
        _user(hasPassword: true),
        screen: _Screen.changePassword,
      );

      expect(find.widgetWithText(AppBar, 'Change Password'), findsOneWidget);
      expect(find.text('Current Password'), findsOneWidget);
      expect(find.text('New Password'), findsOneWidget);
      expect(find.text('Confirm New Password'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Change Password'), findsOneWidget);
      // No profile/contact fields on this screen.
      expect(find.text('Mobile Number'), findsNothing);
      expect(find.text('Email'), findsNothing);
      expect(find.text('customer@example.test'), findsNothing);
      expect(find.text('9876543210'), findsNothing);
    });

    testWidgets('validates current password, length, and match', (tester) async {
      await _pump(
        tester,
        _user(hasPassword: true),
        screen: _Screen.changePassword,
      );

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Current Password'),
        '',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'New Password'),
        'short',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Confirm New Password'),
        'different',
      );
      await tester.tap(
        find.widgetWithText(FilledButton, 'Change Password'),
      );
      await tester.pump();

      expect(find.text('Enter your current password.'), findsOneWidget);
      expect(find.text('Use at least 8 characters.'), findsOneWidget);
      expect(find.text('Passwords do not match.'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('changes the password and pops back', (tester) async {
      await _pump(
        tester,
        _user(hasPassword: true),
        screen: _Screen.changePassword,
      );

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Current Password'),
        'OldPassw0rd!',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'New Password'),
        'NewPassw0rd!',
      );
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Confirm New Password'),
        'NewPassw0rd!',
      );
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Change Password'));
      await tester.pumpAndSettle();

      expect(find.text('Password changed.'), findsOneWidget);
      // Pops back to the previous screen (Login & security in the stack).
      expect(find.text('Login & security'), findsOneWidget);
    });
  });
}

enum _Screen {
  loginSecurity,
  emailChange,
  forgotPassword,
  createPassword,
  changePassword,
}

AuthUser _user({
  String? email = 'customer@example.test',
  bool emailVerified = true,
  bool hasPassword = false,
  String? pendingEmail,
}) => AuthUser(
  publicUserId: 'customer-1',
  displayName: 'Customer User',
  email: email,
  mobile: '9876543210',
  roles: const ['CUSTOMER'],
  permissions: const [],
  branchIds: const [1],
  emailVerified: emailVerified,
  hasPassword: hasPassword,
  pendingEmail: pendingEmail,
);

Future<void> _pump(
  WidgetTester tester,
  AuthUser user, {
  _Screen screen = _Screen.loginSecurity,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  final session = AuthSession(
    user: user,
    accessToken: 'access-token',
    refreshToken: 'refresh-token',
    accessTokenExpiresAtUtc: DateTime.utc(2099),
    refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
  );

  // Screens that live on top of Login & Security in the real navigation stack
  // (email change, create password, change password) are pushed so their
  // success paths can pop back to the previous screen.
  final needsStack = screen == _Screen.emailChange ||
      screen == _Screen.createPassword ||
      screen == _Screen.changePassword;
  final initial = needsStack ? '/customer/security' : _initialLocation(screen);

  final router = GoRouter(
    initialLocation: initial,
    routes: [
      GoRoute(
        path: '/customer/security',
        builder: (context, state) => const LoginSecurityScreen(),
      ),
      GoRoute(
        path: '/customer/security/email',
        builder: (context, state) => const EmailChangeScreen(),
      ),
      GoRoute(
        path: '/create-password',
        builder: (context, state) => const CreatePasswordScreen(),
      ),
      GoRoute(
        path: '/change-password',
        builder: (context, state) => const ChangePasswordScreen(),
      ),
      GoRoute(
        path: '/forgot-password',
        builder: (context, state) => const ForgotPasswordScreen(),
      ),
      GoRoute(
        path: '/home',
        builder: (context, state) =>
            const Scaffold(body: Center(child: Text('Home placeholder'))),
      ),
    ],
  );

  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        sessionControllerProvider.overrideWith(() => _SeededSessionController(session)),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
  await tester.pump();

  if (screen == _Screen.emailChange) {
    router.push('/customer/security/email');
    await tester.pumpAndSettle();
  } else if (screen == _Screen.createPassword) {
    router.push('/create-password');
    await tester.pumpAndSettle();
  } else if (screen == _Screen.changePassword) {
    router.push('/change-password');
    await tester.pumpAndSettle();
  }
}

String _initialLocation(_Screen screen) => switch (screen) {
  _Screen.loginSecurity => '/customer/security',
  _Screen.emailChange => '/customer/security/email',
  _Screen.forgotPassword => '/forgot-password',
  _Screen.createPassword => '/create-password',
  _Screen.changePassword => '/change-password',
};

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.session);

  final AuthSession session;

  @override
  SessionState build() => SessionState.authenticated(session);

  @override
  Future<void> changePassword({
    required String currentPassword,
    required String newPassword,
  }) async {}

  @override
  Future<AuthSession> setPassword(String newPassword) async {
    final updated = AuthSession(
      user: _copyUser(session.user, hasPassword: true),
      accessToken: session.accessToken,
      refreshToken: session.refreshToken,
      accessTokenExpiresAtUtc: session.accessTokenExpiresAtUtc,
      refreshTokenExpiresAtUtc: session.refreshTokenExpiresAtUtc,
    );
    state = SessionState.authenticated(updated);
    return updated;
  }

  @override
  Future<String> forgotPassword(String mobile) async => 'req-reset-1';

  @override
  Future<void> verifyResetOtp({
    required String destination,
    required String code,
    required String reqId,
  }) async {}

  @override
  Future<AuthSession> resetPassword({
    required String mobile,
    required String reqId,
    required String newPassword,
  }) async => session;

  @override
  Future<EmailChangeRequested> requestEmailChange(String newEmail) async =>
      EmailChangeRequested(reqId: 'req-email-1', pendingEmail: newEmail);

  @override
  Future<AuthSession> verifyEmailChange(String code, String reqId) async {
    final updated = AuthSession(
      user: _copyUser(
        session.user,
        email: session.user.pendingEmail ?? session.user.email,
        emailVerified: true,
        pendingEmail: null,
      ),
      accessToken: session.accessToken,
      refreshToken: session.refreshToken,
      accessTokenExpiresAtUtc: session.accessTokenExpiresAtUtc,
      refreshTokenExpiresAtUtc: session.refreshTokenExpiresAtUtc,
    );
    state = SessionState.authenticated(updated);
    return updated;
  }

  AuthUser _copyUser(
    AuthUser source, {
    String? email,
    bool? emailVerified,
    bool? hasPassword,
    String? pendingEmail,
    String? pendingMobile,
  }) => AuthUser(
    publicUserId: source.publicUserId,
    displayName: source.displayName,
    email: email ?? source.email,
    mobile: source.mobile,
    roles: source.roles,
    permissions: source.permissions,
    branchIds: source.branchIds,
    emailVerified: emailVerified ?? source.emailVerified,
    hasPassword: hasPassword ?? source.hasPassword,
    pendingEmail: pendingEmail ?? source.pendingEmail,
    pendingMobile: pendingMobile ?? source.pendingMobile,
  );
}
