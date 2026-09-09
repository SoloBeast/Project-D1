import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('guest session state', () {
    test('guest state is not authenticated and has no session', () {
      const guest = SessionState.guest();

      expect(guest.status, SessionStatus.guest);
      expect(guest.isGuest, isTrue);
      expect(guest.isAuthenticated, isFalse);
      expect(guest.session, isNull);
      expect(guest.role, isNull);
      expect(guest.publicUserId, isNull);
    });

    test('restore with no persisted session yields guest without enterAsGuest',
        () async {
      final repository = _TrackingAuthRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(repository),
          guestCartStorageProvider.overrideWithValue(
            GuestCartStorage(storage: _FakeFlutterSecureStorage()),
          ),
          orderRepositoryProvider.overrideWithValue(_TrackingOrderRepository()),
        ],
      );
      addTearDown(container.dispose);

      // Reading the controller kicks off _restore(), which resolves to null
      // and must fall back to the guest state without any explicit
      // enterAsGuest call (the published app's initial page is the guest
      // customer home, not the login screen).
      container.read(sessionControllerProvider);
      await Future<void>.delayed(Duration.zero);

      final session = container.read(sessionControllerProvider);
      expect(session.isGuest, isTrue);
      expect(session.isAuthenticated, isFalse);
      expect(session.session, isNull);
      expect(session.status, SessionStatus.guest);
    });

    test('enterAsGuest transitions unauthenticated to guest', () async {
      final container = await _guestContainer();
      addTearDown(container.dispose);

      final session = container.read(sessionControllerProvider);
      expect(session.isGuest, isTrue);
      expect(session.session, isNull);
    });

    test('enterAsGuest does not override an authenticated session', () async {
      final container = await _authenticatedContainer();
      addTearDown(container.dispose);

      container.read(sessionControllerProvider.notifier).enterAsGuest();
      await pumpEventQueue();

      final session = container.read(sessionControllerProvider);
      expect(session.isGuest, isFalse);
      expect(session.isAuthenticated, isTrue);
    });

    test('exitGuest returns guest to unauthenticated', () async {
      final container = await _guestContainer();
      addTearDown(container.dispose);

      container.read(sessionControllerProvider.notifier).exitGuest();
      await pumpEventQueue();

      final session = container.read(sessionControllerProvider);
      expect(session.isGuest, isFalse);
      expect(session.isAuthenticated, isFalse);
      expect(session.status, SessionStatus.unauthenticated);
    });

    test('exitGuest keeps an authenticated session', () async {
      final container = await _authenticatedContainer();
      addTearDown(container.dispose);

      container.read(sessionControllerProvider.notifier).exitGuest();
      await pumpEventQueue();

      final session = container.read(sessionControllerProvider);
      expect(session.isAuthenticated, isTrue);
    });

    test('refreshAccessToken returns null for a guest without refreshing', () async {
      final repository = _TrackingAuthRepository();
      final container = await _guestContainer(auth: repository);
      addTearDown(container.dispose);

      final token = await container
          .read(sessionControllerProvider.notifier)
          .refreshAccessToken();

      expect(token, isNull);
      expect(repository.refreshCalls, 0);
    });

    test('signOut from guest does not call the logout API or clear cart', () async {
      final repository = _TrackingAuthRepository();
      final storage = _FakeFlutterSecureStorage();
      final container = await _guestContainer(
        auth: repository,
        storage: storage,
      );
      addTearDown(container.dispose);

      // Seed a guest cart so the test can assert it survives guest exit.
      await storage.write(
        key: GuestCartStorage.storageKey,
        value: _encodedGuestCart(),
      );

      await container.read(sessionControllerProvider.notifier).signOut();
      await pumpEventQueue();

      expect(repository.logoutCalls, 0);
      expect(repository.clearCalls, 0);
      expect(storage.values.containsKey(GuestCartStorage.storageKey), isTrue);
      expect(
        container.read(sessionControllerProvider).status,
        SessionStatus.unauthenticated,
      );
    });

    test('authenticated signOut clears the device cart', () async {
      // Must restore a session so the container actually starts
      // authenticated — otherwise signOut would be a guest/no-op.
      final repository = _TrackingAuthRepository(restoreResult: _session);
      final storage = _FakeFlutterSecureStorage();
      final container = await _authenticatedContainer(
        auth: repository,
        storage: storage,
      );
      addTearDown(container.dispose);

      await storage.write(
        key: GuestCartStorage.storageKey,
        value: _encodedUserCart('customer-1'),
      );

      await container.read(sessionControllerProvider.notifier).signOut();
      await pumpEventQueue();

      expect(repository.logoutCalls, 1);
      expect(storage.values.containsKey(GuestCartStorage.storageKey), isFalse);
      expect(
        container.read(sessionControllerProvider).status,
        SessionStatus.unauthenticated,
      );
    });

    test('guest signOut leaves in-memory cart intact for later sign-in', () async {
      final repository = _TrackingAuthRepository();
      final container = await _guestContainer(auth: repository);
      addTearDown(container.dispose);

      container
          .read(orderControllerProvider.notifier)
          .setCartItem(_product, 2);
      await pumpEventQueue();

      await container.read(sessionControllerProvider.notifier).signOut();
      await pumpEventQueue();

      final orderState = container.read(orderControllerProvider);
      expect(orderState.cart, hasLength(1));
      expect(orderState.cart.single.product.publicId, _product.publicId);
    });
  });
}

/// A container whose session restore yields no session, then enters guest mode.
Future<ProviderContainer> _guestContainer({
  _TrackingAuthRepository? auth,
  _FakeFlutterSecureStorage? storage,
}) async {
  final repository = auth ?? _TrackingAuthRepository();
  final guestCartStorage = GuestCartStorage(storage: storage ?? _FakeFlutterSecureStorage());
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(repository),
      guestCartStorageProvider.overrideWithValue(guestCartStorage),
      orderRepositoryProvider.overrideWithValue(_TrackingOrderRepository()),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  container.read(sessionControllerProvider.notifier).enterAsGuest();
  await Future<void>.delayed(Duration.zero);
  return container;
}

Future<ProviderContainer> _authenticatedContainer({
  _TrackingAuthRepository? auth,
  _FakeFlutterSecureStorage? storage,
}) async {
  final repository =
      auth ??
      _TrackingAuthRepository(restoreResult: _session);
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(repository),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: storage ?? _FakeFlutterSecureStorage()),
      ),
      orderRepositoryProvider.overrideWithValue(_TrackingOrderRepository()),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

/// Auth repository that never touches storage and records calls.
class _TrackingAuthRepository extends AuthRepository {
  _TrackingAuthRepository({this.restoreResult})
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final AuthSession? restoreResult;

  int refreshCalls = 0;
  int logoutCalls = 0;
  int clearCalls = 0;
  int saveSessionCalls = 0;

  @override
  Future<AuthSession?> restore() async => restoreResult;

  @override
  Future<AuthSession> refresh(AuthSession session) async {
    refreshCalls += 1;
    return session;
  }

  @override
  Future<void> logout(AuthSession session) async {
    logoutCalls += 1;
  }

  @override
  Future<void> clear() async {
    clearCalls += 1;
  }

  @override
  Future<void> saveSession(AuthSession session) async {
    saveSessionCalls += 1;
  }
}

/// In-memory secure storage sharing v9.2.4 write/read/delete semantics
/// (writing null deletes the key).
class _FakeFlutterSecureStorage extends FlutterSecureStorage {
  final Map<String, String> values = {};

  @override
  Future<void> write({
    required String key,
    required String? value,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    if (value == null) {
      values.remove(key);
    } else {
      values[key] = value;
    }
  }

  @override
  Future<String?> read({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => values[key];

  @override
  Future<bool> containsKey({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => values.containsKey(key);

  @override
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    values.remove(key);
  }
}

class _TrackingOrderRepository extends OrderRepository {
  _TrackingOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));
}

/// Encodes a minimal guest cart payload for [GuestCartStorage.storageKey].
String _encodedGuestCart() => '{"version":1,"userId":null,"items":[]}';

String _encodedUserCart(String userId) =>
    '{"version":1,"userId":"$userId","items":[]}';

const _product = CatalogueProduct(
  publicId: '00000000-0000-0000-0000-000000000030',
  sku: 'MILK-1L',
  name: 'Whole Milk',
  description: 'Fresh whole milk.',
  category: ProductCategory(
    publicId: '00000000-0000-0000-0000-000000000040',
    code: 'MILK',
    name: 'Milk',
    description: 'Fresh dairy products.',
    isActive: true,
  ),
  unitOfMeasure: 'litre',
  price: 60,
  isActive: true,
  branchAvailability: [
    BranchAvailability(
      branchId: '00000000-0000-0000-0000-000000000050',
      branchCode: 'CENTRAL',
      branchName: 'Central Dairy',
      isAvailable: true,
      maxDailyQuantity: 10,
    ),
  ],
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Asha Sharma',
    email: 'asha@example.test',
    mobile: '9876543210',
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
