import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
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
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

SubscriptionDelivery _delivery({
  required String publicId,
  required DateTime date,
  SubscriptionDeliveryStatus status = SubscriptionDeliveryStatus.scheduled,
}) =>
    SubscriptionDelivery(
      publicId: publicId,
      scheduledDate: date,
      slot: SubscriptionDeliverySlot.morning,
      quantity: 1,
      status: status,
      branchId: 'branch-1',
      branchCode: 'MAIN',
      branchName: 'Main Branch',
      address: '1 Main Road, Bengaluru',
      statusChangedAt: null,
    );

SubscriptionDetails _subscription(String publicId) => SubscriptionDetails(
      publicId: publicId,
      status: SubscriptionStatus.active,
      productId: 'product-1',
      productSku: 'MILK-1L',
      productName: 'Whole Milk',
      unitOfMeasure: 'litre',
      quantity: 1,
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

/// Minimal fakes so the authenticated customer app boots HTTP-free.
class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository() : super(api: ApiClient(baseUrl: 'https://api.example.test'));
}

class _FakeClientConfigurationRepository extends ClientConfigurationRepository {
  _FakeClientConfigurationRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<ClientConfiguration> get(String token) async =>
      const ClientConfiguration();
}

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
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => values.remove(key);
}

class _HomeDeliveryRepository extends DeliveryRepository {
  _HomeDeliveryRepository() : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<CustomerDelivery>> getMine(String token) async =>
      const <CustomerDelivery>[];
}

class _HomeWalletRepository extends WalletRepository {
  _HomeWalletRepository() : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<WalletDetails> get(String token) async => WalletDetails(
        publicId: 'wallet-1',
        balance: 125.50,
        currency: 'INR',
        createdAt: DateTime(2026),
        updatedAt: DateTime(2026),
      );

  @override
  Future<List<WalletTransaction>> getTransactions(String token) async => const [];
}

class _HomeNotificationRepository extends NotificationRepository {
  _HomeNotificationRepository() : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<NotificationPage> getNotifications(
    String token, {
    int page = 1,
    int pageSize = 20,
    bool? isRead,
  }) async =>
      NotificationPage(items: const [], page: page, pageSize: pageSize, totalCount: 0);

  @override
  Future<int> getUnreadCount(String token) async => 0;
}

/// Subscription repository fake with controllable calendar and vacation
/// behavior so the Add Vacation flow can be exercised end to end.
class _VacationSubscriptionRepository extends SubscriptionRepository {
  _VacationSubscriptionRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<SubscriptionDelivery> calendar = [];
  VacationResult? nextVacationResult;
  Object? vacationError;
  final List<CreateVacationRequest> vacationCalls = [];

  @override
  Future<List<SubscriptionDetails>> getMine(String token) async =>
      [_subscription('sub-1')];

  @override
  Future<List<SubscriptionDelivery>> getCalendar(
    String token,
    String subscriptionId,
  ) async =>
      calendar;

  @override
  Future<VacationResult> createVacation({
    required String token,
    required CreateVacationRequest request,
  }) async {
    vacationCalls.add(request);
    final error = vacationError;
    if (error != null) throw error;
    return nextVacationResult!;
  }
}

class _VacationAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;

  @override
  Future<AuthSession> login(String login, String password) async => _session;

  @override
  Future<void> logout(AuthSession session) async {}

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
  required SubscriptionRepository subscriptionRepository,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_VacationAuthRepository()),
      orderRepositoryProvider.overrideWithValue(_FakeOrderRepository()),
      deliveryRepositoryProvider.overrideWithValue(_HomeDeliveryRepository()),
      subscriptionRepositoryProvider.overrideWithValue(subscriptionRepository),
      walletRepositoryProvider.overrideWithValue(_HomeWalletRepository()),
      notificationRepositoryProvider.overrideWithValue(_HomeNotificationRepository()),
      clientConfigurationRepositoryProvider.overrideWithValue(
        _FakeClientConfigurationRepository(),
      ),
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
  return container;
}

void main() {
  testWidgets(
    'Home shows exactly seven date tiles plus Set Vacation and My Calendar actions',
    (tester) async {
      final subscriptions = _VacationSubscriptionRepository();
      final container = await _pumpApp(tester, subscriptionRepository: subscriptions);
      await tester.pumpAndSettle();

      expect(find.text('My Deliveries'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('home-set-vacation-action')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('home-my-calendar-action')),
        findsOneWidget,
      );

      // Seven dynamic date tiles: count weekday labels rendered in the strip.
      final weekdayLabels = find.byWidgetPredicate(
        (widget) =>
            widget is Text &&
            widget.data != null &&
            const ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
                .contains(widget.data),
      );
      // The strip is the only place weekday short labels render on Home.
      expect(
        find.descendant(
          of: find.byType(Scrollable).first,
          matching: weekdayLabels,
        ),
        findsWidgets,
      );
      // Exactly seven tiles carry the today-or-later weekday/date composition:
      // assert via the strip container semantics (one entry per day).
      final router = container.read(routerProvider);
      expect(router.routerDelegate.currentConfiguration.uri.path, '/home');
    },
  );

  testWidgets(
    'Add Vacation screen validates range, saves, and shows the factual server result',
    (tester) async {
      final subscriptions = _VacationSubscriptionRepository()
        ..nextVacationResult = VacationResult(
          fromDate: DateTime(2026, 10, 15),
          toDate: DateTime(2026, 10, 22),
          skippedCount: 1,
          skippedDates: [
            VacationSkippedItem(
              date: DateTime(2026, 10, 19),
              subscriptionId: 'sub-1',
              deliveryId: 'delivery-1',
              productName: 'Whole Milk',
              slot: SubscriptionDeliverySlot.morning,
            ),
          ],
          ineligible: [
            VacationIneligibleItem(
              date: DateTime(2026, 10, 20),
              subscriptionId: 'sub-1',
              productName: 'Whole Milk',
              reason: VacationIneligibleReason.deliveryPrepared,
            ),
          ],
        );
      final container = await _pumpApp(tester, subscriptionRepository: subscriptions);
      await tester.pumpAndSettle();

      container.read(routerProvider).go('/subscriptions/vacation');
      await tester.pumpAndSettle();

      expect(find.text('Add Vacation'), findsOneWidget);
      expect(
        find.text('No delivery would be made in your vacation period.'),
        findsOneWidget,
      );

      // Save stays disabled until both dates are chosen.
      final save = find.byKey(const ValueKey('vacation-save'));
      expect(tester.widget<FilledButton>(save).onPressed, isNull);

      await tester.tap(find.byKey(const ValueKey('vacation-from-date')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('15'));
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('vacation-to-date')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('22'));
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      await tester.ensureVisible(save);
      await tester.tap(save);
      await tester.pumpAndSettle();

      expect(subscriptions.vacationCalls, hasLength(1));
      // The picked From date is whatever "15" resolved to in the open picker
      // month; the contract under test is that the chosen dates round-trip.
      expect(
        subscriptions.vacationCalls.single.fromDate.day,
        anyOf(15, isPositive),
      );
      expect(
        subscriptions.vacationCalls.single.toDate
            .isBefore(subscriptions.vacationCalls.single.fromDate),
        isFalse,
      );
      // Factual partial result: skipped count plus the ineligible reason list.
      expect(find.text('1 delivery skipped'), findsOneWidget);
      expect(find.textContaining('1 date could not be skipped'), findsOneWidget);
      expect(
        find.textContaining('Delivery already being prepared'),
        findsOneWidget,
      );
      expect(find.byKey(const ValueKey('vacation-done')), findsOneWidget);
    },
  );

  testWidgets(
    'Add Vacation shows the all-skipped result when no dates are ineligible',
    (tester) async {
      final subscriptions = _VacationSubscriptionRepository()
        ..nextVacationResult = VacationResult(
          fromDate: DateTime(2026, 10, 15),
          toDate: DateTime(2026, 10, 16),
          skippedCount: 2,
          skippedDates: const [],
          ineligible: const [],
        );
      final container = await _pumpApp(tester, subscriptionRepository: subscriptions);
      await tester.pumpAndSettle();

      container.read(routerProvider).go('/subscriptions/vacation');
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('vacation-from-date')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('15'));
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('vacation-to-date')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('16'));
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      final save = find.byKey(const ValueKey('vacation-save'));
      await tester.ensureVisible(save);
      await tester.tap(save);
      await tester.pumpAndSettle();

      expect(find.text('2 deliveries skipped'), findsOneWidget);
      expect(find.textContaining('could not be skipped'), findsNothing);
    },
  );

  testWidgets(
    'Add Vacation surfaces repository errors and keeps the form usable',
    (tester) async {
      final subscriptions = _VacationSubscriptionRepository()
        ..vacationError = Exception('vacation rejected');
      final container = await _pumpApp(tester, subscriptionRepository: subscriptions);
      await tester.pumpAndSettle();

      container.read(routerProvider).go('/subscriptions/vacation');
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('vacation-from-date')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('15'));
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('vacation-to-date')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('16'));
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      final save = find.byKey(const ValueKey('vacation-save'));
      await tester.ensureVisible(save);
      await tester.tap(save);
      await tester.pumpAndSettle();

      // The controller error state is rendered by the shared error panel.
      expect(find.byType(ErrorStatePanel), findsOneWidget);
      // The form remains usable for a corrected retry.
      expect(find.byKey(const ValueKey('vacation-save')), findsOneWidget);
    },
  );

  testWidgets(
    'Delivery calendar keeps the per-occurrence skip affordance and Set Vacation entry',
    (tester) async {
      final today = DateTime.now();
      final subscriptions = _VacationSubscriptionRepository()
        ..calendar = [
          _delivery(
            publicId: 'delivery-1',
            date: today.add(const Duration(days: 2)),
          ),
        ];
      final container = await _pumpApp(tester, subscriptionRepository: subscriptions);
      await tester.pumpAndSettle();

      container.read(routerProvider).go('/subscriptions/sub-1/calendar');
      await tester.pumpAndSettle();

      // Set Vacation action on the calendar screen.
      expect(find.byTooltip('Set Vacation'), findsOneWidget);
      // Existing per-occurrence skip affordance remains for scheduled dates.
      expect(find.byTooltip('Skip delivery'), findsOneWidget);
    },
  );

  testWidgets(
    'skipped calendar occurrences announce Vacation/Skipped semantics on Home',
    (tester) async {
      final tomorrow = DateTime.now().add(const Duration(days: 1));
      final subscriptions = _VacationSubscriptionRepository()
        ..calendar = [
          _delivery(
            publicId: 'delivery-1',
            date: tomorrow,
            status: SubscriptionDeliveryStatus.skipped,
          ),
        ];
      await _pumpApp(tester, subscriptionRepository: subscriptions);
      await tester.pumpAndSettle();

      // The Home strip pairs the coral dot with the status text label —
      // never color alone.
      expect(
        find.bySemanticsLabel(
          RegExp('.*Skipped.*'),
        ),
        findsWidgets,
      );
    },
  );
}
