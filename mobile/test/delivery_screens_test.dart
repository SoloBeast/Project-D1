import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_repository.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('manager delivery list (mobile-app look)', () {
    testWidgets('renders count header with filters behind the icon',
        (tester) async {
      await _pumpManagement(tester);

      // The front screen keeps the count header and date context; branch,
      // status, type and slot filters live behind the AppBar filter icon.
      expect(find.text('Filters'), findsNothing);
      expect(find.textContaining('deliveries ·'), findsOneWidget);
      expect(find.text('Status'), findsNothing);
      expect(find.text('Delivery type'), findsNothing);
      expect(find.text('Subscription slot'), findsNothing);
      expect(find.byTooltip('Filters'), findsOneWidget);

      await tester.tap(find.byTooltip('Filters'));
      await tester.pumpAndSettle();
      expect(find.text('Filters'), findsOneWidget);
      expect(find.text('Status'), findsOneWidget);
      expect(find.text('Delivery type'), findsOneWidget);
      expect(find.text('Subscription slot'), findsOneWidget);
      // Reference number with a status pill instead of a plain subtitle.
      expect(find.text('ORD-1001'), findsOneWidget);
      expect(find.text('Assigned'), findsWidgets);
    });
  });

  group('manager branch selector and date filter', () {
    testWidgets('single-branch managers see no branch dropdown',
        (tester) async {
      await _pumpManagement(tester);

      expect(find.text('Branch'), findsNothing);
      // The front screen keeps the date context in the subtitle; picking a
      // date stays on the AppBar calendar icon.
      expect(
        find.textContaining(
          'Manage branch deliveries for ${formatDeliveryDate(indiaNow())}',
        ),
        findsOneWidget,
      );
      expect(find.byTooltip('Choose date'), findsOneWidget);
    });

    testWidgets('switching branches reloads the new branch', (tester) async {
      final pumped = await _pumpManagement(tester, _multiBranchSession);

      await tester.tap(find.byTooltip('Filters'));
      await tester.pumpAndSettle();
      expect(find.text('Branch'), findsOneWidget);
      expect(pumped.repository.branchIds, [7]);

      await tester.tap(find.text('Branch'));
      await tester.pumpAndSettle();
      // The menu item lives in the dropdown overlay: the tap lands and the
      // repository assertion below proves the switch really happened.
      await tester.tap(
        find.text('Hadapsar (DB0009)').last,
        warnIfMissed: false,
      );
      await tester.pumpAndSettle();

      expect(pumped.repository.branchIds.last, 9);
      expect(find.text('Hadapsar deliveries'), findsWidgets);
    });

    testWidgets('date button opens the picker and reloads', (tester) async {
      final pumped = await _pumpManagement(tester, _multiBranchSession);
      final day = indiaNow().day == 15 ? '16' : '15';

      await tester.tap(find.byTooltip('Choose date'));
      await tester.pumpAndSettle();
      expect(find.byType(CalendarDatePicker), findsOneWidget);
      await tester.tap(find.text(day));
      await tester.pump();
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      expect(pumped.repository.branchDates.last?.day, int.parse(day));
      expect(find.text('Today'), findsOneWidget);

      await tester.tap(find.text('Today'));
      await tester.pumpAndSettle();
      expect(
        pumped.repository.branchDates.last?.day,
        indiaNow().day,
      );
    });
  });

  group('manager delivery items', () {
    testWidgets('tile shows the subscription product with qty and unit', (
      tester,
    ) async {
      await _pumpManagement(tester);

      // Authoritative snapshot: product name + quantity with its real unit,
      // plus the canonical subscription reference.
      expect(find.text('Fresh Buffalo Milk · 1 litre'), findsOneWidget);
      expect(
        find.textContaining('Subscription SUB/DB0001/26-27/000001'),
        findsOneWidget,
      );
    });

    testWidgets('tile shows one-time order items', (tester) async {
      await _pumpManagement(tester);

      expect(
        find.text('Fresh Buffalo Milk x 2 litre'),
        findsOneWidget,
      );
    });

    testWidgets('detail shows the item breakdown and references', (
      tester,
    ) async {
      final pumped = await _pumpManagement(tester);
      pumped.router.go('/delivery-management/delivery-sub-1');
      await tester.pumpAndSettle();

      expect(find.text('Items'), findsOneWidget);
      expect(find.text('Fresh Buffalo Milk · 1 litre'), findsWidgets);
      expect(
        find.text('Subscription SUB/DB0001/26-27/000001'),
        findsOneWidget,
      );
      expect(
        find.textContaining('SUB-'),
        findsWidgets,
      );
    });
  });

  group('staff delivery list date filter', () {
    testWidgets('shows calendar action and loads the chosen date',
        (tester) async {
      final pumped = await _pumpStaff(tester);

      expect(find.byTooltip('Choose date'), findsOneWidget);
      expect(find.text("Today's deliveries"), findsWidgets);

      // Repository receives the selected date on reload.
      expect(pumped.repository.todayDates, hasLength(1));

      // Status filter keeps working and forwards the current date.
      await tester.tap(find.text('Delivery status'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Delivered').last);
      await tester.pumpAndSettle();
      expect(pumped.repository.todayDates.length, greaterThanOrEqualTo(2));
      expect(pumped.repository.lastStatus, DeliveryStatus.delivered);
    });
  });
}

class _StubPage extends StatelessWidget {
  const _StubPage({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) =>
      Scaffold(appBar: AppBar(title: Text(title)), body: const SizedBox());
}

class _Pumped {
  const _Pumped({required this.router, required this.repository});

  final GoRouter router;
  final _FakeDeliveryRepository repository;
}

Future<_Pumped> _pumpManagement(WidgetTester tester, [AuthSession? session]) =>
    _pump(tester, '/delivery-management/branch/7', session ?? _managerSession);

Future<_Pumped> _pumpStaff(WidgetTester tester) =>
    _pump(tester, '/delivery', _staffSession);

Future<_Pumped> _pump(
  WidgetTester tester,
  String location,
  AuthSession session,
) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final repository = _FakeDeliveryRepository();
  final router = GoRouter(
    initialLocation: location,
    routes: [
      GoRoute(
        path: '/delivery-management/branch/:branchId',
        builder: (context, state) => DeliveryManagementScreen(
          branchId: int.parse(state.pathParameters['branchId']!),
        ),
      ),
      GoRoute(
        path: '/delivery-management/:deliveryId',
        builder: (context, state) => DeliveryManagementDetailScreen(
          deliveryId: state.pathParameters['deliveryId']!,
        ),
      ),
      GoRoute(
        path: '/delivery',
        builder: (context, state) => const StaffDeliveryListScreen(),
      ),
      GoRoute(
        path: '/delivery/:deliveryId',
        builder: (context, state) => const _StubPage(title: 'staff-detail'),
      ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session),
        ),
        deliveryRepositoryProvider.overrideWithValue(repository),
      ],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
  return _Pumped(router: router, repository: repository);
}

class _SeededSessionController extends SessionController {
  _SeededSessionController(this.session);

  final AuthSession session;

  @override
  SessionState build() => SessionState.authenticated(session);
}

class _FakeDeliveryRepository extends DeliveryRepository {
  _FakeDeliveryRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<DateTime> todayDates = [];
  DeliveryStatus? lastStatus;

  Map<String, dynamic> _details(
    String reference,
    String status, {
    String? deliveryId,
    String sourceType = 'OneTimeOrder',
    Map<String, dynamic>? orderSummary,
    String? subscriptionSlot,
    double? quantity,
    String? subscriptionId,
    String? subscriptionNumber,
    String? subscriptionDeliveryId,
    String? subscriptionProductName,
    String? subscriptionUnitOfMeasure,
  }) => {
    'deliveryId': deliveryId ?? 'delivery-$reference',
    'sourceType': sourceType,
    'referenceNumber': reference,
    'status': status,
    'scheduledDate': '2026-10-16',
    'branchId': 7,
    'customerId': 'customer-1',
    'customerName': 'Test Customer',
    'customerMobile': '9999999999',
    'destinationAddress': '1 Main Street, Pune',
    'deliveryInstructions': null,
    'destinationLatitude': 18.5204,
    'destinationLongitude': 73.8567,
    'assignedEmployeeId': 'employee-1',
    'assignedEmployeeName': 'Delivery Agent',
    'assignedAt': '2026-10-16T08:00:00.000',
    'pickedUpAt': null,
    'outForDeliveryAt': null,
    'arrivedAt': null,
    'otpVerifiedAt': null,
    'completedAt': null,
    'failedAt': null,
    'failureReason': null,
    'remarks': null,
    'operationalNotes': null,
    'isTrackingActive': false,
    'latestLocation': null,
    'assignments': [],
    'batchAllocations': [],
    'orderSummary': orderSummary,
    'subscriptionSlot': subscriptionSlot,
    'quantity': quantity,
    'subscriptionId': subscriptionId,
    'subscriptionNumber': subscriptionNumber,
    'subscriptionDeliveryId': subscriptionDeliveryId,
    'subscriptionProductName': subscriptionProductName,
    'subscriptionUnitOfMeasure': subscriptionUnitOfMeasure,
  };

  Map<String, dynamic> _subscriptionDetails(
    String reference, {
    String? deliveryId,
  }) => _details(
    reference,
    'ReadyForAssignment',
    deliveryId: deliveryId,
    sourceType: 'SubscriptionOccurrence',
    subscriptionSlot: 'Morning',
    quantity: 1,
    subscriptionId: 'subscription-1',
    subscriptionNumber: 'SUB/DB0001/26-27/000001',
    subscriptionDeliveryId: 'occurrence-1',
    subscriptionProductName: 'Fresh Buffalo Milk',
    subscriptionUnitOfMeasure: 'litre',
  );

  Map<String, dynamic> _orderDetails(String reference, {String? deliveryId}) =>
      _details(
        reference,
        'Assigned',
        deliveryId: deliveryId,
        orderSummary: {
      'orderNumber': 'ORD/DB0001/26-27/000001',
      'totalQuantity': 2,
      'totalAmount': 160,
      'items': ['Fresh Buffalo Milk x 2 litre'],
    },
  );

  @override
  Future<List<DeliveryDetails>> getToday(
    String token,
    DateTime date, {
    DeliveryStatus? status,
  }) async {
    todayDates.add(date);
    lastStatus = status;
    return [DeliveryDetails.fromJson(_details('ORD-1001', 'Assigned'))];
  }

  final List<int> branchIds = [];
  final List<DateTime?> branchDates = [];

  @override
  Future<List<DeliveryDetails>> getBranch(
    String token,
    int branchId, {
    DateTime? date,
    DeliveryStatus? status,
    DeliverySourceType? sourceType,
    SubscriptionDeliverySlot? slot,
  }) async {
    branchIds.add(branchId);
    branchDates.add(date);
    return [
      DeliveryDetails.fromJson(_orderDetails('ORD-1001')),
      DeliveryDetails.fromJson(
        _subscriptionDetails('SUB-aaa-20261016'),
      ),
    ];
  }

  Map<String, dynamic> _managedDetails(String id) => id == 'delivery-sub-1'
      ? _subscriptionDetails('SUB-aaa-20261016', deliveryId: id)
      : _orderDetails('ORD-1001', deliveryId: id);

  @override
  Future<DeliveryDetails> getManaged(String token, String id) async =>
      DeliveryDetails.fromJson(_managedDetails(id));

  @override
  Future<List<DeliveryEmployee>> getEmployees(
    String token,
    int branchId,
  ) async => [];
}

final _managerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'manager-1',
    displayName: 'Dairy Manager',
    email: null,
    mobile: '9999999999',
    roles: ['DAIRY_MANAGER'],
    permissions: ['DELIVERIES.READ_BRANCH'],
    branchIds: [7],
    branchDetails: [AuthUserBranchInfo(id: 7, code: 'DB0001', name: 'Dabua')],
  ),
  accessToken: 'manager-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _multiBranchSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'manager-1',
    displayName: 'Dairy Manager',
    email: null,
    mobile: '9999999999',
    roles: ['DAIRY_MANAGER'],
    permissions: ['DELIVERIES.READ_BRANCH'],
    branchIds: [7, 9],
    branchDetails: [
      AuthUserBranchInfo(id: 7, code: 'DB0001', name: 'Dabua'),
      AuthUserBranchInfo(id: 9, code: 'DB0009', name: 'Hadapsar'),
    ],
  ),
  accessToken: 'manager-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _staffSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Delivery Agent',
    email: null,
    mobile: '9999999998',
    roles: ['DELIVERY_STAFF'],
    permissions: ['DELIVERIES.READ'],
    branchIds: [7],
  ),
  accessToken: 'staff-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
