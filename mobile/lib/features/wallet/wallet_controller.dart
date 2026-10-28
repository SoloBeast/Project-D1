import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_gateway_contract.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'wallet_models.dart';
import 'wallet_repository.dart';

final walletRepositoryProvider = Provider<WalletRepository>(
  (ref) => WalletRepository(api: authenticatedApiClient(ref)),
);

final walletControllerProvider =
    NotifierProvider<WalletController, WalletState>(WalletController.new);

class WalletState {
  const WalletState({
    this.wallet,
    this.transactions = const <WalletTransaction>[],
    this.withdrawals = const <WithdrawalRequest>[],
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
  });

  final WalletDetails? wallet;
  final List<WalletTransaction> transactions;
  final List<WithdrawalRequest> withdrawals;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;

  WalletState copyWith({
    WalletDetails? wallet,
    List<WalletTransaction>? transactions,
    List<WithdrawalRequest>? withdrawals,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    bool clearError = false,
  }) => WalletState(
    wallet: wallet ?? this.wallet,
    transactions: transactions ?? this.transactions,
    withdrawals: withdrawals ?? this.withdrawals,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class WalletController extends Notifier<WalletState> {
  WalletRepository get _repository => ref.read(walletRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  WalletState build() => const WalletState();

  /// Runs the wallet top-up checkout flow:
  /// 1. Create a pending Razorpay wallet-top-up payment on the backend.
  /// 2. Open the Razorpay checkout for the created payment.
  /// 3. Verify the payment (order id, payment id, signature) with the backend.
  /// 4. Only after the backend confirms success and credits the wallet,
  ///    refresh the wallet balance and transaction ledger.
  ///
  /// Returns a user-facing message describing the outcome (or null when no
  /// action was taken because the session or amount was invalid).
  Future<String?> topUp(double amount) async {
    final token = _token;
    if (token == null || amount <= 0 || state.isSaving) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final key = 'wallet-top-up-${DateTime.now().microsecondsSinceEpoch}';
      final payment = await _repository.topUp(
        token: token,
        amount: amount,
        idempotencyKey: key,
      );

      if (payment.usesRazorpay && payment.status.isPending) {
        try {
          final callback = await ref
              .read(paymentGatewayLauncherProvider)
              .open(payment);
          final verifyError = await _verifyPayment(
            token: token,
            payment: payment,
            callback: callback,
          );
          if (verifyError == null) {
            await load();
            state = state.copyWith(isSaving: false);
            return '₹${payment.amount.toStringAsFixed(2)} added to your wallet.';
          }
          return verifyError;
        } on PaymentGatewayException catch (error) {
          await _cancelAfterGatewayFailure(payment, error.message);
          return error.message;
        }
      }

      // The backend returned a terminal or non-Razorpay payment at creation.
      state = state.copyWith(isSaving: false);
      if (payment.status.isSuccessful) {
        await load();
        return '₹${payment.amount.toStringAsFixed(2)} added to your wallet.';
      }
      final message = payment.failureMessage ?? _topUpFailedMessage;
      state = state.copyWith(errorMessage: message);
      return message;
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
      return error.message;
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
      return _offlineMessage;
    }
  }

  /// Verifies the gateway callback with the backend. The backend validates the
  /// order id, payment id and signature, confirms the payment target, and
  /// credits the wallet atomically on success. Returns null on success, or a
  /// user-facing error message.
  Future<String?> _verifyPayment({
    required String token,
    required PaymentDetails payment,
    required PaymentGatewayCallback callback,
  }) async {
    try {
      final verified = await ref.read(paymentRepositoryProvider).verify(
        token: token,
        paymentId: payment.publicId,
        gatewayOrderId: callback.gatewayOrderId,
        gatewayPaymentId: callback.gatewayPaymentId,
        signature: callback.signature,
      );
      if (!verified.status.isSuccessful) {
        final message =
            verified.failureMessage ?? _topUpFailedMessage;
        state = state.copyWith(isSaving: false, errorMessage: message);
        return message;
      }
      return null;
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
      return error.message;
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
      return _offlineMessage;
    }
  }

  /// Persists cancellation when the customer abandons the Razorpay checkout,
  /// then surfaces the gateway-provided message.
  Future<void> _cancelAfterGatewayFailure(
    PaymentDetails payment,
    String gatewayMessage,
  ) async {
    final token = _token;
    if (token != null) {
      try {
        await ref.read(paymentRepositoryProvider).cancel(
          token: token,
          paymentId: payment.publicId,
        );
      } on Object {
        // Preserve the gateway message if cancellation cannot be persisted.
      }
    }
    state = state.copyWith(isSaving: false, errorMessage: gatewayMessage);
  }

  /// Re-reads the authoritative bank-details state from the server. Throws
  /// [ApiException] when the check itself fails so the UI can show a
  /// retryable error instead of silently allowing withdrawal.
  Future<CustomerBankDetail?> fetchBankDetail() async {
    final token = _token;
    if (token == null) return null;
    return _repository.getBankDetail(token);
  }

  /// Submits a withdrawal request after the UI has verified bank details,
  /// amount, and explicit confirmation. The backend revalidates everything.
  /// Returns a user-facing message describing the outcome.
  Future<String?> submitWithdrawal(double amount) async {
    final token = _token;
    if (token == null || amount <= 0 || state.isSaving) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final key = 'wallet-withdrawal-${DateTime.now().microsecondsSinceEpoch}';
      final request = await _repository.submitWithdrawal(
        token: token,
        amount: amount,
        idempotencyKey: key,
      );
      await load();
      state = state.copyWith(isSaving: false);
      return 'Withdrawal request ${request.requestNumber} is pending review.';
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
      return error.message;
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
      return _offlineMessage;
    }
  }

  Future<void> load() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final results = await Future.wait([
        _repository.get(token),
        _repository.getTransactions(token),
        _repository.getWithdrawals(token),
      ]);
      state = state.copyWith(
        wallet: results[0] as WalletDetails,
        transactions: results[1] as List<WalletTransaction>,
        withdrawals: results[2] as List<WithdrawalRequest>,
        isLoading: false,
      );
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
  }

}

const _topUpFailedMessage =
    'Wallet top-up could not be completed. Please try again.';
const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
