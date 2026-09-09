import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_repository.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/employees/employee_controller.dart';
import 'package:doodh_direct_mobile/features/employees/employee_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('guest router', () {
    testWidgets('fresh browser lands on the guest home without a login redirect',
        (tester) async {
      final harness = await _pumpGuestApp(tester);

      // A fresh browser restores with no persisted session. The restore
      // fallback now yields the guest state directly, so the base URL
      // '/restore' branch settles on the guest home — never on /login.
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Welcome to DoodhDirect'), findsOneWidget);
      expect(find.text('Sign in for orders, payments and more'), findsOneWidget);
      expect(find.text('Shop'), findsOneWidget);
      expect(find.text('My cart'), findsOneWidget);
      expect(find.text('Sign in to your account'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('restored customer lands on the customer home', (tester) async {
      final harness = await _pumpAuthenticatedRouterApp(tester);

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Good day, Asha'), findsOneWidget);
      expect(find.text('Your shortcuts'), findsOneWidget);
      // The hero card, quick actions and context cards repeat labels such as
      // 'Shop', 'Subscribe' and 'Wallet', so assert on a label unique to the
      // customer role home to prove the role screen rendered.
      expect(find.text('My orders'), findsOneWidget);
      expect(find.text('Welcome to DoodhDirect'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('restored delivery staff lands on the delivery workspace',
        (tester) async {
      final harness = await _pumpAuthenticatedRouterApp(
        tester,
        session: _deliveryStaffSession,
      );

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Delivery workspace'), findsOneWidget);
      expect(find.text('Delivery route'), findsOneWidget);
      expect(find.text("Today's deliveries"), findsOneWidget);
      expect(find.text('Sign in to your account'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
        'restored delivery manager lands on the branch delivery management',
        (tester) async {
      final harness = await _pumpAuthenticatedRouterApp(
        tester,
        session: _deliveryManagerSession,
      );

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Delivery workspace'), findsOneWidget);
      expect(find.text('Delivery management'), findsOneWidget);
      expect(find.text('Branch 7 deliveries'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('employee invitation link opens for a guest without login',
        (tester) async {
      final harness = await _pumpGuestApp(
        tester,
        employeeController: _InvitationEmployeeController(_verification),
      );

      harness.router.go('/invite/inv-token-9');
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/invite/inv-token-9',
      );
      expect(find.text('Join DoodhDirect'), findsOneWidget);
      expect(find.text('Your assigned profile'), findsOneWidget);
      expect(find.text('Sign in to your account'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
        'invitation path survives session restoration without becoming home',
        (tester) async {
      final harness = await _pumpGuestApp(
        tester,
        employeeController: _InvitationEmployeeController(_verification),
      );

      harness.router.go('/invite/inv-token-restore');
      await tester.pumpAndSettle();
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/invite/inv-token-restore',
      );

      await harness.container
          .read(sessionControllerProvider.notifier)
          .establishSession(_session);
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/invite/inv-token-restore',
      );
      expect(find.text('Join DoodhDirect'), findsOneWidget);
      expect(find.text('Welcome to DoodhDirect'), findsNothing);
      expect(find.text('Sign in to your account'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('public storefront routes render without redirect for guest',
        (tester) async {
      final harness = await _pumpGuestApp(tester);

      harness.router.go('/catalogue');
      await tester.pumpAndSettle();
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/catalogue',
      );
      expect(find.text('Whole Milk'), findsOneWidget);

      harness.router.go('/catalogue/products/${_product.publicId}');
      await tester.pumpAndSettle();
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/catalogue/products/${_product.publicId}',
      );
      // The detail screen renders the name in both the AppBar and the body.
      expect(find.text('Whole Milk'), findsNWidgets(2));

      harness.router.go('/checkout');
      await tester.pumpAndSettle();
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/checkout',
      );
      expect(find.text('Checkout'), findsOneWidget);
    });

    testWidgets('protected routes redirect to login and capture the intent',
        (tester) async {
      final harness = await _pumpGuestApp(tester);

      const targets = <String>[
        '/orders',
        '/wallet',
        '/cameras',
        '/notifications',
        '/customer/account',
        '/subscriptions',
        '/payments/some-payment-id/result',
        '/deliveries',
        '/deliveries/some-delivery-id',
      ];

      for (final target in targets) {
        harness.router.go(target);
        await tester.pumpAndSettle();

        final config = harness.router.routerDelegate.currentConfiguration.uri;
        expect(config.path, '/login', reason: 'for target $target');
        expect(
          config.queryParameters['redirectTo'],
          target,
          reason: 'for target $target',
        );
      }
    });

    testWidgets(
        'checkout with a seeded cart shows the login boundary and no order actions',
        (tester) async {
      final harness = await _pumpGuestApp(tester);

      // Enter guest mode FIRST so the in-memory cart is not wiped by the guest
      // adopt-on-listener which reads (empty) storage on the guest transition.
      harness.router.go('/home');
      await tester.pumpAndSettle();

      harness.container
          .read(orderControllerProvider.notifier)
          .setCartItem(_product, 2);
      await tester.pumpAndSettle();

      harness.router.go('/checkout');
      await tester.pumpAndSettle();

      expect(find.text('Whole Milk'), findsOneWidget);
      expect(
        find.text('Login required to continue to checkout.'),
        findsOneWidget,
      );
      expect(find.text('Login'), findsOneWidget);
      // The Register entry was removed from the login boundary: customer
      // accounts are created through the OTP onboarding flow after signing in.
      expect(find.text('Register'), findsNothing);
      expect(find.text('Preview order'), findsNothing);
      expect(find.textContaining('Place order'), findsNothing);
    });

    testWidgets('return intent survives sign-in and lands on the original route',
        (tester) async {
      final harness = await _pumpGuestApp(tester);

      harness.router.go('/orders');
      await tester.pumpAndSettle();
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/login',
      );

      await harness.container
          .read(sessionControllerProvider.notifier)
          .establishSession(_session);
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/orders',
      );
      expect(find.text('No orders yet'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('open-redirect is rejected and authenticated lands on home',
        (tester) async {
      final harness = await _pumpAuthenticatedRouterApp(tester);

      harness.router.go('/login?redirectTo=https://evil.example');
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Welcome to DoodhDirect'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('authenticated user can open protected routes', (tester) async {
      final harness = await _pumpAuthenticatedRouterApp(tester);

      harness.router.go('/orders');
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/orders',
      );
      expect(find.text('No orders yet'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('otp-first login flow', () {
    testWidgets('login defaults to the mobile OTP-first form with no register',
        (tester) async {
      final harness = await _pumpGuestApp(tester);

      harness.router.go('/login');
      await tester.pumpAndSettle();

      expect(find.text('Sign in to your account'), findsOneWidget);
      expect(find.text('Send OTP'), findsOneWidget);
      expect(find.text('Use password instead'), findsOneWidget);
      expect(find.text('Mobile number'), findsOneWidget);
      // The password form is hidden until the user asks for it.
      expect(find.text('Email or mobile'), findsNothing);
      expect(find.text('Password'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Sign in'), findsNothing);
      expect(find.text('Forgot password?'), findsNothing);
      expect(find.text('Sign in with mobile OTP'), findsNothing);
      // Register entry points were removed from the login screen.
      expect(find.text('Register'), findsNothing);
      expect(find.text('Create a customer account'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('password sign-in is secondary and can be toggled back to OTP',
        (tester) async {
      final harness = await _pumpGuestApp(tester);

      harness.router.go('/login');
      await tester.pumpAndSettle();

      final usePassword = find.text('Use password instead');
      await tester.ensureVisible(usePassword);
      await tester.tap(usePassword);
      await tester.pumpAndSettle();

      expect(find.text('Email or mobile'), findsOneWidget);
      expect(find.text('Password'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Sign in'), findsOneWidget);
      expect(find.text('Forgot password?'), findsOneWidget);
      expect(find.text('Sign in with mobile OTP'), findsOneWidget);
      expect(find.text('Send OTP'), findsNothing);

      final backToOtp = find.text('Sign in with mobile OTP');
      await tester.ensureVisible(backToOtp);
      await tester.tap(backToOtp);
      await tester.pumpAndSettle();

      expect(find.text('Send OTP'), findsOneWidget);
      expect(find.text('Use password instead'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Sign in'), findsNothing);
      expect(find.text('Email or mobile'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('sending an OTP opens the code screen with the canonical mobile',
        (tester) async {
      final otpAuth = _OtpAuthRepository();
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go('/login');
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '9876543210');
      final sendOtp = find.text('Send OTP');
      await tester.ensureVisible(sendOtp);
      await tester.tap(sendOtp);
      await tester.pumpAndSettle();

      final config = harness.router.routerDelegate.currentConfiguration.uri;
      expect(config.path, '/otp');
      expect(config.queryParameters['mobile'], '+919876543210');
      // The code screen prefills the mobile and auto-sends the code request.
      expect(otpAuth.sendCalls, 1);
      expect(otpAuth.lastSentMobile, '+919876543210');
      expect(find.text('6-digit verification code'), findsOneWidget);
      expect(find.text('Verification code request accepted.'), findsOneWidget);
      expect(find.text('Send a new code'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await _expireSnackBars(tester);
    });

    testWidgets('a shared /otp link prefills the mobile and auto-sends',
        (tester) async {
      final otpAuth = _OtpAuthRepository();
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go('/otp?mobile=%2B919876543210');
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/otp',
      );
      expect(otpAuth.sendCalls, 1);
      expect(otpAuth.lastSentMobile, '+919876543210');
      expect(find.text('6-digit verification code'), findsOneWidget);
      expect(find.text('Verification code request accepted.'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await _expireSnackBars(tester);
    });

    testWidgets('existing customer OTP login lands on the customer home',
        (tester) async {
      final otpAuth = _OtpAuthRepository(
        verifyResults: [
          OtpVerificationResult(requiresOnboarding: false, session: _session),
        ],
      );
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go('/login');
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '9876543210');
      final sendOtp = find.text('Send OTP');
      await tester.ensureVisible(sendOtp);
      await tester.tap(sendOtp);
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField).last, '123456');
      final verifyCode = find.text('Verify code');
      await tester.ensureVisible(verifyCode);
      await tester.tap(verifyCode);
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Good day, Asha'), findsOneWidget);
      expect(find.text('My orders'), findsOneWidget);
      expect(find.text('Welcome to DoodhDirect'), findsNothing);
      expect(otpAuth.verifyCalls, 1);
      expect(otpAuth.verifyMobile, '+919876543210');
      expect(otpAuth.verifyCode, '123456');
      expect(otpAuth.verifyReqId, 'req-send-1');
      expect(
        harness.container.read(sessionControllerProvider).isAuthenticated,
        isTrue,
      );
      expect(tester.takeException(), isNull);
      await _expireSnackBars(tester);
    });

    testWidgets('existing delivery staff OTP login lands on the delivery home',
        (tester) async {
      final otpAuth = _OtpAuthRepository(
        verifyResults: [
          OtpVerificationResult(
            requiresOnboarding: false,
            session: _deliveryStaffSession,
          ),
        ],
      );
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go('/login');
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '9876543210');
      final sendOtp = find.text('Send OTP');
      await tester.ensureVisible(sendOtp);
      await tester.tap(sendOtp);
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField).last, '123456');
      final verifyCode = find.text('Verify code');
      await tester.ensureVisible(verifyCode);
      await tester.tap(verifyCode);
      await tester.pumpAndSettle();

      // The role is decided server-side after verification: a delivery-staff
      // member authenticates into the delivery workspace, never a customer.
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Delivery workspace'), findsOneWidget);
      expect(find.text('Delivery route'), findsOneWidget);
      expect(find.text("Today's deliveries"), findsOneWidget);
      expect(otpAuth.verifyCalls, 1);
      expect(otpAuth.verifyMobile, '+919876543210');
      expect(
        harness.container.read(sessionControllerProvider).isAuthenticated,
        isTrue,
      );
      expect(tester.takeException(), isNull);
      await _expireSnackBars(tester);
    });

    testWidgets('a verified new mobile goes to password onboarding, no session',
        (tester) async {
      final otpAuth = _OtpAuthRepository(
        verifyResults: [
          OtpVerificationResult(
            requiresOnboarding: true,
            verifiedMobile: '+919876543210',
            reqId: 'req-onboard-7',
          ),
        ],
      );
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go('/login');
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '9876543210');
      final sendOtp = find.text('Send OTP');
      await tester.ensureVisible(sendOtp);
      await tester.tap(sendOtp);
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField).last, '123456');
      final verifyCode = find.text('Verify code');
      await tester.ensureVisible(verifyCode);
      await tester.tap(verifyCode);
      await tester.pumpAndSettle();

      final config = harness.router.routerDelegate.currentConfiguration.uri;
      expect(config.path, '/otp/onboarding');
      expect(config.queryParameters['mobile'], '+919876543210');
      expect(config.queryParameters['reqId'], 'req-onboard-7');
      // No session is created until the password is set.
      expect(
        harness.container.read(sessionControllerProvider).isAuthenticated,
        isFalse,
      );
      expect(find.text('Create password'), findsOneWidget);
      expect(find.text('You are almost in'), findsOneWidget);
      expect(find.textContaining('is verified'), findsOneWidget);
      expect(otpAuth.verifyCalls, 1);
      expect(tester.takeException(), isNull);
      await _expireSnackBars(tester);
    });

    testWidgets('onboarding rejects a too-short password without completing',
        (tester) async {
      final otpAuth = _OtpAuthRepository();
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go(
        '/otp/onboarding?mobile=%2B919876543210&reqId=req-onboard-7',
      );
      await tester.pumpAndSettle();

      expect(find.text('Create password'), findsOneWidget);
      await tester.enterText(find.byType(TextFormField).at(0), '1234567');
      await tester.enterText(find.byType(TextFormField).at(1), '1234567');
      final createAccount = find.text('Create account');
      await tester.ensureVisible(createAccount);
      await tester.tap(createAccount);
      await tester.pumpAndSettle();

      expect(find.text('Use at least 8 characters.'), findsOneWidget);
      expect(find.text('Passwords do not match.'), findsNothing);
      expect(otpAuth.onboardingCalls, 0);
      expect(tester.takeException(), isNull);
    });

    testWidgets('onboarding rejects mismatched passwords without completing',
        (tester) async {
      final otpAuth = _OtpAuthRepository();
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go(
        '/otp/onboarding?mobile=%2B919876543210&reqId=req-onboard-7',
      );
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField).at(0), 'password123');
      await tester.enterText(find.byType(TextFormField).at(1), 'different1');
      final createAccount = find.text('Create account');
      await tester.ensureVisible(createAccount);
      await tester.tap(createAccount);
      await tester.pumpAndSettle();

      expect(find.text('Passwords do not match.'), findsOneWidget);
      expect(find.text('Use at least 8 characters.'), findsNothing);
      expect(otpAuth.onboardingCalls, 0);
      expect(tester.takeException(), isNull);
    });

    testWidgets('onboarding with a valid password creates the customer account',
        (tester) async {
      final otpAuth = _OtpAuthRepository();
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go(
        '/otp/onboarding?mobile=%2B919876543210&reqId=req-onboard-7',
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('is verified'), findsOneWidget);
      await tester.enterText(find.byType(TextFormField).at(0), 'secret1234');
      await tester.enterText(find.byType(TextFormField).at(1), 'secret1234');
      final createAccount = find.text('Create account');
      await tester.ensureVisible(createAccount);
      await tester.tap(createAccount);
      await tester.pumpAndSettle();

      expect(otpAuth.onboardingCalls, 1);
      expect(otpAuth.onboardMobile, '+919876543210');
      expect(otpAuth.onboardReqId, 'req-onboard-7');
      expect(otpAuth.onboardPassword, 'secret1234');
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
      expect(find.text('Good day, Asha'), findsOneWidget);
      expect(find.text('My orders'), findsOneWidget);
      expect(
        find.text('Account created. Welcome to DoodhDirect!'),
        findsOneWidget,
      );
      expect(
        harness.container.read(sessionControllerProvider).isAuthenticated,
        isTrue,
      );
      expect(tester.takeException(), isNull);
      await _expireSnackBars(tester);
    });

    testWidgets('onboarding without verified details asks to sign in again',
        (tester) async {
      final otpAuth = _OtpAuthRepository();
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go('/otp/onboarding');
      await tester.pumpAndSettle();

      expect(find.text('Sign in required'), findsOneWidget);
      expect(
        find.textContaining('missing or have expired'),
        findsOneWidget,
      );
      final signIn = find.widgetWithText(FilledButton, 'Sign in');
      expect(signIn, findsOneWidget);
      await tester.ensureVisible(signIn);
      await tester.tap(signIn);
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/login',
      );
      expect(find.text('Send OTP'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('OTP login honours the protected-route return intent',
        (tester) async {
      final otpAuth = _OtpAuthRepository(
        verifyResults: [
          OtpVerificationResult(requiresOnboarding: false, session: _session),
        ],
      );
      final harness = await _pumpGuestApp(tester, authOverride: otpAuth);

      harness.router.go('/orders');
      await tester.pumpAndSettle();

      final loginConfig = harness.router.routerDelegate.currentConfiguration.uri;
      expect(loginConfig.path, '/login');
      expect(loginConfig.queryParameters['redirectTo'], '/orders');

      await tester.enterText(find.byType(TextFormField), '9876543210');
      final sendOtp = find.text('Send OTP');
      await tester.ensureVisible(sendOtp);
      await tester.tap(sendOtp);
      await tester.pumpAndSettle();

      // The redirect intent survives the /login -> /otp hop.
      final otpConfig = harness.router.routerDelegate.currentConfiguration.uri;
      expect(otpConfig.path, '/otp');
      expect(otpConfig.queryParameters['redirectTo'], '/orders');

      await tester.enterText(find.byType(TextFormField).last, '123456');
      final verifyCode = find.text('Verify code');
      await tester.ensureVisible(verifyCode);
      await tester.tap(verifyCode);
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/orders',
      );
      expect(find.text('No orders yet'), findsOneWidget);
      expect(otpAuth.verifyCalls, 1);
      expect(
        harness.container.read(sessionControllerProvider).isAuthenticated,
        isTrue,
      );
      expect(tester.takeException(), isNull);
      await _expireSnackBars(tester);
    });
  });

  group('create-password routing', () {
    testWidgets(
        'authenticated customer without a password opens create-password '
        'instead of redirecting home', (tester) async {
      // The default restored session (Asha Sharma) has hasPassword == false:
      // she is FULLY authenticated even though she has no password yet, so the
      // password-setup route must open for her — never bounce her to /home.
      final harness = await _pumpAuthenticatedRouterApp(tester);

      harness.router.go('/create-password');
      await tester.pumpAndSettle();

      final config = harness.router.routerDelegate.currentConfiguration.uri;
      expect(config.path, '/create-password');
      expect(find.text('Create password'), findsOneWidget);
      expect(find.text('Secure your account'), findsOneWidget);
      expect(find.text('New password'), findsOneWidget);
      expect(find.text('Welcome to DoodhDirect'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
        'tapping the password card on Login & security opens create-password '
        'and does not redirect home', (tester) async {
      // A tall surface keeps the whole Login & security list on screen so the
      // password card (below the email card) is hittable without scrolling.
      await tester.binding.setSurfaceSize(const Size(800, 1400));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final harness = await _pumpAuthenticatedRouterApp(tester);

      harness.router.go('/customer/security');
      await tester.pumpAndSettle();

      expect(find.text('Login & security'), findsOneWidget);
      expect(find.text('No password set'), findsOneWidget);

      // The whole password card is tappable — there is no separate button.
      final passwordCard = find.widgetWithText(Card, 'No password set');
      expect(passwordCard, findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Set password'), findsNothing);
      await tester.ensureVisible(passwordCard);
      await tester.pumpAndSettle();
      await tester.tap(passwordCard);
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);

      // Tapping the card pushes /create-password onto the navigation stack
      // above Login & security. Note: currentConfiguration.uri still reports
      // the declarative URL (/customer/security) because imperative push()
      // pages are layered on top of it, so the widget tree — what the user
      // actually sees — is the ground truth here. The key regression guard is
      // that the customer lands on the Create password screen and never on
      // /home.
      expect(find.text('Create password'), findsOneWidget);
      expect(find.text('Secure your account'), findsOneWidget);
      expect(find.text('New password'), findsOneWidget);
      expect(find.text('No password set'), findsNothing);
      expect(find.text('Good day, Asha'), findsNothing);
      expect(find.text('Welcome to DoodhDirect'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
        'guest reaching create-password is gated to login with no return '
        'intent', (tester) async {
      final harness = await _pumpGuestApp(tester);

      harness.router.go('/create-password');
      await tester.pumpAndSettle();

      // /create-password is NOT an auth route and NOT in the return-intent
      // allowlist, so an unauthenticated visitor is sent to a plain /login
      // with no redirectTo — it must not be globally browsable.
      final uri = harness.router.routerDelegate.currentConfiguration.uri;
      expect(uri.path, '/login');
      expect(uri.queryParameters['redirectTo'], isNull);
      expect(find.text('Sign in to your account'), findsOneWidget);
      expect(find.text('Secure your account'), findsNothing);
      expect(tester.takeException(), isNull);
    });
  });
}

/// Advances the clock past the 4-second SnackBar auto-dismiss so no test ends
/// with a pending timer still scheduled.
Future<void> _expireSnackBars(WidgetTester tester) async {
  await tester.pump(const Duration(seconds: 4));
  await tester.pumpAndSettle();
}

/// Pumps the app as an unauthenticated visitor. Restore yields no session, the
/// guest cart store is backed by [storage], the catalogue fake serves the
/// product, and the order repository is a safe recording stub.
Future<_GuestRouterHarness> _pumpGuestApp(
  WidgetTester tester, {
  _FakeFlutterSecureStorage? storage,
  EmployeeController? employeeController,
  _RestoringAuthRepository? authOverride,
}) async {
  final auth = authOverride ?? _RestoringAuthRepository(restoreResult: null);
  final orders = _RecordingOrderRepository();
  final catalogue = _FakeCatalogueRepository(_product);
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(auth),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: storage ?? _FakeFlutterSecureStorage()),
      ),
      orderRepositoryProvider.overrideWithValue(orders),
      catalogueRepositoryProvider.overrideWithValue(catalogue),
      if (employeeController != null)
        employeeControllerProvider.overrideWith(() => employeeController),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const DoodhDirectApp(),
    ),
  );
  await tester.pumpAndSettle();
  return _GuestRouterHarness(container, orders, auth);
}

/// Pumps the app with a restored authenticated session (defaults to the
/// customer session).
Future<_GuestRouterHarness> _pumpAuthenticatedRouterApp(
  WidgetTester tester, {
  AuthSession? session,
}) async {
  final auth = _RestoringAuthRepository(restoreResult: session ?? _session);
  final orders = _RecordingOrderRepository();
  final catalogue = _FakeCatalogueRepository(_product);
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(auth),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: _FakeFlutterSecureStorage()),
      ),
      orderRepositoryProvider.overrideWithValue(orders),
      catalogueRepositoryProvider.overrideWithValue(catalogue),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const DoodhDirectApp(),
    ),
  );
  await tester.pumpAndSettle();
  return _GuestRouterHarness(container, orders, auth);
}

class _GuestRouterHarness {
  const _GuestRouterHarness(this.container, this.orders, this.auth);

  final ProviderContainer container;
  final _RecordingOrderRepository orders;
  final _RestoringAuthRepository auth;

  GoRouter get router => container.read(routerProvider);
}

/// Auth repository that restores [restoreResult] and never touches storage.
class _RestoringAuthRepository extends AuthRepository {
  _RestoringAuthRepository({this.restoreResult})
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final AuthSession? restoreResult;

  int logoutCalls = 0;

  @override
  Future<AuthSession?> restore() async => restoreResult;

  @override
  Future<void> saveSession(AuthSession session) async {}

  @override
  Future<void> clear() async {}

  @override
  Future<void> logout(AuthSession session) async {
    logoutCalls += 1;
  }
}

/// Auth repository fake for the OTP-first sign-in flows. It scripts the server
/// decision after verification ([verifyResults], consumed in order; an empty
/// list means "existing customer of any role") and records every OTP call so
/// tests can assert the canonical mobile, the consumed reqId, and the
/// onboarding payload sent to the completion endpoint.
class _OtpAuthRepository extends _RestoringAuthRepository {
  _OtpAuthRepository({
    List<OtpVerificationResult>? verifyResults,
  }) : verifyResults = List.of(verifyResults ?? const []);

  final List<OtpVerificationResult> verifyResults;

  int sendCalls = 0;
  int retryCalls = 0;
  int verifyCalls = 0;
  int onboardingCalls = 0;
  String? lastSentMobile;
  String? verifyMobile;
  String? verifyCode;
  String? verifyReqId;
  String? onboardMobile;
  String? onboardReqId;
  String? onboardPassword;

  @override
  Future<String> sendOtp(String mobile) async {
    sendCalls += 1;
    lastSentMobile = mobile;
    return 'req-send-1';
  }

  @override
  Future<String> retryOtp(String mobile) async {
    retryCalls += 1;
    return 'req-retry-2';
  }

  @override
  Future<OtpVerificationResult> verifyOtp(
    String mobile,
    String code,
    String reqId,
  ) async {
    verifyCalls += 1;
    verifyMobile = mobile;
    verifyCode = code;
    verifyReqId = reqId;
    if (verifyResults.isNotEmpty) {
      return verifyResults.removeAt(0);
    }
    return OtpVerificationResult(requiresOnboarding: false, session: _session);
  }

  @override
  Future<AuthSession> completeOtpRegistration({
    required String mobile,
    required String reqId,
    required String newPassword,
  }) async {
    onboardingCalls += 1;
    onboardMobile = mobile;
    onboardReqId = reqId;
    onboardPassword = newPassword;
    return _session;
  }
}

/// Order repository that safely records every protected call. Unlike the
/// throwing stub used by the cart suite, this returns empty data so screens
/// rendered after authentication (e.g. the order history) settle cleanly.
class _RecordingOrderRepository extends OrderRepository {
  _RecordingOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int previewCalls = 0;
  int createCalls = 0;
  int getMineCalls = 0;
  int getCalls = 0;
  int cancelCalls = 0;

  @override
  Future<CheckoutPreview> preview(String token, CheckoutRequest request) async {
    previewCalls += 1;
    throw UnimplementedError('not used by router tests');
  }

  @override
  Future<OrderSummary> create(
    String token,
    CheckoutRequest request,
    String idempotencyKey,
  ) async {
    createCalls += 1;
    throw UnimplementedError('not used by router tests');
  }

  @override
  Future<List<OrderSummary>> getMine(String token) async {
    getMineCalls += 1;
    return const [];
  }

  @override
  Future<OrderSummary> get(String token, String orderId) async {
    getCalls += 1;
    throw UnimplementedError('not used by router tests');
  }

  @override
  Future<OrderSummary> cancel(String token, String orderId) async {
    cancelCalls += 1;
    throw UnimplementedError('not used by router tests');
  }
}

/// Catalogue repository serving the public storefront methods the catalogue
/// controller and product detail screen rely on.
class _FakeCatalogueRepository extends CatalogueRepository {
  _FakeCatalogueRepository(this.product)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final CatalogueProduct product;

  @override
  Future<List<ProductCategory>> getCategories() async => [product.category];

  @override
  Future<List<CatalogueProduct>> getProducts({String? categoryId}) async =>
      [product];

  @override
  Future<CatalogueProduct> getProduct(String productId) async => product;
}

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

const _product = CatalogueProduct(
  publicId: '00000000-0000-0000-0000-000000000030',
  sku: 'MILK-1L',
  name: 'Whole Milk',
  description: 'Fresh whole milk.',
  category: ProductCategory(
    publicId: '00000000-0000-0000-0000-000000000040',
    code: 'MILK',
    name: 'Milk',
    description: 'Fresh dairy products.',
    isActive: true,
  ),
  unitOfMeasure: 'litre',
  price: 60,
  isActive: true,
  branchAvailability: [
    BranchAvailability(
      branchId: '00000000-0000-0000-0000-000000000050',
      branchCode: 'CENTRAL',
      branchName: 'Central Dairy',
      isAvailable: true,
      maxDailyQuantity: 10,
    ),
  ],
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Asha Sharma',
    email: 'asha@example.test',
    mobile: '9876543210',
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _deliveryStaffSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'delivery-staff-1',
    displayName: 'Delivery Staff User',
    email: 'delivery-staff@example.test',
    mobile: null,
    roles: ['DELIVERY_STAFF'],
    permissions: ['IDENTITY.BRANCH.ACCESS'],
    branchIds: [1],
  ),
  accessToken: 'delivery-staff-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _deliveryManagerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'delivery-manager-1',
    displayName: 'Delivery Manager User',
    email: 'delivery-manager@example.test',
    mobile: null,
    roles: ['DELIVERY_MANAGER'],
    permissions: ['IDENTITY.BRANCH.ACCESS', 'DELIVERIES.READ_BRANCH'],
    branchIds: [7],
  ),
  accessToken: 'delivery-manager-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

/// Seeded employee controller that serves a valid invitation verification
/// without touching the network, so a guest can open an /invite/:token link.
class _InvitationEmployeeController extends EmployeeController {
  _InvitationEmployeeController(this.verification);

  final EmployeeInvitationVerification verification;

  @override
  EmployeeState build() => EmployeeState(invitationVerification: verification);

  @override
  Future<EmployeeInvitationVerification?> verifyInvitation(String token) async =>
      verification;
}

const _verification = EmployeeInvitationVerification(
  isValid: true,
  displayName: 'Ramesh Kumar',
  mobile: '9876543210',
  email: 'ramesh@example.test',
  roleCode: 'DELIVERY_STAFF',
  branchId: 7,
);
