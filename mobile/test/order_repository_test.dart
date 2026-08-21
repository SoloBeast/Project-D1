import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

Map<String, dynamic> orderJson() => {
  'publicId': 'order-1',
  'orderNumber': 'DD-000001',
  'type': 'OneTime',
  'status': 'Confirmed',
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

void main() {
  test('preview posts checkout request and parses server quote', () async {
    final client = MockClient((request) async {
      expect(request.method, 'POST');
      expect(
        request.url.toString(),
        'https://api.example.test/api/v1/orders/checkout-preview',
      );
      expect(request.headers['Authorization'], 'Bearer customer-token');
      expect(jsonDecode(request.body), {
        'addressId': 'address-1',
        'items': [
          {'productId': 'product-1', 'quantity': 1.125},
        ],
      });
      return http.Response(
        jsonEncode({
          'success': true,
          'data': {
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
          },
          'errors': [],
        }),
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = OrderRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    final preview = await repository.preview(
      'customer-token',
      const CheckoutRequest(
        addressId: 'address-1',
        items: [OrderItemInput(productId: 'product-1', quantity: 1.125)],
      ),
    );

    expect(preview.payableAmount, 90);
    expect(preview.items.single.quantity, 1.125);
  });

  test('create sends bearer token and customer idempotency key', () async {
    final client = MockClient((request) async {
      expect(request.method, 'POST');
      expect(request.url.toString(), 'https://api.example.test/api/v1/orders');
      expect(request.headers['Authorization'], 'Bearer customer-token');
      expect(request.headers['Idempotency-Key'], 'mobile-checkout-1');
      return http.Response(
        jsonEncode({'success': true, 'data': orderJson(), 'errors': []}),
        201,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = OrderRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    final order = await repository.create(
      'customer-token',
      const CheckoutRequest(addressId: 'address-1', items: []),
      'mobile-checkout-1',
    );

    expect(order.publicId, 'order-1');
  });

  test('history, detail, and cancellation use customer order routes', () async {
    final client = MockClient((request) async {
      expect(request.headers['Authorization'], 'Bearer customer-token');
      if (request.url.path == '/api/v1/orders') {
        expect(request.method, 'GET');
        return http.Response(
          jsonEncode({
            'success': true,
            'data': [orderJson()],
            'errors': [],
          }),
          200,
        );
      }
      if (request.url.path == '/api/v1/orders/order-1') {
        expect(request.method, 'GET');
        return http.Response(
          jsonEncode({'success': true, 'data': orderJson(), 'errors': []}),
          200,
        );
      }
      expect(request.method, 'POST');
      expect(request.url.path, '/api/v1/orders/order-1/cancel');
      return http.Response(
        jsonEncode({'success': true, 'data': orderJson(), 'errors': []}),
        200,
      );
    });
    final repository = OrderRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    expect(
      (await repository.getMine('customer-token')).single.publicId,
      'order-1',
    );
    expect(
      (await repository.get('customer-token', 'order-1')).publicId,
      'order-1',
    );
    expect(
      (await repository.cancel('customer-token', 'order-1')).publicId,
      'order-1',
    );
  });
}
