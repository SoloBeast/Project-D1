import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'dart:typed_data';

import 'catalogue_models.dart';

class CatalogueRepository {
  CatalogueRepository({required this.api});

  final ApiClient api;

  Future<List<ProductCategory>> getCategories() async {
    final response = await api.get('/api/v1/product-categories');
    return _list(response)
        .map(ProductCategory.fromJson)
        .toList(growable: false);
  }

  Future<List<CatalogueProduct>> getProducts({String? categoryId}) async {
    final query = categoryId == null ? '' : '?categoryId=$categoryId';
    final response = await api.get('/api/v1/products$query');
    return _list(response)
        .map(CatalogueProduct.fromJson)
        .toList(growable: false);
  }

  Future<CatalogueProduct> getProduct(String productId) async {
    final response = await api.get('/api/v1/products/$productId');
    return CatalogueProduct.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<List<CatalogueProduct>> getAdminProducts(String accessToken) async {
    final response = await api.get(
      '/api/v1/admin/products',
      accessToken: accessToken,
    );
    return _list(response)
        .map(CatalogueProduct.fromJson)
        .toList(growable: false);
  }

  Future<CatalogueProduct> getAdminProduct(
    String productId,
    String accessToken,
  ) async {
    final response = await api.get(
      '/api/v1/admin/products/$productId',
      accessToken: accessToken,
    );
    return CatalogueProduct.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<CatalogueProduct> createProduct(
    ProductDraft draft,
    String accessToken,
  ) async {
    final response = await api.post(
      '/api/v1/admin/products',
      body: draft.toJson(),
      accessToken: accessToken,
    );
    return CatalogueProduct.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<CatalogueProduct> updateProduct(
    String productId,
    ProductDraft draft,
    String accessToken,
  ) async {
    final response = await api.patch(
      '/api/v1/admin/products/$productId',
      body: draft.toJson(),
      accessToken: accessToken,
    );
    return CatalogueProduct.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<CatalogueProduct> setProductActive(
    String productId,
    bool isActive,
    String accessToken,
  ) async {
    final response = await api.post(
      '/api/v1/admin/products/$productId/${isActive ? 'activate' : 'deactivate'}',
      accessToken: accessToken,
    );
    return CatalogueProduct.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<CatalogueProduct> setBranchAvailability(
    String productId,
    BranchAvailabilityDraft draft,
    String accessToken,
  ) async {
    final response = await api.put(
      '/api/v1/admin/products/$productId/branches',
      body: draft.toJson(),
      accessToken: accessToken,
    );
    return CatalogueProduct.fromJson(response['data'] as Map<String, dynamic>);
  }

  /// Adds the product image, or replaces the current one atomically.
  ///
  /// The image endpoints answer with the backend's `ProductImageResult`
  /// metadata (productId/imageId/fileName/contentType/fileSize/uploadedAtUtc),
  /// NOT a full catalogue product. Parsing that payload as `CatalogueProduct`
  /// throws a Dart `TypeError` even though the upload succeeded — which the
  /// controller then reported as a connectivity outage. Parse the real shape
  /// instead; the refreshed product (with its `imageUrl`) arrives through
  /// [getAdminProducts] during the controller's post-save `load()`.
  Future<ProductImageResult> upsertProductImage(
    String productId,
    String accessToken, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async => ProductImageResult.fromJson(
    (await api.putMultipart(
          '/api/v1/admin/products/$productId/image',
          fieldName: 'image',
          bytes: bytes,
          fileName: fileName,
          contentType: contentType,
          accessToken: accessToken,
        ))['data']
        as Map<String, dynamic>,
  );

  /// Removes the current product image; the product returns to the branded
  /// no-image presentation. Response is the same image metadata shape as
  /// [upsertProductImage] and is parsed, never as a `CatalogueProduct`.
  Future<ProductImageResult> removeProductImage(
    String productId,
    String accessToken,
  ) async => ProductImageResult.fromJson(
    (await api.delete(
          '/api/v1/admin/products/$productId/image',
          accessToken: accessToken,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<List<ProductCategory>> getAdminCategories(String accessToken) async {
    final response = await api.get(
      '/api/v1/admin/product-categories',
      accessToken: accessToken,
    );
    return _list(response)
        .map(ProductCategory.fromJson)
        .toList(growable: false);
  }

  Future<ProductCategory> createCategory(
    CategoryDraft draft,
    String accessToken,
  ) async {
    final response = await api.post(
      '/api/v1/admin/product-categories',
      body: draft.toJson(),
      accessToken: accessToken,
    );
    return ProductCategory.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<ProductCategory> updateCategory(
    String categoryId,
    CategoryDraft draft,
    String accessToken,
  ) async {
    final response = await api.patch(
      '/api/v1/admin/product-categories/$categoryId',
      body: draft.toJson(),
      accessToken: accessToken,
    );
    return ProductCategory.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<ProductCategory> setCategoryActive(
    String categoryId,
    bool isActive,
    String accessToken,
  ) async {
    final response = await api.post(
      '/api/v1/admin/product-categories/$categoryId/${isActive ? 'activate' : 'deactivate'}',
      accessToken: accessToken,
    );
    return ProductCategory.fromJson(response['data'] as Map<String, dynamic>);
  }

  /// Branches are served by the shared Branch Management endpoint
  /// (`GET /api/v1/admin/branches`). The availability picker only offers
  /// active branches, matching the previous catalogue behaviour.
  Future<List<CatalogueBranch>> getBranches(String accessToken) async {
    final response = await api.get(
      '/api/v1/admin/branches',
      accessToken: accessToken,
    );
    return _list(response)
        .map(CatalogueBranch.fromJson)
        .where((branch) => branch.isActive)
        .toList(growable: false);
  }

  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>).cast<Map<String, dynamic>>();
}

final catalogueRepositoryProvider = Provider<CatalogueRepository>(
  (ref) => CatalogueRepository(api: authenticatedApiClient(ref)),
);
