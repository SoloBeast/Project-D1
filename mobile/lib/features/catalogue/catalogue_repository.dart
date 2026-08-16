import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

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

  Future<List<CatalogueBranch>> getBranches(String accessToken) async {
    final response = await api.get(
      '/api/v1/admin/branches',
      accessToken: accessToken,
    );
    return _list(response)
        .map(CatalogueBranch.fromJson)
        .toList(growable: false);
  }

  List<Map<String, dynamic>> _list(Map<String, dynamic> response) =>
      (response['data'] as List<dynamic>).cast<Map<String, dynamic>>();
}

final catalogueRepositoryProvider = Provider<CatalogueRepository>(
  (ref) => CatalogueRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);
