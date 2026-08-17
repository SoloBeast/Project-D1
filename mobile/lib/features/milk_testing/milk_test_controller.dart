import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'milk_test_models.dart';
import 'milk_test_repository.dart';

final milkTestRepositoryProvider = Provider<MilkTestRepository>(
  (ref) => MilkTestRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final milkTestControllerProvider =
    NotifierProvider<MilkTestController, MilkTestState>(MilkTestController.new);

class MilkTestState {
  const MilkTestState({
    this.customerTest,
    this.staffTest,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.errorMessage,
  });

  final CustomerMilkTest? customerTest;
  final StaffMilkTest? staffTest;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final bool isUnauthorized;
  final String? errorMessage;

  MilkTestState copyWith({
    CustomerMilkTest? customerTest,
    bool clearCustomerTest = false,
    StaffMilkTest? staffTest,
    bool clearStaffTest = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    bool? isUnauthorized,
    String? errorMessage,
    bool clearError = false,
  }) => MilkTestState(
    customerTest: clearCustomerTest ? null : customerTest ?? this.customerTest,
    staffTest: clearStaffTest ? null : staffTest ?? this.staffTest,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    isUnauthorized: isUnauthorized ?? this.isUnauthorized,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class MilkTestController extends Notifier<MilkTestState> {
  MilkTestRepository get _repository => ref.read(milkTestRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  MilkTestState build() => const MilkTestState();

  Future<void> loadForCustomer(String deliveryId) => _load(() async {
    final test = await _repository.getForCustomer(_token!, deliveryId);
    state = state.copyWith(
      customerTest: test,
      clearCustomerTest: test == null,
      clearStaffTest: true,
    );
  });

  Future<void> loadForStaff(String deliveryId) => _load(() async {
    final test = await _repository.getForStaff(_token!, deliveryId);
    state = state.copyWith(
      staffTest: test,
      clearStaffTest: test == null,
      clearCustomerTest: true,
    );
  });

  Future<bool> request(String deliveryId) => _save(() async {
    final test = await _repository.request(_token!, deliveryId);
    state = state.copyWith(customerTest: test, clearStaffTest: true);
  });

  Future<bool> uploadImage(
    String deliveryId,
    String milkTestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) => _save(() async {
    await _repository.uploadImage(
      _token!,
      milkTestId,
      bytes: bytes,
      fileName: fileName,
      contentType: contentType,
    );
    final test = await _repository.getForStaff(_token!, deliveryId);
    state = state.copyWith(staffTest: test, clearStaffTest: test == null);
  });

  Future<bool> complete(
    String milkTestId, {
    required List<MilkTestParameter> parameters,
    String? remarks,
  }) => _save(() async {
    final test = await _repository.complete(
      _token!,
      milkTestId,
      parameters: parameters,
      remarks: remarks,
    );
    state = state.copyWith(staffTest: test);
  });

  Future<bool> confirm(String milkTestId, {String? remarks}) =>
      _decide(milkTestId, confirmDecision: true, remarks: remarks);

  Future<bool> reject(String milkTestId, {String? remarks}) =>
      _decide(milkTestId, confirmDecision: false, remarks: remarks);

  Future<bool> _decide(
    String milkTestId, {
    required bool confirmDecision,
    String? remarks,
  }) => _save(() async {
    final test = confirmDecision
        ? await _repository.confirm(_token!, milkTestId, remarks: remarks)
        : await _repository.reject(_token!, milkTestId, remarks: remarks);
    state = state.copyWith(customerTest: test);
  });

  Future<void> _load(Future<void> Function() operation) async {
    if (_token == null) return;
    state = state.copyWith(
      isLoading: true,
      isOffline: false,
      isUnauthorized: false,
      clearError: true,
    );
    try {
      await operation();
      state = state.copyWith(isLoading: false);
    } on Object catch (error) {
      _setFailure(error);
    }
  }

  Future<bool> _save(Future<void> Function() operation) async {
    if (_token == null) return false;
    state = state.copyWith(
      isSaving: true,
      isOffline: false,
      isUnauthorized: false,
      clearError: true,
    );
    try {
      await operation();
      state = state.copyWith(isSaving: false);
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  void clearError() => state = state.copyWith(clearError: true);

  void _setFailure(Object error, {bool saving = false}) {
    final isApiError = error is ApiException;
    final isUnauthorized =
        isApiError && (error.statusCode == 401 || error.statusCode == 403);
    state = state.copyWith(
      isLoading: saving ? state.isLoading : false,
      isSaving: saving ? false : state.isSaving,
      isOffline: !isApiError,
      isUnauthorized: isUnauthorized,
      errorMessage: isApiError ? error.message : _offlineMessage,
    );
  }
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
