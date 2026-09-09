import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_repository.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_repository.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

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
          overrides: [
            authRepositoryProvider.overrideWithValue(repository),
            // Sign out clears the guest cart; without an override the real
            // secure storage plugin throws MissingPluginException in tests.
            guestCartStorageProvider.overrideWithValue(
              GuestCartStorage(storage: _FakeFlutterSecureStorage()),
            ),
          ],
          child: const DoodhDirectApp(),
        ),
      );
      await tester.pumpAndSettle();

      // A fresh browser restores as a guest on the guest home; the login form
      // is only reached after the visitor explicitly asks to sign in.
      expect(find.text('Welcome to DoodhDirect'), findsOneWidget);
      expect(find.text('Sign in to your account'), findsNothing);
      final signInButton = find.widgetWithText(FilledButton, 'Sign in');
      // The prompt card sits at the bottom of the guest home list, so bring
      // it into the viewport before tapping.
      await tester.ensureVisible(signInButton);
      await tester.pumpAndSettle();
      await tester.tap(signInButton);
      await tester.pumpAndSettle();

      expect(find.text('Sign in to your account'), findsOneWidget);
      // /login opens in the OTP-first mode, so the password form is reached
      // through the explicit secondary affordance before entering credentials.
      await tester.tap(find.text('Use password instead'));
      await tester.pumpAndSettle();
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
      expect(find.text('Delivery route'), findsOneWidget);
      expect(find.text("Today's deliveries"), findsOneWidget);

      await tester.tap(find.byTooltip('Sign out'));
      await tester.pumpAndSettle();

      expect(repository.loggedOut, isTrue);
      expect(find.text('Sign in to your account'), findsOneWidget);
    },
  );

  testWidgets(
    'restored Dairy Manager session opens the shared branch delivery workspace',
    (tester) async {
      final auth = _AuthenticatedDairyManagerRepository();
      final deliveries = _EmptyDeliveryRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(auth),
          deliveryRepositoryProvider.overrideWithValue(deliveries),
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

      expect(find.text('Dairy workspace'), findsOneWidget);
      expect(find.text('Delivery Management'), findsOneWidget);
      expect(
        find.text(
          'Manage deliveries, generate subscription deliveries, and assign deliveries to delivery staff.',
        ),
        findsOneWidget,
      );

      final deliveryAction = find.ancestor(
        of: find.byIcon(Icons.local_shipping_outlined),
        matching: find.byType(ListTile),
      );
      expect(deliveryAction, findsOneWidget);
      final deliveryTile = tester.widget<ListTile>(deliveryAction);
      expect(deliveryTile.onTap, isNotNull);

      final router = container.read(routerProvider);
      router.go('/delivery-management/branch/7');
      await tester.pumpAndSettle();
      expect(
        router.routerDelegate.currentConfiguration.uri.path,
        '/delivery-management/branch/7',
      );
      expect(find.byType(DeliveryManagementScreen), findsOneWidget);
      expect(find.text('Branch 7 deliveries'), findsOneWidget);
      expect(deliveries.requestedBranchIds, [7]);
      expect(auth.restoreCount, 1);
    },
  );

  testWidgets(
    'restored Dairy Manager session renders the server-assigned branch name on the dashboard and delivery workspace',
    (tester) async {
      final auth = _AuthenticatedDairyManagerBranchRepository();
      final deliveries = _EmptyDeliveryRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(auth),
          deliveryRepositoryProvider.overrideWithValue(deliveries),
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

      // The dairy home resolves the branch name from the server-enriched
      // session metadata instead of a numeric fallback.
      expect(find.text('Dairy workspace'), findsOneWidget);
      expect(find.text('Dabua dairy dashboard'), findsOneWidget);
      expect(find.text('Branch 7 dairy dashboard'), findsNothing);

      final router = container.read(routerProvider);
      router.go('/delivery-management/branch/7');
      await tester.pumpAndSettle();
      expect(find.byType(DeliveryManagementScreen), findsOneWidget);
      expect(find.text('Dabua deliveries'), findsOneWidget);
      expect(find.text('Branch 7 deliveries'), findsNothing);
      expect(deliveries.requestedBranchIds, [7]);
      expect(auth.restoreCount, 1);
    },
  );

  testWidgets(
    'dashboard falls back to a numeric branch label when metadata is absent',
    (tester) async {
      final auth = _AuthenticatedDairyManagerUnknownBranchRepository();
      final deliveries = _EmptyDeliveryRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(auth),
          deliveryRepositoryProvider.overrideWithValue(deliveries),
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

      // Branch 7 is absent from branchDetails; the UI degrades to a numeric
      // label and never substitutes another branch (no Branch 4).
      expect(find.text('Branch 7 dairy dashboard'), findsOneWidget);
      expect(find.text('Dabua dairy dashboard'), findsNothing);
      expect(find.text('Branch 4 dairy dashboard'), findsNothing);
    },
  );

  group('authenticated customer routing', () {
    testWidgets('customer Home shows an empty Cart action without a badge', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      expect(find.byTooltip('Cart'), findsOneWidget);
      expect(find.text('0'), findsNothing);

      harness.router.go('/checkout');
      await tester.pumpAndSettle();

      expect(find.text('Your cart is empty'), findsOneWidget);
      expect(find.text('Continue Shopping'), findsOneWidget);

      await tester.tap(find.text('Continue Shopping'));
      await tester.pumpAndSettle();
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/catalogue',
      );
    });

    testWidgets('customer Home counts distinct Cart lines and opens Cart', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);
      final secondProduct = CatalogueProduct(
        publicId: '00000000-0000-0000-0000-000000000031',
        sku: 'CURD-500G',
        name: 'Curd',
        description: 'Fresh curd.',
        category: _catalogueProduct.category,
        unitOfMeasure: 'kilogram',
        price: 80,
        isActive: true,
        branchAvailability: _catalogueProduct.branchAvailability,
      );
      final controller = harness.container.read(
        orderControllerProvider.notifier,
      );
      controller.setCartItem(_catalogueProduct, 1);
      controller.setCartItem(secondProduct, 2.5);
      await tester.pump();

      final cartAction = find.ancestor(
        of: find.byTooltip('Cart'),
        matching: find.byType(IconButton),
      );
      expect(cartAction, findsOneWidget);
      expect(find.text('2'), findsOneWidget);

      await tester.tap(cartAction);
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/checkout',
      );
      expect(find.text('Your order'), findsOneWidget);
      expect(find.text(_catalogueProduct.name), findsOneWidget);
      expect(find.text('2.5 kilogram · ₹80.00 each'), findsOneWidget);
      expect(find.text('2.5'), findsOneWidget);
    });

    testWidgets('Shop Cart badge counts distinct Cart lines', (tester) async {
      final harness = await _pumpAuthenticatedApp(tester);
      final secondProduct = CatalogueProduct(
        publicId: '00000000-0000-0000-0000-000000000032',
        sku: 'PANEER-250G',
        name: 'Paneer',
        description: 'Fresh paneer.',
        category: _catalogueProduct.category,
        unitOfMeasure: 'kilogram',
        price: 120,
        isActive: true,
        branchAvailability: _catalogueProduct.branchAvailability,
      );
      final controller = harness.container.read(
        orderControllerProvider.notifier,
      );
      controller.setCartItem(_catalogueProduct, 1);
      controller.setCartItem(secondProduct, 2.5);
      harness.router.go('/catalogue');
      await tester.pumpAndSettle();

      expect(find.text('Cart (2)'), findsOneWidget);
    });

    testWidgets('add-to-cart snackbar dismisses automatically', (tester) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();
      expect(find.text('Add to cart'), findsOneWidget);

      await tester.tap(find.text('Add to cart'));
      await tester.pump();
      expect(
        find.text('${_catalogueProduct.name} added to your cart'),
        findsOneWidget,
      );

      await tester.pump(const Duration(seconds: 4));
      await tester.pumpAndSettle();
      expect(
        find.text('${_catalogueProduct.name} added to your cart'),
        findsNothing,
      );
    });

    testWidgets('View cart clears the add-to-cart snackbar before navigation', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(800, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();
      await tester.tap(find.text('Add to cart'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));
      expect(
        find.text('${_catalogueProduct.name} added to your cart'),
        findsOneWidget,
      );

      await tester.tap(find.text('View cart'));
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/checkout',
      );
      expect(
        find.text('${_catalogueProduct.name} added to your cart'),
        findsNothing,
      );

      await tester.pump(const Duration(seconds: 3));
    });

    testWidgets('forward payment navigation uses the supplied order', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/${_order.publicId}/payment', extra: _order);
      await tester.pumpAndSettle();

      expect(find.text(_order.orderNumber), findsOneWidget);
      expect(find.text('Amount due: ${_order.formattedTotal}'), findsOneWidget);
      expect(harness.orders.requestedOrderIds, isEmpty);
    });

    testWidgets('expired session redirects the protected route to login', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/deliveries/delivery-1');
      await tester.pumpAndSettle();
      await harness.container
          .read(sessionControllerProvider.notifier)
          .expireSession();
      await tester.pumpAndSettle();

      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/login',
      );
      expect(find.text('Sign in to your account'), findsOneWidget);
      expect(find.text('Authentication is required.'), findsNothing);
    });

    testWidgets(
      'customer shell exposes logout and re-enters guest browsing on home after sign out',
      (tester) async {
        final harness = await _pumpAuthenticatedApp(tester);

        expect(find.byTooltip('Sign out'), findsOneWidget);
        await tester.tap(find.byTooltip('Sign out'));
        await tester.pumpAndSettle();

        expect(harness.auth.loggedOut, isTrue);
        expect(
          harness.router.routerDelegate.currentConfiguration.uri.path,
          '/login',
        );
        expect(find.text('Sign in to your account'), findsOneWidget);

        // The public storefront is open to guests: after sign out, navigating
        // home auto-enters guest mode instead of staying locked on login.
        harness.router.go('/home');
        await tester.pumpAndSettle();
        expect(
          harness.router.routerDelegate.currentConfiguration.uri.path,
          '/home',
        );
        expect(find.text('Welcome to DoodhDirect'), findsOneWidget);
        expect(find.text('Sign in to your account'), findsNothing);
      },
    );

    testWidgets('restored payment URL loads order when extra is absent', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/${_order.publicId}/payment');
      await tester.pumpAndSettle();

      expect(find.text(_order.orderNumber), findsOneWidget);
      expect(harness.orders.requestedOrderIds, [_order.publicId]);
      expect(tester.takeException(), isNull);
    });

    testWidgets('back to a payment URL without extra does not crash', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/${_order.publicId}/payment');
      await tester.pumpAndSettle();
      harness.router.go('/home');
      await tester.pumpAndSettle();
      harness.router.go('/orders/${_order.publicId}/payment');
      await tester.pumpAndSettle();

      expect(find.text(_order.orderNumber), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('blank required route parameter shows a safe error state', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders/%20/payment');
      await tester.pumpAndSettle();

      expect(find.text('Invalid Order link'), findsOneWidget);
      expect(
        find.text('The required order identifier is missing.'),
        findsOneWidget,
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('customer detail routes pop back to their parent screens', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      harness.router.go('/orders');
      await tester.pumpAndSettle();
      harness.router.push('/orders/${_order.publicId}');
      await tester.pumpAndSettle();
      expect(find.text('Order details'), findsOneWidget);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('My orders'), findsOneWidget);

      harness.router.go('/catalogue');
      await tester.pumpAndSettle();
      harness.router.push('/catalogue/products/%20');
      await tester.pumpAndSettle();
      expect(find.text('Invalid Product link'), findsOneWidget);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('Catalogue'), findsOneWidget);

      harness.router.go('/customer/account');
      await tester.pumpAndSettle();
      harness.router.push('/customer/profile/edit');
      await tester.pumpAndSettle();
      expect(find.text('Edit profile'), findsOneWidget);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('My account'), findsOneWidget);

      harness.router.push('/customer/addresses/new');
      await tester.pumpAndSettle();
      expect(find.text('Add address'), findsOneWidget);
      expect(find.text('Latitude'), findsNothing);
      expect(find.text('Longitude'), findsNothing);
      expect(find.textContaining('enter coordinates manually'), findsNothing);
      harness.router.pop();
      await tester.pumpAndSettle();
      expect(find.text('My account'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('profile and address forms remain usable on narrow screens', (
      tester,
    ) async {
      await tester.binding.setSurfaceSize(const Size(360, 800));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final harness = await _pumpAuthenticatedApp(tester);
      expect(
        tester.takeException(),
        isNull,
        reason: 'customer home overflowed',
      );

      harness.router.go('/customer/profile/edit');
      await tester.pumpAndSettle();
      expect(find.text('Edit profile'), findsOneWidget);
      expect(tester.takeException(), isNull, reason: 'profile form overflowed');
      await tester.ensureVisible(find.text('Save profile'));
      expect(find.text('Save profile'), findsOneWidget);
      expect(tester.takeException(), isNull);

      harness.router.go('/customer/addresses/new');
      await tester.pumpAndSettle();
      expect(find.text('Add address'), findsOneWidget);
      expect(find.text('Latitude'), findsNothing);
      expect(find.text('Longitude'), findsNothing);
      expect(find.textContaining('enter coordinates manually'), findsNothing);
      await tester.scrollUntilVisible(
        find.text('Save address'),
        500,
        scrollable: find.byType(Scrollable).first,
      );
      expect(find.text('Save address'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
      'My Account consolidates all profile fields and opens Change Password',
      (tester) async {
        final harness = await _pumpAuthenticatedApp(
          tester,
          session: AuthSession(
            user: const AuthUser(
              publicUserId: '00000000-0000-0000-0000-000000000001',
              displayName: 'Customer User',
              email: 'customer@example.test',
              mobile: '9876543210',
              roles: ['CUSTOMER'],
              permissions: [],
              branchIds: [],
              hasPassword: true,
            ),
            accessToken: 'customer-access-token',
            refreshToken: 'customer-refresh-token',
            accessTokenExpiresAtUtc: DateTime.utc(2099),
            refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
          ),
          customerState: CustomerState(
            profile: CustomerProfile(
              publicId: 'customer-1',
              firstName: 'Rahul',
              lastName: 'Sharma',
              dateOfBirth: DateTime(1990, 8, 15),
              gender: 'Male',
              alternateMobile: '9876501234',
              customerNumber: 'CUS-1001',
            ),
          ),
        );

        harness.router.go('/customer/account');
        await tester.pumpAndSettle();

        // The consolidated My Account screen shows every profile/contact field
        // in one place — no 'Login & security' grouping remains.
        expect(find.text('My account'), findsOneWidget);
        expect(find.text('Customer number: CUS-1001'), findsOneWidget);
        for (final label in [
          'Name',
          'Last Name',
          'Alternate Number',
          'Gender',
          'Date of Birth',
          'Mobile Number',
          'Email',
        ]) {
          expect(find.text(label), findsOneWidget);
        }
        expect(find.text('Rahul'), findsOneWidget);
        expect(find.text('Sharma'), findsOneWidget);
        expect(find.text('9876501234'), findsOneWidget);
        expect(find.text('Male'), findsOneWidget);
        expect(find.text('15/08/1990'), findsOneWidget);
        expect(find.text('9876543210'), findsOneWidget);
        expect(find.text('customer@example.test'), findsOneWidget);

        // The Mobile Number row opens the preserved mobile change/OTP flow.
        await tester.tap(find.text('Mobile Number'));
        await tester.pumpAndSettle();
        expect(
          find.widgetWithText(AppBar, 'Change mobile'),
          findsOneWidget,
        );
        harness.router.pop();
        await tester.pumpAndSettle();
        expect(find.text('My account'), findsOneWidget);

        // The Email row opens the preserved email change/OTP flow (the seeded
        // session email is unverified, so the verification step shows first).
        await tester.tap(find.text('Email'));
        await tester.pumpAndSettle();
        expect(
          find.widgetWithText(AppBar, 'Verify email'),
          findsOneWidget,
        );
        harness.router.pop();
        await tester.pumpAndSettle();
        expect(find.text('My account'), findsOneWidget);

        // A user with a password routes to the dedicated Change Password screen,
        // which shows only password fields and none of the profile fields.
        expect(
          find.text('Update the password you sign in with.'),
          findsOneWidget,
        );
        await tester.ensureVisible(find.text('Change Password'));
        await tester.pumpAndSettle();
        await tester.tap(find.text('Change Password'));
        await tester.pumpAndSettle();

        expect(find.widgetWithText(AppBar, 'Change Password'), findsOneWidget);
        expect(find.text('Current Password'), findsOneWidget);
        expect(find.text('New Password'), findsOneWidget);
        expect(find.text('Confirm New Password'), findsOneWidget);
        expect(find.text('Mobile Number'), findsNothing);
        expect(find.text('Email'), findsNothing);
        expect(find.text('Rahul'), findsNothing);

        harness.router.pop();
        await tester.pumpAndSettle();
        expect(find.text('My account'), findsOneWidget);
        expect(tester.takeException(), isNull);
      },
    );
  });
}

Future<_RouterHarness> _pumpAuthenticatedApp(
  WidgetTester tester, {
  CatalogueRepository? catalogue,
  AuthSession? session,
  CustomerState? customerState,
}) async {
  final auth = _AuthenticatedCustomerRepository(session: session);
  final orders = _FakeOrderRepository();
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(auth),
      orderRepositoryProvider.overrideWithValue(orders),
      // The address editor fetches the Google Maps Web Client Key at runtime;
      // a deterministic fake keeps route tests HTTP-free. Returning null keeps
      // the picker showing its "not configured" panel.
      clientConfigurationRepositoryProvider.overrideWithValue(
        _FakeClientConfigurationRepository(),
      ),
      if (catalogue != null)
        catalogueRepositoryProvider.overrideWithValue(catalogue),
      if (customerState != null)
        customerControllerProvider.overrideWith(
          () => _SeededCustomerController(customerState),
        ),
      // Sign out clears the guest cart and cart mutations persist it; without
      // an override the real secure storage plugin throws MissingPluginException
      // in tests.
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: _FakeFlutterSecureStorage()),
      ),
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
  return _RouterHarness(container, orders, auth);
}

class _RouterHarness {
  const _RouterHarness(this.container, this.orders, this.auth);

  final ProviderContainer container;
  final _FakeOrderRepository orders;
  final _AuthenticatedCustomerRepository auth;

  GoRouter get router => container.read(routerProvider);
}

class _AuthenticatedDairyManagerRepository extends AuthRepository {
  int restoreCount = 0;

  @override
  Future<AuthSession?> restore() async {
    restoreCount += 1;
    return _dairyManagerSession;
  }
}

/// Session whose branchDetails resolve branch 7 to 'Dabua'; proves the dairy
/// home and delivery workspace render the server-assigned branch name.
class _AuthenticatedDairyManagerBranchRepository extends AuthRepository {
  int restoreCount = 0;

  @override
  Future<AuthSession?> restore() async {
    restoreCount += 1;
    return _dairyManagerSessionWithBranchDetails;
  }
}

/// Session whose branchDetails do NOT include branch 7; the UI must degrade to
/// a numeric label and never substitute another branch.
class _AuthenticatedDairyManagerUnknownBranchRepository extends AuthRepository {
  int restoreCount = 0;

  @override
  Future<AuthSession?> restore() async {
    restoreCount += 1;
    return _dairyManagerSessionUnknownBranch;
  }
}

class _AuthenticatedCustomerRepository extends AuthRepository {
  _AuthenticatedCustomerRepository({this.session});

  /// Optional session override; defaults to [_customerSession] so every
  /// customer route test keeps its existing behavior.
  final AuthSession? session;

  bool loggedOut = false;
  bool cleared = false;

  @override
  Future<AuthSession?> restore() async => session ?? _customerSession;

  @override
  Future<void> logout(AuthSession session) async {
    loggedOut = true;
  }

  @override
  Future<void> clear() async {
    cleared = true;
  }
}

/// Seeded customer controller so the My Account overview renders a profile
/// without hitting the network (same pattern as subscription_test.dart).
class _SeededCustomerController extends CustomerController {
  _SeededCustomerController(this.initialState);

  final CustomerState initialState;

  @override
  CustomerState build() => initialState;

  @override
  Future<void> load() async {}
}

class _FakeCatalogueRepository extends CatalogueRepository {
  _FakeCatalogueRepository(this.product)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final CatalogueProduct product;

  @override
  Future<CatalogueProduct> getProduct(String productId) async => product;
}

class _EmptyDeliveryRepository extends DeliveryRepository {
  _EmptyDeliveryRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<int> requestedBranchIds = [];

  @override
  Future<List<DeliveryDetails>> getBranch(
    String token,
    int branchId, {
    DateTime? date,
    DeliveryStatus? status,
    DeliverySourceType? sourceType,
    SubscriptionDeliverySlot? slot,
  }) async {
    requestedBranchIds.add(branchId);
    return [];
  }

  @override
  Future<List<DeliveryEmployee>> getEmployees(
    String token,
    int branchId,
  ) async => [];
}

class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<String> requestedOrderIds = [];

  @override
  Future<List<OrderSummary>> getMine(String token) async => [_order];

  @override
  Future<OrderSummary> get(String token, String orderId) async {
    requestedOrderIds.add(orderId);
    return _order;
  }
}

class _FakeClientConfigurationRepository extends ClientConfigurationRepository {
  _FakeClientConfigurationRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<ClientConfiguration> get(String token) async =>
      const ClientConfiguration();
}

final _catalogueProduct = CatalogueProduct(
  publicId: '00000000-0000-0000-0000-000000000030',
  sku: 'MILK-1L',
  name: 'Whole Milk',
  description: 'Fresh whole milk.',
  category: const ProductCategory(
    publicId: '00000000-0000-0000-0000-000000000040',
    code: 'MILK',
    name: 'Milk',
    description: 'Fresh dairy products.',
    isActive: true,
  ),
  unitOfMeasure: 'litre',
  price: 60,
  isActive: true,
  branchAvailability: const [
    BranchAvailability(
      branchId: '00000000-0000-0000-0000-000000000050',
      branchCode: 'CENTRAL',
      branchName: 'Central Dairy',
      isAvailable: true,
      maxDailyQuantity: 10,
    ),
  ],
);

final _order = OrderSummary(
  publicId: '00000000-0000-0000-0000-000000000010',
  orderNumber: 'ORD-TEST-10',
  type: 'OneTime',
  status: 'PendingPayment',
  createdAt: DateTime(2026, 8, 16),
  addressLabel: 'Home',
  city: 'Pune',
  branchName: 'Central Dairy',
  items: const [
    OrderItem(
      productId: '00000000-0000-0000-0000-000000000020',
      productName: 'Whole Milk',
      sku: 'MILK-1L',
      unitOfMeasure: 'litre',
      quantity: 1,
      unitPrice: 60,
      lineTotal: 60,
    ),
  ],
  subtotal: 60,
  discountAmount: 0,
  payableAmount: 60,
  cancelledAt: null,
  paymentPublicId: null,
  paymentStatus: null,
  gatewayPaymentId: null,
  deliveryPublicId: null,
  deliveryReferenceNumber: null,
  deliveryStatus: null,
);

final _dairyManagerSession = AuthSession(
  user: const AuthUser(
    publicUserId: '00000000-0000-0000-0000-000000000007',
    displayName: 'Dairy Manager User',
    email: 'dairy-manager@example.test',
    mobile: null,
    roles: ['DAIRY_MANAGER'],
    permissions: [
      'IDENTITY.BRANCH.ACCESS',
      'DELIVERIES.READ_BRANCH',
      'DELIVERIES.ASSIGN_BRANCH',
    ],
    branchIds: [7],
  ),
  accessToken: 'dairy-manager-access-token',
  refreshToken: 'dairy-manager-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

/// Dairy-manager session carrying server-enriched branch metadata so the
/// dashboard/delivery labels resolve to 'Dabua' instead of 'Branch 7'.
final _dairyManagerSessionWithBranchDetails = AuthSession(
  user: const AuthUser(
    publicUserId: '00000000-0000-0000-0000-000000000007',
    displayName: 'Dairy Manager User',
    email: 'dairy-manager@example.test',
    mobile: null,
    roles: ['DAIRY_MANAGER'],
    permissions: [
      'IDENTITY.BRANCH.ACCESS',
      'DELIVERIES.READ_BRANCH',
      'DELIVERIES.ASSIGN_BRANCH',
    ],
    branchIds: [7],
    branchDetails: [AuthUserBranchInfo(id: 7, code: 'MAIN', name: 'Dabua')],
  ),
  accessToken: 'dairy-manager-access-token',
  refreshToken: 'dairy-manager-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

/// Dairy-manager session whose metadata covers a different branch id; branch 7
/// is unknown so the UI must fall back to a numeric label.
final _dairyManagerSessionUnknownBranch = AuthSession(
  user: const AuthUser(
    publicUserId: '00000000-0000-0000-0000-000000000007',
    displayName: 'Dairy Manager User',
    email: 'dairy-manager@example.test',
    mobile: null,
    roles: ['DAIRY_MANAGER'],
    permissions: [
      'IDENTITY.BRANCH.ACCESS',
      'DELIVERIES.READ_BRANCH',
      'DELIVERIES.ASSIGN_BRANCH',
    ],
    branchIds: [7],
    branchDetails: [AuthUserBranchInfo(id: 3, code: 'NIT3', name: 'NIT3')],
  ),
  accessToken: 'dairy-manager-access-token',
  refreshToken: 'dairy-manager-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _customerSession = AuthSession(
  user: const AuthUser(
    publicUserId: '00000000-0000-0000-0000-000000000001',
    displayName: 'Customer User',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-access-token',
  refreshToken: 'customer-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

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
