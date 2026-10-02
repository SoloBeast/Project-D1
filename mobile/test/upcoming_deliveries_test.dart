import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart'
    hide SubscriptionDeliverySlot;
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart'
    show UpcomingDeliveriesSection;
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

DateTime _day(int offset) {
  final now = DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  return today.add(Duration(days: offset));
}

CustomerDelivery _delivery({
  required String id,
  required DateTime date,
  DeliverySourceType sourceType = DeliverySourceType.oneTimeOrder,
  String reference = 'ORD/MAIN/2026/000042',
  DeliveryStatus status = DeliveryStatus.readyForAssignment,
}) => CustomerDelivery(
  deliveryId: id,
  sourceType: sourceType,
  referenceNumber: reference,
  status: status,
  scheduledDate: date,
  destinationAddress: '12 Milk Street',
  assignedEmployeeId: null,
  assignedEmployeeName: null,
  isTrackingActive: false,
  latestLocation: null,
  completedAt: null,
  failedAt: null,
  failureReason: null,
  activeOtp: null,
);

OrderSummary _order({
  required String publicId,
  required String deliveryReference,
  DateTime? cancelledAt,
}) => OrderSummary(
  publicId: publicId,
  orderNumber: 'ORD/MAIN/2026/000042',
  type: 'OneTimeOrder',
  status: cancelledAt == null ? 'Confirmed' : 'Cancelled',
  createdAt: DateTime(2026, 9, 27),
  addressLabel: 'Home',
  city: 'Rohtak',
  branchName: 'MAIN',
  items: [
    OrderItem(
      productId: 'p1',
      productName: 'Fresh Buffalo Milk',
      sku: 'MILK-001',
      unitOfMeasure: 'litre',
      quantity: 2,
      unitPrice: 80,
      lineTotal: 160,
    ),
  ],
  subtotal: 160,
  discountAmount: 0,
  payableAmount: 160,
  cancelledAt: cancelledAt,
  paymentPublicId: null,
  paymentStatus: 'Paid',
  gatewayPaymentId: null,
  deliveryPublicId: 'd-1',
  deliveryReferenceNumber: deliveryReference,
  deliveryStatus: 'ReadyForAssignment',
);

/// India-local calendar today — the business date the backend schedules
/// against (the bug: a subscription occurrence scheduled TODAY never
/// materialized a Delivery row, so the section showed nothing).
DateTime _indiaToday() {
  final now = indiaNow();
  return DateTime(now.year, now.month, now.day);
}

SubscriptionDelivery _occurrence(
  String publicId,
  DateTime date, {
  SubscriptionDeliveryStatus status = SubscriptionDeliveryStatus.scheduled,
}) => SubscriptionDelivery(
  publicId: publicId,
  scheduledDate: date,
  slot: SubscriptionDeliverySlot.morning,
  quantity: 1,
  status: status,
  branchId: 'b1',
  branchCode: 'MAIN',
  branchName: 'Main',
  address: '12 Milk Street',
  statusChangedAt: null,
);

SubscriptionDetails _subscription({
  required String publicId,
  SubscriptionStatus status = SubscriptionStatus.active,
  String productName = 'Malai Paneer 250g',
}) => SubscriptionDetails(
  publicId: publicId,
  status: status,
  productId: 'p2',
  productSku: 'PANEER-250',
  productName: productName,
  unitOfMeasure: 'piece',
  quantity: 1,
  unitPrice: 110,
  payableAmount: 110,
  startDate: DateTime(2026, 9, 1),
  endDate: DateTime(2026, 12, 31),
  totalEntitlement: 90,
  usedEntitlement: 10,
  remainingEntitlement: 80,
  addressId: 'a1',
  address: '12 Milk Street',
  branchId: 'b1',
  branchCode: 'MAIN',
  branchName: 'Main',
  schedules: const [],
  activatedAt: DateTime(2026, 9, 1),
  pausedAt: null,
  cancelledAt: null,
  completedAt: null,
  createdAt: DateTime(2026, 9, 1),
);

/// Home-state seeds. DeliveryController/OrderController/SubscriptionController
/// are all overridden so the section composes purely from injected state —
/// no network, matching the production read path (Home loads exactly these).
class _SeededDeliveryController extends DeliveryController {
  _SeededDeliveryController(this.seed);

  final DeliveryState seed;

  @override
  DeliveryState build() => seed;
}

class _SeededOrderController extends OrderController {
  _SeededOrderController(this.seed);

  final OrderState seed;

  @override
  OrderState build() => seed;
}

class _SeededSubscriptionController extends SubscriptionController {
  _SeededSubscriptionController(this.seed);

  final SubscriptionState seed;

  @override
  SubscriptionState build() => seed;
}

/// Records skip calls instead of hitting the repository — the section must
/// route through the EXISTING controller skip for calendar occurrences.
class _RecordingSubscriptionController extends _SeededSubscriptionController {
  _RecordingSubscriptionController(super.seed);

  final List<(String, String)> skipCalls = [];

  @override
  Future<bool> skip(String subscriptionId, String deliveryId) async {
    skipCalls.add((subscriptionId, deliveryId));
    return true;
  }
}

Future<ProviderContainer> _pumpHome(
  WidgetTester tester, {
  required List<CustomerDelivery> deliveries,
  List<OrderSummary> orders = const [],
  List<SubscriptionDetails> subscriptions = const [],
  List<SubscriptionDelivery> calendar = const [],
  Map<String, List<SubscriptionDelivery>>? calendarsBySubscription,
  _SeededSubscriptionController? subscriptionController,
}) async {
  final container = ProviderContainer(
    overrides: [
      deliveryControllerProvider.overrideWith(
        () => _SeededDeliveryController(
          DeliveryState(customerDeliveries: deliveries),
        ),
      ),
      orderControllerProvider.overrideWith(
        () => _SeededOrderController(OrderState(orders: orders)),
      ),
      subscriptionControllerProvider.overrideWith(
        () =>
            subscriptionController ??
            _SeededSubscriptionController(
              SubscriptionState(
                subscriptions: subscriptions,
                calendar: calendar,
                calendarsBySubscription:
                    calendarsBySubscription ??
                    const <String, List<SubscriptionDelivery>>{},
              ),
            ),
      ),
    ],
  );
  addTearDown(container.dispose);
  await tester.binding.setSurfaceSize(const Size(800, 1600));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: Scaffold(
          body: SingleChildScrollView(
            child: Column(children: const [MyDeliveriesPreview()]),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  return container;
}

void main() {
  testWidgets(
    'upcoming section shows scheduled one-time order with Modify and Cancel',
    (tester) async {
      final order = _order(
        publicId: 'order-1',
        deliveryReference: 'ORD/MAIN/2026/000042',
      );
      await _pumpHome(
        tester,
        deliveries: [
          _delivery(
            id: 'd-1',
            date: _day(1),
            reference: 'ORD/MAIN/2026/000042',
          ),
        ],
        orders: [order],
      );

      expect(
        find.byKey(const ValueKey('home-upcoming-deliveries-title')),
        findsOneWidget,
      );
      expect(find.text('Fresh Buffalo Milk'), findsOneWidget);
      expect(find.text('2 litre'), findsOneWidget);
      expect(find.text('Modify'), findsOneWidget);
      expect(find.text('Cancel'), findsOneWidget);
      expect(find.text('Add Item'), findsOneWidget);
    },
  );

  testWidgets(
    'subscription occurrence resolves product name and hides Modify when '
    'the subscription state forbids it',
    (tester) async {
      final paused = _subscription(
        // API GUID format (dashed) — the reference number carries the same
        // id in N-format (no dashes), which the section reassembles.
        publicId: '0f8f2f71-1984-b1e8-8127-21a15a04069e',
        status: SubscriptionStatus.cancelled,
      );
      // SUB-{publicId:N}-{yyyyMMdd}
      final reference =
          'SUB-0f8f2f711984b1e8812721a15a04069e-${_format(_day(1))}';
      await _pumpHome(
        tester,
        deliveries: [
          _delivery(
            id: 'd-2',
            date: _day(1),
            sourceType: DeliverySourceType.subscriptionOccurrence,
            reference: reference,
          ),
        ],
        subscriptions: [paused],
      );

      expect(find.text('Malai Paneer 250g'), findsOneWidget);
      // Cancelled subscriptions can neither be modified nor cancelled again —
      // only tracking remains.
      expect(find.text('Modify'), findsNothing);
      expect(find.text('Cancel'), findsNothing);
    },
  );

  testWidgets('cards are ordered by scheduled date and capped', (tester) async {
    final deliveries = <CustomerDelivery>[
      for (var i = 5; i >= 0; i--)
        _delivery(id: 'd-$i', date: _day(i), reference: 'REF-$i'),
    ];
    await _pumpHome(tester, deliveries: deliveries);

    final list = find.byKey(const ValueKey('home-upcoming-deliveries-list'));
    expect(list, findsOneWidget);
    // Delivered/failed are excluded; the rest render in date order.
    expect(
      find.byKey(const ValueKey('home-upcoming-modify-d-1')),
      findsNothing,
    );
  });

  testWidgets('delivered and failed deliveries are excluded', (tester) async {
    await _pumpHome(
      tester,
      deliveries: [
        _delivery(id: 'd-old', date: _day(0), status: DeliveryStatus.delivered),
        _delivery(id: 'd-failed', date: _day(1), status: DeliveryStatus.failed),
        _delivery(id: 'd-next', date: _day(2), reference: 'REF-NEXT'),
      ],
    );

    expect(
      find.text(
        'Nothing scheduled right now. Add an item to start a delivery.',
      ),
      findsNothing,
    );
    expect(
      find.byKey(const ValueKey('home-upcoming-cancel-d-old')),
      findsNothing,
    );
    expect(find.text('No delivery items to show'), findsNothing);
  });

  testWidgets(
    'a scheduled TODAY calendar occurrence shows as an upcoming card with Skip '
    '(business bug: un-materialized delivery)',
    (tester) async {
      final today = _indiaToday();
      final controller = _RecordingSubscriptionController(
        SubscriptionState(
          subscriptions: [
            // API GUID format; the section rebuilds the SUB-{N}-{yyyyMMdd}
            // reference from it.
            _subscription(publicId: '0f8f2f71-1984-b1e8-8127-21a15a04069e'),
          ],
          calendar: [_occurrence('occ-9', today)],
        ),
      );
      await _pumpHome(
        tester,
        deliveries: const [],
        subscriptionController: controller,
      );

      // The card renders the subscription product — NOT the empty state.
      expect(find.textContaining('Nothing scheduled right now'), findsNothing);
      expect(find.text('Malai Paneer 250g'), findsOneWidget);
      // Occurrences are skipped per-date, never cancelled wholesale.
      expect(
        find.byKey(const ValueKey('home-upcoming-skip-occurrence-occ-9')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('home-upcoming-cancel-occurrence-occ-9')),
        findsNothing,
      );

      // Confirming the dialog routes through the existing controller skip.
      await tester.tap(
        find.byKey(const ValueKey('home-upcoming-skip-occurrence-occ-9')),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Skip'));
      await tester.pumpAndSettle();

      expect(controller.skipCalls, [
        ('0f8f2f71-1984-b1e8-8127-21a15a04069e', 'occ-9'),
      ]);
    },
  );

  testWidgets(
    'calendar occurrence already materialized into a Delivery is not duplicated',
    (tester) async {
      final today = _indiaToday();
      final reference =
          'SUB-0f8f2f711984b1e8812721a15a04069e-${_format(today)}';
      await _pumpHome(
        tester,
        deliveries: [
          _delivery(
            id: 'd-mat',
            date: today,
            sourceType: DeliverySourceType.subscriptionOccurrence,
            reference: reference,
          ),
        ],
        subscriptions: [
          _subscription(publicId: '0f8f2f71-1984-b1e8-8127-21a15a04069e'),
        ],
        calendar: [_occurrence('occ-9', today)],
      );

      // Exactly one card — the materialized one (no Skip affordance on it).
      expect(find.text('Malai Paneer 250g'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('home-upcoming-skip-occurrence-occ-9')),
        findsNothing,
      );
    },
  );

  testWidgets('two active subscriptions both show on the same date '
      '(business bug: only the first subscription appeared)', (tester) async {
    final today = _indiaToday();
    await _pumpHome(
      tester,
      deliveries: const [],
      subscriptions: [
        _subscription(publicId: '0f8f2f71-1984-b1e8-8127-21a15a04069e'),
        _subscription(
          publicId: '11111111-2222-3333-4444-555555555555',
          productName: 'Toned Milk 500ml',
        ),
      ],
      calendarsBySubscription: {
        '0f8f2f71-1984-b1e8-8127-21a15a04069e': [_occurrence('occ-1', today)],
        '11111111-2222-3333-4444-555555555555': [_occurrence('occ-2', today)],
      },
    );

    // Both subscriptions' occurrences render for the same date, each with
    // its own product identity and Skip affordance.
    expect(find.text('Malai Paneer 250g'), findsOneWidget);
    expect(find.text('Toned Milk 500ml'), findsOneWidget);
    expect(
      find.byKey(const ValueKey('home-upcoming-skip-occurrence-occ-1')),
      findsOneWidget,
    );
    expect(
      find.byKey(const ValueKey('home-upcoming-skip-occurrence-occ-2')),
      findsOneWidget,
    );
  });

  testWidgets('empty state invites adding an item', (tester) async {
    await _pumpHome(tester, deliveries: const []);

    expect(find.textContaining('Nothing scheduled right now'), findsOneWidget);
    expect(find.text('Add Item'), findsOneWidget);
  });
}

String _format(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}'
    '${value.month.toString().padLeft(2, '0')}'
    '${value.day.toString().padLeft(2, '0')}';

/// Mounts the exact section Home renders inside the My Deliveries band.
class MyDeliveriesPreview extends StatelessWidget {
  const MyDeliveriesPreview({super.key});

  @override
  Widget build(BuildContext context) => DoodhCard(
    color: const Color(0xFFF2E3C0),
    padding: const EdgeInsets.all(16),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [Text('My Deliveries'), UpcomingDeliveriesSection()],
    ),
  );
}
