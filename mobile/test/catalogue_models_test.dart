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
    });
    expect(category.toJson(), {
      'code': 'MILK',
      'name': 'Milk',
      'description': 'Fresh dairy products',
    });
  });

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
