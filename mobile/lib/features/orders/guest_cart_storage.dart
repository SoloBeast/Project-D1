import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'order_models.dart';

/// A snapshot of the device-local cart: the optional owning user (null while a
/// pure guest, the customer's [AuthUser.publicUserId] once the guest signs in)
/// plus the cart lines. Scoping the payload to the owner prevents a cart built
/// by one identity from leaking into another identity or into guest mode.
class GuestCartSnapshot {
  const GuestCartSnapshot({required this.userId, required this.items});

  final String? userId;
  final List<OrderCartItem> items;
}

/// Client-side persistence for the guest cart (deferred login).
///
/// The cart lives ONLY on this device under its own storage key so that:
///  * it survives guest navigation, guest → login, and app/browser restarts;
///  * it never leaks across users — the payload is scoped to the owning user
///    and is cleared on explicit sign-out, identity change, or a successful
///    authoritative payment (per existing logout and checkout semantics).
///    A session expiry does NOT clear it, so a customer who signs in again
///    on the same device keeps the exact items they built.
///
/// No business records are created server-side while browsing as a guest.
class GuestCartStorage {
  GuestCartStorage({FlutterSecureStorage? storage})
    : _storage = storage ?? const FlutterSecureStorage();

  static const storageKey = 'identity.guest.cart.v1';
  static const _version = 1;

  final FlutterSecureStorage _storage;

  /// Restores the persisted cart snapshot, or an empty snapshot when nothing
  /// valid was stored. Corrupt or older-version payloads are discarded safely.
  Future<GuestCartSnapshot> read() async {
    final encoded = await _storage.read(key: storageKey);
    if (encoded == null || encoded.isEmpty) {
      return const GuestCartSnapshot(userId: null, items: []);
    }

    try {
      final root = jsonDecode(encoded);
      if (root is! Map<String, dynamic> || root['version'] != _version) {
        return const GuestCartSnapshot(userId: null, items: []);
      }
      final rawItems = root['items'];
      if (rawItems is! List) {
        return const GuestCartSnapshot(userId: null, items: []);
      }
      final items = <OrderCartItem>[];
      for (final raw in rawItems) {
        if (raw is Map<String, dynamic>) {
          items.add(OrderCartItem.fromJson(raw));
        }
      }
      return GuestCartSnapshot(
        userId: root['userId'] as String?,
        items: items,
      );
    } on Object {
      // Never fail app startup over a corrupt guest cart payload.
      return const GuestCartSnapshot(userId: null, items: []);
    }
  }

  /// Persists the current cart under the owning identity (null while a guest).
  /// Writes are fire-and-forget; a failure to write must never interrupt the
  /// shopping flow, and the in-memory cart remains the source of truth.
  Future<void> write({
    required String? userId,
    required List<OrderCartItem> items,
  }) {
    try {
      return _storage.write(
        key: storageKey,
        value: jsonEncode({
          'version': _version,
          'userId': userId,
          'items': items.map((item) => item.toJson()).toList(growable: false),
        }),
      );
    } on Object {
      return Future.value();
    }
  }

  /// Removes the persisted cart. Called after a successful authoritative
  /// payment, on sign-out, on session expiry, and on identity change.
  Future<void> clear() => _storage.delete(key: storageKey);
}

/// Provides the device-local guest cart store. Override in tests with an
/// in-memory or mocked [FlutterSecureStorage].
final guestCartStorageProvider = Provider<GuestCartStorage>(
  (ref) => GuestCartStorage(),
);
