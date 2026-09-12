import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('order controller staff inspection', () {
    test('loads a branch-scoped order via the staff route', () async {
      final repository = _RecordingOrderRepository();
      final container = await _containerWith(repository: repository);
      addTearDown(container.dispose);

      await container.read(orderControllerProvider.notifier).loadStaffOrder(
            'order-1',
          );

      final state = container.read(orderControllerProvider);
      expect(state.selectedOrder?.publicId, 'order-1');
      expect(state.isLoading, isFalse);
      expect(state.errorMessage, isNull);
      expect(repository.staffReadCount, 1);
      expect(repository.lastToken, 'manager-token');
      // Staff inspection must not read through the customer path.
      expect(repository.getCalls, 0);
    });

    test('maps staff authorization failures to an inline error', () async {
      final repository = _RecordingOrderRepository()
        ..failure = ApiException(
          403,
          'FORBIDDEN',
          'Order access denied.',
        );
      final container = await _containerWith(repository: repository);
      addTearDown(container.dispose);

      await container.read(orderControllerProvider.notifier).loadStaffOrder(
            'order-1',
          );

      final state = container.read(orderControllerProvider);
      expect(state.selectedOrder, isNull);
      expect(state.isLoading, isFalse);
      expect(state.errorMessage, 'Order access denied.');
    });

    test('maps unexpected staff failures to the offline message', () async {
      final repository = _RecordingOrderRepository()
        ..failure = ApiNetworkException('socket closed');
      final container = await _containerWith(repository: repository);
      addTearDown(container.dispose);

      await container.read(orderControllerProvider.notifier).loadStaffOrder(
            'order-1',
          );

      final state = container.read(orderControllerProvider);
      expect(state.isLoading, isFalse);
      expect(state.errorMessage, contains('Check your connection'));
    });

    test('does not call the staff route without a token', () async {
      final repository = _RecordingOrderRepository();
      final container = await _containerWith(
        repository: repository,
        authenticated: false,
      );
      addTearDown(container.dispose);

      await container.read(orderControllerProvider.notifier).loadStaffOrder(
            'order-1',
          );

      expect(repository.staffReadCount, 0);
      expect(repository.lastToken, isNull);
    });
  });
}

Future<ProviderContainer> _containerWith({
  required _RecordingOrderRepository repository,
  bool authenticated = true,
}) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(
        _SeededAuthRepository(authenticated ? _managerSession : null),
      ),
      guestCartStorageProvider.overrideWithValue(
        GuestCartStorage(storage: _FakeFlutterSecureStorage()),
      ),
      orderRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

class _SeededAuthRepository extends AuthRepository {
  _SeededAuthRepository(this.session)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final AuthSession? session;

  @override
  Future<AuthSession?> restore() async => session;

  @override
  Future<void> saveSession(AuthSession session) async {}

  @override
  Future<void> clear() async {}
}

class _RecordingOrderRepository extends OrderRepository {
  _RecordingOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;
  int staffReadCount = 0;
  int getCalls = 0;
  Object? failure;

  @override
  Future<OrderSummary> getForStaff(String token, String orderId) async {
    lastToken = token;
    staffReadCount++;
    if (failure != null) {
      throw failure!;
    }
    return _orderSummary(orderId);
  }

  @override
  Future<OrderSummary> get(String token, String orderId) {
    getCalls++;
    throw UnimplementedError('staff inspection must not use the customer path');
  }
}

OrderSummary _orderSummary(String orderId) => OrderSummary(
  publicId: orderId,
  orderNumber: 'DD-000001',
  type: 'OneTime',
  status: 'Confirmed',
  createdAt: DateTime.utc(2026, 8, 16, 9),
  addressLabel: 'Home',
  city: 'Bengaluru',
  branchName: 'Main Branch',
  items: const [
    OrderItem(
      productId: 'product-1',
      productName: 'Whole Milk',
      sku: 'MILK-1L',
      unitOfMeasure: 'litre',
      quantity: 1,
      unitPrice: 60,
      lineTotal: 60,
    ),
  ],
  subtotal: 60,
  discountAmount: 0,
  payableAmount: 60,
  cancelledAt: null,
  paymentPublicId: 'payment-1',
  paymentStatus: 'Captured',
  gatewayPaymentId: null,
  deliveryPublicId: null,
  deliveryReferenceNumber: null,
  deliveryStatus: null,
);

final _managerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Dairy Manager',
    email: null,
    mobile: '9999999999',
    roles: ['DAIRY_MANAGER'],
    permissions: ['ORDERS.READ_BRANCH'],
    branchIds: [7],
  ),
  accessToken: 'manager-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

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
