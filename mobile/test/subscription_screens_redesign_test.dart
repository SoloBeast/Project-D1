import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

final _authenticatedSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2026, 8, 16, 6),
  refreshTokenExpiresAtUtc: DateTime.utc(2026, 9, 16),
);

class _AuthenticatedAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _authenticatedSession;
}

class _SeededSubscriptionController extends SubscriptionController {
  _SeededSubscriptionController(this.initialState);

  final SubscriptionState initialState;

  @override
  SubscriptionState build() => initialState;
}

class _SeededCatalogueController extends CatalogueController {
  _SeededCatalogueController(this.initialState);

  final CatalogueState initialState;

  @override
  CatalogueState build() => initialState;
}

class _SeededCustomerController extends CustomerController {
  _SeededCustomerController(this.initialState);

  final CustomerState initialState;

  @override
  CustomerState build() => initialState;
}

CatalogueProduct _product() => CatalogueProduct(
  publicId: 'product-1',
  sku: 'MILK-1L',
  name: 'Whole Milk',
  description: null,
  category: const ProductCategory(
    publicId: 'category-1',
    code: 'MILK',
    name: 'Milk',
    description: null,
    isActive: true,
  ),
  unitOfMeasure: 'litre',
  price: 60,
  isActive: true,
  branchAvailability: [
    BranchAvailability(
      branchId: 'branch-1',
      branchCode: 'CENTRAL',
      branchName: 'Central Dairy',
      isAvailable: true,
      maxDailyQuantity: null,
    ),
  ],
);

CustomerAddress _address() => CustomerAddress(
  publicId: 'address-1',
  label: 'Home',
  addressLine1: '1 Main Street',
  addressLine2: null,
  locality: 'Kothrud',
  city: 'Pune',
  state: 'Maharashtra',
  pinCode: '411001',
  landmark: null,
  deliveryInstructions: null,
  contactName: 'Test Customer',
  contactMobile: '9999999999',
  latitude: 18.5204,
  longitude: 73.8567,
  isDefault: true,
  isActive: true,
);

SubscriptionDetails _subscription({
  SubscriptionStatus status = SubscriptionStatus.active,
}) => SubscriptionDetails(
  publicId: 'subscription-1',
  status: status,
  productId: 'product-1',
  productSku: 'MILK-1L',
  productName: 'Whole Milk',
  unitOfMeasure: 'litre',
  quantity: 1.125,
  unitPrice: 60,
  payableAmount: 2025,
  startDate: DateTime(2026, 8, 17),
  endDate: DateTime(2026, 10, 23),
  totalEntitlement: 30,
  usedEntitlement: 6,
  remainingEntitlement: 24,
  addressId: 'address-1',
  address: 'Home, 1 Main Street, Pune 411001',
  branchId: 'branch-1',
  branchCode: 'CENTRAL',
  branchName: 'Central Dairy',
  schedules: const [
    SubscriptionSchedule(
      dayOfWeek: DeliveryWeekday.monday,
      slot: SubscriptionDeliverySlot.morning,
    ),
    SubscriptionSchedule(
      dayOfWeek: DeliveryWeekday.wednesday,
      slot: SubscriptionDeliverySlot.morning,
    ),
    SubscriptionSchedule(
      dayOfWeek: DeliveryWeekday.friday,
      slot: SubscriptionDeliverySlot.morning,
    ),
  ],
  activatedAt: DateTime(2026, 8, 16, 10),
  pausedAt: null,
  cancelledAt: null,
  completedAt: null,
  createdAt: DateTime(2026, 8, 16, 9),
);

SubscriptionDelivery _delivery(
  SubscriptionDeliveryStatus status,
  DateTime date,
) => SubscriptionDelivery(
  publicId: 'delivery-${status.name}',
  scheduledDate: date,
  slot: SubscriptionDeliverySlot.evening,
  quantity: 1.125,
  status: status,
  branchId: 'branch-1',
  branchCode: 'CENTRAL',
  branchName: 'Central Dairy',
  address: 'Home, 1 Main Street, Pune 411001',
  statusChangedAt: null,
);

/// Tall surface so long forms and lists stay fully built and hittable.
void _useSurface(WidgetTester tester, Size size) {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = size;
  addTearDown(tester.view.reset);
}

Future<void> _pumpScreen(
  WidgetTester tester,
  Widget screen, {
  SubscriptionState subscriptionState = const SubscriptionState(),
  CatalogueState? catalogueState,
  CustomerState? customerState,
  GoRouter? router,
  bool settle = true,
}) async {
  final goRouter =
      router ??
      GoRouter(
        routes: [
          GoRoute(path: '/', builder: (context, state) => screen),
        ],
      );
  addTearDown(goRouter.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
        subscriptionControllerProvider.overrideWith(
          () => _SeededSubscriptionController(subscriptionState),
        ),
        catalogueControllerProvider.overrideWith(
          () => _SeededCatalogueController(
            catalogueState ?? CatalogueState(products: [_product()]),
          ),
        ),
        customerControllerProvider.overrideWith(
          () => _SeededCustomerController(
            customerState ?? CustomerState(addresses: [_address()]),
          ),
        ),
      ],
      child: MaterialApp.router(routerConfig: goRouter),
    ),
  );
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    await tester.pump();
    await tester.pump();
  }
}

GoRouter _subscriptionRouter() => GoRouter(
  initialLocation: '/subscriptions',
  routes: [
    GoRoute(
      path: '/subscriptions',
      builder: (context, state) => const SubscriptionListScreen(),
    ),
    GoRoute(
      path: '/subscriptions/new',
      builder: (context, state) => const SubscriptionSetupScreen(),
    ),
    GoRoute(
      path: '/subscriptions/:subscriptionId',
      builder: (context, state) => SubscriptionDetailScreen(
        subscriptionId: state.pathParameters['subscriptionId']!,
      ),
    ),
    GoRoute(
      path: '/subscriptions/:subscriptionId/calendar',
      builder: (context, state) => SubscriptionCalendarScreen(
        subscriptionId: state.pathParameters['subscriptionId']!,
      ),
    ),
  ],
);

void main() {
  group('subscription list', () {
    testWidgets('plan card shows product, schedule, status and entitlement', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1600));
      await _pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: SubscriptionState(
          subscriptions: [_subscription()],
        ),
      );

      expect(find.text('Your recurring plans'), findsOneWidget);
      expect(find.text('Whole Milk'), findsOneWidget);
      expect(find.text('1.125 litre per delivery'), findsOneWidget);
      expect(
        find.text('Mon Morning, Wed Morning, Fri Morning'),
        findsOneWidget,
      );
      expect(find.text('Active'), findsOneWidget);
      expect(find.textContaining('24 deliveries remaining'), findsOneWidget);
      expect(find.text('6/30'), findsOneWidget);
      expect(find.text('View details'), findsOneWidget);
      // Entitlement progress is announced, not just colour-coded. The card's
      // own semantics label merges with its texts, so match by substring.
      expect(
        find.bySemanticsLabel(RegExp('6 of 30 prepaid deliveries used')),
        findsOneWidget,
      );
    });

    testWidgets('plan card is exposed as a tappable button with plan context', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1600));
      final router = _subscriptionRouter();
      await _pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: SubscriptionState(subscriptions: [_subscription()]),
        router: router,
      );

      // DoodhCard exposes the tappable card as a button with the full plan
      // context for assistive tech.
      expect(
        find.bySemanticsLabel(
          RegExp(
            'Whole Milk, 1.125 litre per delivery, Active, '
            '24 deliveries remaining',
          ),
        ),
        findsOneWidget,
      );
      // And the detail route it targets is registered and renders the plan.
      router.go('/subscriptions/subscription-1');
      await tester.pumpAndSettle();
      expect(
        router.routerDelegate.currentConfiguration.uri.path,
        '/subscriptions/subscription-1',
      );
      expect(find.text('Whole Milk'), findsOneWidget);
      expect(find.text('Your plan'), findsOneWidget);
    });

    testWidgets('empty, offline and loading states are preserved', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: const SubscriptionState(),
      );
      expect(find.text('No subscriptions yet'), findsOneWidget);
      expect(find.text('Create subscription'), findsOneWidget);

      await _pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: const SubscriptionState(
          isOffline: true,
          errorMessage:
              'Unable to reach DoodhDirect. Check your connection and try again.',
        ),
      );
      expect(find.text('You are offline'), findsOneWidget);

      await _pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: const SubscriptionState(isLoading: true),
        settle: false,
      );
      expect(find.byType(LoadingStatePanel), findsOneWidget);
    });
  });

  group('subscription setup', () {
    testWidgets('setup shows grouped sections, product and address', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 2400));
      await _pumpScreen(tester, const SubscriptionSetupScreen());

      expect(find.text('New subscription'), findsOneWidget);
      expect(find.text('1. Choose your product'), findsOneWidget);
      expect(find.text('2. Set quantity and prepaid coverage'), findsOneWidget);
      expect(find.text('3. Choose your delivery rhythm'), findsOneWidget);
      expect(find.text('4. Delivery address and start date'), findsOneWidget);
      expect(find.text('5. Review and pay'), findsOneWidget);
      // Product identity, price and selection control.
      expect(find.text('Whole Milk'), findsNWidgets(2));
      expect(find.bySemanticsLabel('Price ₹60'), findsOneWidget);
      expect(find.text('Home - Pune'), findsOneWidget);
      // Weekday chips exist for all seven days with the default set selected.
      expect(find.text('Mon'), findsOneWidget);
      expect(find.text('Sun'), findsOneWidget);
      expect(
        find.bySemanticsLabel('Delivery days, 3 selected'),
        findsOneWidget,
      );
      // Prepaid estimate remains server-confirmed wording with semantics.
      expect(
        find.bySemanticsLabel(RegExp('Prepaid estimate for')),
        findsOneWidget,
      );
      expect(find.text('Continue to payment'), findsOneWidget);
    });

    testWidgets('weekday chips toggle the selection', (tester) async {
      _useSurface(tester, const Size(800, 2400));
      await _pumpScreen(tester, const SubscriptionSetupScreen());

      expect(
        find.bySemanticsLabel('Delivery days, 3 selected'),
        findsOneWidget,
      );
      await tester.tap(find.text('Sun'));
      await tester.pumpAndSettle();
      expect(
        find.bySemanticsLabel('Delivery days, 4 selected'),
        findsOneWidget,
      );
      await tester.tap(find.text('Sun'));
      await tester.pumpAndSettle();
      expect(
        find.bySemanticsLabel('Delivery days, 3 selected'),
        findsOneWidget,
      );
    });

    testWidgets('setup requires products and addresses before enabling', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const SubscriptionSetupScreen(),
        catalogueState: const CatalogueState(),
      );
      expect(
        find.text('No active products are available for subscription.'),
        findsOneWidget,
      );
    });
  });

  group('subscription detail', () {
    testWidgets('active plan shows status, entitlement, plan and address', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1600));
      await _pumpScreen(
        tester,
        SubscriptionDetailScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          subscriptions: [_subscription()],
          selectedSubscription: _subscription(),
        ),
      );

      expect(find.text('Whole Milk'), findsOneWidget);
      expect(find.text('1.125 litre per delivery'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      expect(find.text('24 deliveries remaining'), findsOneWidget);
      expect(find.text('6 of 30 deliveries used'), findsOneWidget);
      expect(find.text('Prepaid plan value ₹2025.00'), findsOneWidget);
      expect(find.text('Your plan'), findsOneWidget);
      expect(find.text('Delivery days'), findsOneWidget);
      expect(find.text('Active window'), findsOneWidget);
      expect(find.text('Unit price'), findsOneWidget);
      expect(
        find.text('Home, 1 Main Street, Pune 411001'),
        findsOneWidget,
      );
      // Schedule updates are disabled: hold (pause/resume) or cancel only.
      expect(find.text('Update schedule'), findsNothing);
      expect(find.text('Pause subscription'), findsOneWidget);
      expect(find.text('Cancel subscription'), findsOneWidget);
      expect(find.text('Resume subscription'), findsNothing);
      expect(find.text('Retry Payment'), findsNothing);
      expect(find.text('View delivery calendar'), findsOneWidget);
      // Entitlement progress is announced, not just colour-coded. Anchored so
      // the containing card's longer label is not also matched.
      expect(
        find.bySemanticsLabel(RegExp('^6 of 30 prepaid deliveries used\$')),
        findsOneWidget,
      );
    });

    testWidgets('payment-pending plan offers Complete Payment only', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1600));
      await _pumpScreen(
        tester,
        SubscriptionDetailScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          subscriptions: [
            _subscription(status: SubscriptionStatus.paymentPending),
          ],
          selectedSubscription: _subscription(
            status: SubscriptionStatus.paymentPending,
          ),
        ),
      );

      expect(find.text('Status: Payment Pending'), findsOneWidget);
      expect(find.text('Amount Due: ₹2025.00'), findsOneWidget);
      expect(find.text('Complete Payment'), findsNWidgets(2));
      expect(find.text('DoodhDirect Wallet'), findsOneWidget);
      expect(find.text('Razorpay'), findsOneWidget);
      // Lifecycle actions respect the server status gate.
      expect(find.text('Pause subscription'), findsNothing);
      expect(find.text('Resume subscription'), findsNothing);
      // Schedule updates are disabled entirely (not merely gated).
      expect(find.text('Update schedule'), findsNothing);
    });

    testWidgets('paused plan offers Resume only', (tester) async {
      _useSurface(tester, const Size(800, 1600));
      await _pumpScreen(
        tester,
        SubscriptionDetailScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          subscriptions: [_subscription(status: SubscriptionStatus.paused)],
          selectedSubscription: _subscription(
            status: SubscriptionStatus.paused,
          ),
        ),
      );

      expect(find.text('Paused'), findsOneWidget);
      expect(find.text('Resume subscription'), findsOneWidget);
      expect(find.text('Pause subscription'), findsNothing);
      expect(find.text('Update schedule'), findsNothing);
    });

    testWidgets('wide layout balances status and actions into columns', (
      tester,
    ) async {
      _useSurface(tester, const Size(1440, 900));
      await _pumpScreen(
        tester,
        SubscriptionDetailScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          subscriptions: [_subscription()],
          selectedSubscription: _subscription(),
        ),
      );

      expect(tester.takeException(), isNull);
      expect(find.text('Whole Milk'), findsOneWidget);
      expect(find.text('View delivery calendar'), findsOneWidget);
      // The hero stays on a constrained reading width, not stretched wide.
      final heroWidth = tester.getSize(find.text('Whole Milk')).width;
      expect(heroWidth, lessThan(700));
    });
  });

  group('subscription calendar', () {
    testWidgets('calendar separates upcoming and history with real statuses', (
      tester,
    ) async {
      final upcoming = DateTime.now().add(const Duration(days: 5));
      final past = DateTime.now().subtract(const Duration(days: 5));
      await _pumpScreen(
        tester,
        SubscriptionCalendarScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          calendar: [
            _delivery(SubscriptionDeliveryStatus.scheduled, upcoming),
            _delivery(SubscriptionDeliveryStatus.delivered, past),
            _delivery(SubscriptionDeliveryStatus.skipped, past),
          ],
        ),
      );

      expect(find.text('Delivery calendar'), findsOneWidget);
      expect(find.text('Upcoming deliveries'), findsOneWidget);
      expect(find.text('Delivery history'), findsOneWidget);
      expect(find.textContaining('Scheduled'), findsOneWidget);
      expect(find.textContaining('Delivered'), findsOneWidget);
      expect(find.textContaining('Skipped'), findsOneWidget);
      // Only the scheduled upcoming occurrence offers Skip.
      expect(find.byTooltip('Skip delivery'), findsOneWidget);
    });

    testWidgets('failed and cancelled deliveries render their status', (
      tester,
    ) async {
      final past = DateTime.now().subtract(const Duration(days: 5));
      await _pumpScreen(
        tester,
        SubscriptionCalendarScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          calendar: [
            _delivery(SubscriptionDeliveryStatus.failed, past),
            _delivery(SubscriptionDeliveryStatus.cancelled, past),
          ],
        ),
      );

      expect(find.textContaining('Failed'), findsOneWidget);
      expect(find.textContaining('Cancelled'), findsOneWidget);
      expect(find.byTooltip('Skip delivery'), findsNothing);
    });

    testWidgets('calendar empty, offline and error states are preserved', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        SubscriptionCalendarScreen(subscriptionId: 'subscription-1'),
        subscriptionState: const SubscriptionState(),
      );
      expect(find.text('No deliveries scheduled'), findsOneWidget);

      await _pumpScreen(
        tester,
        SubscriptionCalendarScreen(subscriptionId: 'subscription-1'),
        subscriptionState: const SubscriptionState(
          isOffline: true,
          errorMessage:
              'Unable to reach DoodhDirect. Check your connection and try again.',
        ),
      );
      expect(find.text('You are offline'), findsOneWidget);

      await _pumpScreen(
        tester,
        SubscriptionCalendarScreen(subscriptionId: 'subscription-1'),
        subscriptionState: const SubscriptionState(
          errorMessage: 'Subscription service rejected the request.',
        ),
      );
      expect(
        find.text('Subscription service rejected the request.'),
        findsOneWidget,
      );
    });
  });
}
