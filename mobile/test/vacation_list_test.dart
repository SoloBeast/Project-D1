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
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/vacation_list_screen.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

DateTime _day(DateTime value) => DateTime(value.year, value.month, value.day);
DateTime get _today => _day(DateTime.now());

SubscriptionDelivery _occurrence(
  String id,
  DateTime date, {
  SubscriptionDeliveryStatus status = SubscriptionDeliveryStatus.skipped,
}) => SubscriptionDelivery(
  publicId: id,
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

String _iso(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';

void main() {
  group('groupVacationRanges', () {
    test('groups consecutive skipped days and splits on gaps', () {
      final occurrences = [
        _occurrence('a', DateTime(2026, 10, 16)),
        _occurrence('b', DateTime(2026, 10, 17)),
        _occurrence('c', DateTime(2026, 10, 18)),
        _occurrence('d', DateTime(2026, 10, 21)),
      ];
      final ranges = groupVacationRanges(occurrences, (_) => 'Whole Milk');

      expect(ranges, hasLength(2));
      // Newest first.
      expect(ranges.first.from, DateTime(2026, 10, 21));
      expect(ranges.first.dayCount, 1);
      expect(ranges.last.from, DateTime(2026, 10, 16));
      expect(ranges.last.to, DateTime(2026, 10, 18));
      expect(ranges.last.dayCount, 3);
      expect(ranges.last.skippedCount, 3);
      expect(ranges.last.productNames, {'Whole Milk'});
    });

    test('ignores non-vacation occurrences', () {
      final occurrences = [
        _occurrence(
          'a',
          DateTime(2026, 10, 16),
          status: SubscriptionDeliveryStatus.scheduled,
        ),
        _occurrence(
          'b',
          DateTime(2026, 10, 17),
          status: SubscriptionDeliveryStatus.delivered,
        ),
        _occurrence('c', DateTime(2026, 10, 18)),
      ];
      final ranges = groupVacationRanges(occurrences, (_) => 'Whole Milk');

      expect(ranges, hasLength(1));
      expect(ranges.single.from, DateTime(2026, 10, 18));
    });

    test('returns empty when nothing is skipped', () {
      final ranges = groupVacationRanges(
        [
          _occurrence(
            'a',
            DateTime(2026, 10, 16),
            status: SubscriptionDeliveryStatus.scheduled,
          ),
        ],
        (_) => 'Whole Milk',
      );
      expect(ranges, isEmpty);
    });
  });

  group('VacationListScreen', () {
    testWidgets('shows upcoming/past ranges under their tabs', (tester) async {
      final upcomingA = _today.add(const Duration(days: 10));
      final upcomingB = _today.add(const Duration(days: 11));
      final past = _today.subtract(const Duration(days: 5));
      final repository = _ListSubscriptionRepository([
        _occurrence('up-1', upcomingA),
        _occurrence('up-2', upcomingB),
        _occurrence('past-1', past),
        _occurrence(
          'sched-1',
          _today.add(const Duration(days: 12)),
          status: SubscriptionDeliveryStatus.scheduled,
        ),
      ]);
      final container = await _pumpApp(
        tester,
        subscriptionRepository: repository,
      );
      await tester.pumpAndSettle();

      container.read(routerProvider).go('/subscriptions/vacations');
      await tester.pumpAndSettle();

      expect(find.text('My Vacations'), findsOneWidget);
      // Default tab is Upcoming: the consecutive pair forms one range card
      // keyed by its start date.
      expect(
        find.byKey(ValueKey('vacation-range-card-${_iso(upcomingA)}')),
        findsOneWidget,
      );
      expect(find.textContaining('deliveries skipped'), findsOneWidget);
      expect(find.text('Past (1)'), findsOneWidget);

      // Past tab shows the single past range.
      await tester.tap(find.text('Past (1)'));
      await tester.pumpAndSettle();
      expect(
        find.byKey(ValueKey('vacation-range-card-${_iso(past)}')),
        findsOneWidget,
      );
      expect(find.text('Past'), findsOneWidget);

      // Present tab is empty (no range covers today).
      await tester.tap(find.text('Present (0)'));
      await tester.pumpAndSettle();
      expect(find.text('No ongoing vacation'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('empty calendars show the empty state with a set action',
        (tester) async {
      final repository = _ListSubscriptionRepository([]);
      final container = await _pumpApp(
        tester,
        subscriptionRepository: repository,
      );
      await tester.pumpAndSettle();

      container.read(routerProvider).go('/subscriptions/vacations');
      await tester.pumpAndSettle();

      expect(find.text('No upcoming vacations'), findsOneWidget);
      expect(find.text('Set vacation'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });
}

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

class _ListSubscriptionRepository extends SubscriptionRepository {
  _ListSubscriptionRepository(this.calendar)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<SubscriptionDelivery> calendar;

  @override
  Future<List<SubscriptionDetails>> getMine(String token) async => [
    _subscription('sub-1'),
  ];

  @override
  Future<List<SubscriptionDelivery>> getCalendar(
    String token,
    String subscriptionId,
  ) async => calendar;
}

class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));
}

class _FakeClientConfigurationRepository
    extends ClientConfigurationRepository {
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

class _FakeDeliveryRepository extends DeliveryRepository {
  _FakeDeliveryRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<CustomerDelivery>> getMine(String token) async =>
      const <CustomerDelivery>[];
}

class _FakeWalletRepository extends WalletRepository {
  _FakeWalletRepository()
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

class _FakeNotificationRepository extends NotificationRepository {
  _FakeNotificationRepository()
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

class _ListAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;

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
      authRepositoryProvider.overrideWithValue(_ListAuthRepository()),
      orderRepositoryProvider.overrideWithValue(_FakeOrderRepository()),
      deliveryRepositoryProvider.overrideWithValue(_FakeDeliveryRepository()),
      subscriptionRepositoryProvider.overrideWithValue(subscriptionRepository),
      walletRepositoryProvider.overrideWithValue(_FakeWalletRepository()),
      notificationRepositoryProvider.overrideWithValue(
        _FakeNotificationRepository(),
      ),
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
