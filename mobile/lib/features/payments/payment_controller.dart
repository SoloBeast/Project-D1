import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'payment_gateway_launcher.dart';
import 'payment_models.dart';
import 'payment_repository.dart';

final paymentRepositoryProvider = Provider<PaymentRepository>(
  (ref) => PaymentRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final paymentGatewayLauncherProvider = Provider<PaymentGatewayLauncher>(
  (ref) => createPaymentGatewayLauncher(),
);

final paymentControllerProvider =
    NotifierProvider<PaymentController, PaymentState>(PaymentController.new);

class PaymentState {
  const PaymentState({
    this.payment,
    this.capabilities = const [],
    this.selectedMethod = PaymentMethod.wallet,
    this.isLoading = false,
    this.errorMessage,
  });

  final PaymentDetails? payment;
  final List<PaymentCapability> capabilities;
  final PaymentMethod selectedMethod;
  final bool isLoading;
  final String? errorMessage;

  PaymentState copyWith({
    PaymentDetails? payment,
    List<PaymentCapability>? capabilities,
    PaymentMethod? selectedMethod,
    bool? isLoading,
    String? errorMessage,
    bool clearPayment = false,
    bool clearError = false,
  }) => PaymentState(
    payment: clearPayment ? null : payment ?? this.payment,
    capabilities: capabilities ?? this.capabilities,
    selectedMethod: selectedMethod ?? this.selectedMethod,
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
      final available = capabilities.where((item) => item.isAvailable).toList();
      final selected = available.any((item) => item.method == state.selectedMethod)
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

  void adopt(PaymentDetails payment) {
    state = state.copyWith(
      payment: payment,
      selectedMethod: payment.method,
      isLoading: false,
      clearError: true,
    );
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
    final token = _token;
    final current = state.payment;
    if (token == null ||
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
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
    return false;
  }

  Future<bool> completeDevelopment() async {
    final token = _token;
    final current = state.payment;
    if (token == null || current == null || !current.usesDevelopmentMock) {
      return false;
    }

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final payment = await _repository.completeDevelopment(
        token: token,
        paymentId: current.publicId,
      );
      state = state.copyWith(payment: payment, isLoading: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
    return false;
  }
}

String _paymentErrorMessage(ApiException error) {
  if (error.code == 'INSUFFICIENT_WALLET_BALANCE') {
    return error.message;
  }

  return error.message;
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
