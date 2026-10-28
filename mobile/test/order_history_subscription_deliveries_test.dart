import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

/// Orders screen surfaces subscription occurrences alongside one-time
/// orders: subscription deliveries arrive as deliveries (not orders), so
/// the screen lists them in their own section with tracking one tap away.
void main() {
  group('order history subscription deliveries', () {
    testWidgets('subscription section lists occurrence deliveries', (
      tester,
    ) async {
      await _pumpHistory(
        tester,
        deliveries: [
          CustomerDelivery.fromJson(
            _subscriptionDeliveryJson(
              reference: 'SUB-445da95c-20261019',
              status: 'Delivered',
              completedAt: '2026-10-19T08:30:00.000',
            ),
          ),
        ],
      );

      expect(find.text('Subscription deliveries'), findsOneWidget);
      expect(find.text('SUB-445da95c-20261019'), findsOneWidget);
      expect(find.text('No orders yet'), findsNothing);
    });

    testWidgets('one-time deliveries stay out of the subscription section', (
      tester,
    ) async {
      await _pumpHistory(
        tester,
        deliveries: [
          CustomerDelivery.fromJson(
            _subscriptionDeliveryJson(
              reference: 'SUB-445da95c-20261019',
              status: 'OutForDelivery',
            ),
          ),
          CustomerDelivery.fromJson(
            _oneTimeDeliveryJson(reference: 'ORD-1001'),
          ),
        ],
      );

      expect(find.text('Subscription deliveries'), findsOneWidget);
      expect(find.text('SUB-445da95c-20261019'), findsOneWidget);
      expect(find.text('ORD-1001'), findsNothing);
    });

    testWidgets('empty orders and deliveries show the empty state', (
      tester,
    ) async {
      await _pumpHistory(tester, deliveries: []);

      expect(find.text('No orders yet'), findsOneWidget);
      expect(find.text('Subscription deliveries'), findsNothing);
    });
  });
}

Map<String, dynamic> _subscriptionDeliveryJson({
  required String reference,
  required String status,
  String? completedAt,
}) => {
  'deliveryId': 'delivery-sub-1',
  'sourceType': 'SubscriptionOccurrence',
  'referenceNumber': reference,
  'status': status,
  'scheduledDate': '2026-10-19',
  'destinationAddress': 'E-1492, Dabua Colony, Faridabad',
  'assignedEmployeeId': 'employee-1',
  'assignedEmployeeName': 'Delivery Agent',
  'isTrackingActive': false,
  'latestLocation': null,
  'completedAt': completedAt,
  'failedAt': null,
  'failureReason': null,
  'activeOtp': null,
};

Map<String, dynamic> _oneTimeDeliveryJson({required String reference}) => {
  'deliveryId': 'delivery-one-1',
  'sourceType': 'OneTimeOrder',
  'referenceNumber': reference,
  'status': 'OutForDelivery',
  'scheduledDate': '2026-10-19',
  'destinationAddress': '1 Main Street, Pune',
  'assignedEmployeeId': 'employee-1',
  'assignedEmployeeName': 'Delivery Agent',
  'isTrackingActive': true,
  'latestLocation': null,
  'completedAt': null,
  'failedAt': null,
  'failureReason': null,
  'activeOtp': null,
};

Future<void> _pumpHistory(
  WidgetTester tester, {
  required List<CustomerDelivery> deliveries,
}) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        orderControllerProvider.overrideWith(
          () => _SeededOrderController(const OrderState()),
        ),
        deliveryControllerProvider.overrideWith(
          () => _SeededDeliveryController(
            DeliveryState(customerDeliveries: deliveries),
          ),
        ),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: const OrderHistoryScreen()),
    ),
  );
  await tester.pumpAndSettle();
}

class _SeededOrderController extends OrderController {
  _SeededOrderController(this.seed);

  final OrderState seed;

  @override
  OrderState build() => seed;

  @override
  Future<void> loadOrders() async {}
}

class _SeededDeliveryController extends DeliveryController {
  _SeededDeliveryController(this.seed);

  final DeliveryState seed;

  @override
  DeliveryState build() => seed;

  @override
  Future<void> loadCustomerDeliveries() async {}
}
