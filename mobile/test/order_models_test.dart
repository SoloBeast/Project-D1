import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('checkout request serializes address and decimal item quantities', () {
    const request = CheckoutRequest(
      addressId: 'address-1',
      items: [OrderItemInput(productId: 'product-1', quantity: 1.125)],
    );

    expect(request.toJson(), {
      'addressId': 'address-1',
      'items': [
        {'productId': 'product-1', 'quantity': 1.125},
      ],
    });
  });

  test('checkout preview parses backend authoritative quote', () {
    final preview = CheckoutPreview.fromJson({
      'addressId': 'address-1',
      'addressLabel': 'Home',
      'addressLine1': '1 Main Street',
      'addressLine2': null,
      'locality': 'Central',
      'city': 'Bengaluru',
      'state': 'Karnataka',
      'pinCode': '560001',
      'contactName': 'Customer',
      'contactMobile': '9999999999',
      'branchId': 'branch-1',
      'branchCode': 'MAIN',
      'branchName': 'Main Branch',
      'distanceKm': 4.25,
      'items': [
        {
          'productId': 'product-1',
          'productName': 'Milk',
          'sku': 'MILK-001',
          'unitOfMeasure': 'litre',
          'quantity': 1.125,
          'unitPrice': 80,
          'lineTotal': 90,
        },
      ],
      'subtotal': 90,
      'discountAmount': 0,
      'payableAmount': 90,
    });

    expect(preview.branchName, 'Main Branch');
    expect(preview.items.single.quantity, 1.125);
    expect(preview.payableAmount, 90);
  });

  test('order summary exposes cancellation only for confirmed orders', () {
    Map<String, dynamic> json(String status) => {
      'publicId': 'order-1',
      'orderNumber': 'DD-000001',
      'type': 'OneTime',
      'status': status,
      'createdAt': '2026-08-16T00:00:00.000',
      'addressLabel': 'Home',
      'city': 'Bengaluru',
      'branchName': 'Main Branch',
      'items': <Map<String, dynamic>>[],
      'subtotal': 90,
      'discountAmount': 0,
      'payableAmount': 90,
      'cancelledAt': null,
    };

    expect(OrderSummary.fromJson(json('Confirmed')).canCancel, isTrue);
    expect(OrderSummary.fromJson(json('Cancelled')).canCancel, isFalse);
    expect(OrderSummary.fromJson(json('Confirmed')).formattedTotal, '₹90.00');
  });

  test('order timestamps remain UTC instants and display in device local time', () {
    final order = OrderSummary.fromJson({
      'publicId': 'order-1',
      'orderNumber': 'DD-000001',
      'type': 'OneTime',
      'status': 'PendingPayment',
      'createdAt': '2026-08-20T02:41:00.000',
      'addressLabel': 'Home',
      'city': 'Bengaluru',
      'branchName': 'Main Branch',
      'items': <Map<String, dynamic>>[],
      'subtotal': 90,
      'discountAmount': 0,
      'payableAmount': 90,
      'cancelledAt': null,
    });

    expect(order.createdAt.isUtc, isFalse);
    expect(order.createdAt.toIso8601String(), '2026-08-20T02:41:00.000');
    expect(formatOrderDate(order.createdAt), '20-Aug-2026 02:41 AM');
  });
}
