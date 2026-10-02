import 'dart:async';

import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_repository.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_repository.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart'
    hide SubscriptionDeliverySlot;
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_screens.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_repository.dart';
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
    testWidgets('customer Home renders the dashboard sections and actions', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      expect(find.text('DoodhDirect'), findsWidgets);
      expect(find.byKey(const ValueKey('home-search')), findsOneWidget);
      expect(find.byKey(const ValueKey('my-deliveries-card')), findsOneWidget);
      expect(find.text('My Deliveries'), findsOneWidget);
      // Header actions stay mounted only while the top of the scroll view is
      // visible, so verify them before scrolling deeper into the dashboard.
      expect(find.byKey(const ValueKey('home-wallet-action')), findsOneWidget);
      expect(
        find.byWidgetPredicate(
          (widget) =>
              widget is Tooltip &&
              (widget.message?.contains('125.50') ?? false),
        ),
        findsOneWidget,
      );
      // The Home header bell is the only notification action; the customer
      // shell no longer renders a duplicate bell.
      expect(find.byTooltip('Notifications'), findsOneWidget);

      await tester.drag(
        find.byKey(const ValueKey('customer-home-scroll')),
        const Offset(0, -500),
      );
      await tester.pumpAndSettle();
      // The reference-layout "Upcoming Deliveries" section sits inside the My
      // Deliveries band (reintroduced by business request) with the Add Item
      // affordance next to its title.
      expect(
        find.byKey(const ValueKey('home-upcoming-deliveries-title')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('home-add-item-action')),
        findsOneWidget,
      );
      // The Quick actions grid was removed (business request): the bottom
      // navigation and the My Deliveries band cover those destinations.
      expect(find.text('Quick actions'), findsNothing);
      expect(find.byKey(const ValueKey('home-calendar-action')), findsNothing);
      expect(tester.takeException(), isNull);

      harness.router.go('/home');
      await tester.pumpAndSettle();
      expect(
        harness.router.routerDelegate.currentConfiguration.uri.path,
        '/home',
      );
    });

    testWidgets(
      'My Deliveries band uses the tan surface with the four-state legend',
      (tester) async {
        await _pumpAuthenticatedApp(tester);

        final band = find.byKey(const ValueKey('my-deliveries-card'));
        expect(band, findsOneWidget);
        // Reference language: the delivery/subscription band carries the warm
        // tan surface instead of the previous mint.
        expect(tester.widget<DoodhCard>(band).color, DoodhColors.tanSurface);

        // Four-state legend mapped to the real delivery statuses.
        expect(
          find.descendant(of: band, matching: find.text('Vacation / Skipped')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: band, matching: find.text('Delivered')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: band, matching: find.text('Upcoming')),
          findsOneWidget,
        );
        expect(
          find.descendant(of: band, matching: find.text('No Delivery')),
          findsOneWidget,
        );

        // The delivery calendar shows exactly seven calendar dates (the
        // restored one-week window): every tile declares its weekday + day in
        // its semantics label.
        expect(
          find.descendant(
            of: band,
            matching: find.byWidgetPredicate(
              (widget) =>
                  widget is Semantics &&
                  widget.properties.label != null &&
                  RegExp(r'^\w{3}, \d{1,2}, ')
                      .hasMatch(widget.properties.label!),
            ),
          ),
          findsNWidgets(7),
        );

        // Today is marked selected in semantics and renders the larger
        // square 84px tile (reference layout); on compact phones the same
        // tile shrinks to 72px so the strip scrolls instead of squeezing.
        final todayTile = find.descendant(
          of: band,
          matching: find.byWidgetPredicate(
            (widget) =>
                widget is Semantics && (widget.properties.selected ?? false),
          ),
        );
        expect(todayTile, findsOneWidget);
        final todaySize = tester.getSize(todayTile);
        expect(todaySize.width, 84);
        expect(todaySize.height, 84);

        // A colored status dot on every tile plus one dot per legend entry
        // (7 + 4).
        final dots = find.descendant(
          of: band,
          matching: find.byWidgetPredicate(
            (widget) =>
                widget is Container &&
                widget.decoration is BoxDecoration &&
                (widget.decoration! as BoxDecoration).shape == BoxShape.circle,
          ),
        );
        expect(dots, findsNWidgets(11));
      },
    );

    testWidgets(
      'My Deliveries calendar stays usable at compact and wide widths',
      (tester) async {
        // Small phone: all seven tiles shrink so the week fits.
        tester.view.physicalSize = const Size(360, 900);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        await _pumpAuthenticatedApp(tester);

        final band = find.byKey(const ValueKey('my-deliveries-card'));
        // At 360px the lazy strip only builds on-screen tiles, so assert the
        // calendar's exact item count at the widget level: exactly 7 dates.
        final strip = tester.widget<ListView>(
          find.descendant(of: band, matching: find.byType(ListView)),
        );
        final delegate = strip.childrenDelegate as SliverChildBuilderDelegate;
        // ListView.separated builds one separator between dates:
        // 7 dates + 6 separators = 13 children.
        expect(delegate.childCount, 13);
        final compactToday = find.descendant(
          of: band,
          matching: find.byWidgetPredicate(
            (widget) =>
                widget is Semantics && (widget.properties.selected ?? false),
          ),
        );
        expect(compactToday, findsOneWidget);
        final compactSize = tester.getSize(compactToday);
        // Square tiles at both breakpoints.
        expect(compactSize.width, 72);
        expect(compactSize.height, 72);
        expect(tester.takeException(), isNull);

        // Tablet/web width: the tiles keep the larger reference sizing.
        tester.view.physicalSize = const Size(1440, 1000);
        await tester.pumpAndSettle();
        final wideToday = find.descendant(
          of: band,
          matching: find.byWidgetPredicate(
            (widget) =>
                widget is Semantics && (widget.properties.selected ?? false),
          ),
        );
        expect(wideToday, findsOneWidget);
        final wideSize = tester.getSize(wideToday);
        expect(wideSize.width, 84);
        expect(wideSize.height, 84);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets('My Deliveries dates are generated from the real clock', (
      tester,
    ) async {
      await _pumpAuthenticatedApp(tester);

      // The today tile is derived from the current date at build time — its
      // label carries today's weekday and day number, never hard-coded dates.
      final now = DateTime.now();
      final weekday = const [
        'Mon',
        'Tue',
        'Wed',
        'Thu',
        'Fri',
        'Sat',
        'Sun',
      ][now.weekday - 1];
      final todayTile = find.descendant(
        of: find.byKey(const ValueKey('my-deliveries-card')),
        matching: find.byWidgetPredicate(
          (widget) =>
              widget is Semantics && (widget.properties.selected ?? false),
        ),
      );
      final label = tester.firstWidget<Semantics>(todayTile).properties.label!;
      expect(label, contains('$weekday, ${now.day}, '));
      expect(label, contains(', today'));
    });

    testWidgets('wallet balance renders as a visible header chip', (
      tester,
    ) async {
      await _pumpAuthenticatedApp(tester);

      final walletChip = find.byKey(const ValueKey('home-wallet-action'));
      expect(walletChip, findsOneWidget);
      // The chip shows the server-authoritative balance, not just an icon.
      expect(
        find.descendant(
          of: walletChip,
          matching: find.textContaining('125.50'),
        ),
        findsOneWidget,
      );
    });

    testWidgets(
      'customer Home keeps a single notification bell with More menu access',
      (tester) async {
        final harness = await _pumpAuthenticatedApp(tester);

        // The customer shell no longer renders its own bell, so the Home
        // header bell is the only notification action. The fake notification
        // repository reports three unread notifications.
        expect(find.byTooltip('Notifications'), findsOneWidget);
        final badge = find.byKey(const ValueKey('home-notification-count'));
        expect(badge, findsOneWidget);
        expect(tester.widget<Text>(badge).data, '3');

        // The bell pushes the notification inbox above Home.
        await tester.tap(find.byTooltip('Notifications'));
        await tester.pumpAndSettle();
        expect(
          harness
              .router
              .routerDelegate
              .currentConfiguration
              .matches
              .last
              .matchedLocation,
          '/notifications',
        );
        expect(find.text('No notifications'), findsOneWidget);

        harness.router.go('/home');
        await tester.pumpAndSettle();

        // The More menu still exposes the Notifications destination.
        await tester.tap(
          find.byKey(const ValueKey('customer-more-navigation')),
        );
        await tester.pumpAndSettle();
        final notificationsEntry = find.text('Notifications');
        await tester.scrollUntilVisible(
          notificationsEntry,
          120,
          scrollable: find.byType(Scrollable).last,
        );
        expect(notificationsEntry, findsOneWidget);
      },
    );

    testWidgets('customer Home My Calendar band action opens the calendar', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      final bandAction = find.byKey(const ValueKey('home-my-calendar-action'));
      await tester.ensureVisible(bandAction);
      await tester.pumpAndSettle();
      await tester.tap(bandAction);
      await tester.pumpAndSettle();

      // My Calendar opens the dedicated customer-wide calendar screen.
      expect(
        harness
            .router
            .routerDelegate
            .currentConfiguration
            .matches
            .last
            .matchedLocation,
        '/my-calendar',
      );
      expect(find.text('My Calendar'), findsOneWidget);
    });

    testWidgets('customer Home remains usable at compact width', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(360, 800);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      await _pumpAuthenticatedApp(tester);

      expect(find.byKey(const ValueKey('my-deliveries-card')), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('customer Home shows an empty Cart action without a badge', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(tester);

      expect(
        find.byKey(const ValueKey('customer-cart-action')),
        findsOneWidget,
      );
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

      final cartAction = find.byKey(const ValueKey('customer-cart-action'));
      expect(cartAction, findsOneWidget);
      // The distinct line count is preserved in both customer Cart entry points:
      // the existing AppBar action and the new bottom navigation destination.
      // Scope each assertion: an unscoped find.text('2') would also match the
      // My Deliveries date strip whenever the 5-day window contains the 2nd.
      expect(
        find.descendant(
          of: find.byKey(const ValueKey('customer-cart-action')),
          matching: find.text('2'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byType(NavigationBar),
          matching: find.text('2'),
        ),
        findsOneWidget,
      );

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

      expect(
        find.byKey(const ValueKey('catalogue-header-cart')),
        findsOneWidget,
      );
      expect(find.byTooltip('Cart, 2 product lines'), findsOneWidget);
    });

    testWidgets('add to cart shows no confirmation snackbar', (tester) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();
      expect(find.text('Add to cart'), findsOneWidget);

      await tester.tap(find.text('Add to cart'));
      await tester.pump();

      // The add-to-cart confirmation snackbar was removed: the header cart
      // badge is the feedback surface.
      expect(
        find.text('${_catalogueProduct.name} added to your cart'),
        findsNothing,
      );
      final cart = harness.container.read(orderControllerProvider).cart;
      expect(cart, hasLength(1));
      expect(cart.single.quantity, 1);
    });

    testWidgets('catalogue list add to cart shows no confirmation snackbar', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(
          _catalogueProduct,
          products: [_catalogueProduct],
        ),
      );

      harness.router.go('/catalogue');
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Add to cart').first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Add to cart').first);
      await tester.pump();

      expect(
        find.text('${_catalogueProduct.name} added to your cart'),
        findsNothing,
      );
      expect(
        harness.container.read(orderControllerProvider).cart,
        hasLength(1),
      );
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

        await tester.tap(
          find.byKey(const ValueKey('customer-more-navigation')),
        );
        await tester.pumpAndSettle();
        final signOut = find.byKey(const ValueKey('customer-more-sign-out'));
        await tester.scrollUntilVisible(
          signOut,
          120,
          scrollable: find.byType(Scrollable).last,
        );
        await tester.tap(signOut);
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
      'My Account makes the default delivery address visually obvious',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(600, 1600));
        addTearDown(() => tester.binding.setSurfaceSize(null));
        final harness = await _pumpAuthenticatedApp(
          tester,
          customerState: const CustomerState(
            profile: CustomerProfile(
              publicId: 'customer-1',
              firstName: 'Rahul',
              lastName: 'Sharma',
              dateOfBirth: null,
              gender: 'Male',
              alternateMobile: null,
              customerNumber: 'CUS-1001',
            ),
            addresses: [
              CustomerAddress(
                publicId: 'address-home',
                label: 'Home',
                addressLine1: '12 Milk Lane',
                addressLine2: null,
                locality: 'Kothrud',
                city: 'Pune',
                state: 'Maharashtra',
                pinCode: '411038',
                landmark: null,
                deliveryInstructions: null,
                contactName: 'Rahul Sharma',
                contactMobile: '9876501234',
                latitude: 18.5074,
                longitude: 73.8077,
                isDefault: true,
                isActive: true,
              ),
              CustomerAddress(
                publicId: 'address-office',
                label: 'Office',
                addressLine1: '5 Dairy Road',
                addressLine2: null,
                locality: 'Baner',
                city: 'Pune',
                state: 'Maharashtra',
                pinCode: '411045',
                landmark: null,
                deliveryInstructions: null,
                contactName: 'Rahul Sharma',
                contactMobile: '9876501234',
                latitude: 18.5590,
                longitude: 73.7868,
                isDefault: false,
                isActive: true,
              ),
            ],
          ),
        );

        harness.router.go('/customer/account');
        await tester.pumpAndSettle();

        await tester.scrollUntilVisible(
          find.text('Delivery addresses'),
          200,
          scrollable: find.byType(Scrollable).first,
        );

        // The default address is announced as such and carries the status pill,
        // while the non-default address is a plain entry with the "Set as
        // default" affordance available from its menu.
        expect(
          find.bySemanticsLabel('Home, default delivery address'),
          findsOneWidget,
        );
        expect(find.text('Default'), findsOneWidget);
        expect(
          find.descendant(
            of: find.bySemanticsLabel('Home, default delivery address'),
            matching: find.text('Home'),
          ),
          findsOneWidget,
        );
        expect(find.text('Office'), findsOneWidget);
        expect(find.byIcon(Icons.star), findsOneWidget);
        expect(find.byIcon(Icons.location_on_outlined), findsOneWidget);

        // Only the non-default address offers "Set as default".
        await tester.tap(
          find.descendant(
            of: find.bySemanticsLabel('Office'),
            matching: find.byIcon(Icons.more_vert),
          ),
        );
        await tester.pumpAndSettle();
        expect(find.text('Set as default'), findsOneWidget);
        expect(tester.takeException(), isNull);
      },
    );

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
        // in one place. Internal identifiers (customer number) are not shown.
        expect(find.text('My account'), findsOneWidget);
        expect(find.text('Customer number: CUS-1001'), findsNothing);
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
        // The rows sit below the fold in the two-column layout, so scroll
        // them into view first (offstage widgets are skipped by finders).
        await tester.scrollUntilVisible(
          find.text('Mobile Number'),
          200,
          scrollable: find.byType(Scrollable).first,
        );
        await tester.pumpAndSettle();
        await tester.tap(find.text('Mobile Number'));
        await tester.pumpAndSettle();
        expect(find.widgetWithText(AppBar, 'Change mobile'), findsOneWidget);
        harness.router.pop();
        await tester.pumpAndSettle();
        expect(find.text('My account'), findsOneWidget);

        // The Email row opens the preserved email change/OTP flow (the seeded
        // session email is unverified, so the verification step shows first).
        await tester.scrollUntilVisible(
          find.text('Email'),
          200,
          scrollable: find.byType(Scrollable).first,
        );
        await tester.pumpAndSettle();
        await tester.tap(find.text('Email'));
        await tester.pumpAndSettle();
        expect(find.widgetWithText(AppBar, 'Verify email'), findsOneWidget);
        harness.router.pop();
        await tester.pumpAndSettle();
        expect(find.text('My account'), findsOneWidget);

        // A user with a password routes to the dedicated Change Password screen,
        // which shows only password fields and none of the profile fields.
        //
        // The account action sits below the personal-information card, so on a
        // short viewport it starts offstage — scroll it into view before
        // asserting and tapping (offstage widgets are skipped by finders).
        await tester.scrollUntilVisible(
          find.text('Change Password'),
          200,
          scrollable: find.byType(Scrollable).first,
        );
        await tester.pumpAndSettle();
        expect(
          find.text('Update the password you sign in with.'),
          findsOneWidget,
        );
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

    testWidgets('product detail shows the branded image fallback and label', (
      tester,
    ) async {
      // Dispose inside the body: `addTearDown` runs after the framework's
      // end-of-test semantics-handle verification and would be reported as a
      // leak.
      final semantics = tester.ensureSemantics();
      try {
        final harness = await _pumpAuthenticatedApp(
          tester,
          catalogue: _FakeCatalogueRepository(_catalogueProduct),
        );

        harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
        await tester.pumpAndSettle();

        // No image URL is supplied, so the shared branded fallback is shown -
        // the screen never fabricates product photography.
        expect(
          find.bySemanticsLabel('Whole Milk product image'),
          findsOneWidget,
        );
        expect(find.text('DoodhDirect'), findsOneWidget);
        expect(tester.takeException(), isNull);
      } finally {
        semantics.dispose();
      }
    });

    testWidgets('product detail shows the name, price and unit hierarchy', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      // The name renders in both the AppBar and the purchase panel.
      expect(find.text('Whole Milk'), findsNWidgets(2));
      expect(find.text('MILK'), findsOneWidget);
      expect(find.text('₹60'), findsOneWidget);
      expect(find.text('per litre'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('product detail announces availability and branch limits', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      expect(find.text('Available'), findsOneWidget);
      expect(find.text('About this product'), findsOneWidget);
      expect(find.text('Fresh whole milk.'), findsOneWidget);
      expect(find.text('Available at 1 branch'), findsOneWidget);
      expect(find.text('Central Dairy'), findsOneWidget);
      expect(find.text('Daily limit: 10 litre'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('out-of-stock product detail shows a warning banner', (
      tester,
    ) async {
      final outOfStock = CatalogueProduct(
        publicId: '00000000-0000-0000-0000-000000000031',
        sku: 'MILK-OOS',
        name: 'Limited Milk',
        description: 'Seasonal milk.',
        category: _catalogueProduct.category,
        unitOfMeasure: 'litre',
        price: 75,
        isActive: true,
        branchAvailability: const [
          BranchAvailability(
            branchId: '00000000-0000-0000-0000-000000000051',
            branchCode: 'SOUTH',
            branchName: 'South Dairy',
            isAvailable: false,
            maxDailyQuantity: null,
          ),
        ],
      );
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(outOfStock),
      );

      harness.router.go('/catalogue/products/${outOfStock.publicId}');
      await tester.pumpAndSettle();

      expect(find.text('Out of stock'), findsOneWidget);
      expect(
        find.text(
          'This product is currently out of stock. '
          'See its branch availability below.',
        ),
        findsOneWidget,
      );
      expect(find.text('Not available'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('product detail rejects a non-positive quantity', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '0');
      await tester.tap(find.text('Add to cart'));
      await tester.pump();

      expect(find.text('Enter a positive quantity'), findsOneWidget);
      expect(harness.container.read(orderControllerProvider).cart, isEmpty);

      await tester.pump(const Duration(seconds: 5));
      await tester.pumpAndSettle();
    });

    testWidgets('product detail rejects more than three decimal places', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '1.2345');
      await tester.tap(find.text('Add to cart'));
      await tester.pump();

      expect(find.text('Use up to three decimal places'), findsOneWidget);
      expect(harness.container.read(orderControllerProvider).cart, isEmpty);

      await tester.pump(const Duration(seconds: 5));
      await tester.pumpAndSettle();
    });

    testWidgets('product detail adds the typed quantity to the cart', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextFormField), '2.5');
      await tester.tap(find.text('Add to cart'));
      await tester.pump();

      // No confirmation snackbar: the cart itself is the feedback.
      expect(find.text('Whole Milk added to your cart'), findsNothing);
      final cart = harness.container.read(orderControllerProvider).cart;
      expect(cart, hasLength(1));
      expect(cart.single.quantity, 2.5);
    });

    testWidgets('product detail Subscribe action opens subscription setup', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      await tester.tap(find.text('Subscribe'));
      await tester.pumpAndSettle();

      // The action uses `context.push`, an imperative navigation: the pushed
      // route renders on top of the product detail. `routerDelegate
      // .currentConfiguration.uri` only tracks the base location for `go`
      // navigations, so the assertion targets the rendered screen instead.
      expect(find.byType(SubscriptionSetupScreen), findsOneWidget);
      expect(find.widgetWithText(AppBar, 'New subscription'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('product detail lists related products in the same category', (
      tester,
    ) async {
      // A tall surface keeps the related-products sliver inside the viewport;
      // it is the second sliver and is otherwise never laid out at 800x600.
      await tester.binding.setSurfaceSize(const Size(800, 2400));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final related = CatalogueProduct(
        publicId: '00000000-0000-0000-0000-000000000031',
        sku: 'CURD-500G',
        name: 'Fresh Curd',
        description: 'Creamy curd.',
        category: _catalogueProduct.category,
        unitOfMeasure: 'kilogram',
        price: 80,
        isActive: true,
        branchAvailability: _catalogueProduct.branchAvailability,
      );
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(
          _catalogueProduct,
          products: [_catalogueProduct, related],
        ),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      // The detail screen reuses the already-loaded catalogue list.
      await harness.container.read(catalogueControllerProvider.notifier).load();
      await tester.pumpAndSettle();

      expect(find.text('More in Milk'), findsOneWidget);
      expect(find.text('Fresh Curd'), findsOneWidget);
      // The related card reuses the shared action, so the label now appears on
      // both the sticky bar and the related card.
      expect(find.text('Add to cart'), findsNWidgets(2));
      expect(tester.takeException(), isNull);
    });

    testWidgets('product detail shows a loader until the product arrives', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _PendingCatalogueRepository(),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 500));

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      expect(find.text('Add to cart'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('product detail surfaces a load failure with retry', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FailingCatalogueRepository(),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);
      expect(find.text('Add to cart'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('product detail stays usable at compact and wide widths', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(360, 1200);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      expect(find.text('Quantity'), findsOneWidget);
      expect(find.text('Add to cart'), findsOneWidget);
      expect(tester.takeException(), isNull);

      // Widening switches the same content to the two-column layout.
      tester.view.physicalSize = const Size(1400, 1000);
      await tester.pumpAndSettle();

      expect(find.text('Quantity'), findsOneWidget);
      expect(find.text('Add to cart'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('product detail sticky bar shows the live quantity total', (
      tester,
    ) async {
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      final sticky = find.byKey(const ValueKey('product-detail-sticky-total'));
      expect(sticky, findsOneWidget);
      // Default quantity 1 → the authoritative unit price.
      expect(tester.widget<Text>(sticky).data, '₹60.00');

      // Editing the quantity updates the preview from the same unit price.
      await tester.enterText(find.byType(TextFormField), '2.5');
      await tester.pump();
      expect(tester.widget<Text>(sticky).data, '₹150.00');
    });

    testWidgets('product detail sticky total is announced to assistive tech', (
      tester,
    ) async {
      // Enable semantics before pumping so the nodes are generated.
      final semanticsHandle = tester.ensureSemantics();
      final harness = await _pumpAuthenticatedApp(
        tester,
        catalogue: _FakeCatalogueRepository(_catalogueProduct),
      );

      harness.router.go('/catalogue/products/${_catalogueProduct.publicId}');
      await tester.pumpAndSettle();

      // The merged semantics node for the total announces the full amount,
      // not just the visible shorthand.
      final semantics = tester.getSemantics(
        find.byKey(const ValueKey('product-detail-sticky-total')),
      );
      expect(semantics.label, contains('Sticky total'));
      expect(semantics.label, contains('₹60.00'));
      semanticsHandle.dispose();
    });
  });

  group('guest product detail', () {
    testWidgets(
      'guest can browse a product and is sent to sign in to subscribe',
      (tester) async {
        final container = await _pumpGuestApp(
          tester,
          catalogue: _FakeCatalogueRepository(_catalogueProduct),
        );

        // The public storefront keeps a deep link to a product reachable without
        // an account.
        container
            .read(routerProvider)
            .go('/catalogue/products/${_catalogueProduct.publicId}');
        await tester.pumpAndSettle();

        expect(find.text('Whole Milk'), findsWidgets);
        expect(find.text('₹60'), findsOneWidget);
        expect(find.text('Quantity'), findsOneWidget);
        // Guest browsing preserves the purchase affordances.
        expect(find.text('Add to cart'), findsOneWidget);
        expect(find.text('Subscribe'), findsOneWidget);

        // Subscription setup is a protected action: the guest is routed to sign
        // in instead of reaching the protected route.
        await tester.tap(find.text('Subscribe'));
        await tester.pumpAndSettle();

        expect(find.text('Sign in to your account'), findsOneWidget);
        expect(find.widgetWithText(AppBar, 'New subscription'), findsNothing);
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
      deliveryRepositoryProvider.overrideWithValue(
        _CustomerHomeDeliveryRepository(),
      ),
      subscriptionRepositoryProvider.overrideWithValue(
        _CustomerHomeSubscriptionRepository(),
      ),
      walletRepositoryProvider.overrideWithValue(
        _CustomerHomeWalletRepository(),
      ),
      notificationRepositoryProvider.overrideWithValue(
        _CustomerHomeNotificationRepository(),
      ),
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

/// Boots the app as a guest: [_FakeAuthRepository.restore] resolves to null, so
/// the public-storefront redirect auto-enters guest browsing on the home route.
Future<ProviderContainer> _pumpGuestApp(
  WidgetTester tester, {
  required CatalogueRepository catalogue,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_FakeAuthRepository()),
      orderRepositoryProvider.overrideWithValue(_FakeOrderRepository()),
      catalogueRepositoryProvider.overrideWithValue(catalogue),
      // Cart mutations and sign out persist the guest cart; without an override
      // the real secure storage plugin throws MissingPluginException in tests.
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
  return container;
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
  _FakeCatalogueRepository(this.product, {this.products})
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final CatalogueProduct product;

  /// Optional catalogue seeding for tests that assert related products. When
  /// omitted the provider stays empty, which keeps the existing single-product
  /// detail tests free of related-product cards.
  final List<CatalogueProduct>? products;

  @override
  Future<List<ProductCategory>> getCategories() async => [product.category];

  @override
  Future<List<CatalogueProduct>> getProducts({String? categoryId}) async =>
      products ?? const [];

  @override
  Future<CatalogueProduct> getProduct(String productId) async => product;
}

/// Catalogue repository whose product lookup never completes so the detail
/// screen's loading panel can be asserted deterministically.
class _PendingCatalogueRepository extends CatalogueRepository {
  _PendingCatalogueRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<ProductCategory>> getCategories() async => const [];

  @override
  Future<List<CatalogueProduct>> getProducts({String? categoryId}) async =>
      const [];

  @override
  Future<CatalogueProduct> getProduct(String productId) =>
      Completer<CatalogueProduct>().future;
}

/// Catalogue repository that fails the product lookup so the error panel can
/// be asserted without any network access.
class _FailingCatalogueRepository extends CatalogueRepository {
  _FailingCatalogueRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<ProductCategory>> getCategories() async => const [];

  @override
  Future<List<CatalogueProduct>> getProducts({String? categoryId}) async =>
      const [];

  @override
  Future<CatalogueProduct> getProduct(String productId) async {
    throw const ApiException(500, 'server_error', 'Catalogue unavailable');
  }
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

class _CustomerHomeDeliveryRepository extends DeliveryRepository {
  _CustomerHomeDeliveryRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<CustomerDelivery>> getMine(String token) async => const [];
}

class _CustomerHomeSubscriptionRepository extends SubscriptionRepository {
  _CustomerHomeSubscriptionRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<SubscriptionDetails>> getMine(String token) async => const [];
}

class _CustomerHomeWalletRepository extends WalletRepository {
  _CustomerHomeWalletRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<WalletDetails> get(String token) async => WalletDetails(
    publicId: 'wallet-1',
    balance: 125.50,
    currency: 'INR',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );

  @override
  Future<List<WalletTransaction>> getTransactions(String token) async =>
      const [];
}

class _CustomerHomeNotificationRepository extends NotificationRepository {
  _CustomerHomeNotificationRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<NotificationPage> getNotifications(
    String token, {
    int page = 1,
    int pageSize = 20,
    bool? isRead,
  }) async => NotificationPage(
    items: const [],
    page: page,
    pageSize: pageSize,
    totalCount: 0,
  );

  @override
  Future<int> getUnreadCount(String token) async => 3;
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
