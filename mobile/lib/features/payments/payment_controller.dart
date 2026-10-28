import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'payment_gateway_launcher.dart';
import 'payment_models.dart';
import 'payment_repository.dart';

final paymentRepositoryProvider = Provider<PaymentRepository>(
  (ref) => PaymentRepository(api: authenticatedApiClient(ref)),
);

final paymentGatewayLauncherProvider = Provider<PaymentGatewayLauncher>(
  (ref) => createPaymentGatewayLauncher(),
);

final paymentControllerProvider =
    NotifierProvider<PaymentController, PaymentState>(PaymentController.new);

class PaymentState {
  const PaymentState({
    this.payment,
    this.plan,
    this.capabilities = const [],
    this.selectedMethod = PaymentMethod.wallet,
    this.useWalletBalance = false,
    this.isLoading = false,
    this.errorMessage,
  });

  final PaymentDetails? payment;
  final PaymentPlan? plan;
  final List<PaymentCapability> capabilities;
  final PaymentMethod selectedMethod;
  /// Wallet-first opt-in. Defaults OFF per business rules; the server
  /// computes every amount and the UI only displays the confirmed breakdown.
  final bool useWalletBalance;
  final bool isLoading;
  final String? errorMessage;

  PaymentState copyWith({
    PaymentDetails? payment,
    PaymentPlan? plan,
    List<PaymentCapability>? capabilities,
    PaymentMethod? selectedMethod,
    bool? useWalletBalance,
    bool? isLoading,
    String? errorMessage,
    bool clearPayment = false,
    bool clearPlan = false,
    bool clearError = false,
  }) => PaymentState(
    payment: clearPayment ? null : payment ?? this.payment,
    plan: clearPlan ? null : plan ?? this.plan,
    capabilities: capabilities ?? this.capabilities,
    selectedMethod: selectedMethod ?? this.selectedMethod,
    useWalletBalance: useWalletBalance ?? this.useWalletBalance,
    isLoading: isLoading ?? this.isLoading,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class PaymentController extends Notifier<PaymentState> {
  PaymentRepository get _repository => ref.read(paymentRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  PaymentState build() {
    Future.microtask(loadCapabilities);
    return const PaymentState();
  }

  Future<bool> loadCapabilities() async {
    final token = _token;
    if (token == null) return false;
    try {
      final capabilities = await _repository.getCapabilities(token);
      // The build-time fetch can outlive the provider (rebuild/dispose).
      if (!ref.mounted) return false;
      final available = capabilities.where((item) => item.isAvailable).toList();
      final selected =
          available.any((item) => item.method == state.selectedMethod)
          ? state.selectedMethod
          : available.firstOrNull?.method ?? PaymentMethod.wallet;
      state = state.copyWith(
        capabilities: capabilities,
        selectedMethod: selected,
        clearError: true,
      );
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(errorMessage: error.message);
    } on Object {
      state = state.copyWith(errorMessage: _offlineMessage);
    }
    return false;
  }

  void selectMethod(PaymentMethod method) {
    state = state.copyWith(selectedMethod: method, clearError: true);
  }

  /// Wallet-first opt-in toggle. Defaults OFF; switching starts a fresh
  /// plan attempt (previous attempt keys are terminal-scoped server-side).
  void setUseWalletBalance(bool value) {
    if (state.isLoading) return;
    state = state.copyWith(
      useWalletBalance: value,
      clearPlan: true,
      clearError: true,
    );
  }

  void adopt(PaymentDetails payment) {
    state = state.copyWith(
      payment: payment,
      selectedMethod: payment.method,
      isLoading: false,
      clearError: true,
    );
  }

  /// Stable idempotency key for the current plan attempt. Reused across
  /// network retries for the same attempt; a new key is minted only when
  /// starting a fresh attempt after the previous plan reached a terminal
  /// state (caller passes [forceNewAttempt] after verifying terminality).
  String? _planAttemptKey;
  String _planKeyFor(String orderId, {bool forceNewAttempt = false}) {
    if (forceNewAttempt || _planAttemptKey == null) {
      _planAttemptKey =
          'mobile-plan-$orderId-${DateTime.now().microsecondsSinceEpoch}';
    }
    return _planAttemptKey!;
  }

  void startNewPlanAttempt() {
    _planAttemptKey = null;
    state = state.copyWith(clearPlan: true, clearError: true);
  }

  /// Creates the server-authoritative split plan for an order and stores
  /// the confirmed breakdown. Never shows success here: callers must wait
  /// for backend-confirmed component states before navigating to success.
  Future<PaymentPlan?> createPlanForOrder(
    String orderId, {
    bool forceNewAttempt = false,
  }) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final plan = await _repository.createPlan(
        token: token,
        orderId: orderId,
        useWalletBalance: state.useWalletBalance,
        idempotencyKey: _planKeyFor(orderId, forceNewAttempt: forceNewAttempt),
      );
      if (!ref.mounted) return null;
      state = state.copyWith(
        plan: plan,
        payment: plan.payment,
        selectedMethod: plan.payment.method,
        isLoading: false,
      );
      return plan;
    } on ApiException catch (error) {
      // Sub-₹1 split rejection preserves checkout state: keep the opt-in
      // value and plan key so the customer can toggle OFF and retry with
      // the full Razorpay amount, or top up the wallet.
      state = state.copyWith(
        isLoading: false,
        errorMessage: _paymentErrorMessage(error),
      );
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
    return null;
  }

  Future<bool> createForOrder(String orderId) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final attemptId = DateTime.now().microsecondsSinceEpoch;
      final payment = await _repository.create(
        token: token,
        orderId: orderId,
        method: state.selectedMethod,
        idempotencyKey:
            'mobile-payment-$orderId-'
            '${state.selectedMethod.apiValue.toLowerCase()}-$attemptId',
        useWalletBalance: state.useWalletBalance,
      );
      state = state.copyWith(payment: payment, isLoading: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isLoading: false,
        errorMessage: _paymentErrorMessage(error),
      );
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
    return false;
  }

  Future<bool> load(String paymentId) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final payment = await _repository.get(token, paymentId);
      state = state.copyWith(payment: payment, isLoading: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
    return false;
  }

  Future<bool> refresh() {
    final current = state.payment;
    return current == null ? Future.value(false) : load(current.publicId);
  }

  Future<bool> openRazorpayAndVerify() async {
    final current = state.payment;
    if (_token == null ||
        current == null ||
        !current.usesRazorpay ||
        !current.status.isPending) {
      return false;
    }

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final callback = await ref
          .read(paymentGatewayLauncherProvider)
          .open(current);
      final token = _token;
      if (token == null) {
        state = state.copyWith(isLoading: false);
        return false;
      }
      final payment = await _repository.verify(
        token: token,
        paymentId: current.publicId,
        gatewayOrderId: callback.gatewayOrderId,
        gatewayPaymentId: callback.gatewayPaymentId,
        signature: callback.signature,
      );
      state = state.copyWith(payment: payment, isLoading: false);
      return true;
    } on PaymentGatewayException catch (error) {
      final token = _token;
      if (token != null) {
        try {
          final cancelled = await _repository.cancel(
            token: token,
            paymentId: current.publicId,
          );
          state = state.copyWith(
            payment: cancelled,
            isLoading: false,
            errorMessage: error.message,
          );
          return false;
        } on Object {
          // Preserve the gateway message if cancellation cannot be persisted.
        }
      }
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
    return false;
  }

}

String _paymentErrorMessage(ApiException error) {
  if (error.code == 'INSUFFICIENT_WALLET_BALANCE' ||
      error.code == 'WALLET_SPLIT_REMAINDER_BELOW_MINIMUM') {
    return error.message;
  }

  return error.message;
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
