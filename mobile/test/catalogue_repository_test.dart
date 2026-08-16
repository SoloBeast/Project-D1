import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

Map<String, dynamic> productJson() => {
  'publicId': 'product-1',
  'sku': 'MILK-001',
  'name': 'Fresh Buffalo Milk',
  'description': null,
  'category': {
    'publicId': 'category-1',
    'code': 'MILK',
    'name': 'Milk',
    'description': null,
    'isActive': true,
  },
  'unitOfMeasure': 'litre',
  'price': 80,
  'isActive': true,
  'branchAvailability': <Map<String, dynamic>>[],
};

void main() {
  test('gets products with category filter and parses response data', () async {
    final client = MockClient((request) async {
      expect(request.method, 'GET');
      expect(
        request.url.toString(),
        'https://api.example.test/api/v1/products?categoryId=category-1',
      );
      expect(request.headers['Authorization'], isNull);
      return http.Response(
        jsonEncode({
          'success': true,
          'data': [productJson()],
          'errors': [],
        }),
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = CatalogueRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    final products = await repository.getProducts(categoryId: 'category-1');

    expect(products.single.sku, 'MILK-001');
    expect(products.single.price, 80.0);
  });

  test(
    'updates branch availability with bearer token and decimal body',
    () async {
      final client = MockClient((request) async {
        expect(request.method, 'PUT');
        expect(
          request.url.toString(),
          'https://api.example.test/api/v1/admin/products/product-1/branches',
        );
        expect(request.headers['Authorization'], 'Bearer admin-token');
        expect(request.headers['Content-Type'], 'application/json');
        expect(jsonDecode(request.body), {
          'branchId': 'branch-1',
          'isAvailable': true,
          'maxDailyQuantity': 75.125,
        });
        return http.Response(
          jsonEncode({'success': true, 'data': productJson(), 'errors': []}),
          200,
          headers: {'content-type': 'application/json'},
        );
      });
      final repository = CatalogueRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final product = await repository.setBranchAvailability(
        'product-1',
        const BranchAvailabilityDraft(
          branchId: 'branch-1',
          isAvailable: true,
          maxDailyQuantity: 75.125,
        ),
        'admin-token',
      );

      expect(product.publicId, 'product-1');
    },
  );

  test('creates product with normalized draft body', () async {
    final client = MockClient((request) async {
      expect(request.method, 'POST');
      expect(
        request.url.toString(),
        'https://api.example.test/api/v1/admin/products',
      );
      expect(request.headers['Authorization'], 'Bearer admin-token');
      expect(jsonDecode(request.body), {
        'sku': 'MILK-001',
        'name': 'Fresh Buffalo Milk',
        'description': null,
        'categoryId': 'category-1',
        'unitOfMeasure': 'litre',
        'price': 80.25,
      });
      return http.Response(
        jsonEncode({'success': true, 'data': productJson(), 'errors': []}),
        201,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = CatalogueRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    final product = await repository.createProduct(
      const ProductDraft(
        sku: ' MILK-001 ',
        name: ' Fresh Buffalo Milk ',
        description: ' ',
        categoryId: 'category-1',
        unitOfMeasure: 'litre',
        price: 80.25,
      ),
      'admin-token',
    );

    expect(product.name, 'Fresh Buffalo Milk');
  });
}
