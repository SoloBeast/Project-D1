class ProductCategory {
  const ProductCategory({
    required this.publicId,
    required this.code,
    required this.name,
    required this.description,
    required this.isActive,
  });

  factory ProductCategory.fromJson(Map<String, dynamic> json) =>
      ProductCategory(
        publicId: json['publicId'] as String,
        code: json['code'] as String,
        name: json['name'] as String,
        description: json['description'] as String?,
        isActive: json['isActive'] as bool,
      );

  final String publicId;
  final String code;
  final String name;
  final String? description;
  final bool isActive;
}

class BranchAvailability {
  const BranchAvailability({
    required this.branchId,
    required this.branchCode,
    required this.branchName,
    required this.isAvailable,
    required this.maxDailyQuantity,
  });

  factory BranchAvailability.fromJson(Map<String, dynamic> json) =>
      BranchAvailability(
        branchId: json['branchId'] as String,
        branchCode: json['branchCode'] as String,
        branchName: json['branchName'] as String,
        isAvailable: json['isAvailable'] as bool,
        maxDailyQuantity: (json['maxDailyQuantity'] as num?)?.toDouble(),
      );

  final String branchId;
  final String branchCode;
  final String branchName;
  final bool isAvailable;
  final double? maxDailyQuantity;
}

class CatalogueProduct {
  const CatalogueProduct({
    required this.publicId,
    required this.sku,
    required this.name,
    required this.description,
    required this.category,
    required this.unitOfMeasure,
    required this.price,
    required this.isActive,
    required this.branchAvailability,
  });

  factory CatalogueProduct.fromJson(Map<String, dynamic> json) =>
      CatalogueProduct(
        publicId: json['publicId'] as String,
        sku: json['sku'] as String,
        name: json['name'] as String,
        description: json['description'] as String?,
        category: ProductCategory.fromJson(
          json['category'] as Map<String, dynamic>,
        ),
        unitOfMeasure: json['unitOfMeasure'] as String,
        price: (json['price'] as num).toDouble(),
        isActive: json['isActive'] as bool,
        branchAvailability: (json['branchAvailability'] as List<dynamic>)
            .map(
              (item) => BranchAvailability.fromJson(
                item as Map<String, dynamic>,
              ),
            )
            .toList(growable: false),
      );

  final String publicId;
  final String sku;
  final String name;
  final String? description;
  final ProductCategory category;
  final String unitOfMeasure;
  final double price;
  final bool isActive;
  final List<BranchAvailability> branchAvailability;

  String get unitLabel => unitOfMeasure == 'litre' ? 'litre' : unitOfMeasure;
  String get formattedPrice => '₹${price.toStringAsFixed(2)} / $unitLabel';
}

class CatalogueBranch {
  const CatalogueBranch({
    required this.publicId,
    required this.code,
    required this.name,
    required this.city,
    required this.state,
    required this.isActive,
  });

  factory CatalogueBranch.fromJson(Map<String, dynamic> json) =>
      CatalogueBranch(
        publicId: json['publicId'] as String,
        code: json['code'] as String,
        name: json['name'] as String,
        city: json['city'] as String,
        state: json['state'] as String,
        isActive: json['isActive'] as bool,
      );

  final String publicId;
  final String code;
  final String name;
  final String city;
  final String state;
  final bool isActive;
}

class ProductDraft {
  const ProductDraft({
    required this.sku,
    required this.name,
    required this.description,
    required this.categoryId,
    required this.unitOfMeasure,
    required this.price,
  });

  factory ProductDraft.fromProduct(CatalogueProduct product) => ProductDraft(
    sku: product.sku,
    name: product.name,
    description: product.description,
    categoryId: product.category.publicId,
    unitOfMeasure: product.unitOfMeasure,
    price: product.price,
  );

  final String sku;
  final String name;
  final String? description;
  final String categoryId;
  final String unitOfMeasure;
  final double price;

  Map<String, dynamic> toJson() => {
    'sku': sku.trim(),
    'name': name.trim(),
    'description': _optional(description),
    'categoryId': categoryId,
    'unitOfMeasure': unitOfMeasure,
    'price': price,
  };
}

class CategoryDraft {
  const CategoryDraft({
    required this.code,
    required this.name,
    required this.description,
  });

  factory CategoryDraft.fromCategory(ProductCategory category) => CategoryDraft(
    code: category.code,
    name: category.name,
    description: category.description,
  );

  final String code;
  final String name;
  final String? description;

  Map<String, dynamic> toJson() => {
    'code': code.trim(),
    'name': name.trim(),
    'description': _optional(description),
  };
}

class BranchAvailabilityDraft {
  const BranchAvailabilityDraft({
    required this.branchId,
    required this.isAvailable,
    required this.maxDailyQuantity,
  });

  final String branchId;
  final bool isAvailable;
  final double? maxDailyQuantity;

  Map<String, dynamic> toJson() => {
    'branchId': branchId,
    'isAvailable': isAvailable,
    'maxDailyQuantity': maxDailyQuantity,
  };
}

String formatQuantity(double value) {
  final fixed = value.toStringAsFixed(3);
  return fixed.replaceFirst(RegExp(r'\.?0+$'), '');
}

String? _optional(String? value) {
  final normalized = value?.trim();
  return normalized == null || normalized.isEmpty ? null : normalized;
}
