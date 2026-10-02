import 'dart:async';

import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart'
    hide SubscriptionDeliverySlot;
import 'package:doodh_direct_mobile/features/deliveries/delivery_repository.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_screens.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

/// Customer Home cleanup tests:
/// 1. The permanent "Upcoming Deliveries" section is gone.
/// 2. The 7-day calendar stays; tapping a date shows ONLY that date's
///    deliveries in a bottom sheet (loading/empty/error states kept apart).
/// 3. Skip stays available exactly for eligible Scheduled occurrences and
///    reuses the existing subscription controller (backend authoritative).
/// 4. Home surfaces use the shared Doodh theme tokens.

DateTime _dateOnly(DateTime value) =>
    DateTime(value.year, value.month, value.day);

final DateTime _today = _dateOnly(DateTime.now());

DateTime _day(int offset) => _today.add(Duration(days: offset));

String _iso(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';

String _heading(DateTime date) =>
    'Deliveries for ${date.day}/${date.month}/${date.year}';

SubscriptionDelivery _subscriptionOccurrence(
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

CustomerDelivery _orderDelivery(String id, DateTime date) => CustomerDelivery(
  deliveryId: id,
  sourceType: DeliverySourceType.oneTimeOrder,
  referenceNumber: 'REF-1',
  status: DeliveryStatus.outForDelivery,
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

SubscriptionDetails _subscription(String publicId) => SubscriptionDetails(
  publicId: publicId,
  status: SubscriptionStatus.active,
  productId: 'product-1',
  productSku: 'MILK-1L',
  productName: 'Whole Milk',
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

// ---------------------------------------------------------------------------
// Minimal fakes so the authenticated customer app boots HTTP-free.
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

/// Delivery fake with controllable data, a failure switch, and a pending gate
/// so the loading state can be observed deterministically.
class _SelectedDateDeliveryRepository extends DeliveryRepository {
  _SelectedDateDeliveryRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<CustomerDelivery> customerDeliveries = [];

  /// When set, the next [getMine] throws this object (error/offline path).
  Object? error;

  /// When active, [getMine] stays pending until the test completes it.
  Completer<List<CustomerDelivery>>? pendingGetMine;

  @override
  Future<List<CustomerDelivery>> getMine(String token) {
    final failure = error;
    if (failure != null) throw failure;
    final pending = pendingGetMine;
    if (pending != null) return pending.future;
    return Future.value(customerDeliveries);
  }
}

/// Subscription fake: calendar + skip recording through the real controller.
class _SelectedDateSubscriptionRepository extends SubscriptionRepository {
  _SelectedDateSubscriptionRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<SubscriptionDelivery> calendar = [];

  /// Subscriptions reported by getMine; empty means the customer has none
  /// (drives the My Calendar fallback to the subscriptions list).
  List<SubscriptionDetails> subscriptions = [_subscription('sub-1')];

  /// Skips recorded as (subscriptionId, deliveryId) — the controller path.
  final List<(String, String)> skipCalls = [];

  @override
  Future<List<SubscriptionDetails>> getMine(String token) async =>
      subscriptions;

  @override
  Future<List<SubscriptionDelivery>> getCalendar(
    String token,
    String subscriptionId,
  ) async => calendar;

  @override
  Future<SubscriptionDelivery> skip({
    required String token,
    required String subscriptionId,
    required String deliveryId,
  }) async {
    skipCalls.add((subscriptionId, deliveryId));
    return _subscriptionOccurrence(
      deliveryId,
      _day(1),
      status: SubscriptionDeliveryStatus.skipped,
    );
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

Future<ProviderContainer> _pumpApp(
  WidgetTester tester, {
  required _SelectedDateDeliveryRepository deliveryRepository,
  required _SelectedDateSubscriptionRepository subscriptionRepository,
  Size? surface,
}) async {
  if (surface != null) {
    await tester.binding.setSurfaceSize(surface);
    addTearDown(() => tester.binding.setSurfaceSize(null));
  }
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthRepositoryFake()),
      orderRepositoryProvider.overrideWithValue(_OrderRepositoryFake()),
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
  return container;
}

Finder _dateTile(DateTime date) =>
    find.byKey(ValueKey('home-date-tile-${_iso(date)}'));

Future<void> _openDaySheet(WidgetTester tester, DateTime date) async {
  await tester.ensureVisible(_dateTile(date));
  await tester.tap(_dateTile(date));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
    'Home shows the permanent Upcoming Deliveries section (business request)',
    (tester) async {
      await _pumpApp(
        tester,
        deliveryRepository: _SelectedDateDeliveryRepository(),
        subscriptionRepository: _SelectedDateSubscriptionRepository(),
      );

      // The calendar band remains at the top of the page.
      expect(find.byKey(const ValueKey('my-deliveries-card')), findsOneWidget);
      expect(find.text('My Deliveries'), findsOneWidget);

      // The Upcoming Deliveries section is its own white card BELOW the
      // My Deliveries band (business ask: two separate sections), so scroll
      // down to it before asserting.
      final title = find.byKey(
        const ValueKey('home-upcoming-deliveries-title'),
      );
      // scrollUntilVisible needs a Scrollable widget; the home list is a
      // ListView keyed 'customer-home-scroll', so drag it directly.
      await tester.dragUntilVisible(
        title,
        find.byKey(const ValueKey('customer-home-scroll')),
        const Offset(0, -150),
      );
      await tester.pumpAndSettle();
      expect(title, findsOneWidget);
      expect(
        find.byKey(const ValueKey('home-add-item-action')),
        findsOneWidget,
      );
    },
  );

  testWidgets('exactly 7 calendar days remain and every tile is tappable', (
    tester,
  ) async {
    await _pumpApp(
      tester,
      deliveryRepository: _SelectedDateDeliveryRepository(),
      subscriptionRepository: _SelectedDateSubscriptionRepository(),
    );

    final tiles = find.byWidgetPredicate(
      (widget) =>
          widget is InkWell &&
          widget.key is ValueKey<String> &&
          (widget.key as ValueKey<String>).value.startsWith('home-date-tile-'),
    );
    expect(tiles, findsNWidgets(7));

    // Today renders with the shared deep-forest selected treatment.
    final todayDecoration =
        tester
                .widget<Container>(
                  find
                      .descendant(
                        of: _dateTile(_today),
                        matching: find.byType(Container),
                      )
                      .first,
                )
                .decoration
            as BoxDecoration;
    expect(todayDecoration.color, DoodhColors.tealDark);
  });

  testWidgets(
    'compact 320px layout keeps the 7-day calendar usable with no overflow',
    (tester) async {
      await _pumpApp(
        tester,
        deliveryRepository: _SelectedDateDeliveryRepository(),
        subscriptionRepository: _SelectedDateSubscriptionRepository(),
        surface: const Size(320, 900),
      );

      // The 7-day strip is a horizontal ListView: scroll to the far end and
      // verify the last tile exists there (all seven days are reachable).
      await tester.scrollUntilVisible(
        _dateTile(_day(6)),
        120,
        scrollable: find.byType(Scrollable).last,
      );
      expect(_dateTile(_day(6)), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('tablet/wide layouts keep the calendar usable with no overflow', (
    tester,
  ) async {
    for (final width in [900.0, 1440.0]) {
      await _pumpApp(
        tester,
        deliveryRepository: _SelectedDateDeliveryRepository(),
        subscriptionRepository: _SelectedDateSubscriptionRepository(),
        surface: Size(width, 1000),
      );

      final tiles = find.byWidgetPredicate(
        (widget) =>
            widget is InkWell &&
            widget.key is ValueKey<String> &&
            (widget.key as ValueKey<String>).value.startsWith(
              'home-date-tile-',
            ),
      );
      expect(tiles, findsNWidgets(7));
      expect(tester.takeException(), isNull);
    }
  });

  testWidgets(
    'tapping a date shows only that date\u2019s deliveries; multiple entries render separately',
    (tester) async {
      final deliveries = _SelectedDateDeliveryRepository()
        ..customerDeliveries = [_orderDelivery('od-1', _day(1))];
      final subscriptions = _SelectedDateSubscriptionRepository()
        ..calendar = [
          _subscriptionOccurrence('d-9', _day(1)),
          _subscriptionOccurrence(
            'd-10',
            _day(1),
            slot: SubscriptionDeliverySlot.evening,
          ),
          _subscriptionOccurrence('d-8', _day(2)),
        ];
      await _pumpApp(
        tester,
        deliveryRepository: deliveries,
        subscriptionRepository: subscriptions,
      );

      await _openDaySheet(tester, _day(1));

      expect(find.text(_heading(_day(1))), findsOneWidget);
      // Both occurrences for the selected date, listed separately with their
      // own slots and Skip affordances.
      expect(
        find.byKey(const ValueKey('date-delivery-skip-d-9')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('date-delivery-skip-d-10')),
        findsOneWidget,
      );
      expect(find.textContaining('Morning ·'), findsOneWidget);
      expect(find.textContaining('Evening ·'), findsOneWidget);
      // The Home band also lists these occurrences as upcoming cards, so the
      // product-name assertion is scoped to the opened sheet itself.
      final sheetScope = find.byType(BottomSheet);
      expect(
        find.descendant(of: sheetScope, matching: find.text('Whole Milk')),
        findsNWidgets(2),
      );
      // The next day's occurrence is NOT shown.
      expect(
        find.byKey(const ValueKey('date-delivery-skip-d-8')),
        findsNothing,
      );
      // The order-driven delivery row is present too.
      expect(find.text('One-time · REF-1'), findsOneWidget);

      // Item cards follow the shared token surfaces: subscription occurrences
      // on pale sage/mint, order deliveries on white product-card white.
      final sheetCards = tester
          .widgetList<DoodhCard>(find.byType(DoodhCard))
          .toList();
      expect(
        sheetCards
            .where(
              (card) => (card.semanticLabel ?? '').contains('Subscription'),
            )
            .map((card) => card.color),
        everyElement(DoodhColors.mint),
      );
      expect(
        sheetCards
            .where((card) => (card.semanticLabel ?? '').contains('One-time'))
            .map((card) => card.color),
        everyElement(Colors.white),
      );
    },
  );

  testWidgets('a date with no delivery shows the No Delivery state', (
    tester,
  ) async {
    await _pumpApp(
      tester,
      deliveryRepository: _SelectedDateDeliveryRepository(),
      subscriptionRepository: _SelectedDateSubscriptionRepository(),
    );

    await _openDaySheet(tester, _day(3));

    expect(find.text(_heading(_day(3))), findsOneWidget);
    expect(find.byKey(const ValueKey('date-deliveries-empty')), findsOneWidget);
    expect(find.text('No delivery scheduled for this day.'), findsOneWidget);
  });

  testWidgets('loading is never confused with No Delivery', (tester) async {
    final deliveries = _SelectedDateDeliveryRepository();
    final subscriptions = _SelectedDateSubscriptionRepository();
    final container = await _pumpApp(
      tester,
      deliveryRepository: deliveries,
      subscriptionRepository: subscriptions,
    );

    // The sheet opens on the loaded (empty) state.
    await _openDaySheet(tester, _day(1));
    expect(find.text('No delivery scheduled for this day.'), findsOneWidget);

    // A reload starts while the sheet is open: the sheet must switch to the
    // loading branch and must NOT claim "No delivery".
    deliveries.pendingGetMine = Completer<List<CustomerDelivery>>();
    final reload = container
        .read(deliveryControllerProvider.notifier)
        .loadCustomerDeliveries();
    await tester.pump();
    expect(
      find.byKey(const ValueKey('date-deliveries-loading')),
      findsOneWidget,
    );
    expect(find.text('No delivery scheduled for this day.'), findsNothing);

    // Completing the reload returns the sheet to the factual empty state.
    deliveries.pendingGetMine!.complete(const []);
    await reload;
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('date-deliveries-empty')), findsOneWidget);
  });

  testWidgets('offline/error keeps the existing unavailable handling', (
    tester,
  ) async {
    final deliveries = _SelectedDateDeliveryRepository()
      ..error = const ApiException(500, 'server_error', 'boom');
    await _pumpApp(
      tester,
      deliveryRepository: deliveries,
      subscriptionRepository: _SelectedDateSubscriptionRepository(),
    );

    // Band-level unavailable handling is preserved…
    expect(find.text('Unable to refresh delivery status'), findsOneWidget);
    // …and date tiles are disabled, so no sheet can present fake content.
    final tile = tester.widget<InkWell>(_dateTile(_day(1)));
    expect(tile.onTap, isNull);
  });

  testWidgets(
    'Skip action: exposed for Scheduled occurrences, reused controller path, hidden when ineligible',
    (tester) async {
      final deliveries = _SelectedDateDeliveryRepository();
      final subscriptions = _SelectedDateSubscriptionRepository()
        ..calendar = [
          // Eligible: Scheduled occurrence.
          _subscriptionOccurrence('d-9', _day(1)),
          // Ineligible: already skipped — no Skip affordance, status pill only.
          _subscriptionOccurrence(
            'd-11',
            _day(1),
            status: SubscriptionDeliveryStatus.skipped,
          ),
        ];
      final container = await _pumpApp(
        tester,
        deliveryRepository: deliveries,
        subscriptionRepository: subscriptions,
      );

      await _openDaySheet(tester, _day(1));

      expect(
        find.byKey(const ValueKey('date-delivery-skip-d-9')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('date-delivery-skip-d-11')),
        findsNothing,
      );
      expect(find.text('Skipped'), findsOneWidget);

      // Confirming the dialog routes through the EXISTING controller skip —
      // no new provider, no new API surface.
      await tester.tap(find.byKey(const ValueKey('date-delivery-skip-d-9')));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Skip'));
      await tester.pumpAndSettle();

      expect(subscriptions.skipCalls, [('sub-1', 'd-9')]);
      expect(
        container.read(subscriptionControllerProvider).calendar,
        isNot(contains(_subscriptionOccurrence('d-9', _day(1)))),
      );
    },
  );

  testWidgets(
    'Vacation/Skipped state stays correct: the band keeps Vacation actions and the legend',
    (tester) async {
      final subscriptions = _SelectedDateSubscriptionRepository()
        ..calendar = [
          _subscriptionOccurrence(
            'd-9',
            _day(1),
            status: SubscriptionDeliveryStatus.skipped,
          ),
        ];
      await _pumpApp(
        tester,
        deliveryRepository: _SelectedDateDeliveryRepository(),
        subscriptionRepository: subscriptions,
      );

      expect(
        find.byKey(const ValueKey('home-set-vacation-action')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('home-my-calendar-action')),
        findsOneWidget,
      );
      expect(find.text('Vacation / Skipped'), findsOneWidget);
      expect(find.text('Delivered'), findsOneWidget);
      expect(find.text('Upcoming'), findsOneWidget);
      expect(find.text('No Delivery'), findsOneWidget);
    },
  );

  testWidgets('My Calendar opens the dedicated customer-wide calendar screen', (
    tester,
  ) async {
    final subscriptions = _SelectedDateSubscriptionRepository();
    await _pumpApp(
      tester,
      deliveryRepository: _SelectedDateDeliveryRepository(),
      subscriptionRepository: subscriptions,
    );

    final bandButton = find.byKey(const ValueKey('home-my-calendar-action'));
    await tester.ensureVisible(bandButton);
    // Nudge the band into mid-viewport so the button sits clear of the
    // shell header (top) and bottom navigation overlays.
    await tester.drag(
      find.byKey(const ValueKey('customer-home-scroll')),
      const Offset(0, 120),
    );
    await tester.pumpAndSettle();
    await tester.tap(bandButton);
    await tester.pumpAndSettle();

    // The customer-wide My Calendar screen — NOT the per-subscription
    // Delivery Calendar.
    expect(find.text('My Calendar'), findsOneWidget);
    expect(find.text('Delivery calendar'), findsNothing);
    expect(find.byType(SubscriptionCalendarScreen), findsNothing);
    expect(find.byKey(const ValueKey('my-calendar-card')), findsOneWidget);
  });

  testWidgets(
    'My Calendar opens without any active subscription (customer-wide)',
    (tester) async {
      final subscriptions = _SelectedDateSubscriptionRepository()
        ..subscriptions = const [];
      await _pumpApp(
        tester,
        deliveryRepository: _SelectedDateDeliveryRepository(),
        subscriptionRepository: subscriptions,
      );

      final bandButton = find.byKey(const ValueKey('home-my-calendar-action'));
      await tester.ensureVisible(bandButton);
      await tester.drag(
        find.byKey(const ValueKey('customer-home-scroll')),
        const Offset(0, 120),
      );
      await tester.pumpAndSettle();
      await tester.tap(bandButton);
      await tester.pumpAndSettle();

      // No subscription is required: the wide calendar still opens.
      expect(find.text('My Calendar'), findsOneWidget);
      expect(find.byKey(const ValueKey('my-calendar-card')), findsOneWidget);
      expect(find.byType(SubscriptionListScreen), findsNothing);
    },
  );
}
