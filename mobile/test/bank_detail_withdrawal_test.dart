import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_repository.dart';
import 'package:doodh_direct_mobile/features/customer/customer_screens.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_repository.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

const _missingDetailsMessage =
    'Please check the bank details on profile section before withdraw request.';

Map<String, dynamic> _bankDetailJson() => {
  'publicId': 'bank-1',
  'accountHolderName': 'Asha Sharma',
  'bankName': 'State Bank of India',
  'maskedAccountNumber': '···· 9012',
  'accountLast4': '9012',
  'ifsc': 'SBIN0001234',
  'isComplete': true,
  'createdAt': '2026-08-16T05:30:00',
  'updatedAt': '2026-08-16T05:35:00',
};

CustomerBankDetail _bankDetail() =>
    CustomerBankDetail.fromJson(_bankDetailJson());

Map<String, dynamic> _withdrawalJson({String status = 'Pending'}) => {
  'publicId': 'wd-1',
  'requestNumber': 'WD/000001',
  'amount': 250,
  'currency': 'INR',
  'status': status,
  'maskedDestination': 'State Bank of India ···· 9012',
  'bankNameSnapshot': 'State Bank of India',
  'decisionRemarks': null,
  'createdAt': '2026-08-16T07:30:00',
  'decidedAt': null,
};

WalletDetails _wallet([double balance = 1000]) => WalletDetails(
  publicId: 'wallet-1',
  balance: balance,
  currency: 'INR',
  createdAt: DateTime(2026, 8, 16, 5, 30),
  updatedAt: DateTime(2026, 8, 16, 5, 35),
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: 'customer@example.test',
    mobile: '+919876543210',
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
  Future<AuthSession?> restore() async => _session;
}

class _StubOrderController extends OrderController {
  @override
  OrderState build() => const OrderState();
}

http.Response _ok(Object? data, {int statusCode = 200}) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  statusCode,
  headers: {'content-type': 'application/json'},
);

void main() {
  group('bank detail validation', () {
    test('holder and bank names are required and length-limited', () {
      expect(validateAccountHolderName(''), isNotNull);
      expect(validateAccountHolderName('  '), isNotNull);
      expect(validateAccountHolderName('A' * 101), isNotNull);
      expect(validateAccountHolderName('Asha Sharma'), isNull);
      expect(validateBankName(null), isNotNull);
      expect(validateBankName('B' * 101), isNotNull);
      expect(validateBankName('State Bank of India'), isNull);
    });

    test('account numbers accept 9 to 18 digits only', () {
      expect(
        validateBankAccountNumber('', isRequired: true),
        isNotNull,
      );
      expect(validateBankAccountNumber('', isRequired: false), isNull);
      expect(
        validateBankAccountNumber('12345678', isRequired: true),
        isNotNull,
      );
      expect(
        validateBankAccountNumber('1234567890123456789', isRequired: true),
        isNotNull,
      );
      expect(
        validateBankAccountNumber('12345678901A', isRequired: true),
        isNotNull,
      );
      expect(
        validateBankAccountNumber('123456789', isRequired: true),
        isNull,
      );
      expect(
        validateBankAccountNumber('123456789012345678', isRequired: true),
        isNull,
      );
    });

    test('IFSC follows the four-letters zero six-alphanumerics shape', () {
      expect(validateIfsc(''), isNotNull);
      expect(validateIfsc('SBIN123'), isNotNull);
      expect(validateIfsc('SBIN000123'), isNotNull);
      expect(validateIfsc('SB1N0001234'), isNotNull);
      expect(validateIfsc('SBIN0001234'), isNull);
      expect(validateIfsc('sbin0001234'), isNull);
      expect(validateIfsc('HDFC0001234'), isNull);
    });
  });

  group('withdrawal amount validation', () {
    test('rejects empty, malformed, zero, and negative input', () {
      expect(validateWithdrawalAmount('', 1000), isNotNull);
      expect(validateWithdrawalAmount('abc', 1000), isNotNull);
      expect(validateWithdrawalAmount('0', 1000), isNotNull);
      expect(validateWithdrawalAmount('-50', 1000), isNotNull);
    });

    test('rejects precision beyond two decimals and over-balance', () {
      expect(validateWithdrawalAmount('10.125', 1000), isNotNull);
      expect(validateWithdrawalAmount('1500', 1000), isNotNull);
      expect(
        validateWithdrawalAmount('1500', 1000),
        contains('₹1000.00'),
      );
    });

    test('accepts positive two-decimal amounts within balance', () {
      expect(validateWithdrawalAmount('250', 1000), isNull);
      expect(validateWithdrawalAmount('250.50', 1000), isNull);
      expect(validateWithdrawalAmount('1000', 1000), isNull);
    });
  });

  group('models', () {
    test('bank detail parses masked fields and never a full number', () {
      final detail = _bankDetail();
      expect(detail.maskedAccountNumber, '···· 9012');
      expect(detail.accountLast4, '9012');
      expect(detail.isComplete, isTrue);
    });

    test('bank draft uppercases IFSC and omits a blank account number', () {
      const draft = BankDetailDraft(
        accountHolderName: 'Asha Sharma',
        bankName: 'HDFC Bank',
        accountNumber: '  ',
        ifsc: 'hdfc0001234',
      );
      final json = draft.toJson();
      expect(json['ifsc'], 'HDFC0001234');
      expect(json['accountNumber'], isNull);
    });

    test('withdrawal parses statuses and labels', () {
      final pending = WithdrawalRequest.fromJson(_withdrawalJson());
      expect(pending.isPending, isTrue);
      expect(pending.statusLabel, 'Pending review');
      expect(pending.maskedDestination, 'State Bank of India ···· 9012');
      expect(pending.formattedAmount, '₹250.00');
      expect(
        WithdrawalRequest.fromJson(
          _withdrawalJson(status: 'Completed'),
        ).isCompleted,
        isTrue,
      );
      expect(
        WithdrawalRequest.fromJson(
          _withdrawalJson(status: 'Rejected'),
        ).statusLabel,
        'Rejected',
      );
      expect(
        () => WithdrawalRequest.fromJson(_withdrawalJson(status: 'Paid')),
        throwsFormatException,
      );
    });
  });

  group('repositories', () {
    test('bank detail GET returns null when absent and parses when present', () async {
      var calls = 0;
      final api = ApiClient(
        client: MockClient((request) async {
          calls++;
          expect(request.url.path, '/api/v1/customers/me/bank-detail');
          return _ok(calls == 1 ? null : _bankDetailJson());
        }),
        baseUrl: 'https://test',
      );
      final repository = CustomerRepository(api: api);

      expect(await repository.getBankDetail('token'), isNull);
      final detail = await repository.getBankDetail('token');
      expect(detail?.maskedAccountNumber, '···· 9012');
    });

    test('bank detail save PUTs the draft and parses the masked result', () async {
      late http.Request captured;
      final api = ApiClient(
        client: MockClient((request) async {
          captured = request;
          return _ok(_bankDetailJson());
        }),
        baseUrl: 'https://test',
      );
      final detail = await CustomerRepository(api: api).saveBankDetail(
        'token',
        const BankDetailDraft(
          accountHolderName: 'Asha Sharma',
          bankName: 'State Bank of India',
          accountNumber: '123456789012',
          ifsc: 'sbin0001234',
        ),
      );

      expect(captured.method, 'PUT');
      expect(captured.url.path, '/api/v1/customers/me/bank-detail');
      final body = jsonDecode(captured.body) as Map<String, dynamic>;
      expect(body['ifsc'], 'SBIN0001234');
      expect(detail.accountLast4, '9012');
    });

    test('withdrawal submit posts amount with idempotency key', () async {
      late http.Request captured;
      final api = ApiClient(
        client: MockClient((request) async {
          captured = request;
          if (request.url.path == '/api/v1/customers/me/bank-detail') {
            return _ok(_bankDetailJson());
          }
          if (request.url.path == '/api/v1/wallet/withdrawals' &&
              request.method == 'GET') {
            return _ok([_withdrawalJson()]);
          }
          return _ok(_withdrawalJson());
        }),
        baseUrl: 'https://test',
      );
      final repository = WalletRepository(api: api);

      final created = await repository.submitWithdrawal(
        token: 'token',
        amount: 250,
        idempotencyKey: 'key-1',
      );
      expect(captured.method, 'POST');
      expect(captured.headers['Idempotency-Key'], 'key-1');
      expect(
        (jsonDecode(captured.body) as Map<String, dynamic>)['amount'],
        250,
      );
      expect(created.requestNumber, 'WD/000001');
      expect(created.isPending, isTrue);

      final list = await repository.getWithdrawals('token');
      expect(list, hasLength(1));
      expect(list.single.statusLabel, 'Pending review');
    });

    test('server validation errors surface message and field', () async {
      final api = ApiClient(
        client: MockClient((request) async => http.Response(
          jsonEncode({
            'success': false,
            'data': null,
            'message': 'Amount must be positive.',
            'errors': [
              {'code': 'VALIDATION_ERROR', 'field': 'amount', 'message': 'Amount must be positive.'},
            ],
          }),
          400,
          headers: {'content-type': 'application/json'},
        )),
        baseUrl: 'https://test',
      );
      final error = await _captureApiError(
        () => WalletRepository(api: api).submitWithdrawal(
          token: 'token',
          amount: 0,
          idempotencyKey: 'key-1',
        ),
      );
      expect(error?.message, 'Amount must be positive.');
      expect(error?.field, 'amount');
    });
  });

  group('wallet withdraw flow', () {
    testWidgets('withdraw opens the missing-details message with profile action', (
      tester,
    ) async {
      final controller = _ScriptedWalletController(
        WalletState(wallet: _wallet(), transactions: []),
      );
      final router = _walletRouter();
      addTearDown(router.dispose);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authRepositoryProvider.overrideWithValue(
              _AuthenticatedAuthRepository(),
            ),
            walletControllerProvider.overrideWith(() => controller),
          ],
          child: MaterialApp.router(routerConfig: router),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.widgetWithText(OutlinedButton, 'Withdraw'), findsOneWidget);
      await tester.tap(find.widgetWithText(OutlinedButton, 'Withdraw'));
      await tester.pumpAndSettle();

      expect(find.text(_missingDetailsMessage), findsOneWidget);
      await tester.tap(find.text('Open bank details'));
      await tester.pumpAndSettle();
      expect(find.text('Bank detail target'), findsOneWidget);
      expect(router.state.uri.toString(), '/customer/bank-detail');
    });

    testWidgets('bank check failure offers retry and makes no request', (
      tester,
    ) async {
      final controller = _ScriptedWalletController(
        WalletState(wallet: _wallet(), transactions: []),
        bankDetailError: 'Bank service is down.',
      );
      await _pumpWallet(tester, controller: controller);

      await tester.tap(find.widgetWithText(OutlinedButton, 'Withdraw'));
      await tester.pumpAndSettle();

      expect(find.text('Could not check bank details'), findsOneWidget);
      expect(find.text('Bank service is down.'), findsOneWidget);
      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();
      expect(controller.bankDetailChecks, 2);
      expect(controller.submittedAmounts, isEmpty);
    });

    testWidgets('complete details allow amount entry, confirmation, and submit', (
      tester,
    ) async {
      final controller = _ScriptedWalletController(
        WalletState(wallet: _wallet(), transactions: []),
        bankDetail: _bankDetail(),
      );
      await _pumpWallet(tester, controller: controller);

      await tester.tap(find.widgetWithText(OutlinedButton, 'Withdraw'));
      await tester.pumpAndSettle();

      // Amount dialog repeats the reminder with the masked destination.
      expect(find.text('Withdraw to bank'), findsOneWidget);
      expect(find.text(_missingDetailsMessage), findsOneWidget);
      expect(find.text('State Bank of India'), findsOneWidget);
      expect(find.text('···· 9012'), findsOneWidget);
      expect(find.text('Available balance: ₹1000.00'), findsOneWidget);

      // Invalid amounts are blocked client-side.
      await tester.enterText(
        find.widgetWithText(TextField, 'Amount'),
        '1500',
      );
      await tester.tap(find.text('Continue'));
      await tester.pump();
      expect(find.textContaining('cannot exceed your available balance'), findsOneWidget);
      expect(controller.submittedAmounts, isEmpty);

      await tester.enterText(
        find.widgetWithText(TextField, 'Amount'),
        '250',
      );
      await tester.tap(find.text('Continue'));
      await tester.pumpAndSettle();

      // Explicit confirmation names amount and destination.
      expect(
        find.widgetWithText(FilledButton, 'Confirm withdrawal'),
        findsOneWidget,
      );
      expect(
        find.textContaining('Withdraw ₹250.00 to State Bank of India'),
        findsOneWidget,
      );
      await tester.tap(find.text('Confirm withdrawal').last);
      await tester.pumpAndSettle();

      expect(controller.submittedAmounts, [250]);
      expect(
        find.text('Withdrawal request WD/000001 is pending review.'),
        findsOneWidget,
      );
    });

    testWidgets('pending withdrawals render status and masked destination', (
      tester,
    ) async {
      await _pumpWallet(
        tester,
        controller: _ScriptedWalletController(
          WalletState(
            wallet: _wallet(750),
            transactions: [],
            withdrawals: [
              WithdrawalRequest.fromJson(_withdrawalJson()),
              WithdrawalRequest.fromJson(
                _withdrawalJson(status: 'Rejected'),
              ),
            ],
          ),
        ),
      );

      expect(find.text('Withdrawals'), findsOneWidget);
      expect(find.text('WD/000001'), findsNWidgets(2));
      expect(find.text('Pending review'), findsOneWidget);
      expect(find.text('Rejected'), findsOneWidget);
      expect(find.text('State Bank of India ···· 9012'), findsNWidgets(2));
    });
  });

  group('profile bank details', () {
    testWidgets('section shows masked details with edit entry', (tester) async {
      await _pumpAccount(
        tester,
        state: CustomerState(
          profile: _profile(),
          addresses: [],
          bankDetail: _bankDetail(),
        ),
      );

      expect(find.text('Bank details'), findsOneWidget);
      expect(find.text('State Bank of India'), findsOneWidget);
      expect(find.text('···· 9012'), findsOneWidget);
      expect(find.text('Asha Sharma'), findsWidgets);
    });

    testWidgets('empty section prompts to add details', (tester) async {
      await _pumpAccount(
        tester,
        state: CustomerState(profile: _profile(), addresses: []),
      );

      expect(find.text('Add bank details'), findsOneWidget);
      expect(
        find.text('Add your account to request wallet withdrawals.'),
        findsOneWidget,
      );
    });

    testWidgets('edit screen validates and saves the draft', (tester) async {
      _useSurface(tester, const Size(800, 1400));
      final repository = _RecordingCustomerRepository();
      final router = GoRouter(
        initialLocation: '/stub-home',
        routes: [
          GoRoute(
            path: '/stub-home',
            builder: (context, state) => Scaffold(
              body: TextButton(
                onPressed: () => context.push('/customer/bank-detail'),
                child: const Text('Open bank details'),
              ),
            ),
          ),
          GoRoute(
            path: '/customer/bank-detail',
            builder: (context, state) =>
                const CustomerBankDetailEditScreen(),
          ),
        ],
      );
      addTearDown(router.dispose);
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(
            _AuthenticatedAuthRepository(),
          ),
          orderControllerProvider.overrideWith(() => _StubOrderController()),
          customerRepositoryProvider.overrideWithValue(repository),
        ],
      );
      addTearDown(container.dispose);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp.router(routerConfig: router),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Open bank details'));
      await tester.pumpAndSettle();

      // Empty submit surfaces field validation without any API call.
      await tester.ensureVisible(find.text('Save bank details'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Save bank details'));
      await tester.pump();
      expect(find.text('Account holder name is required.'), findsOneWidget);
      expect(find.text('Bank name is required.'), findsOneWidget);
      expect(find.text('Account number is required.'), findsOneWidget);
      expect(find.text('IFSC is required.'), findsOneWidget);
      expect(repository.savedDrafts, isEmpty);

      await tester.enterText(
        find.widgetWithText(TextField, 'Account holder name'),
        'Asha Sharma',
      );
      await tester.enterText(
        find.widgetWithText(TextField, 'Bank name'),
        'State Bank of India',
      );
      await tester.enterText(
        find.widgetWithText(TextField, 'Account number'),
        '12345',
      );
      await tester.enterText(
        find.widgetWithText(TextField, 'IFSC'),
        'SBIN123',
      );
      await tester.ensureVisible(find.text('Save bank details'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Save bank details'));
      await tester.pump();
      expect(
        find.text('Account number must be 9 to 18 digits.'),
        findsOneWidget,
      );
      expect(find.text('IFSC must look like ABCD0123456.'), findsOneWidget);
      expect(repository.savedDrafts, isEmpty);

      await tester.enterText(
        find.widgetWithText(TextField, 'Account number'),
        '123456789012',
      );
      await tester.enterText(
        find.widgetWithText(TextField, 'IFSC'),
        'SBIN0001234',
      );
      await tester.ensureVisible(find.text('Save bank details'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Save bank details'));
      await tester.pumpAndSettle();

      expect(repository.savedDrafts, hasLength(1));
      expect(repository.savedDrafts.single.accountNumber, '123456789012');
      expect(repository.savedDrafts.single.ifsc, 'SBIN0001234');
      // A successful save returns to the previous screen.
      expect(find.text('Open bank details'), findsOneWidget);
    });
  });
}

Future<ApiException?> _captureApiError(Future<void> Function() action) async {
  try {
    await action();
  } on ApiException catch (error) {
    return error;
  }
  return null;
}

CustomerProfile _profile() => const CustomerProfile(
  publicId: 'customer-1',
  firstName: 'Asha',
  lastName: 'Sharma',
  dateOfBirth: null,
  gender: null,
  alternateMobile: null,
  customerNumber: 'CUST/0001',
);

class _ScriptedWalletController extends WalletController {
  _ScriptedWalletController(this.initialState, {this.bankDetail, this.bankDetailError});

  final WalletState initialState;
  final CustomerBankDetail? bankDetail;
  final String? bankDetailError;
  var bankDetailChecks = 0;
  final submittedAmounts = <double>[];

  @override
  WalletState build() => initialState;

  @override
  Future<void> load() async {}

  @override
  Future<CustomerBankDetail?> fetchBankDetail() async {
    bankDetailChecks++;
    if (bankDetailError != null) throw ApiException(500, 'ERROR', bankDetailError!);
    return bankDetail;
  }

  @override
  Future<String?> submitWithdrawal(double amount) async {
    submittedAmounts.add(amount);
    state = state.copyWith(isSaving: false);
    return 'Withdrawal request WD/000001 is pending review.';
  }
}

GoRouter _walletRouter() => GoRouter(
  initialLocation: '/wallet',
  routes: [
    GoRoute(
      path: '/wallet',
      builder: (context, state) => const WalletScreen(),
    ),
    GoRoute(
      path: '/customer/bank-detail',
      builder: (context, state) =>
          const Scaffold(body: Text('Bank detail target')),
    ),
  ],
);

Future<void> _pumpWallet(
  WidgetTester tester, {
  required _ScriptedWalletController controller,
}) async {
  final router = _walletRouter();
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
        walletControllerProvider.overrideWith(() => controller),
      ],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _pumpAccount(
  WidgetTester tester, {
  required CustomerState state,
}) async {
  final router = GoRouter(
    initialLocation: '/customer/account',
    routes: [
      GoRoute(
        path: '/customer/account',
        builder: (context, state) => const CustomerOverviewScreen(),
      ),
      GoRoute(
        path: '/customer/bank-detail',
        builder: (context, state) =>
            const Scaffold(body: Text('Bank detail target')),
      ),
    ],
  );
  addTearDown(router.dispose);
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
      orderControllerProvider.overrideWith(() => _StubOrderController()),
      customerControllerProvider.overrideWith(
        () => _SeededCustomerController(state),
      ),
    ],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
}

class _SeededCustomerController extends CustomerController {
  _SeededCustomerController(this.initialState);

  final CustomerState initialState;

  @override
  CustomerState build() => initialState;

  @override
  Future<void> load() async {}
}

void _useSurface(WidgetTester tester, Size size) {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = size;
  addTearDown(tester.view.reset);
}

class _RecordingCustomerRepository extends CustomerRepository {
  _RecordingCustomerRepository()
    : super(
        api: ApiClient(
          client: MockClient((request) async => _ok(_bankDetailJson())),
          baseUrl: 'https://test',
        ),
      );

  final savedDrafts = <BankDetailDraft>[];

  @override
  Future<CustomerBankDetail> saveBankDetail(
    String token,
    BankDetailDraft request,
  ) async {
    savedDrafts.add(request);
    return _bankDetail();
  }
}
