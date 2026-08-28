import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'number_series_models.dart';
import 'number_series_repository.dart';

final numberSeriesRepositoryProvider = Provider<NumberSeriesRepository>(
  (ref) => NumberSeriesRepository(api: authenticatedApiClient(ref)),
);

final numberSeriesControllerProvider =
    NotifierProvider<NumberSeriesController, NumberSeriesState>(
      NumberSeriesController.new,
    );

class NumberSeriesState {
  const NumberSeriesState({
    this.series = const <NumberSeries>[],
    this.preview,
    this.isLoading = false,
    this.isSaving = false,
    this.isPreviewing = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
    this.savedMessage,
  });

  final List<NumberSeries> series;
  final NumberSeriesPreview? preview;
  final bool isLoading;
  final bool isSaving;
  final bool isPreviewing;
  final String? errorMessage;
  final Map<String, String> fieldErrors;
  final String? savedMessage;

  NumberSeriesState copyWith({
    List<NumberSeries>? series,
    NumberSeriesPreview? preview,
    bool clearPreview = false,
    bool? isLoading,
    bool? isSaving,
    bool? isPreviewing,
    String? errorMessage,
    Map<String, String>? fieldErrors,
    bool clearError = false,
    String? savedMessage,
    bool clearSaved = false,
  }) => NumberSeriesState(
    series: series ?? this.series,
    preview: clearPreview ? null : preview ?? this.preview,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isPreviewing: isPreviewing ?? this.isPreviewing,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    fieldErrors: clearError
        ? const <String, String>{}
        : fieldErrors ?? this.fieldErrors,
    savedMessage: clearSaved ? null : savedMessage ?? this.savedMessage,
  );
}

class NumberSeriesController extends Notifier<NumberSeriesState> {
  String? _activeUserId;

  NumberSeriesRepository get _repository =>
      ref.read(numberSeriesRepositoryProvider);

  SessionState get _session => ref.read(sessionControllerProvider);

  String? get _token => _session.session?.accessToken;

  @override
  NumberSeriesState build() {
    ref.listen<SessionState>(sessionControllerProvider, (previous, next) {
      final userId = next.publicUserId;
      if (!next.isAuthenticated ||
          (_activeUserId != null && _activeUserId != userId)) {
        state = const NumberSeriesState();
      }
      _activeUserId = userId;
    }, fireImmediately: true);
    return const NumberSeriesState();
  }

  Future<void> load() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final series = await _repository.list(token);
      if (token != _token) return;
      state = state.copyWith(series: series, isLoading: false);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage:
            'Unable to load number series. Check your connection and try again.',
      );
    }
  }

  Future<void> previewTemplate(
    String code,
    String template, {
    int? nextNumber,
    String? scope,
  }) async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(
      isPreviewing: true,
      clearError: true,
      clearPreview: true,
    );
    try {
      final preview = await _repository.preview(
        token,
        NumberSeriesPreviewRequest(
          code: code,
          template: template,
          nextNumber: nextNumber,
          scope: scope,
        ),
      );
      if (token != _token) return;
      state = state.copyWith(preview: preview, isPreviewing: false);
    } on ApiException catch (error) {
      state = state.copyWith(
        isPreviewing: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
    } on Object {
      state = state.copyWith(
        isPreviewing: false,
        errorMessage:
            'Unable to preview the template. Check your connection and try again.',
      );
    }
  }

  /// Creates a new series and refreshes the list. Returns the created series or
  /// null on failure.
  Future<NumberSeries?> create(CreateNumberSeriesRequest request) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final created = await _repository.create(token, request);
      if (token != _token) return null;
      final series = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        series: series,
        isSaving: false,
        savedMessage: 'Series ${created.code} created.',
      );
      return created;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return null;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage:
            'Unable to create the series. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Updates an existing series and refreshes the list. Returns the updated
  /// series or null on failure.
  Future<NumberSeries?> update(
    String code,
    UpdateNumberSeriesRequest request, {
    String? scope,
  }) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final updated = await _repository.update(token, code, request, scope: scope);
      if (token != _token) return null;
      final series = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        series: series,
        isSaving: false,
        savedMessage: 'Series ${updated.code} updated.',
      );
      return updated;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return null;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage:
            'Unable to update the series. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Activates or deactivates a series and refreshes the list. Returns the
  /// updated series or null on failure.
  Future<NumberSeries?> setActive(
    String code,
    bool isActive, {
    String? scope,
  }) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final updated = await _repository.setActive(
        token,
        code,
        isActive,
        scope: scope,
      );
      if (token != _token) return null;
      final series = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        series: series,
        isSaving: false,
        savedMessage: isActive
            ? 'Series ${updated.code} activated.'
            : 'Series ${updated.code} deactivated.',
      );
      return updated;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return null;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage:
            'Unable to update the series. Check your connection and try again.',
      );
      return null;
    }
  }

  Map<String, String> _fieldErrors(ApiException error) {
    final field = error.field;
    if (field == null || field.trim().isEmpty) {
      return const <String, String>{};
    }
    final normalized = field[0].toLowerCase() + field.substring(1);
    return <String, String>{normalized: error.message};
  }
}
