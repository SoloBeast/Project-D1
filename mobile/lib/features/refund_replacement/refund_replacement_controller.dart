import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'refund_replacement_models.dart';
import 'refund_replacement_repository.dart';

final refundReplacementRepositoryProvider = Provider<RefundReplacementRepository>(
  (ref) => RefundReplacementRepository(api: authenticatedApiClient(ref)),
);

final refundReplacementControllerProvider =
    NotifierProvider<RefundReplacementController, RefundReplacementState>(
      RefundReplacementController.new,
    );

class RefundReplacementState {
  const RefundReplacementState({
    this.eligibility,
    this.customerRequests = const [],
    this.selectedCustomerRequest,
    this.staffPage,
    this.selectedStaffRequest,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.errorMessage,
  });

  final RefundReplacementEligibility? eligibility;
  final List<RefundReplacementRequest> customerRequests;
  final RefundReplacementRequest? selectedCustomerRequest;
  final RefundReplacementPage? staffPage;
  final RefundReplacementRequest? selectedStaffRequest;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final bool isUnauthorized;
  final String? errorMessage;

  RefundReplacementState copyWith({
    RefundReplacementEligibility? eligibility,
    bool clearEligibility = false,
    List<RefundReplacementRequest>? customerRequests,
    RefundReplacementRequest? selectedCustomerRequest,
    bool clearSelectedCustomerRequest = false,
    RefundReplacementPage? staffPage,
    RefundReplacementRequest? selectedStaffRequest,
    bool clearSelectedStaffRequest = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    bool? isUnauthorized,
    String? errorMessage,
    bool clearError = false,
  }) => RefundReplacementState(
    eligibility: clearEligibility ? null : eligibility ?? this.eligibility,
    customerRequests: customerRequests ?? this.customerRequests,
    selectedCustomerRequest: clearSelectedCustomerRequest
        ? null
        : selectedCustomerRequest ?? this.selectedCustomerRequest,
    staffPage: staffPage ?? this.staffPage,
    selectedStaffRequest: clearSelectedStaffRequest
        ? null
        : selectedStaffRequest ?? this.selectedStaffRequest,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    isUnauthorized: isUnauthorized ?? this.isUnauthorized,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class RefundReplacementController extends Notifier<RefundReplacementState> {
  RefundReplacementRepository get _repository =>
      ref.read(refundReplacementRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  RefundReplacementState build() => const RefundReplacementState();

  Future<void> loadEligibility(String deliveryId) => _load(() async {
    final eligibility = await _repository.getEligibility(_token!, deliveryId);
    state = state.copyWith(
      eligibility: eligibility,
      clearSelectedCustomerRequest: true,
      clearSelectedStaffRequest: true,
    );
  });

  Future<bool> submitRequest(
    String deliveryId, {
    required RefundReplacementType type,
    required String reason,
    String? remarks,
    String? milkTestId,
  }) => _save(() async {
    final request = await _repository.submit(
      _token!,
      deliveryId,
      type: type,
      reason: reason,
      remarks: remarks,
      milkTestId: milkTestId,
    );
    state = state.copyWith(
      eligibility: null,
      selectedCustomerRequest: request,
    );
  });

  Future<void> loadCustomerRequests() => _load(() async {
    final requests = await _repository.listForCustomer(_token!);
    state = state.copyWith(
      customerRequests: requests,
      clearEligibility: true,
      clearSelectedCustomerRequest: true,
      clearSelectedStaffRequest: true,
    );
  });

  Future<void> loadCustomerRequest(String requestId) => _load(() async {
    final request = await _repository.getForCustomer(_token!, requestId);
    state = state.copyWith(
      selectedCustomerRequest: request,
      clearSelectedCustomerRequest: request == null,
    );
  });

  Future<bool> uploadImage(
    String requestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) => _save(() async {
    final image = await _repository.uploadImage(
      _token!,
      requestId,
      bytes: bytes,
      fileName: fileName,
      contentType: contentType,
    );
    final request = state.selectedCustomerRequest;
    if (request != null) {
      state = state.copyWith(
        selectedCustomerRequest: RefundReplacementRequest(
          requestId: request.requestId,
          requestNumber: request.requestNumber,
          orderId: request.orderId,
          orderPublicId: request.orderPublicId,
          orderNumber: request.orderNumber,
          deliveryId: request.deliveryId,
          deliveryNumber: request.deliveryNumber,
          customerId: request.customerId,
          customerName: request.customerName,
          customerMobile: request.customerMobile,
          branchId: request.branchId,
          branchCode: request.branchCode,
          branchName: request.branchName,
          milkTestId: request.milkTestId,
          milkTestPublicId: request.milkTestPublicId,
          type: request.type,
          source: request.source,
          status: request.status,
          reason: request.reason,
          remarks: request.remarks,
          submittedAtUtc: request.submittedAtUtc,
          deadlineUtc: request.deadlineUtc,
          decidedByUserId: request.decidedByUserId,
          decidedByName: request.decidedByName,
          decidedAtUtc: request.decidedAtUtc,
          decisionRemarks: request.decisionRemarks,
          completedByUserId: request.completedByUserId,
          completedByName: request.completedByName,
          completedAtUtc: request.completedAtUtc,
          completionRemarks: request.completionRemarks,
          proofImageRequired: request.proofImageRequired,
          images: [...request.images, image],
        ),
      );
    }
  });

  Future<void> loadStaffPage({
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) => _load(() async {
    final result = await _repository.listForStaff(
      _token!,
      branchId: branchId,
      status: status,
      page: page,
      pageSize: pageSize,
    );
    state = state.copyWith(
      staffPage: result,
      clearSelectedStaffRequest: true,
      clearEligibility: true,
    );
  });

  Future<void> loadStaffRequest(String requestId) => _load(() async {
    final request = await _repository.getForStaff(_token!, requestId);
    state = state.copyWith(selectedStaffRequest: request);
  });

  Future<bool> approve(String requestId, {String? remarks}) =>
      _decide(requestId, 'approve', remarks);

  Future<bool> reject(String requestId, {String? remarks}) =>
      _decide(requestId, 'reject', remarks);

  Future<bool> complete(String requestId, {String? remarks}) =>
      _decide(requestId, 'complete', remarks);

  Future<bool> _decide(
    String requestId,
    String decision,
    String? remarks,
  ) => _save(() async {
    final request = switch (decision) {
      'approve' => await _repository.approve(
          _token!,
          requestId,
          remarks: remarks,
        ),
      'reject' => await _repository.reject(
          _token!,
          requestId,
          remarks: remarks,
        ),
      _ => await _repository.complete(
          _token!,
          requestId,
          remarks: remarks,
        ),
    };
    state = state.copyWith(selectedStaffRequest: request);
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
    final isNetworkError = error is ApiNetworkException;
    final isApiError = error is ApiException;
    final isUnauthorized =
        isApiError && (error.statusCode == 401 || error.statusCode == 403);
    state = state.copyWith(
      isLoading: saving ? state.isLoading : false,
      isSaving: saving ? false : state.isSaving,
      isOffline: isNetworkError,
      isUnauthorized: isUnauthorized,
      errorMessage: isNetworkError
          ? _offlineMessage
          : isApiError
          ? error.message
          : _processingErrorMessage,
    );
  }
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
const _processingErrorMessage =
    'The refund/replacement request could not be processed. Please try again.';
