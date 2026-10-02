import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/login_screen.dart';
import 'package:doodh_direct_mobile/features/auth/otp_screen.dart';
import 'package:doodh_direct_mobile/features/auth/security_screens.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

// Focused tests for the redesigned customer Login & Security experience
// (security hub, mobile change, OTP validation, forgot-password resend,
// login error surface, responsive layout). Authentication behavior itself is
// unchanged — controllers are seeded/faked at the same boundary the existing
// suites use.

AuthSession _session({
  AuthUser? user,
}) => AuthSession(
  user:
      user ??
      const AuthUser(
        publicUserId: 'customer-1',
        displayName: 'Customer User',
        email: 'customer@example.test',
        mobile: '+919876543210',
        roles: ['CUSTOMER'],
        permissions: [],
        branchIds: [],
        emailVerified: true,
        hasPassword: true,
      ),
  accessToken: 'access-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

AuthUser _user({
  String? email = 'customer@example.test',
  bool emailVerified = true,
  bool hasPassword = true,
  String? mobile = '+919876543210',
  String? pendingEmail,
  String? pendingMobile,
}) => AuthUser(
  publicUserId: 'customer-1',
  displayName: 'Customer User',
  email: email,
  mobile: mobile,
  roles: const ['CUSTOMER'],
  permissions: const [],
  branchIds: const [],
  emailVerified: emailVerified,
  hasPassword: hasPassword,
  pendingEmail: pendingEmail,
  pendingMobile: pendingMobile,
);

/// Session controller with recording fakes for the security flows exercised
/// by the redesigned UI. No network, no OTP generation, no secrets.
class _RecordingSessionController extends SessionController {
  _RecordingSessionController(this.initialState);

  final SessionState initialState;

  int sendOtpCalls = 0;
  int verifyOtpCalls = 0;
  int requestMobileChangeCalls = 0;
  int verifyMobileChangeCalls = 0;
  int forgotPasswordCalls = 0;
  String? lastRequestedMobile;

  @override
  SessionState build() => initialState;

  @override
  Future<String> sendOtp(String mobile) async {
    sendOtpCalls += 1;
    lastRequestedMobile = mobile;
    return 'req-send-1';
  }

  @override
  Future<OtpVerificationResult> verifyOtp(
    String mobile,
    String code,
    String reqId,
  ) async {
    verifyOtpCalls += 1;
    return OtpVerificationResult(
      requiresOnboarding: false,
      session: _session(),
      verifiedMobile: mobile,
      reqId: reqId,
    );
  }

  @override
  Future<MobileChangeRequested> requestMobileChange(String newMobile) async {
    requestMobileChangeCalls += 1;
    lastRequestedMobile = newMobile;
    return MobileChangeRequested(reqId: 'req-mobile-1', pendingMobile: newMobile);
  }

  @override
  Future<AuthSession> verifyMobileChange(
    String mobile,
    String code,
    String reqId,
  ) async {
    verifyMobileChangeCalls += 1;
    final updated = AuthSession(
      user: _user(mobile: mobile),
      accessToken: 'access-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    );
    state = SessionState.authenticated(updated);
    return updated;
  }

  @override
  Future<String> forgotPassword(String mobile) async {
    forgotPasswordCalls += 1;
    lastRequestedMobile = mobile;
    return 'req-reset-1';
  }

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
  }) async => _session();
}

void _useSurface(WidgetTester tester, Size size) {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = size;
  addTearDown(tester.view.reset);
}

GoRouter _securityRouter() => GoRouter(
  initialLocation: '/security',
  routes: [
    GoRoute(
      path: '/security',
      builder: (context, state) => const LoginSecurityScreen(),
    ),
    GoRoute(
      path: '/security/mobile',
      builder: (context, state) => const MobileChangeScreen(),
    ),
    GoRoute(
      path: '/security/email',
      builder: (context, state) => const EmailChangeScreen(),
    ),
    GoRoute(
      path: '/create-password',
      builder: (context, state) => const CreatePasswordScreen(),
    ),
    GoRoute(
      path: '/login',
      builder: (context, state) => const LoginScreen(),
    ),
    GoRoute(
      path: '/otp',
      builder: (context, state) => OtpScreen(
        initialMobile: state.uri.queryParameters['mobile'],
      ),
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

Future<_RecordingSessionController> _pumpScreen(
  WidgetTester tester, {
  required Widget Function() screen,
  required String location,
  required _RecordingSessionController controller,
  bool push = false,
}) async {
  final router = _securityRouter();
  addTearDown(router.dispose);
  final container = ProviderContainer(
    overrides: [
      sessionControllerProvider.overrideWith(() => controller),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pump();
  await tester.pump();
  if (push) {
    router.push(location);
  } else {
    router.go(location);
  }
  await tester.pumpAndSettle();
  return controller;
}

void main() {
  group('LoginSecurityScreen hub', () {
    testWidgets('mobile card shows the number with an Active pill', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginSecurityScreen(),
        location: '/security',
        controller: _RecordingSessionController(
          SessionState.authenticated(_session()),
        ),
      );

      expect(find.text('+919876543210'), findsOneWidget);
      // Two Active pills: the mobile number and the password both set.
      expect(find.text('Active'), findsNWidgets(2));
      expect(find.byTooltip('Change mobile'), findsOneWidget);
    });

    testWidgets('a missing mobile is a neutral Not set state, not an alarm', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginSecurityScreen(),
        location: '/security',
        controller: _RecordingSessionController(
          SessionState.authenticated(_session(user: _user(mobile: null))),
        ),
      );

      expect(find.text('No mobile number'), findsOneWidget);
      expect(find.text('Not set'), findsOneWidget);
      expect(find.byTooltip('Add mobile'), findsOneWidget);
    });

    testWidgets('pending mobile shows a Pending pill and verify action', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginSecurityScreen(),
        location: '/security',
        controller: _RecordingSessionController(
          SessionState.authenticated(
            _session(
              user: _user(
                pendingMobile: '+919999000001',
              ),
            ),
          ),
        ),
      );

      expect(find.text('Pending'), findsOneWidget);
      expect(
        find.text('Pending confirmation: +919999000001'),
        findsOneWidget,
      );
      expect(find.text('Verify pending mobile'), findsOneWidget);
    });

    testWidgets('unverified email is labelled in text, not colour alone', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginSecurityScreen(),
        location: '/security',
        controller: _RecordingSessionController(
          SessionState.authenticated(
            _session(user: _user(emailVerified: false)),
          ),
        ),
      );

      expect(find.text('Unverified'), findsOneWidget);
      expect(
        find.text('Not yet verified — verify to secure your account.'),
        findsOneWidget,
      );
    });

    testWidgets('pending email shows the verify action tile', (tester) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginSecurityScreen(),
        location: '/security',
        controller: _RecordingSessionController(
          SessionState.authenticated(
            _session(
              user: _user(
                pendingEmail: 'new@example.test',
              ),
            ),
          ),
        ),
      );

      expect(find.text('Verify pending email'), findsOneWidget);
      expect(
        find.text('Pending confirmation: new@example.test'),
        findsOneWidget,
      );
    });

    testWidgets('intro card frames the hub without security claims', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginSecurityScreen(),
        location: '/security',
        controller: _RecordingSessionController(
          SessionState.authenticated(_session()),
        ),
      );

      expect(find.text('Your sign-in details'), findsOneWidget);
      expect(
        find.text(
          'Manage the mobile number, email address and password you use '
          'to sign in to DoodhDirect.',
        ),
        findsOneWidget,
      );
      // No invented guarantees, scores or certifications.
      expect(find.textContaining('encrypted'), findsNothing);
      expect(find.textContaining('protected by'), findsNothing);
      expect(find.textContaining('score'), findsNothing);
    });
  });

  group('MobileChangeScreen', () {
    testWidgets('requesting a code moves to verification with the destination',
        (tester) async {
      _useSurface(tester, const Size(800, 1400));
      final controller = await _pumpScreen(
        tester,
        screen: () => const MobileChangeScreen(),
        location: '/security/mobile',
        push: true,
        controller: _RecordingSessionController(
          SessionState.authenticated(_session(user: _user(mobile: null))),
        ),
      );

      await tester.enterText(find.byType(TextFormField).first, '9876543210');
      await tester.tap(find.text('Send verification code'));
      await tester.pumpAndSettle();

      expect(controller.requestMobileChangeCalls, 1);
      expect(controller.lastRequestedMobile, '+919876543210');
      expect(find.text('Verify your mobile'), findsOneWidget);
      expect(
        find.text('Enter the 6-digit code sent to +919876543210.'),
        findsOneWidget,
      );
    });

    testWidgets('verification requests a fresh code when the reqId is gone',
        (tester) async {
      _useSurface(tester, const Size(800, 1400));
      final controller = await _pumpScreen(
        tester,
        screen: () => const MobileChangeScreen(),
        location: '/security/mobile',
        push: true,
        controller: _RecordingSessionController(
          SessionState.authenticated(
            _session(user: _user(pendingMobile: '+919876543210')),
          ),
        ),
      );

      // The reqId lives only in memory, so the first verify attempt must
      // request a fresh code instead of failing.
      await tester.enterText(find.byType(TextFormField).first, '123456');
      await tester.tap(find.widgetWithText(FilledButton, 'Verify mobile'));
      await tester.pumpAndSettle();
      expect(controller.requestMobileChangeCalls, 1);
      expect(controller.verifyMobileChangeCalls, 0);

      // The second attempt verifies with the refreshed reqId.
      await tester.tap(find.widgetWithText(FilledButton, 'Verify mobile'));
      await tester.pumpAndSettle();
      expect(controller.verifyMobileChangeCalls, 1);
    });
  });

  group('OtpScreen', () {
    testWidgets('an incomplete code is rejected locally without a verify call',
        (tester) async {
      _useSurface(tester, const Size(800, 1400));
      final controller = await _pumpScreen(
        tester,
        screen: () => const LoginScreen(),
        location: '/login',
        controller: _RecordingSessionController(
          SessionState.authenticated(_session()),
        ),
      );

      await tester.enterText(find.byType(TextFormField).first, '9876543210');
      await tester.tap(find.text('Send OTP'));
      await tester.pumpAndSettle();

      expect(find.text('6-digit verification code'), findsOneWidget);
      await tester.enterText(
        find.widgetWithText(TextFormField, '6-digit verification code'),
        '12345',
      );
      await tester.tap(find.text('Verify code'));
      await tester.pump();

      expect(find.text('Enter the 6-digit code.'), findsOneWidget);
      expect(controller.verifyOtpCalls, 0);
    });
  });

  group('ForgotPasswordScreen', () {
    testWidgets('resend requests another reset OTP for the same mobile', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      final controller = await _pumpScreen(
        tester,
        screen: () => const ForgotPasswordScreen(),
        location: '/forgot-password',
        controller: _RecordingSessionController(
          SessionState.authenticated(_session()),
        ),
      );

      await tester.enterText(find.byType(TextFormField).first, '9999000021');
      await tester.tap(find.text('Send OTP'));
      await tester.pumpAndSettle();
      expect(controller.forgotPasswordCalls, 1);

      await tester.tap(find.text('Send a new code'));
      await tester.pumpAndSettle();
      expect(controller.forgotPasswordCalls, 2);
      expect(controller.lastRequestedMobile, '+919999000021');
    });
  });

  group('LoginScreen', () {
    testWidgets('session errors surface as an inline error banner', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginScreen(),
        location: '/login',
        controller: _RecordingSessionController(
          SessionState.unauthenticated(
            errorMessage: 'Incorrect email/mobile or password.',
          ),
        ),
      );

      expect(find.text('Sign in to your account'), findsOneWidget);
      expect(find.text('Incorrect email/mobile or password.'), findsOneWidget);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
    });

    testWidgets('show/hide password stays accessible via its tooltip', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginScreen(),
        location: '/login',
        controller: _RecordingSessionController(
          SessionState.unauthenticated(),
        ),
      );

      await tester.tap(find.text('Use password instead'));
      await tester.pumpAndSettle();

      final showToggle = find.byTooltip('Show password');
      expect(showToggle, findsOneWidget);
      await tester.tap(showToggle);
      await tester.pumpAndSettle();
      expect(find.byTooltip('Hide password'), findsOneWidget);
    });

    testWidgets('login stays usable from compact to wide without overflow', (
      tester,
    ) async {
      _useSurface(tester, const Size(360, 800));
      await _pumpScreen(
        tester,
        screen: () => const LoginScreen(),
        location: '/login',
        controller: _RecordingSessionController(
          SessionState.unauthenticated(),
        ),
      );
      expect(tester.takeException(), isNull);
      expect(find.text('Send OTP'), findsOneWidget);
    });
  });

  group('accessibility', () {
    testWidgets('security status pills expose their label to semantics', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpScreen(
        tester,
        screen: () => const LoginSecurityScreen(),
        location: '/security',
        controller: _RecordingSessionController(
          SessionState.authenticated(_session()),
        ),
      );

      expect(find.bySemanticsLabel('Active'), findsNWidgets(2));
      expect(find.bySemanticsLabel('Verified'), findsOneWidget);
    });
  });
}
