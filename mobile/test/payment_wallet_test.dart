import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_repository.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_repository.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

Map<String, dynamic> paymentJson({
  String method = 'Razorpay',
  String status = 'Pending',
  String? gatewayOrderId = 'order_mock_payment_1',
}) => {
  'publicId': 'payment-1',
  'orderId': 'order-1',
  'orderNumber': 'DD-000001',
  'method': method,
  'status': status,
  'amount': 90,
  'refundedAmount': 0,
  'currency': 'INR',
  'gatewayOrderId': gatewayOrderId,
  'gatewayPaymentId': null,
  'gatewayKeyId': null,
  'failureCode': null,
  'failureMessage': null,
  'expiresAtUtc': '2026-08-16T01:00:00Z',
  'verifiedAtUtc': null,
  'createdAtUtc': '2026-08-16T00:00:00Z',
};

Map<String, dynamic> walletJson() => {
  'publicId': 'wallet-1',
  'balance': 410.5,
  'currency': 'INR',
  'createdAtUtc': '2026-08-16T00:00:00Z',
  'updatedAtUtc': '2026-08-16T00:05:00Z',
};

Map<String, dynamic> walletTransactionJson({double amount = 500}) => {
  'publicId': 'transaction-1',
  'type': 'TopUp',
  'balanceBefore': 0,
  'amount': amount,
  'balanceAfter': amount,
  'currency': 'INR',
  'description': 'Development wallet top-up',
  'occurredAtUtc': '2026-08-16T00:05:00Z',
  'paymentId': null,
  'orderId': null,
};

http.Response successResponse(Object data, {int statusCode = 200}) =>
    http.Response(
      jsonEncode({'success': true, 'data': data, 'errors': []}),
      statusCode,
      headers: {'content-type': 'application/json'},
    );

void main() {
  group('payment models', () {
    test('parse backend state and expose status semantics', () {
      final pending = PaymentDetails.fromJson(paymentJson());
      final failed = PaymentDetails.fromJson(paymentJson(status: 'Failed'));
      final refunded = PaymentDetails.fromJson(paymentJson(status: 'Refunded'));

      expect(pending.method, PaymentMethod.razorpay);
      expect(pending.status.isPending, isTrue);
      expect(pending.usesMockGateway, isTrue);
      expect(pending.formattedAmount, '₹90.00');
      expect(failed.status.isTerminalFailure, isTrue);
      expect(refunded.status.isSuccessful, isTrue);
      expect(PaymentStatus.fromApi('unexpected'), PaymentStatus.unknown);
    });
  });

  group('payment repository', () {
    test(
      'create sends authoritative order and method with idempotency',
      () async {
        final client = MockClient((request) async {
          expect(request.method, 'POST');
          expect(
            request.url.toString(),
            'https://api.example.test/api/v1/payments/create',
          );
          expect(request.headers['Authorization'], 'Bearer customer-token');
          expect(request.headers['Idempotency-Key'], 'payment-attempt-1');
          expect(jsonDecode(request.body), {
            'orderId': 'order-1',
            'method': 'Wallet',
          });
          return successResponse(
            paymentJson(method: 'Wallet', status: 'Success'),
            statusCode: 201,
          );
        });
        final repository = PaymentRepository(
          api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
        );

        final payment = await repository.create(
          token: 'customer-token',
          orderId: 'order-1',
          method: PaymentMethod.wallet,
          idempotencyKey: 'payment-attempt-1',
        );

        expect(payment.method, PaymentMethod.wallet);
        expect(payment.status, PaymentStatus.success);
      },
    );

    test(
      'verify submits gateway callback and get refreshes server state',
      () async {
        var requestCount = 0;
        final client = MockClient((request) async {
          requestCount++;
          expect(request.headers['Authorization'], 'Bearer customer-token');
          if (requestCount == 1) {
            expect(request.method, 'POST');
            expect(request.url.path, '/api/v1/payments/verify');
            expect(jsonDecode(request.body), {
              'paymentId': 'payment-1',
              'gatewayOrderId': 'order_gateway_1',
              'gatewayPaymentId': 'pay_gateway_1',
              'signature': 'verified-signature',
            });
            return successResponse(paymentJson(status: 'Success'));
          }

          expect(request.method, 'GET');
          expect(request.url.path, '/api/v1/payments/payment-1');
          return successResponse(paymentJson(status: 'Refunded'));
        });
        final repository = PaymentRepository(
          api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
        );

        final verified = await repository.verify(
          token: 'customer-token',
          paymentId: 'payment-1',
          gatewayOrderId: 'order_gateway_1',
          gatewayPaymentId: 'pay_gateway_1',
          signature: 'verified-signature',
        );
        final refreshed = await repository.get('customer-token', 'payment-1');

        expect(verified.status, PaymentStatus.success);
        expect(refreshed.status, PaymentStatus.refunded);
        expect(requestCount, 2);
      },
    );

    test('preserves insufficient-wallet code and friendly message', () async {
      const message =
          'Insufficient wallet balance. Please add ₹460 to your wallet or '
          'choose another payment method.';
      final client = MockClient(
        (_) async => http.Response(
          jsonEncode({
            'success': false,
            'message': message,
            'errors': [
              {'code': 'INSUFFICIENT_WALLET_BALANCE', 'message': message},
            ],
          }),
          422,
          headers: {'content-type': 'application/json'},
        ),
      );
      final repository = PaymentRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      expect(
        () => repository.create(
          token: 'customer-token',
          orderId: 'order-1',
          method: PaymentMethod.wallet,
          idempotencyKey: 'payment-attempt-1',
        ),
        throwsA(
          isA<ApiException>()
              .having((error) => error.statusCode, 'statusCode', 422)
              .having(
                (error) => error.code,
                'code',
                'INSUFFICIENT_WALLET_BALANCE',
              )
              .having((error) => error.message, 'message', message)
              .having(
                (error) => error.message,
                'does not expose generic server failure text',
                allOf(
                  isNot(contains('Internal Server Error')),
                  isNot(contains('500')),
                ),
              ),
        ),
      );
    });
  });

  group('payment controller', () {
    test('shows the server-provided insufficient-wallet message', () async {
      const message =
          'Insufficient wallet balance. Please add ₹460 to your wallet or '
          'choose another payment method.';
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(
            _AuthenticatedAuthRepository(),
          ),
          paymentRepositoryProvider.overrideWithValue(
            _InsufficientWalletPaymentRepository(message),
          ),
        ],
      );
      addTearDown(container.dispose);

      container.read(sessionControllerProvider);
      await Future<void>.delayed(Duration.zero);

      final created = await container
          .read(paymentControllerProvider.notifier)
          .createForOrder('order-1');
      final state = container.read(paymentControllerProvider);

      expect(created, isFalse);
      expect(state.isLoading, isFalse);
      expect(state.payment, isNull);
      expect(state.errorMessage, message);
      expect(state.errorMessage, isNot(contains('Internal Server Error')));
      expect(state.errorMessage, isNot(contains('500')));
    });
  });

  group('wallet models and repository', () {
    test('parse balance and validate credit and debit ledger arithmetic', () {
      final wallet = WalletDetails.fromJson(walletJson());
      final credit = WalletTransaction.fromJson(walletTransactionJson());
      final debit = WalletTransaction.fromJson({
        ...walletTransactionJson(amount: -90),
        'type': 'PaymentDebit',
        'balanceBefore': 500,
        'balanceAfter': 410,
        'paymentId': 'payment-1',
        'orderId': 'order-1',
      });

      expect(wallet.formattedBalance, '₹410.50');
      expect(credit.isCredit, isTrue);
      expect(credit.isReconciled, isTrue);
      expect(credit.formattedAmount, '+₹500.00');
      expect(debit.isCredit, isFalse);
      expect(debit.isReconciled, isTrue);
      expect(debit.formattedAmount, '-₹90.00');
    });

    test('loads wallet and ledger using authenticated routes', () async {
      var requestCount = 0;
      final client = MockClient((request) async {
        requestCount++;
        expect(request.method, 'GET');
        expect(request.headers['Authorization'], 'Bearer customer-token');
        if (request.url.path == '/api/v1/wallet') {
          return successResponse(walletJson());
        }
        expect(request.url.path, '/api/v1/wallet/transactions');
        return successResponse([walletTransactionJson()]);
      });
      final repository = WalletRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final wallet = await repository.get('customer-token');
      final transactions = await repository.getTransactions('customer-token');

      expect(wallet.balance, 410.5);
      expect(transactions.single.type, 'TopUp');
      expect(requestCount, 2);
    });

    test('top-up sends amount and idempotency key', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/wallet/topup');
        expect(request.headers['Authorization'], 'Bearer customer-token');
        expect(request.headers['Idempotency-Key'], 'wallet-topup-1');
        expect(jsonDecode(request.body), {'amount': 500.0});
        return successResponse(walletTransactionJson());
      });
      final repository = WalletRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final transaction = await repository.topUp(
        token: 'customer-token',
        amount: 500,
        idempotencyKey: 'wallet-topup-1',
      );

      expect(transaction.amount, 500);
      expect(transaction.isReconciled, isTrue);
    });

    testWidgets('shows development top-up in debug builds', (tester) async {
      expect(developmentWalletTopUpEnabled, isTrue);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            walletControllerProvider.overrideWith(
              () => _SeededWalletController(
                WalletState(wallet: WalletDetails.fromJson(walletJson())),
              ),
            ),
          ],
          child: const MaterialApp(home: WalletScreen()),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Development top-up'), findsOneWidget);
      expect(find.text('₹410.50'), findsOneWidget);
    });
  });
}

final _authenticatedSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2026, 8, 16, 6),
  refreshTokenExpiresAtUtc: DateTime.utc(2026, 9, 16),
);

class _AuthenticatedAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _authenticatedSession;
}

class _SeededWalletController extends WalletController {
  _SeededWalletController(this.initialState);

  final WalletState initialState;

  @override
  WalletState build() => initialState;

  @override
  Future<void> load() async {}
}

class _InsufficientWalletPaymentRepository extends PaymentRepository {
  _InsufficientWalletPaymentRepository(this.message)
    : super(
        api: ApiClient(
          client: MockClient((_) async => http.Response('', 500)),
          baseUrl: 'https://api.example.test',
        ),
      );

  final String message;

  @override
  Future<PaymentDetails> create({
    required String token,
    required String orderId,
    required PaymentMethod method,
    required String idempotencyKey,
  }) async {
    expect(token, 'customer-token');
    expect(orderId, 'order-1');
    expect(method, PaymentMethod.wallet);
    throw ApiException(422, 'INSUFFICIENT_WALLET_BALANCE', message);
  }
}
