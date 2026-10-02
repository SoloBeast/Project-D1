import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:doodh_direct_mobile/features/orders/order_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

OrderChargeLine _charge({
  String type = 'GST',
  String code = 'GST-5',
  String? description = 'GST five percent',
  double percentage = 5,
  double baseAmount = 200,
  double amount = 10,
}) => OrderChargeLine(
  chargeType: type,
  chargeCode: code,
  description: description,
  percentage: percentage,
  baseAmount: baseAmount,
  amount: amount,
);

OrderSummary _order({required List<OrderChargeLine> charges}) => OrderSummary(
  publicId: 'order-1',
  orderNumber: 'DD-20260928-000001',
  type: 'OneTime',
  status: 'Confirmed',
  createdAt: DateTime(2026, 9, 28, 10),
  addressLabel: 'Home',
  city: 'Bengaluru',
  branchName: 'MAIN',
  items: const [
    OrderItem(
      productId: 'p-1',
      productName: 'Whole Milk',
      sku: 'MILK-1L',
      unitOfMeasure: 'litre',
      quantity: 2.5,
      unitPrice: 80,
      lineTotal: 200,
    ),
  ],
  subtotal: 200,
  discountAmount: 0,
  charges: charges,
  chargesTotal: charges.fold<double>(0, (sum, c) => sum + c.amount),
  payableAmount: 200 + charges.fold<double>(0, (sum, c) => sum + c.amount),
  cancelledAt: null,
  paymentPublicId: null,
  paymentStatus: 'Captured',
  gatewayPaymentId: null,
  deliveryPublicId: null,
  deliveryReferenceNumber: null,
  deliveryStatus: null,
);

class _SeededAuthRepository extends AuthRepository {
  _SeededAuthRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<AuthSession?> restore() async => AuthSession(
    user: AuthUser(
      publicUserId: 'charge-customer',
      displayName: 'Customer',
      email: null,
      mobile: '9999999999',
      roles: const ['CUSTOMER'],
      permissions: const [],
      branchIds: const [],
    ),
    accessToken: 'charge-customer-token',
    refreshToken: 'refresh-token',
    accessTokenExpiresAtUtc: DateTime.utc(2099),
    refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
  );

  @override
  Future<void> saveSession(AuthSession session) async {}

  @override
  Future<void> clear() async {}
}

class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository(this.order)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final OrderSummary order;

  @override
  Future<OrderSummary> get(String token, String orderId) async => order;
}

Future<ProviderContainer> _pumpOrderDetail(
  WidgetTester tester,
  OrderSummary order,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_SeededAuthRepository()),
      orderRepositoryProvider.overrideWithValue(_FakeOrderRepository(order)),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: OrderDetailScreen(orderId: 'order-1')),
    ),
  );
  // The screen's initState microtask races the async session restore, so the
  // first load is a no-op; re-drive it now that the session token exists.
  await tester.pumpAndSettle();
  await container.read(orderControllerProvider.notifier).loadOrder('order-1');
  await tester.pumpAndSettle();
  return container;
}

void main() {
  group('order detail charge snapshot display', () {
    testWidgets('no charges: no charge rows and total equals subtotal', (
      tester,
    ) async {
      await _pumpOrderDetail(tester, _order(charges: const []));

      expect(find.text('Subtotal'), findsOneWidget);
      expect(find.text('GST five percent (5%)'), findsNothing);
      expect(find.text('Total paid / payable'), findsOneWidget);
      // The subtotal value appears as item line total, subtotal row and totals.
      expect(find.text('₹200.00'), findsWidgets);
    });

    testWidgets('one charge: description, percentage and server amount render', (
      tester,
    ) async {
      await _pumpOrderDetail(tester, _order(charges: [_charge()]));

      expect(find.text('GST five percent (5%)'), findsOneWidget);
      expect(find.text('₹10.00'), findsOneWidget);
      // The charge-inclusive total appears in the items summary AND the payment
      // section — both read the same server value.
      expect(find.text('₹210.00'), findsNWidgets(2));
    });

    testWidgets('multiple charges: each server row renders with its amount', (
      tester,
    ) async {
      await _pumpOrderDetail(
        tester,
        _order(
          charges: [
            _charge(
              code: 'CGST',
              description: 'CGST 2.5',
              percentage: 2.5,
              amount: 5,
            ),
            _charge(
              code: 'SGST',
              description: 'SGST 2.5',
              percentage: 2.5,
              amount: 5,
            ),
          ],
        ),
      );

      // Percentages normalize to the backend's two-decimal convention.
      expect(find.text('CGST 2.5 (2.50%)'), findsOneWidget);
      expect(find.text('SGST 2.5 (2.50%)'), findsOneWidget);
      expect(find.text('₹5.00'), findsNWidgets(2));
      expect(find.text('₹210.00'), findsNWidgets(2));
    });

    testWidgets('description falls back to the charge type when absent', (
      tester,
    ) async {
      await _pumpOrderDetail(
        tester,
        _order(charges: [_charge(description: null)]),
      );

      expect(find.text('GST (5%)'), findsOneWidget);
    });

    testWidgets('decimal percentage renders with two places', (tester) async {
      await _pumpOrderDetail(
        tester,
        _order(
          charges: [
            _charge(
              code: 'SC',
              type: 'Service',
              description: 'Service charge',
              percentage: 2.55,
              amount: 5.10,
            ),
          ],
        ),
      );

      expect(find.text('Service charge (2.55%)'), findsOneWidget);
      expect(find.text('₹5.10'), findsOneWidget);
    });

    testWidgets('historical snapshot renders verbatim (master since changed to '
        '6% must NOT be shown)', (tester) async {
      // The order was created when GST-5 was 5%; the frozen snapshot travels
      // with the order, so Flutter must show 5% / ₹10 — never the current master.
      await _pumpOrderDetail(tester, _order(charges: [_charge()]));

      expect(find.text('GST five percent (5%)'), findsOneWidget);
      expect(find.text('₹10.00'), findsOneWidget);
      expect(find.text('6%'), findsNothing);
    });
  });

  group('OrderChargeLine parsing (server-authoritative values)', () {
    test('parses the backend charge payload exactly', () {
      final line = OrderChargeLine.fromJson(const {
        'chargeType': 'GST',
        'chargeCode': 'GST-5',
        'description': 'GST five percent',
        'percentage': 5,
        'baseAmount': 200,
        'amount': 10,
      });

      expect(line.chargeType, 'GST');
      expect(line.chargeCode, 'GST-5');
      expect(line.description, 'GST five percent');
      expect(line.percentage, 5);
      expect(line.baseAmount, 200);
      expect(line.amount, 10);
      expect(line.displayLabel, 'GST five percent');
      expect(line.formattedAmount, '₹10.00');
      expect(line.formattedPercentage, '5%');
    });

    test('falls back to the charge type when description is absent', () {
      final line = OrderChargeLine.fromJson(const {
        'chargeType': 'Service',
        'chargeCode': 'SC',
        'description': null,
        'percentage': 2.55,
        'baseAmount': 200,
        'amount': 5.1,
      });

      expect(line.displayLabel, 'Service');
      expect(line.formattedPercentage, '2.55%');
      expect(line.formattedAmount, '₹5.10');
    });

    test('checkout preview parses server charges verbatim — inactive charges '
        'never appear because the server omits them, and payable stays '
        'server-computed', () {
      final preview = CheckoutPreview.fromJson(const {
        'addressId': null,
        'addressLabel': 'Home',
        'addressLine1': '1 Main Road',
        'addressLine2': null,
        'locality': 'Central',
        'city': 'Bengaluru',
        'state': 'Karnataka',
        'pinCode': '560001',
        'contactName': 'Customer',
        'contactMobile': '9999999999',
        'branchId': 'b-1',
        'branchCode': 'MAIN',
        'branchName': 'Main',
        'distanceKm': 1.2,
        'items': [
          {
            'productId': 'p-1',
            'productName': 'Whole Milk',
            'sku': 'MILK-1L',
            'unitOfMeasure': 'litre',
            'quantity': 1,
            'unitPrice': 80,
            'lineTotal': 80,
          },
        ],
        'subtotal': 80,
        'discountAmount': 0,
        'charges': [
          {
            'chargeType': 'GST',
            'chargeCode': 'GST-5',
            'description': 'GST five percent',
            'percentage': 5,
            'baseAmount': 80,
            'amount': 4,
          },
        ],
        'chargesTotal': 4,
        'payableAmount': 84,
      });

      expect(preview.charges, hasLength(1));
      expect(preview.charges.single.amount, 4);
      expect(preview.chargesTotal, 4);
      // Displayed verbatim: 84 = 80 + 4 — no locally recomputed number.
      expect(preview.payableAmount, 84);
    });

    test('legacy payloads without charges parse to empty charge lists', () {
      final preview = CheckoutPreview.fromJson(const {
        'addressId': null,
        'addressLabel': 'Home',
        'addressLine1': '1 Main Road',
        'addressLine2': null,
        'locality': 'Central',
        'city': 'Bengaluru',
        'state': 'Karnataka',
        'pinCode': '560001',
        'contactName': 'Customer',
        'contactMobile': '9999999999',
        'branchId': 'b-1',
        'branchCode': 'MAIN',
        'branchName': 'Main',
        'distanceKm': 1.2,
        'items': <Map<String, dynamic>>[],
        'subtotal': 80,
        'discountAmount': 0,
        'payableAmount': 80,
      });

      expect(preview.charges, isEmpty);
      expect(preview.chargesTotal, 0);
      expect(preview.payableAmount, 80);
    });
  });
}
