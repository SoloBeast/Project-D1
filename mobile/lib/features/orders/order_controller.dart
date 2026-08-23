import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'order_models.dart';
import 'order_repository.dart';

final orderRepositoryProvider = Provider<OrderRepository>(
  (ref) => OrderRepository(api: authenticatedApiClient(ref)),
);

final orderControllerProvider = NotifierProvider<OrderController, OrderState>(
  OrderController.new,
);

class OrderState {
  const OrderState({
    this.cart = const <OrderCartItem>[],
    this.preview,
    this.orders = const <OrderSummary>[],
    this.selectedOrder,
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
  });

  final List<OrderCartItem> cart;
  final CheckoutPreview? preview;
  final List<OrderSummary> orders;
  final OrderSummary? selectedOrder;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;

  OrderState copyWith({
    List<OrderCartItem>? cart,
    CheckoutPreview? preview,
    bool clearPreview = false,
    List<OrderSummary>? orders,
    OrderSummary? selectedOrder,
    bool clearSelectedOrder = false,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    bool clearError = false,
  }) => OrderState(
    cart: cart ?? this.cart,
    preview: clearPreview ? null : preview ?? this.preview,
    orders: orders ?? this.orders,
    selectedOrder: clearSelectedOrder
        ? null
        : selectedOrder ?? this.selectedOrder,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class OrderController extends Notifier<OrderState> {
  OrderRepository get _repository => ref.read(orderRepositoryProvider);

  late String _checkoutIdempotencyKey;

  @override
  OrderState build() {
    _checkoutIdempotencyKey = _newCheckoutIdempotencyKey();
    return const OrderState();
  }

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  void setCartItem(CatalogueProduct product, double quantity) {
    final items = [...state.cart];
    final index = items.indexWhere(
      (item) => item.product.publicId == product.publicId,
    );
    final value = OrderCartItem(product: product, quantity: quantity);
    if (index == -1) {
      items.add(value);
    } else {
      items[index] = value;
    }
    state = state.copyWith(
      cart: items,
      clearPreview: true,
      clearError: true,
    );
  }

  void updateCartQuantity(String productId, double quantity) {
    if (quantity <= 0) {
      removeCartItem(productId);
      return;
    }
    final items = state.cart
        .map(
          (item) => item.product.publicId == productId
              ? item.copyWith(quantity: quantity)
              : item,
        )
        .toList(growable: false);
    state = state.copyWith(cart: items, clearPreview: true, clearError: true);
  }

  void incrementCartItem(String productId) {
    final item = state.cart.firstWhere((item) => item.product.publicId == productId);
    updateCartQuantity(productId, item.quantity + 1);
  }

  void decrementCartItem(String productId) {
    final item = state.cart.firstWhere((item) => item.product.publicId == productId);
    updateCartQuantity(productId, item.quantity - 1);
  }

  void removeCartItem(String productId) {
    state = state.copyWith(
      cart: state.cart
          .where((item) => item.product.publicId != productId)
          .toList(growable: false),
      clearPreview: true,
    );
  }

  void clearCartAfterSuccessfulPayment() {
    if (state.cart.isEmpty) return;
    state = state.copyWith(cart: const <OrderCartItem>[], clearPreview: true);
  }

  void clearPreview() {
    state = state.copyWith(clearPreview: true);
  }

  CheckoutRequest requestFor(String addressId) => CheckoutRequest(
    addressId: addressId,
    items: state.cart
        .map(
          (item) => OrderItemInput(
            productId: item.product.publicId,
            quantity: item.quantity,
          ),
        )
        .toList(growable: false),
  );

  Future<bool> previewFor(String addressId) async {
    final token = _token;
    if (token == null || state.cart.isEmpty) return false;
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final preview = await _repository.preview(token, requestFor(addressId));
      state = state.copyWith(preview: preview, isSaving: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
    }
    return false;
  }

  Future<OrderSummary?> create(String addressId) async {
    final token = _token;
    if (token == null || state.cart.isEmpty) return null;
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final order = await _repository.create(
        token,
        requestFor(addressId),
        _checkoutIdempotencyKey,
      );
      state = state.copyWith(
        isSaving: false,
        selectedOrder: order,
      );
      _checkoutIdempotencyKey = _newCheckoutIdempotencyKey();
      return order;
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
    }
    return null;
  }

  Future<void> loadOrders() async {
    final token = _token;
    if (token == null) return;
    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final orders = await _repository.getMine(token);
      state = state.copyWith(orders: orders, isLoading: false);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
  }

  Future<void> loadOrder(String orderId) async {
    final token = _token;
    if (token == null) return;
    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final order = await _repository.get(token, orderId);
      state = state.copyWith(selectedOrder: order, isLoading: false);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
  }

  Future<bool> cancel(String orderId) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final order = await _repository.cancel(token, orderId);
      state = state.copyWith(
        selectedOrder: order,
        orders: state.orders
            .map((item) => item.publicId == order.publicId ? order : item)
            .toList(growable: false),
        isSaving: false,
      );
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
    }
    return false;
  }
  String _newCheckoutIdempotencyKey() =>
      'mobile-${DateTime.now().microsecondsSinceEpoch}';
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
