import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'wallet_models.dart';
import 'wallet_repository.dart';

final walletRepositoryProvider = Provider<WalletRepository>(
  (ref) => WalletRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final walletControllerProvider =
    NotifierProvider<WalletController, WalletState>(WalletController.new);

class WalletState {
  const WalletState({
    this.wallet,
    this.transactions = const <WalletTransaction>[],
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
  });

  final WalletDetails? wallet;
  final List<WalletTransaction> transactions;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;

  WalletState copyWith({
    WalletDetails? wallet,
    List<WalletTransaction>? transactions,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    bool clearError = false,
  }) => WalletState(
    wallet: wallet ?? this.wallet,
    transactions: transactions ?? this.transactions,
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

  Future<void> load() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final results = await Future.wait([
        _repository.get(token),
        _repository.getTransactions(token),
      ]);
      state = state.copyWith(
        wallet: results[0] as WalletDetails,
        transactions: results[1] as List<WalletTransaction>,
        isLoading: false,
      );
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isLoading: false, errorMessage: _offlineMessage);
    }
  }

  Future<bool> topUp(double amount) async {
    final token = _token;
    if (token == null || amount <= 0) return false;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      await _repository.topUp(
        token: token,
        amount: amount,
        idempotencyKey:
            'mobile-wallet-topup-${DateTime.now().microsecondsSinceEpoch}',
      );
      await load();
      state = state.copyWith(isSaving: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(isSaving: false, errorMessage: _offlineMessage);
    }
    return false;
  }
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
