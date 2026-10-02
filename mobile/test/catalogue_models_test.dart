import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('product parses decimal price and branch quantities', () {
    final product = CatalogueProduct.fromJson({
      'publicId': 'product-1',
      'sku': 'FRESH-BUFFALO-MILK',
      'name': 'Fresh Buffalo Milk',
      'description': 'Fresh milk sold loose by the litre.',
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
      'branchAvailability': [
        {
          'branchId': 'branch-1',
          'branchCode': 'MAIN',
          'branchName': 'Main Branch',
          'isAvailable': true,
          'maxDailyQuantity': 125.375,
        },
      ],
    });

    expect(product.price, 80.0);
    expect(product.formattedPrice, '₹80.00 / litre');
    expect(product.category.code, 'MILK');
    expect(product.branchAvailability.single.maxDailyQuantity, 125.375);
  });

  test('category and branch parse administration fields', () {
    final category = ProductCategory.fromJson({
      'publicId': 'category-1',
      'code': 'MILK',
      'name': 'Milk',
      'description': null,
      'isActive': false,
    });
    final branch = CatalogueBranch.fromJson({
      'publicId': 'branch-1',
      'code': 'MAIN',
      'name': 'Main Branch',
      'city': 'Bengaluru',
      'state': 'Karnataka',
      'isActive': true,
    });

    expect(category.description, isNull);
    expect(category.isActive, isFalse);
    expect(branch.city, 'Bengaluru');
    expect(branch.isActive, isTrue);
  });

  test('product and category drafts trim text and null empty optionals', () {
    const product = ProductDraft(
      sku: ' MILK-001 ',
      name: ' Fresh Buffalo Milk ',
      description: '   ',
      categoryId: 'category-1',
      unitOfMeasure: 'litre',
      price: 80.25,
      branchIds: ['branch-1', 'branch-2'],
    );
    const category = CategoryDraft(
      code: ' MILK ',
      name: ' Milk ',
      description: ' Fresh dairy products ',
    );

    expect(product.toJson(), {
      'sku': 'MILK-001',
      'name': 'Fresh Buffalo Milk',
      'description': null,
      'categoryId': 'category-1',
      'unitOfMeasure': 'litre',
      'price': 80.25,
      'branchIds': ['branch-1', 'branch-2'],
      'applicableChargeIds': <String>[],
    });
    expect(category.toJson(), {
      'code': 'MILK',
      'name': 'Milk',
      'description': 'Fresh dairy products',
    });
  });

  test('product draft from product carries every assigned branch id', () {
    final product = CatalogueProduct.fromJson({
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
      'branchAvailability': [
        {
          'branchId': 'branch-1',
          'branchCode': 'MAIN',
          'branchName': 'Main Branch',
          'isAvailable': true,
          'maxDailyQuantity': null,
        },
        {
          'branchId': 'branch-2',
          'branchCode': 'NIT3',
          'branchName': 'NIT 3',
          'isAvailable': true,
          'maxDailyQuantity': 50,
        },
      ],
    });

    final draft = ProductDraft.fromProduct(product);

    expect(draft.branchIds, ['branch-1', 'branch-2']);
    expect(draft.toJson()['branchIds'], ['branch-1', 'branch-2']);
  });

  test(
    'applicable charges parse from admin payload and absent key defaults empty',
    () {
      const mappingJson = {
        'chargeId': 'chg-1',
        'chargeCode': 'GST-5',
        'chargeType': 'GST',
        'description': 'GST five percent',
        'percentage': 5,
        'isActive': false,
      };
      final adminPayload = <String, dynamic>{
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
        'branchAvailability': <Map<String, dynamic>>[
          {
            'branchId': 'branch-1',
            'branchCode': 'MAIN',
            'branchName': 'Main Branch',
            'isAvailable': true,
            'maxDailyQuantity': null,
          },
        ],
        'applicableCharges': [mappingJson],
      };

      final admin = CatalogueProduct.fromJson(adminPayload);
      expect(admin.applicableCharges, hasLength(1));
      expect(admin.applicableCharges.single.chargeId, 'chg-1');
      expect(admin.applicableCharges.single.chargeCode, 'GST-5');
      expect(admin.applicableCharges.single.isActive, isFalse);

      // Customer payloads omit the key entirely: no crash, empty list, and
      // fromProduct round-trips nothing.
      final customerPayload = Map<String, dynamic>.from(adminPayload)
        ..remove('applicableCharges');
      final customer = CatalogueProduct.fromJson(customerPayload);
      expect(customer.applicableCharges, isEmpty);
      expect(ProductDraft.fromProduct(customer).chargeIds, isEmpty);
      expect(ProductDraft.fromProduct(admin).chargeIds, ['chg-1']);
    },
  );

  test(
    'availability draft preserves loose decimal quantity and null capacity',
    () {
      const limited = BranchAvailabilityDraft(
        branchId: 'branch-1',
        isAvailable: true,
        maxDailyQuantity: 75.125,
      );
      const unlimited = BranchAvailabilityDraft(
        branchId: 'branch-1',
        isAvailable: true,
        maxDailyQuantity: null,
      );

      expect(limited.toJson()['maxDailyQuantity'], 75.125);
      expect(unlimited.toJson()['maxDailyQuantity'], isNull);
    },
  );

  test('quantity formatting keeps up to three decimal places', () {
    expect(formatQuantity(1), '1');
    expect(formatQuantity(1.5), '1.5');
    expect(formatQuantity(1.125), '1.125');
    expect(formatQuantity(1.2344), '1.234');
  });
}
