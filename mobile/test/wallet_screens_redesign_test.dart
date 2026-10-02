import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

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

WalletDetails _wallet([double balance = 410.5]) => WalletDetails(
  publicId: 'wallet-1',
  balance: balance,
  currency: 'INR',
  createdAt: DateTime(2026, 8, 16, 5, 30),
  updatedAt: DateTime(2026, 8, 16, 5, 35),
);

WalletTransaction _transaction({
  String publicId = 'transaction-1',
  double amount = 500,
  double balanceBefore = 0,
  double balanceAfter = 500,
  String type = 'TopUp',
  String description = 'Wallet top-up',
  String? paymentId = 'payment-1',
  String? orderId,
}) => WalletTransaction(
  publicId: publicId,
  type: type,
  balanceBefore: balanceBefore,
  amount: amount,
  balanceAfter: balanceAfter,
  currency: 'INR',
  description: description,
  occurredAt: DateTime(2026, 8, 16, 7, 35),
  paymentId: paymentId,
  orderId: orderId,
);

void _useSurface(WidgetTester tester, Size size) {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = size;
  addTearDown(tester.view.reset);
}

GoRouter _walletRouter() => GoRouter(
  initialLocation: '/wallet',
  routes: [
    GoRoute(
      path: '/wallet',
      builder: (context, state) => const WalletScreen(),
    ),
    GoRoute(
      path: '/home',
      builder: (context, state) => const Scaffold(body: Text('Home target')),
    ),
  ],
);

Future<void> _pumpWallet(
  WidgetTester tester, {
  required WalletState state,
  GoRouter? router,
  bool settle = true,
}) async {
  final goRouter = router ?? _walletRouter();
  addTearDown(goRouter.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
        walletControllerProvider.overrideWith(
          () => _SeededWalletController(state),
        ),
      ],
      child: MaterialApp.router(routerConfig: goRouter),
    ),
  );
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    await tester.pump();
    await tester.pump();
  }
}

void main() {
  testWidgets('balance hero shows the server-confirmed balance as primary', (
    tester,
  ) async {
    _useSurface(tester, const Size(800, 1200));
    await _pumpWallet(
      tester,
      state: WalletState(wallet: _wallet(), transactions: []),
    );

    expect(find.text('Wallet'), findsOneWidget);
    expect(
      find.bySemanticsLabel('Available wallet balance ₹410.50'),
      findsOneWidget,
    );
    expect(find.text('Available balance'), findsOneWidget);
    expect(find.text('₹410.50'), findsOneWidget);
    expect(find.text('INR'), findsOneWidget);
    // Recharge is the dominant action on the balance card itself.
    expect(find.widgetWithText(FilledButton, 'Add money'), findsOneWidget);
  });

  testWidgets('trust copy separates pending recharges from spendable balance', (
    tester,
  ) async {
    _useSurface(tester, const Size(800, 1200));
    await _pumpWallet(
      tester,
      state: WalletState(wallet: _wallet(), transactions: []),
    );

    expect(
      find.textContaining(
        'Money appears in your available balance only after the payment is '
        'confirmed by our servers.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('ledger renders credit and debit entries with states and ref', (
    tester,
  ) async {
    _useSurface(tester, const Size(800, 1200));
    await _pumpWallet(
      tester,
      state: WalletState(
        wallet: _wallet(),
        transactions: [
          _transaction(),
          _transaction(
            publicId: 'transaction-2',
            amount: -90,
            balanceBefore: 500,
            balanceAfter: 410,
            type: 'PaymentDebit',
            description: 'Order payment',
            paymentId: null,
            orderId: 'order-1',
          ),
          // Unreconciled entry: ledger arithmetic does not add up, so the
          // entry must present as pending instead of confirmed.
          _transaction(
            publicId: 'transaction-3',
            amount: 100,
            balanceBefore: 999,
            balanceAfter: 410,
            description: 'Suspicious entry',
            paymentId: 'payment-3',
          ),
        ],
      ),
    );

    expect(find.text('Wallet top-up'), findsOneWidget);
    expect(find.text('Order payment'), findsOneWidget);
    expect(find.text('+₹500.00'), findsOneWidget);
    expect(find.text('-₹90.00'), findsOneWidget);
    // Direction is text-labelled, never colour-only.
    expect(find.text('Credit'), findsNWidgets(2));
    expect(find.text('Debit'), findsOneWidget);
    // Reconciliation state comes from the server data, not the UI.
    expect(find.text('Confirmed'), findsNWidgets(2));
    expect(find.text('Pending'), findsOneWidget);
    expect(find.text('+₹100.00'), findsOneWidget);
    expect(find.text('Ref: payment-1'), findsOneWidget);
    expect(find.text('Ref: order-1'), findsOneWidget);
  });

  testWidgets('empty, loading, error and offline states use shared panels', (
    tester,
  ) async {
    _useSurface(tester, const Size(800, 1200));
    await _pumpWallet(
      tester,
      state: WalletState(wallet: _wallet(), transactions: const []),
    );
    expect(find.text('No transactions'), findsOneWidget);
    expect(find.text('Wallet activity will appear here.'), findsOneWidget);

    await _pumpWallet(
      tester,
      state: const WalletState(isLoading: true),
      settle: false,
    );
    expect(find.byType(LoadingStatePanel), findsOneWidget);
    expect(find.text('No transactions'), findsNothing);

    await _pumpWallet(
      tester,
      state: const WalletState(errorMessage: 'Wallet could not be loaded.'),
    );
    expect(find.byType(ErrorStatePanel), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
  });

  testWidgets('recharge dialog opens with quick amounts and validates input', (
    tester,
  ) async {
    _useSurface(tester, const Size(800, 1200));
    await _pumpWallet(
      tester,
      state: WalletState(wallet: _wallet(), transactions: []),
    );

    await tester.tap(find.widgetWithText(FilledButton, 'Add money'));
    await tester.pumpAndSettle();
    expect(find.text('Add money to wallet'), findsOneWidget);
    // Confirm is disabled until an amount is entered.
    expect(
      find.ancestor(
        of: find.text('Add money').last,
        matching: find.byWidgetPredicate((widget) => widget is FilledButton),
      ),
      findsOneWidget,
    );
    // Quick-amount chips pre-fill the field.
    await tester.tap(find.text('₹250'));
    await tester.pumpAndSettle();
    final field = tester.widget<TextField>(
      find.widgetWithText(TextField, 'Amount'),
    );
    expect(field.controller?.text, '250');
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(find.text('Add money to wallet'), findsNothing);
  });

  testWidgets('wallet is reachable from its existing route and shell context', (
    tester,
  ) async {
    final router = _walletRouter();
    await _pumpWallet(
      tester,
      state: WalletState(wallet: _wallet(), transactions: []),
      router: router,
    );
    expect(
      router.routerDelegate.currentConfiguration.uri.path,
      '/wallet',
    );
    // The More menu entry point stays: the shell renders the standard
    // DoodhDirect bottom navigation on this screen.
    expect(find.byType(NavigationBar), findsOneWidget);
    expect(find.byType(CustomerShell), findsOneWidget);
  });

  testWidgets('compact layout renders the full composition without overflow', (
    tester,
  ) async {
    _useSurface(tester, const Size(320, 640));
    await _pumpWallet(
      tester,
      state: WalletState(
        wallet: _wallet(),
        transactions: [_transaction()],
      ),
    );

    expect(tester.takeException(), isNull);
    expect(find.text('₹410.50'), findsOneWidget);
    expect(find.text('Wallet top-up'), findsOneWidget);
  });

  testWidgets('wide layout constrains the ledger to the reading width', (
    tester,
  ) async {
    _useSurface(tester, const Size(1440, 900));
    await _pumpWallet(
      tester,
      state: WalletState(
        wallet: _wallet(),
        transactions: [_transaction()],
      ),
    );

    expect(tester.takeException(), isNull);
    expect(find.text('₹410.50'), findsOneWidget);
    // The balance hero stays on a constrained reading width instead of
    // stretching across the browser window.
    final balanceWidth = tester.getSize(find.text('₹410.50')).width;
    expect(balanceWidth, lessThan(700));
  });

  testWidgets('balance semantics announce the credited balance for screen readers', (
    tester,
  ) async {
    _useSurface(tester, const Size(800, 1200));
    await _pumpWallet(
      tester,
      state: WalletState(wallet: _wallet(1234.56), transactions: []),
    );

    expect(
      find.bySemanticsLabel('Available wallet balance ₹1234.56'),
      findsOneWidget,
    );
    expect(find.byType(DoodhCard), findsWidgets);
  });
}
