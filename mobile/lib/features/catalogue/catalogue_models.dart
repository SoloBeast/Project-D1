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

  Map<String, dynamic> toJson() => {
    'publicId': publicId,
    'code': code,
    'name': name,
    'description': description,
    'isActive': isActive,
  };

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

  Map<String, dynamic> toJson() => {
    'branchId': branchId,
    'branchCode': branchCode,
    'branchName': branchName,
    'isAvailable': isAvailable,
    'maxDailyQuantity': maxDailyQuantity,
  };

  final String branchId;
  final String branchCode;
  final String branchName;
  final bool isAvailable;
  final double? maxDailyQuantity;
}

/// A Tax & Charges assignment on a product (admin payloads only — customer
/// catalogue payloads never carry this). Mirrors the backend
/// `ProductApplicableChargeResult`.
class ProductApplicableCharge {
  const ProductApplicableCharge({
    required this.chargeId,
    required this.chargeCode,
    required this.chargeType,
    required this.description,
    required this.percentage,
    required this.isActive,
  });

  factory ProductApplicableCharge.fromJson(Map<String, dynamic> json) =>
      ProductApplicableCharge(
        chargeId: json['chargeId'] as String,
        chargeCode: json['chargeCode'] as String,
        chargeType: json['chargeType'] as String,
        description: json['description'] as String?,
        percentage: (json['percentage'] as num).toDouble(),
        isActive: json['isActive'] as bool,
      );

  /// Public id of the assigned charge — the value sent back in
  /// `applicableChargeIds` when saving.
  final String chargeId;
  final String chargeCode;
  final String chargeType;
  final String? description;
  final double percentage;

  /// A mapped-but-inactive charge stays assigned (configuration survives
  /// deactivation) but must not apply at checkout — the dialog shows it as a
  /// locked reference chip.
  final bool isActive;
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
    this.imageUrl,
    this.applicableCharges = const [],
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
        imageUrl: json['imageUrl'] as String?,
        branchAvailability: (json['branchAvailability'] as List<dynamic>)
            .map(
              (item) =>
                  BranchAvailability.fromJson(item as Map<String, dynamic>),
            )
            .toList(growable: false),
        // Admin payloads carry the tax assignments; customer payloads omit
        // the key entirely, so fall back to an empty list.
        applicableCharges:
            (json['applicableCharges'] as List<dynamic>?)
                ?.map(
                  (item) => ProductApplicableCharge.fromJson(
                    item as Map<String, dynamic>,
                  ),
                )
                .toList(growable: false) ??
            const [],
      );

  Map<String, dynamic> toJson() => {
    'publicId': publicId,
    'sku': sku,
    'name': name,
    'description': description,
    'category': category.toJson(),
    'unitOfMeasure': unitOfMeasure,
    'price': price,
    'isActive': isActive,
    'imageUrl': imageUrl,
    'branchAvailability': branchAvailability
        .map((item) => item.toJson())
        .toList(growable: false),
  };

  final String publicId;
  final String sku;
  final String name;
  final String? description;
  final ProductCategory category;
  final String unitOfMeasure;
  final double price;
  final bool isActive;
  final List<BranchAvailability> branchAvailability;

  /// Optional product imagery. Additive and nullable: the API does not send it
  /// today, so callers MUST render a branded fallback when this is `null` or
  /// blank. Never invent a value for it.
  final String? imageUrl;

  /// Tax & Charges assigned to this product. Admin payloads only — always
  /// empty on customer-facing catalogue data.
  final List<ProductApplicableCharge> applicableCharges;

  String get unitLabel => unitOfMeasure == 'litre' ? 'litre' : unitOfMeasure;
  String get formattedPrice => '₹${price.toStringAsFixed(2)} / $unitLabel';

  /// A trimmed, non-empty image URL, or `null` when no usable image exists.
  String? get usableImageUrl {
    final trimmed = imageUrl?.trim();
    return trimmed == null || trimmed.isEmpty ? null : trimmed;
  }

  /// Products are shown at the branch level; a product is "available" when at
  /// least one branch that can fulfil it reports availability. This is a
  /// presentation-only projection of the authoritative `branchAvailability`
  /// payload — it does not compute stock or change any ordering rule.
  bool get isAvailable =>
      branchAvailability.any((branch) => branch.isAvailable);

  /// Distinct branch names offering the product, in payload order.
  List<String> get availableBranchNames => branchAvailability
      .where((branch) => branch.isAvailable)
      .map((branch) => branch.branchName)
      .toList(growable: false);
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
    this.branchIds = const [],
    this.chargeIds = const [],
  });

  factory ProductDraft.fromProduct(CatalogueProduct product) => ProductDraft(
    sku: product.sku,
    name: product.name,
    description: product.description,
    categoryId: product.category.publicId,
    unitOfMeasure: product.unitOfMeasure,
    price: product.price,
    branchIds: product.branchAvailability
        .map((branch) => branch.branchId)
        .toList(growable: false),
    chargeIds: product.applicableCharges
        .map((charge) => charge.chargeId)
        .toList(growable: false),
  );

  final String sku;
  final String name;
  final String? description;
  final String categoryId;
  final String unitOfMeasure;
  final double price;

  /// Public ids of every branch this product is assigned to. Sent as
  /// `branchIds` on create/update so the server can apply multi-branch
  /// replace semantics via the ProductBranch association.
  final List<String> branchIds;

  /// Public ids of every Tax & Charge this product carries. Sent as
  /// `applicableChargeIds` on create/update so the server can apply replace
  /// semantics via the ProductCharge association — the complete selection
  /// (including kept inactive assignments) must always be sent.
  final List<String> chargeIds;

  Map<String, dynamic> toJson() => {
    'sku': sku.trim(),
    'name': name.trim(),
    'description': _optional(description),
    'categoryId': categoryId,
    'unitOfMeasure': unitOfMeasure,
    'price': price,
    'branchIds': branchIds,
    'applicableChargeIds': chargeIds,
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

/// Metadata returned by the admin product-image PUT/DELETE endpoints
/// (`/api/v1/admin/products/{id}/image`). Matches the backend
/// `ProductImageResult` record — deliberately NOT a `CatalogueProduct`: the
/// image endpoints respond with image metadata only, and the authoritative
/// product/image URL state arrives through the normal product GET payloads.
class ProductImageResult {
  const ProductImageResult({
    required this.productId,
    required this.imageId,
    required this.fileName,
    required this.contentType,
    required this.fileSize,
    required this.uploadedAtUtc,
  });

  factory ProductImageResult.fromJson(Map<String, dynamic> json) =>
      ProductImageResult(
        productId: json['productId'] as String,
        imageId: json['imageId'] as String,
        fileName: json['fileName'] as String,
        contentType: json['contentType'] as String,
        fileSize: (json['fileSize'] as num).toInt(),
        uploadedAtUtc: DateTime.parse(json['uploadedAtUtc'] as String),
      );

  final String productId;
  final String imageId;
  final String fileName;
  final String contentType;
  final int fileSize;
  final DateTime uploadedAtUtc;
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
