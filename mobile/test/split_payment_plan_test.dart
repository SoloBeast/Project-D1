import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

Map<String, dynamic> _paymentJson({
  required String publicId,
  required String method,
  required String provider,
  required String status,
  required double amount,
  String? gatewayOrderId,
}) => {
  'publicId': publicId,
  'paymentGroupId': 'group-1',
  'orderId': 'order-1',
  'orderNumber': 'DD-000001',
  'subscriptionId': null,
  'method': method,
  'provider': provider,
  'status': status,
  'amount': amount,
  'refundedAmount': 0,
  'currency': 'INR',
  'gatewayOrderId': gatewayOrderId,
  'gatewayPaymentId': null,
  'gatewayKeyId': 'rzp_test_key',
  'failureCode': null,
  'failureMessage': null,
  'expiresAtUtc': '2026-08-16T01:00:00Z',
  'verifiedAtUtc': null,
  'createdAtUtc': '2026-08-16T00:00:00Z',
};

Map<String, dynamic> _planJson({
  double walletApplied = 100,
  double razorpayAmount = 150,
  bool usesWallet = true,
}) => {
  'paymentGroupId': 'group-1',
  'payableAmount': 250,
  'walletApplied': walletApplied,
  'razorpayAmount': razorpayAmount,
  'usesWallet': usesWallet,
  'payment': _paymentJson(
    publicId: 'payment-rzp-1',
    method: 'Razorpay',
    provider: 'Razorpay',
    status: 'Pending',
    amount: razorpayAmount,
    gatewayOrderId: 'order_test_1',
  ),
  'components': [
    _paymentJson(
      publicId: 'payment-wallet-1',
      method: 'Wallet',
      provider: 'Wallet',
      status: 'Success',
      amount: walletApplied,
    ),
    _paymentJson(
      publicId: 'payment-rzp-1',
      method: 'Razorpay',
      provider: 'Razorpay',
      status: 'Pending',
      amount: razorpayAmount,
      gatewayOrderId: 'order_test_1',
    ),
  ],
};

Map<String, dynamic> _envelope(Object? data) => {
  'success': true,
  'data': data,
  'message': null,
  'errors': [],
};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('PaymentPlan model', () {
    test('parses server breakdown and exposes wallet-only state', () {
      final plan = PaymentPlan.fromJson(_planJson());
      expect(plan.payableAmount, 250);
      expect(plan.walletApplied, 100);
      expect(plan.razorpayAmount, 150);
      expect(plan.usesWallet, isTrue);
      expect(plan.needsRazorpay, isTrue);
      expect(plan.isWalletOnly, isFalse);
      expect(plan.components, hasLength(2));
      expect(plan.formattedPayable, '₹250.00');
    });

    test('wallet-only plan needs no Razorpay step', () {
      final plan = PaymentPlan.fromJson(
        _planJson(walletApplied: 250, razorpayAmount: 0),
      );
      expect(plan.isWalletOnly, isTrue);
      expect(plan.needsRazorpay, isFalse);
    });

    test('legacy payment payloads without a group still parse', () {
      final json = _paymentJson(
        publicId: 'payment-legacy',
        method: 'Razorpay',
        provider: 'Razorpay',
        status: 'Pending',
        amount: 90,
      )..remove('paymentGroupId');
      expect(PaymentDetails.fromJson(json).paymentGroupId, isNull);
    });
  });

  group('PaymentRepository.createPlan', () {
    test('posts opt-in flag and idempotency key to create-plan', () async {
      http.BaseRequest? captured;
      final client = MockClient((request) async {
        captured = request;
        return http.Response(jsonEncode(_envelope(_planJson())), 201);
      });
      final repository = PaymentRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final plan = await repository.createPlan(
        token: 'token',
        orderId: 'order-1',
        useWalletBalance: true,
        idempotencyKey: 'plan-key-1',
      );

      expect(plan.paymentGroupId, 'group-1');
      final http.Request httpRequest = captured! as http.Request;
      expect(httpRequest.url.path, '/api/v1/payments/create-plan');
      expect(httpRequest.headers['Idempotency-Key'], 'plan-key-1');
      final body = jsonDecode(httpRequest.body) as Map<String, dynamic>;
      expect(body['orderId'], 'order-1');
      expect(body['useWalletBalance'], isTrue);
    });
  });

  group('PaymentController wallet opt-in', () {
    // Path-aware mock: capabilities load succeeds (empty) so the build-time
    // fetch never interferes; only the plan POST is scripted per test.
    // The session is seeded through the auth repository (the established
    // controller-test pattern) rather than by overriding the session
    // controller itself.
    Future<ProviderContainer> makeContainer(
      Future<http.Response> Function(http.BaseRequest) onPlan,
    ) async {
      final client = MockClient((request) async {
        if (request.url.path == '/api/v1/payments/capabilities') {
          return http.Response(jsonEncode(_envelope([])), 200);
        }
        return onPlan(request);
      });
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(
            _AuthenticatedRepository(),
          ),
          paymentRepositoryProvider.overrideWithValue(
            PaymentRepository(
              api: ApiClient(
                client: client,
                baseUrl: 'https://api.example.test',
              ),
            ),
          ),
        ],
      );
      container.read(sessionControllerProvider);
      await Future<void>.delayed(Duration.zero);
      return container;
    }

    test('defaults wallet usage to OFF', () async {
      final container = await makeContainer(
        (_) async => http.Response('', 500),
      );
      addTearDown(container.dispose);
      expect(
        container.read(paymentControllerProvider).useWalletBalance,
        isFalse,
      );
      expect(container.read(paymentControllerProvider).plan, isNull);
    });

    test('toggle preserves opt-in across a sub-1-rupee rejection', () async {
      // Response.bytes + explicit UTF-8: the plain Response(String)
      // constructor only accepts Latin-1 and throws on the ₹ character.
      final container = await makeContainer(
        (_) async => http.Response.bytes(
          utf8.encode(
            jsonEncode({
              'success': false,
              'data': null,
              'message': 'Below minimum',
              'errors': [
                {
                  'code': 'WALLET_SPLIT_REMAINDER_BELOW_MINIMUM',
                  'field': null,
                  'message':
                      'Your wallet balance would leave ₹0.50 to pay by card.',
                },
              ],
            }),
          ),
          422,
          headers: {'content-type': 'application/json; charset=utf-8'},
        ),
      );
      addTearDown(container.dispose);
      container.read(paymentControllerProvider.notifier).setUseWalletBalance(
        true,
      );

      final plan = await container
          .read(paymentControllerProvider.notifier)
          .createPlanForOrder('order-1');

      expect(plan, isNull);
      final state = container.read(paymentControllerProvider);
      // Checkout state survives: opt-in kept, actionable message shown.
      expect(state.useWalletBalance, isTrue);
      expect(state.errorMessage, contains('₹0.50'));
    });

    test('successful plan stores server breakdown and reuses attempt key', () async {
      final keys = <String?>[];
      final container = await makeContainer((request) async {
        if (request.url.path == '/api/v1/payments/create-plan') {
          keys.add(request.headers['Idempotency-Key']);
        }
        return http.Response(jsonEncode(_envelope(_planJson())), 201);
      });
      addTearDown(container.dispose);
      container.read(paymentControllerProvider.notifier).setUseWalletBalance(
        true,
      );

      final first = await container
          .read(paymentControllerProvider.notifier)
          .createPlanForOrder('order-1');
      final second = await container
          .read(paymentControllerProvider.notifier)
          .createPlanForOrder('order-1');

      expect(first, isNotNull);
      expect(second, isNotNull);
      expect(keys, hasLength(2));
      // Same attempt key reused across the network retry.
      expect(keys[0], isNotNull);
      expect(keys[0], keys[1]);
      final state = container.read(paymentControllerProvider);
      expect(state.plan!.walletApplied, 100);
      expect(state.plan!.razorpayAmount, 150);
      expect(state.payment!.publicId, 'payment-rzp-1');
    });
  });

  group('subscription wallet opt-in', () {
    test('create request defaults wallet usage to OFF', () {
      final request = CreateSubscriptionRequest(
        productId: 'product-1',
        addressId: 'address-1',
        quantity: 1,
        startDate: DateTime(2026, 8, 17),
        deliveryDays: {DeliveryWeekday.monday},
        totalEntitlement: 4,
        paymentMethod: PaymentMethod.razorpay,
      );
      expect(request.useWalletBalance, isFalse);
      expect(request.toJson()['useWalletBalance'], isFalse);
    });
  });
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Customer',
    email: null,
    mobile: '9999999999',
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'token',
  refreshToken: 'refresh',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
