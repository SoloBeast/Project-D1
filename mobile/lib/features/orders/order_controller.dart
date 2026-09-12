import 'dart:async';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'guest_cart_storage.dart';
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
    this.checkoutAddress,
    this.preview,
    this.orders = const <OrderSummary>[],
    this.selectedOrder,
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
  });

  final List<OrderCartItem> cart;
  final CheckoutAddressSelection? checkoutAddress;
  final CheckoutPreview? preview;
  final List<OrderSummary> orders;
  final OrderSummary? selectedOrder;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;

  OrderState copyWith({
    List<OrderCartItem>? cart,
    CheckoutAddressSelection? checkoutAddress,
    bool clearCheckoutAddress = false,
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
    checkoutAddress: clearCheckoutAddress
        ? null
        : checkoutAddress ?? this.checkoutAddress,
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

    // Track the auth state so the device-local cart is managed correctly:
    //  * guest         → adopt the unowned guest cart (survives restarts) and
    //                    never the items of a previously signed-in account;
    //  * authenticated → restore the cart scoped to this user (survives
    //                    guest → login and app/browser restarts) and re-scope
    //                    the snapshot created before sign-in;
    //  * leaving auth  → the in-memory cart belongs to the previous account
    //                    and is dropped so it cannot leak into guest browsing
    //                    or another user. The persisted snapshot survives
    //                    until an explicit sign-out or a successful payment.
    ref.listen<SessionState>(
      sessionControllerProvider,
      (previous, next) {
        if (next.isGuest) {
          unawaited(_adoptGuestCart());
        } else if (next.isAuthenticated) {
          unawaited(_restoreGuestCart(next.publicUserId));
          if (previous != null && previous.isGuest) {
            // The cart was just carried from guest to signed-in: re-scope the
            // persisted snapshot to this user so it survives a restart.
            _persistGuestCart();
          }
        } else if (previous != null && previous.isAuthenticated) {
          _dropInMemoryCart();
        }
      },
      fireImmediately: true,
    );

    return const OrderState();
  }

  GuestCartStorage get _guestCartStorage =>
      ref.read(guestCartStorageProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  /// Restores the persisted cart for [userId] (null = guest) when the stored
  /// snapshot belongs to that same identity. A snapshot owned by another
  /// identity is removed from the device so it can never leak. A failed
  /// restore must never block the app — the current cart is kept.
  Future<void> _restoreGuestCart(String? userId) async {
    try {
      final snapshot = await _guestCartStorage.read();
      // The read is async and yields, so a mutation made while it was in
      // flight must never be clobbered: only adopt the snapshot when the
      // in-memory cart is still empty.
      if (state.cart.isNotEmpty) return;
      if (snapshot.userId == userId) {
        if (snapshot.items.isNotEmpty) {
          state = state.copyWith(cart: snapshot.items, clearPreview: true);
        }
      } else if (snapshot.userId == null &&
          userId != null &&
          snapshot.items.isNotEmpty) {
        // A guest snapshot left on this device is adopted when the user signs
        // in (deferred login): `_persistGuestCart` then re-scopes the payload
        // to this account so the exact items survive a restart after login.
        state = state.copyWith(cart: snapshot.items, clearPreview: true);
      } else if (snapshot.userId != null && snapshot.userId != userId) {
        // Identity change on this device: another user's cart must not leak
        // into the current session (or guest mode).
        await _guestCartStorage.clear();
      }
    } on Object {
      // A failed restore must never block the app; keep the current cart.
    }
  }

  /// Adopts the unowned guest cart when entering guest browsing. A snapshot
  /// owned by a signed-in account is never surfaced to a guest and is removed
  /// so the guest cannot pick up the previous customer's items.
  Future<void> _adoptGuestCart() async {
    try {
      final snapshot = await _guestCartStorage.read();
      if (snapshot.userId != null) {
        _dropInMemoryCart();
        await _guestCartStorage.clear();
        return;
      }
      // The read is async and yields, so a mutation made while it was in
      // flight must never be clobbered: only adopt the snapshot when the
      // in-memory cart is still empty.
      if (state.cart.isEmpty) {
        state = state.copyWith(cart: snapshot.items, clearPreview: true);
      }
    } on Object {
      // A failed restore must never block the app; keep the current cart.
    }
  }

  /// Persists the current cart under the active identity's scope — null while
  /// guest, the customer's public id once signed in — so the exact items built
  /// before sign-in survive a restart after login and never leak across users.
  void _persistGuestCart() {
    final session = ref.read(sessionControllerProvider);
    if (!session.isGuest && !session.isAuthenticated) return;
    final userId = session.isGuest ? null : session.publicUserId;
    unawaited(_guestCartStorage.write(userId: userId, items: state.cart));
  }

  /// Drops the in-memory cart so items belonging to a previous account can
  /// never surface in guest browsing or under another user.
  void _dropInMemoryCart() {
    if (state.cart.isEmpty) return;
    state = state.copyWith(cart: const <OrderCartItem>[], clearPreview: true);
  }

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
    _persistGuestCart();
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
    _persistGuestCart();
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
    _persistGuestCart();
  }

  void clearCartAfterSuccessfulPayment() {
    if (state.cart.isEmpty) return;
    state = state.copyWith(cart: const <OrderCartItem>[], clearPreview: true);
    unawaited(_guestCartStorage.clear());
  }

  void clearPreview() {
    state = state.copyWith(clearPreview: true);
  }

  void selectSavedAddress(String addressId) {
    final normalized = addressId.trim();
    if (normalized.isEmpty) return;
    state = state.copyWith(
      checkoutAddress: CheckoutAddressSelection.saved(normalized),
      clearPreview: true,
      clearError: true,
    );
  }

  void selectManualAddress(CheckoutAddressDraft draft) {
    state = state.copyWith(
      checkoutAddress: CheckoutAddressSelection.manual(draft),
      clearPreview: true,
      clearError: true,
    );
  }

  void clearCheckoutAddress() {
    state = state.copyWith(
      clearCheckoutAddress: true,
      clearPreview: true,
      clearError: true,
    );
  }

  CheckoutAddressSelection? _resolveSelection(Object address) {
    if (address is CheckoutAddressSelection && address.isValid) return address;
    if (address is String && address.trim().isNotEmpty) {
      return CheckoutAddressSelection.saved(address.trim());
    }
    return null;
  }

  CheckoutRequest? requestFor(Object address) {
    final selection = _resolveSelection(address) ?? state.checkoutAddress;
    if (selection == null || !selection.isValid) return null;
    return CheckoutRequest(
      addressId: selection.addressId,
      manualAddress: selection.manualAddress,
      items: state.cart
          .map(
            (item) => OrderItemInput(
              productId: item.product.publicId,
              quantity: item.quantity,
            ),
          )
          .toList(growable: false),
    );
  }

  Future<bool> previewFor(Object address) async {
    final token = _token;
    final request = requestFor(address);
    if (token == null || request == null || state.cart.isEmpty) return false;
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final preview = await _repository.preview(token, request);
      state = state.copyWith(preview: preview, isSaving: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
    }
    return false;
  }

  Future<OrderSummary?> create(Object address) async {
    final token = _token;
    final request = requestFor(address);
    if (token == null || request == null || state.cart.isEmpty) return null;
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final order = await _repository.create(
        token,
        request,
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

  Future<void> loadStaffOrder(String orderId) async {
    final token = _token;
    if (token == null) return;
    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final order = await _repository.getForStaff(token, orderId);
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
