import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'vacation_refund_models.dart';
import 'vacation_refund_repository.dart';

final vacationRefundStaffControllerProvider =
    NotifierProvider<VacationRefundStaffController, VacationRefundStaffState>(
      VacationRefundStaffController.new,
    );

class VacationRefundStaffState {
  const VacationRefundStaffState({
    this.staffRequests = const [],
    this.selectedStaffRequest,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.errorMessage,
  });

  final List<VacationRefundRequest> staffRequests;
  final VacationRefundRequest? selectedStaffRequest;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final bool isUnauthorized;
  final String? errorMessage;

  VacationRefundStaffState copyWith({
    List<VacationRefundRequest>? staffRequests,
    VacationRefundRequest? selectedStaffRequest,
    bool clearSelectedStaffRequest = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    bool? isUnauthorized,
    String? errorMessage,
    bool clearError = false,
  }) => VacationRefundStaffState(
    staffRequests: staffRequests ?? this.staffRequests,
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

class VacationRefundStaffController extends Notifier<VacationRefundStaffState> {
  VacationRefundRepository get _repository =>
      ref.read(vacationRefundRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  VacationRefundStaffState build() => const VacationRefundStaffState();

  /// Staff queue: end-of-subscription vacation-refund requests. The server
  /// scopes rows to the actor's branches (global access sees everything);
  /// the client never filters by branch itself.
  Future<void> loadStaffRequests() => _load(() async {
    final requests = await _repository.listForStaff(_token!);
    state = state.copyWith(
      staffRequests: requests,
      clearSelectedStaffRequest: true,
    );
  });

  Future<void> loadStaffRequest(String requestId) => _load(() async {
    final request = await _repository.getForStaff(_token!, requestId);
    state = state.copyWith(selectedStaffRequest: request);
  });

  /// Approve, reject, or complete through the authorized settlement
  /// workflow. Approval never credits the wallet — only completion does.
  Future<bool> approve(String requestId, {String? remarks}) =>
      _decide(requestId, 'approve', remarks);

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
        state = state.copyWith(
          selectedStaffRequest: request,
          staffRequests: [
            for (final item in state.staffRequests)
              item.requestId == request.requestId ? request : item,
          ],
        );
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
    'The vacation-refund request could not be processed. Please try again.';
