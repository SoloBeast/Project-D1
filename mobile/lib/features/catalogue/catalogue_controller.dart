import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'catalogue_models.dart';
import 'catalogue_repository.dart';

final catalogueControllerProvider =
    NotifierProvider<CatalogueController, CatalogueState>(
      CatalogueController.new,
    );

final adminCatalogueControllerProvider =
    NotifierProvider<AdminCatalogueController, AdminCatalogueState>(
      AdminCatalogueController.new,
    );

class CatalogueState {
  const CatalogueState({
    this.products = const [],
    this.categories = const [],
    this.selectedCategoryId,
    this.isLoading = false,
    this.errorMessage,
  });

  final List<CatalogueProduct> products;
  final List<ProductCategory> categories;
  final String? selectedCategoryId;
  final bool isLoading;
  final String? errorMessage;

  CatalogueState copyWith({
    List<CatalogueProduct>? products,
    List<ProductCategory>? categories,
    String? selectedCategoryId,
    bool clearCategory = false,
    bool? isLoading,
    String? errorMessage,
    bool clearError = false,
  }) => CatalogueState(
    products: products ?? this.products,
    categories: categories ?? this.categories,
    selectedCategoryId: clearCategory
        ? null
        : selectedCategoryId ?? this.selectedCategoryId,
    isLoading: isLoading ?? this.isLoading,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class CatalogueController extends Notifier<CatalogueState> {
  CatalogueRepository get _repository => ref.read(catalogueRepositoryProvider);

  @override
  CatalogueState build() => const CatalogueState();

  Future<void> load() async {
    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final results = await Future.wait([
        _repository.getCategories(),
        _repository.getProducts(categoryId: state.selectedCategoryId),
      ]);
      state = state.copyWith(
        categories: results[0] as List<ProductCategory>,
        products: results[1] as List<CatalogueProduct>,
        isLoading: false,
        clearError: true,
      );
    } on Object catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: _message(error));
    }
  }

  Future<void> selectCategory(String? categoryId) async {
    state = state.copyWith(
      selectedCategoryId: categoryId,
      clearCategory: categoryId == null,
      isLoading: true,
      clearError: true,
    );
    try {
      state = state.copyWith(
        products: await _repository.getProducts(categoryId: categoryId),
        isLoading: false,
      );
    } on Object catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: _message(error));
    }
  }
}

class AdminCatalogueState {
  const AdminCatalogueState({
    this.products = const [],
    this.categories = const [],
    this.branches = const [],
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
  });

  final List<CatalogueProduct> products;
  final List<ProductCategory> categories;
  final List<CatalogueBranch> branches;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;

  AdminCatalogueState copyWith({
    List<CatalogueProduct>? products,
    List<ProductCategory>? categories,
    List<CatalogueBranch>? branches,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    bool clearError = false,
  }) => AdminCatalogueState(
    products: products ?? this.products,
    categories: categories ?? this.categories,
    branches: branches ?? this.branches,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class AdminCatalogueController extends Notifier<AdminCatalogueState> {
  CatalogueRepository get _repository => ref.read(catalogueRepositoryProvider);
  String get _token =>
      ref.read(sessionControllerProvider).session!.accessToken;

  @override
  AdminCatalogueState build() => const AdminCatalogueState();

  Future<void> load() async {
    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final results = await Future.wait([
        _repository.getAdminProducts(_token),
        _repository.getAdminCategories(_token),
        _repository.getBranches(_token),
      ]);
      state = state.copyWith(
        products: results[0] as List<CatalogueProduct>,
        categories: results[1] as List<ProductCategory>,
        branches: results[2] as List<CatalogueBranch>,
        isLoading: false,
      );
    } on Object catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: _message(error));
    }
  }

  Future<bool> saveProduct(String? productId, ProductDraft draft) => _save(
    () => productId == null
        ? _repository.createProduct(draft, _token)
        : _repository.updateProduct(productId, draft, _token),
  );

  Future<bool> setProductActive(String productId, bool isActive) => _save(
    () => _repository.setProductActive(productId, isActive, _token),
  );

  Future<bool> saveCategory(String? categoryId, CategoryDraft draft) => _save(
    () => categoryId == null
        ? _repository.createCategory(draft, _token)
        : _repository.updateCategory(categoryId, draft, _token),
  );

  Future<bool> setCategoryActive(String categoryId, bool isActive) => _save(
    () => _repository.setCategoryActive(categoryId, isActive, _token),
  );

  Future<bool> setBranchAvailability(
    String productId,
    BranchAvailabilityDraft draft,
  ) => _save(
    () => _repository.setBranchAvailability(productId, draft, _token),
  );

  Future<bool> _save(Future<Object> Function() operation) async {
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      await operation();
      await load();
      state = state.copyWith(isSaving: false);
      return true;
    } on Object catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: _message(error));
      return false;
    }
  }
}

String _message(Object error) => error is ApiException
    ? error.message
    : 'Unable to reach DoodhDirect. Check your connection and try again.';
