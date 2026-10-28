import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_repository.dart';
import 'package:doodh_direct_mobile/features/admin_reports/report_export_saver.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'bank_export_models.dart';
import 'bank_export_repository.dart';

final bankExportRepositoryProvider = Provider<BankExportRepository>(
  (ref) => BankExportRepository(api: authenticatedApiClient(ref)),
);

final bankExportControllerProvider =
    NotifierProvider<BankExportController, BankExportState>(
      BankExportController.new,
    );

class BankExportState {
  const BankExportState({
    this.candidates = const [],
    this.batches = const [],
    this.selectedBatch,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.errorMessage,
    this.lastSavedFile,
  });

  final List<BankExportCandidate> candidates;
  final List<BankExportBatch> batches;
  final BankExportBatch? selectedBatch;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final bool isUnauthorized;
  final String? errorMessage;
  final String? lastSavedFile;

  BankExportState copyWith({
    List<BankExportCandidate>? candidates,
    List<BankExportBatch>? batches,
    BankExportBatch? selectedBatch,
    bool clearSelectedBatch = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    bool? isUnauthorized,
    String? errorMessage,
    bool clearError = false,
    String? lastSavedFile,
    bool clearLastSavedFile = false,
  }) => BankExportState(
    candidates: candidates ?? this.candidates,
    batches: batches ?? this.batches,
    selectedBatch: clearSelectedBatch
        ? null
        : selectedBatch ?? this.selectedBatch,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    isUnauthorized: isUnauthorized ?? this.isUnauthorized,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    lastSavedFile: clearLastSavedFile
        ? null
        : lastSavedFile ?? this.lastSavedFile,
  );
}

class BankExportController extends Notifier<BankExportState> {
  BankExportRepository get _repository =>
      ref.read(bankExportRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  BankExportState build() => const BankExportState();

  Future<void> refresh() async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(isUnauthorized: true);
      return;
    }
    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final candidates = await _repository.eligible(token);
      final batches = await _repository.batches(token);
      state = state.copyWith(
        candidates: candidates,
        batches: batches,
        isLoading: false,
      );
      if (state.selectedBatch != null) {
        await selectBatch(state.selectedBatch!.batchId);
      }
    } on ApiException catch (e) {
      state = _apiError(e, isLoading: false);
    }
  }

  Future<void> selectBatch(String batchId) async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(isUnauthorized: true);
      return;
    }
    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final batch = await _repository.getBatch(token, batchId);
      state = state.copyWith(selectedBatch: batch, isLoading: false);
    } on ApiException catch (e) {
      state = _apiError(e, isLoading: false);
    }
  }

  Future<bool> createBatch(List<String> ticketIds) async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(isUnauthorized: true);
      return false;
    }
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final batch = await _repository.createBatch(token, ticketIds);
      state = state.copyWith(
        selectedBatch: batch,
        isSaving: false,
      );
      await refresh();
      return true;
    } on ApiException catch (e) {
      state = _apiError(e, isSaving: false);
      return false;
    }
  }

  Future<bool> generate(String batchId) =>
      _mutateBatch((token) => _repository.generate(token, batchId));

  Future<bool> submit(String batchId) =>
      _mutateBatch((token) => _repository.submit(token, batchId));

  Future<bool> voidBatch(String batchId, String reason) => _mutateBatch(
    (token) => _repository.voidBatch(token, batchId, reason),
  );

  Future<bool> download(String batchId) async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(isUnauthorized: true);
      return false;
    }
    state = state.copyWith(
      isSaving: true,
      clearError: true,
      clearLastSavedFile: true,
    );
    try {
      final download = await _repository.download(token, batchId);
      final destination = await ref
          .read(reportExportSaverProvider)
          .save(
            ReportExportFile(
              bytes: download.bytes,
              fileName: download.fileName,
              contentType: 'text/csv',
            ),
          );
      state = state.copyWith(isSaving: false, lastSavedFile: destination);
      return true;
    } on ApiException catch (e) {
      state = _apiError(e, isSaving: false);
      return false;
    }
  }

  Future<bool> confirmItem(String itemId, String utr, String? remarks) =>
      _mutateItem(
        (token) => _repository.confirmItem(token, itemId, utr, remarks),
      );

  Future<bool> failItem(String itemId, String reason) =>
      _mutateItem((token) => _repository.failItem(token, itemId, reason));

  Future<bool> voidItem(String itemId, String reason) =>
      _mutateItem((token) => _repository.voidItem(token, itemId, reason));

  Future<bool> releaseItem(String itemId, String reason) =>
      _mutateItem((token) => _repository.releaseItem(token, itemId, reason));

  Future<bool> _mutateBatch(
    Future<BankExportBatch> Function(String token) action,
  ) async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(isUnauthorized: true);
      return false;
    }
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final batch = await action(token);
      state = state.copyWith(selectedBatch: batch, isSaving: false);
      await refresh();
      return true;
    } on ApiException catch (e) {
      state = _apiError(e, isSaving: false);
      return false;
    }
  }

  Future<bool> _mutateItem(
    Future<BankExportItem> Function(String token) action,
  ) async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(isUnauthorized: true);
      return false;
    }
    state = state.copyWith(isSaving: true, clearError: true);
    try {
      await action(token);
      state = state.copyWith(isSaving: false);
      final selectedId = state.selectedBatch?.batchId;
      await refresh();
      if (selectedId != null) {
        await selectBatch(selectedId);
      }
      return true;
    } on ApiException catch (e) {
      state = _apiError(e, isSaving: false);
      return false;
    }
  }

  BankExportState _apiError(ApiException e, {bool? isLoading, bool? isSaving}) {
    if (e.statusCode == 401) {
      return state.copyWith(
        isUnauthorized: true,
        isLoading: isLoading ?? state.isLoading,
        isSaving: isSaving ?? state.isSaving,
      );
    }
    return state.copyWith(
      errorMessage: e.message,
      isOffline: e.statusCode == 0,
      isLoading: isLoading ?? state.isLoading,
      isSaving: isSaving ?? state.isSaving,
    );
  }
}
