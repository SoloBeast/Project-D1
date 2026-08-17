import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'dairy_models.dart';
import 'dairy_repository.dart';

final dairyRepositoryProvider = Provider<DairyRepository>(
  (ref) => DairyRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final dairyControllerProvider = NotifierProvider<DairyController, DairyState>(
  DairyController.new,
);

class DairyState {
  const DairyState({
    this.dashboard,
    this.production = const [],
    this.batches = const [],
    this.availability,
    this.usage = const [],
    this.selectedBatch,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.errorMessage,
  });

  final DairyDashboard? dashboard;
  final List<MilkProduction> production;
  final List<MilkBatch> batches;
  final MilkAvailability? availability;
  final List<MilkUsage> usage;
  final MilkBatch? selectedBatch;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final bool isUnauthorized;
  final String? errorMessage;

  DairyState copyWith({
    DairyDashboard? dashboard,
    List<MilkProduction>? production,
    List<MilkBatch>? batches,
    MilkAvailability? availability,
    List<MilkUsage>? usage,
    MilkBatch? selectedBatch,
    bool clearSelectedBatch = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    bool? isUnauthorized,
    String? errorMessage,
    bool clearError = false,
  }) => DairyState(
    dashboard: dashboard ?? this.dashboard,
    production: production ?? this.production,
    batches: batches ?? this.batches,
    availability: availability ?? this.availability,
    usage: usage ?? this.usage,
    selectedBatch: clearSelectedBatch
        ? null
        : selectedBatch ?? this.selectedBatch,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    isUnauthorized: isUnauthorized ?? this.isUnauthorized,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class DairyController extends Notifier<DairyState> {
  DairyRepository get _repository => ref.read(dairyRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  DairyState build() => const DairyState();

  Future<void> loadDashboard(int branchId, {DateTime? productionDate}) async =>
      _load(() async {
        final dashboard = await _repository.getDashboard(
          _token!,
          branchId,
          productionDate: productionDate,
        );
        state = state.copyWith(dashboard: dashboard);
      });

  Future<void> loadProduction(
    int branchId, {
    DateTime? fromDate,
    DateTime? toDate,
  }) async => _load(() async {
    final production = await _repository.getProductionHistory(
      _token!,
      branchId,
      fromDate: fromDate,
      toDate: toDate,
    );
    state = state.copyWith(production: production);
  });

  Future<void> loadBatches(int branchId, {MilkBatchStatus? status}) async =>
      _load(() async {
        final batches = await _repository.getBatches(
          _token!,
          branchId,
          status: status,
        );
        state = state.copyWith(batches: batches);
      });

  Future<void> loadBatch(String batchId) async => _load(() async {
    final batch = await _repository.getBatch(_token!, batchId);
    state = state.copyWith(batches: _upsertBatch(batch), selectedBatch: batch);
  });

  Future<void> loadAvailability(int branchId) async => _load(() async {
    final availability = await _repository.getAvailability(_token!, branchId);
    state = state.copyWith(availability: availability);
  });

  Future<void> loadUsage(
    int branchId, {
    DateTime? fromDate,
    DateTime? toDate,
  }) async => _load(() async {
    final usage = await _repository.getUsageHistory(
      _token!,
      branchId,
      fromDate: fromDate,
      toDate: toDate,
    );
    state = state.copyWith(usage: usage);
  });

  Future<bool> recordProduction(
    int branchId,
    RecordMilkProductionRequest request,
  ) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final result = await _repository.recordProduction(
        token,
        branchId,
        request,
      );
      state = state.copyWith(
        production: [result, ...state.production],
        batches: _upsertBatch(result.batch),
        isSaving: false,
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<bool> recordUsage(
    String batchId,
    RecordMilkUsageRequest request,
  ) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final result = await _repository.recordUsage(token, batchId, request);
      state = state.copyWith(usage: [result, ...state.usage], isSaving: false);
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<void> _load(Future<void> Function() operation) async {
    final token = _token;
    if (token == null) return;
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

  List<MilkBatch> _upsertBatch(MilkBatch batch) {
    final items = [...state.batches];
    final index = items.indexWhere((item) => item.publicId == batch.publicId);
    if (index == -1) {
      items.insert(0, batch);
    } else {
      items[index] = batch;
    }
    return items;
  }

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
