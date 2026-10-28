import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'refund_replacement_models.dart';
import 'refund_replacement_repository.dart';

final refundReplacementRepositoryProvider =
    Provider<RefundReplacementRepository>(
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
    this.orderRefundPreview,
    this.isLoading = false,
    this.isSaving = false,
    this.isPreviewLoading = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.errorMessage,
  });

  final RefundReplacementEligibility? eligibility;
  final List<RefundReplacementRequest> customerRequests;
  final RefundReplacementRequest? selectedCustomerRequest;
  final RefundReplacementPage? staffPage;
  final RefundReplacementRequest? selectedStaffRequest;
  final OrderRefundPreview? orderRefundPreview;
  final bool isLoading;
  final bool isSaving;
  final bool isPreviewLoading;
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
    OrderRefundPreview? orderRefundPreview,
    bool clearOrderRefundPreview = false,
    bool? isLoading,
    bool? isSaving,
    bool? isPreviewLoading,
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
    orderRefundPreview: clearOrderRefundPreview
        ? null
        : orderRefundPreview ?? this.orderRefundPreview,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isPreviewLoading: isPreviewLoading ?? this.isPreviewLoading,
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

  /// Stage 2.1: when [imageBytes] is supplied the evidence rides with the
  /// submission in one atomic server transaction; otherwise the plain JSON
  /// submission is used (which the server rejects for photo-required tickets).
  Future<bool> submitRequest(
    String deliveryId, {
    required RefundReplacementType type,
    required String reason,
    String? remarks,
    String? milkTestId,
    Uint8List? imageBytes,
    String? imageFileName,
    String? imageContentType,
  }) => _save(() async {
    final request = imageBytes == null
        ? await _repository.submit(
            _token!,
            deliveryId,
            type: type,
            reason: reason,
            remarks: remarks,
            milkTestId: milkTestId,
          )
        : await _repository.submitWithImage(
            _token!,
            deliveryId,
            type: type,
            reason: reason,
            remarks: remarks,
            milkTestId: milkTestId,
            imageBytes: imageBytes,
            fileName: imageFileName ?? 'proof.jpg',
            contentType: imageContentType ?? 'image/jpeg',
          );
    state = state.copyWith(eligibility: null, selectedCustomerRequest: request);
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

  /// D1-B: records the customer's Refund / Re-Delivery choice and refreshes
  /// both the detail selection and the matching list row. Returns false and
  /// surfaces the server message when the window expired, the ticket is
  /// already resolved, or a different choice was attempted after selection.
  Future<bool> chooseCustomerChoice(String requestId, CustomerChoice choice) =>
      _save(() async {
        final request = await _repository.choose(_token!, requestId, choice);
        _replaceCustomerRequest(request);
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
          subscriptionDeliveryPublicId: request.subscriptionDeliveryPublicId,
          failedAtUtc: request.failedAtUtc,
          failureReasonSnapshot: request.failureReasonSnapshot,
          customerChoice: request.customerChoice,
          customerChoiceAtUtc: request.customerChoiceAtUtc,
          replacementDate: request.replacementDate,
          replacementDeliveryNumber: request.replacementDeliveryNumber,
          isRefundOnly: request.isRefundOnly,
          appliedDeductionPercentage: request.appliedDeductionPercentage,
          deductionWaived: request.deductionWaived,
          deductionWaiverReason: request.deductionWaiverReason,
          supersedesRequestNumber: request.supersedesRequestNumber,
          supersedesRequestPublicId: request.supersedesRequestPublicId,
          supersededByRequestNumber: request.supersededByRequestNumber,
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

  /// D1-D: approves a request, forwarding an optional staff-confirmed
  /// replacement date for re-delivery tickets. Null preserves the existing
  /// refund/one-time approval behavior.
  ///
  /// Stage 2: for order-backed Refund tickets [applyDeduction] carries
  /// support's explicit apply/waive decision and [waiverReason] the required
  /// reason when waiving. Approval records the decision and frozen snapshot
  /// only — it never credits the wallet.
  Future<bool> approve(
    String requestId, {
    String? remarks,
    DateTime? replacementDate,
    bool? applyDeduction,
    String? waiverReason,
    String? settlementMethod,
  }) => _save(() async {
    final request = await _repository.approve(
      _token!,
      requestId,
      remarks: remarks,
      replacementDate: replacementDate,
      applyDeduction: applyDeduction,
      waiverReason: waiverReason,
      settlementMethod: settlementMethod,
    );
    state = state.copyWith(
      selectedStaffRequest: request,
      clearOrderRefundPreview: true,
    );
  });

  /// Stage 2: loads the authoritative refund preview for an order-backed
  /// Refund ticket. Returns the preview (or null on failure) and caches it
  /// in state; the preview never mutates server state. A failed load keeps
  /// any previously loaded preview and surfaces the server message.
  Future<OrderRefundPreview?> previewOrderRefund(
    String requestId, {
    required bool applyDeduction,
  }) async {
    final token = _token;
    if (token == null) return null;
    state = state.copyWith(
      isPreviewLoading: true,
      isOffline: false,
      isUnauthorized: false,
      clearError: true,
    );
    try {
      final preview = await _repository.getOrderRefundPreview(
        token,
        requestId,
        applyDeduction: applyDeduction,
      );
      state = state.copyWith(
        orderRefundPreview: preview,
        isPreviewLoading: false,
      );
      return preview;
    } on Object catch (error) {
      _setFailure(error);
      state = state.copyWith(isPreviewLoading: false);
      return null;
    }
  }

  void clearOrderRefundPreview() =>
      state = state.copyWith(clearOrderRefundPreview: true);

  /// Legacy remediation: replaces an eligible unsupported Replacement
  /// ticket with a linked Pending Refund ticket. Returns the pair on
  /// success (and selects the superseded legacy ticket, so the detail
  /// refreshes); returns null on any failure with the server message in
  /// [RefundReplacementState.errorMessage]. Guards against duplicate
  /// submissions while a save is already in flight. Callers reload
  /// authoritative state after a conflict before permitting another
  /// attempt. Never issues a refund or credits the wallet — the new ticket
  /// follows the standard approval and settlement workflow.
  Future<RemediationPair?> remediateLegacyReplacement(
    String requestId, {
    required String reason,
  }) async {
    final token = _token;
    if (token == null || state.isSaving) return null;
    state = state.copyWith(
      isSaving: true,
      isOffline: false,
      isUnauthorized: false,
      clearError: true,
    );
    try {
      final pair = await _repository.remediate(
        token,
        requestId,
        reason: reason,
      );
      state = state.copyWith(
        selectedStaffRequest: pair.legacyTicket,
        isSaving: false,
      );
      return pair;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return null;
    }
  }

  Future<bool> reject(String requestId, {String? remarks}) =>
      _decide(requestId, 'reject', remarks);

  Future<bool> complete(String requestId, {String? remarks}) =>
      _decide(requestId, 'complete', remarks);

  Future<bool> _decide(String requestId, String decision, String? remarks) =>
      _save(() async {
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
          _ => await _repository.complete(_token!, requestId, remarks: remarks),
        };
        state = state.copyWith(selectedStaffRequest: request);
      });

  void _replaceCustomerRequest(RefundReplacementRequest request) {
    final updated = [
      for (final item in state.customerRequests)
        item.requestId == request.requestId ? request : item,
    ];
    state = state.copyWith(
      selectedCustomerRequest: request,
      customerRequests: updated,
    );
  }

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
