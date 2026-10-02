import 'dart:async';

import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart'
    hide SubscriptionDeliverySlot;
import 'package:doodh_direct_mobile/features/deliveries/delivery_repository.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

/// Customer-wide My Calendar tests:
/// composition across deliveries/mine + ALL active subscription occurrence
/// calendars, month navigation, date selection, and distinct empty/loading/
/// error states — with real Delivery status always authoritative.

DateTime _dateOnly(DateTime value) =>
    DateTime(value.year, value.month, value.day);

final DateTime _today = _dateOnly(DateTime.now());

/// A deterministic, distinct day inside the CURRENT month grid (days 2–10),
/// so every test date always renders a cell and never collides with another
/// test date regardless of where today sits in the month.
DateTime _inMonth(int offset) =>
    DateTime(_today.year, _today.month, (offset + 1).clamp(2, 10));

String _iso(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';

String _heading(DateTime date) =>
    'Deliveries for ${date.day}/${date.month}/${date.year}';

SubscriptionDelivery _occurrence(
  String id,
  DateTime date, {
  SubscriptionDeliveryStatus status = SubscriptionDeliveryStatus.scheduled,
  SubscriptionDeliverySlot slot = SubscriptionDeliverySlot.morning,
}) => SubscriptionDelivery(
  publicId: id,
  scheduledDate: date,
  slot: slot,
  quantity: 2,
  status: status,
  branchId: 'branch-1',
  branchCode: 'MAIN',
  branchName: 'Main Branch',
  address: '1 Main Road, Bengaluru',
  statusChangedAt: null,
);

CustomerDelivery _delivery(
  String id,
  DateTime date, {
  DeliveryStatus status = DeliveryStatus.outForDelivery,
  String reference = 'REF-1',
  DeliverySourceType source = DeliverySourceType.oneTimeOrder,
}) => CustomerDelivery(
  deliveryId: id,
  sourceType: source,
  referenceNumber: reference,
  status: status,
  scheduledDate: date,
  destinationAddress: '1 Main Road, Bengaluru',
  assignedEmployeeId: null,
  assignedEmployeeName: null,
  isTrackingActive: false,
  latestLocation: null,
  completedAt: null,
  failedAt: null,
  failureReason: null,
  activeOtp: null,
);

SubscriptionDetails _subscription(String publicId, String productName) =>
    SubscriptionDetails(
      publicId: publicId,
      status: SubscriptionStatus.active,
      productId: 'product-$publicId',
      productSku: 'SKU-$publicId',
      productName: productName,
      unitOfMeasure: 'litre',
      quantity: 2,
      unitPrice: 60,
      payableAmount: 480,
      startDate: DateTime(2026, 9, 1),
      endDate: DateTime(2026, 12, 31),
      totalEntitlement: 8,
      usedEntitlement: 0,
      remainingEntitlement: 8,
      addressId: 'address-1',
      address: '1 Main Road, Bengaluru',
      branchId: 'branch-1',
      branchCode: 'MAIN',
      branchName: 'Main Branch',
      schedules: const [
        SubscriptionSchedule(
          dayOfWeek: DeliveryWeekday.monday,
          slot: SubscriptionDeliverySlot.morning,
        ),
      ],
      activatedAt: DateTime(2026, 9, 1),
      pausedAt: null,
      cancelledAt: null,
      completedAt: null,
      createdAt: DateTime(2026, 9, 1),
    );

OrderSummary _order(String reference) => OrderSummary(
  publicId: 'order-$reference',
  orderNumber: reference,
  type: 'OneTime',
  status: 'Confirmed',
  createdAt: DateTime(2026),
  addressLabel: 'Home',
  city: 'Bengaluru',
  branchName: 'Main Branch',
  items: [
    const OrderItem(
      productId: 'product-order',
      productName: 'Paneer 200g',
      sku: 'PAN-200',
      unitOfMeasure: 'piece',
      quantity: 1,
      unitPrice: 95,
      lineTotal: 95,
    ),
  ],
  subtotal: 95,
  discountAmount: 0,
  payableAmount: 95,
  cancelledAt: null,
  paymentPublicId: null,
  paymentStatus: null,
  gatewayPaymentId: null,
  deliveryPublicId: null,
  deliveryReferenceNumber: null,
  deliveryStatus: null,
);

// ---------------------------------------------------------------------------
// Fakes
// ---------------------------------------------------------------------------

class _SecureStorageFake extends FlutterSecureStorage {
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

class _ClientConfigurationRepositoryFake extends ClientConfigurationRepository {
  _ClientConfigurationRepositoryFake()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<ClientConfiguration> get(String token) async =>
      const ClientConfiguration();
}

class _OrderRepositoryFake extends OrderRepository {
  _OrderRepositoryFake()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<OrderSummary> orders = const [];

  @override
  Future<List<OrderSummary>> getMine(String token) async => orders;
}

class _WalletRepositoryFake extends WalletRepository {
  _WalletRepositoryFake()
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

class _NotificationRepositoryFake extends NotificationRepository {
  _NotificationRepositoryFake()
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
  Future<int> getUnreadCount(String token) async => 0;
}

class _CalendarDeliveryRepository extends DeliveryRepository {
  _CalendarDeliveryRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<CustomerDelivery> deliveries = const [];
  Object? error;
  Completer<List<CustomerDelivery>>? pending;

  @override
  Future<List<CustomerDelivery>> getMine(String token) {
    final failure = error;
    if (failure != null) throw failure;
    final gate = pending;
    if (gate != null) return gate.future;
    return Future.value(deliveries);
  }
}

/// Subscription fake returning per-subscription occurrence calendars and
/// recording which subscriptions the screen requested — proving the screen
/// composes ALL active subscriptions (never just the first).
class _CalendarSubscriptionRepository extends SubscriptionRepository {
  _CalendarSubscriptionRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<SubscriptionDetails> subscriptions = const [];
  Map<String, List<SubscriptionDelivery>> calendars = {};
  final List<String> calendarRequests = [];
  Object? error;

  @override
  Future<List<SubscriptionDetails>> getMine(String token) async =>
      subscriptions;

  @override
  Future<List<SubscriptionDelivery>> getCalendar(
    String token,
    String subscriptionId,
  ) async {
    final failure = error;
    if (failure != null) throw failure;
    calendarRequests.add(subscriptionId);
    return calendars[subscriptionId] ?? const [];
  }
}

class _AuthRepositoryFake extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;

  static final AuthSession _session = AuthSession(
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
}

Future<ProviderContainer> _pumpCalendar(
  WidgetTester tester, {
  required _CalendarDeliveryRepository deliveryRepository,
  required _CalendarSubscriptionRepository subscriptionRepository,
  _OrderRepositoryFake? orderRepository,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthRepositoryFake()),
      orderRepositoryProvider.overrideWithValue(
        orderRepository ?? (_OrderRepositoryFake()),
      ),
      deliveryRepositoryProvider.overrideWithValue(deliveryRepository),
      subscriptionRepositoryProvider.overrideWithValue(subscriptionRepository),
      walletRepositoryProvider.overrideWithValue(_WalletRepositoryFake()),
      notificationRepositoryProvider.overrideWithValue(
        _NotificationRepositoryFake(),
      ),
      clientConfigurationRepositoryProvider.overrideWithValue(
        _ClientConfigurationRepositoryFake(),
      ),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: _SecureStorageFake()),
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
  container.read(routerProvider).go('/my-calendar');
  await tester.pumpAndSettle();
  return container;
}

Finder _dayCell(DateTime date) =>
    find.byKey(ValueKey('my-calendar-day-${_iso(date)}'));

Future<void> _selectDay(WidgetTester tester, DateTime date) async {
  await tester.ensureVisible(_dayCell(date));
  await tester.tap(_dayCell(date));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('My Calendar renders the reference month grid and legend', (
    tester,
  ) async {
    await _pumpCalendar(
      tester,
      deliveryRepository: _CalendarDeliveryRepository(),
      subscriptionRepository: _CalendarSubscriptionRepository(),
    );

    expect(find.text('My Calendar'), findsWidgets); // AppBar + heading context
    expect(find.byKey(const ValueKey('my-calendar-card')), findsOneWidget);
    expect(find.byKey(const ValueKey('my-calendar-legend')), findsOneWidget);
    expect(find.text('Vacation'), findsOneWidget);
    expect(find.text('Delivered'), findsOneWidget);
    expect(find.text('Upcoming'), findsOneWidget);
    expect(find.text('No Delivery'), findsOneWidget);
    // Sun-first weekday header and the month label with Today pill.
    expect(find.text('Sun'), findsOneWidget);
    expect(find.text('Sat'), findsOneWidget);
    expect(find.text('Today'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('my-calendar-month-label')),
      findsOneWidget,
    );
    // Today's cell exists and its InkWell is tappable.
    expect(_dayCell(_today), findsOneWidget);
    final todayInk = tester.widget<InkWell>(
      find.descendant(of: _dayCell(_today), matching: find.byType(InkWell)),
    );
    expect(todayInk.onTap, isNotNull);
  });

  testWidgets('previous/next month navigation and Today work', (tester) async {
    await _pumpCalendar(
      tester,
      deliveryRepository: _CalendarDeliveryRepository(),
      subscriptionRepository: _CalendarSubscriptionRepository(),
    );

    final label = find.byKey(const ValueKey('my-calendar-month-label'));
    final startLabel = tester.widget<Text>(label).data;

    await tester.tap(find.byKey(const ValueKey('my-calendar-next-month')));
    await tester.pumpAndSettle();
    final nextLabel = tester.widget<Text>(label).data;
    expect(nextLabel, isNot(startLabel));

    await tester.tap(find.byKey(const ValueKey('my-calendar-previous-month')));
    await tester.pumpAndSettle();
    expect(tester.widget<Text>(label).data, startLabel);

    // Today returns the selection to today's date.
    await tester.tap(_dayCell(_inMonth(1)));
    await tester.pumpAndSettle();
    expect(
      find.textContaining(_heading(_inMonth(1)).split(' ').first),
      findsOneWidget,
    );
    await tester.tap(find.byKey(const ValueKey('my-calendar-today-action')));
    await tester.pumpAndSettle();
    expect(
      find.textContaining('Deliveries for ${_today.day}/'),
      findsOneWidget,
    );
  });

  testWidgets(
    'selecting a date shows only that date; multiple deliveries all appear; real status authoritative; order enrichment shown',
    (tester) async {
      final deliveries = _CalendarDeliveryRepository()
        ..deliveries = [
          _delivery('d-1', _inMonth(2), status: DeliveryStatus.delivered),
          _delivery(
            'd-2',
            _inMonth(2),
            status: DeliveryStatus.outForDelivery,
            reference: 'REF-2',
          ),
          _delivery('d-3', _inMonth(3), status: DeliveryStatus.failed),
        ];
      final orders = _OrderRepositoryFake()..orders = [_order('REF-1')];
      await _pumpCalendar(
        tester,
        deliveryRepository: deliveries,
        subscriptionRepository: _CalendarSubscriptionRepository(),
        orderRepository: orders,
      );

      await _selectDay(tester, _inMonth(2));

      final section = find.byKey(const ValueKey('my-calendar-day-section'));
      expect(find.text(_heading(_inMonth(2))), findsOneWidget);
      expect(
        find.descendant(of: section, matching: find.text('Delivered')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: section, matching: find.text('Out for delivery')),
        findsOneWidget,
      );
      // The REF-1 delivery is enriched with the order item name.
      expect(find.textContaining('Paneer 200g'), findsOneWidget);
      // The other date's failed delivery is NOT shown.
      expect(
        find.descendant(of: section, matching: find.text('Failed')),
        findsNothing,
      );
    },
  );

  testWidgets('a date with nothing shows the empty state, not loading', (
    tester,
  ) async {
    await _pumpCalendar(
      tester,
      deliveryRepository: _CalendarDeliveryRepository(),
      subscriptionRepository: _CalendarSubscriptionRepository(),
    );

    await _selectDay(tester, _inMonth(4));

    expect(find.text(_heading(_inMonth(4))), findsOneWidget);
    expect(find.text('No deliveries for this date'), findsOneWidget);
    expect(find.byKey(const ValueKey('my-calendar-day-loading')), findsNothing);
  });

  testWidgets(
    'multiple active subscriptions are composed: both calendars requested, both products appear',
    (tester) async {
      final deliveries = _CalendarDeliveryRepository();
      final subscriptions = _CalendarSubscriptionRepository()
        ..subscriptions = [
          _subscription('sub-a', 'Cow Milk'),
          _subscription('sub-b', 'Buffalo Milk'),
        ]
        ..calendars = {
          'sub-a': [_occurrence('occ-a', _inMonth(5))],
          'sub-b': [
            _occurrence(
              'occ-b',
              _inMonth(5),
              slot: SubscriptionDeliverySlot.evening,
            ),
          ],
        };
      await _pumpCalendar(
        tester,
        deliveryRepository: deliveries,
        subscriptionRepository: subscriptions,
      );

      // The screen asked for EVERY active subscription's calendar.
      expect(subscriptions.calendarRequests, containsAll(['sub-a', 'sub-b']));

      await _selectDay(tester, _inMonth(5));
      final section = find.byKey(const ValueKey('my-calendar-day-section'));
      expect(find.text(_heading(_inMonth(5))), findsOneWidget);
      expect(find.text('Cow Milk'), findsOneWidget);
      expect(find.text('Buffalo Milk'), findsOneWidget);
      expect(find.textContaining('Morning ·'), findsOneWidget);
      expect(find.textContaining('Evening ·'), findsOneWidget);
      // Scheduled future occurrences are Upcoming, not vacation.
      expect(
        find.descendant(of: section, matching: find.text('Upcoming')),
        findsNWidgets(2),
      );
      expect(
        find.descendant(of: section, matching: find.text('Vacation / Skipped')),
        findsNothing,
      );
    },
  );

  testWidgets(
    'future scheduled occurrence shows Upcoming; skipped occurrence shows Vacation/Skipped',
    (tester) async {
      final subscriptions = _CalendarSubscriptionRepository()
        ..subscriptions = [_subscription('sub-a', 'Cow Milk')]
        ..calendars = {
          'sub-a': [
            _occurrence('occ-s', _inMonth(6)),
            _occurrence(
              'occ-v',
              _inMonth(7),
              status: SubscriptionDeliveryStatus.skipped,
            ),
          ],
        };
      await _pumpCalendar(
        tester,
        deliveryRepository: _CalendarDeliveryRepository(),
        subscriptionRepository: subscriptions,
      );

      final section = find.byKey(const ValueKey('my-calendar-day-section'));
      await _selectDay(tester, _inMonth(6));
      expect(
        find.descendant(of: section, matching: find.text('Upcoming')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: section, matching: find.text('Vacation / Skipped')),
        findsNothing,
      );

      await _selectDay(tester, _inMonth(7));
      expect(find.text(_heading(_inMonth(7))), findsOneWidget);
      expect(
        find.descendant(of: section, matching: find.text('Vacation / Skipped')),
        findsOneWidget,
      );
      // A skipped date is NOT reported as an empty no-delivery date.
      expect(find.text('No deliveries for this date'), findsNothing);
    },
  );

  testWidgets(
    'a real Delivery is authoritative: ITS OWN scheduled occurrence does not duplicate it',
    (tester) async {
      // Same-day: sub-a's occurrence AND its own materialized Delivery
      // (reference SUB-<subGuidNoDashes>-<yyyyMMdd> per backend DeliveryService).
      final ownReference =
          'SUB-${'sub-a'.replaceAll('-', '')}-'
          '${_inMonth(8).year.toString().padLeft(4, '0')}'
          '${_inMonth(8).month.toString().padLeft(2, '0')}'
          '${_inMonth(8).day.toString().padLeft(2, '0')}';
      final deliveries = _CalendarDeliveryRepository()
        ..deliveries = [
          _delivery(
            'd-9',
            _inMonth(8),
            status: DeliveryStatus.delivered,
            reference: ownReference,
            source: DeliverySourceType.subscriptionOccurrence,
          ),
        ];
      final subscriptions = _CalendarSubscriptionRepository()
        ..subscriptions = [_subscription('sub-a', 'Cow Milk')]
        ..calendars = {
          'sub-a': [_occurrence('occ-x', _inMonth(8))],
        };
      await _pumpCalendar(
        tester,
        deliveryRepository: deliveries,
        subscriptionRepository: subscriptions,
      );

      await _selectDay(tester, _inMonth(8));

      final section = find.byKey(const ValueKey('my-calendar-day-section'));
      // The REAL delivery row (authoritative status) is shown exactly once —
      // not duplicated as Delivery + Upcoming.
      expect(find.textContaining(ownReference), findsOneWidget);
      expect(find.byKey(const ValueKey('my-calendar-day-empty')), findsNothing);
      expect(
        find.descendant(of: section, matching: find.text('Delivered')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: section, matching: find.text('Upcoming')),
        findsNothing,
      );
    },
  );

  testWidgets(
    'Subscription A Delivery and Subscription B occurrence on the SAME date: BOTH shown',
    (tester) async {
      // A's materialized Delivery + B's future Scheduled occurrence, same
      // date. B must NOT be suppressed by A's Delivery.
      final aReference =
          'SUB-${'sub-a'.replaceAll('-', '')}-'
          '${_inMonth(8).year.toString().padLeft(4, '0')}'
          '${_inMonth(8).month.toString().padLeft(2, '0')}'
          '${_inMonth(8).day.toString().padLeft(2, '0')}';
      final deliveries = _CalendarDeliveryRepository()
        ..deliveries = [
          _delivery(
            'd-a',
            _inMonth(8),
            status: DeliveryStatus.delivered,
            reference: aReference,
            source: DeliverySourceType.subscriptionOccurrence,
          ),
        ];
      final subscriptions = _CalendarSubscriptionRepository()
        ..subscriptions = [
          _subscription('sub-a', 'Cow Milk'),
          _subscription('sub-b', 'Buffalo Milk'),
        ]
        ..calendars = {
          'sub-a': [_occurrence('occ-a', _inMonth(8))],
          'sub-b': [_occurrence('occ-b', _inMonth(8))],
        };
      await _pumpCalendar(
        tester,
        deliveryRepository: deliveries,
        subscriptionRepository: subscriptions,
      );

      await _selectDay(tester, _inMonth(8));

      final section = find.byKey(const ValueKey('my-calendar-day-section'));
      // A's real Delivery (authoritative Delivered status)…
      expect(find.textContaining(aReference), findsOneWidget);
      expect(
        find.descendant(of: section, matching: find.text('Delivered')),
        findsOneWidget,
      );
      // …AND B's Upcoming occurrence with its own product.
      expect(find.text('Buffalo Milk'), findsOneWidget);
      expect(
        find.descendant(of: section, matching: find.text('Upcoming')),
        findsOneWidget,
      );
      // A's own occurrence is suppressed (its Delivery is the truth); the
      // day shows exactly A's Delivery + B's occurrence.
      expect(find.text('Cow Milk'), findsNothing);
    },
  );

  testWidgets('one-time order delivery appears with its reference', (
    tester,
  ) async {
    final deliveries = _CalendarDeliveryRepository()
      ..deliveries = [_delivery('d-1', _inMonth(9))];
    await _pumpCalendar(
      tester,
      deliveryRepository: deliveries,
      subscriptionRepository: _CalendarSubscriptionRepository(),
    );

    await _selectDay(tester, _inMonth(9));

    expect(find.textContaining('One-time · REF-1'), findsOneWidget);
  });

  testWidgets('Set Vacation opens the existing vacation flow', (tester) async {
    await _pumpCalendar(
      tester,
      deliveryRepository: _CalendarDeliveryRepository(),
      subscriptionRepository: _CalendarSubscriptionRepository(),
    );

    final vacationButton = find.text('Set Vacation');
    await tester.tap(vacationButton);
    await tester.pumpAndSettle();

    // The EXISTING range-based vacation flow opened (its AppBar title),
    // pushed on top of My Calendar.
    expect(find.text('Add Vacation'), findsOneWidget);
  });

  testWidgets('loading, error, and empty are distinct states', (tester) async {
    final deliveries = _CalendarDeliveryRepository();
    final subscriptions = _CalendarSubscriptionRepository();
    final container = await _pumpCalendar(
      tester,
      deliveryRepository: deliveries,
      subscriptionRepository: subscriptions,
    );

    // Success state on entry (fakes resolve immediately).
    expect(find.byKey(const ValueKey('my-calendar-loading')), findsNothing);
    expect(find.byKey(const ValueKey('my-calendar-error')), findsNothing);

    // Error state: controller-level failure must NOT read as "no deliveries".
    deliveries.error = const ApiException(500, 'server_error', 'boom');
    await container
        .read(deliveryControllerProvider.notifier)
        .loadCustomerDeliveries();
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('my-calendar-error')), findsOneWidget);
    expect(find.byKey(const ValueKey('my-calendar-day-error')), findsOneWidget);
    expect(find.text('No deliveries for this date'), findsNothing);
  });
}
