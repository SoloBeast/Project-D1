import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'refund_replacement_config_models.dart';
import 'refund_replacement_config_repository.dart';

final refundReplacementConfigRepositoryProvider =
    Provider<RefundReplacementConfigRepository>(
      (ref) => RefundReplacementConfigRepository(api: authenticatedApiClient(ref)),
    );

final refundReplacementConfigControllerProvider =
    NotifierProvider<
      RefundReplacementConfigController,
      RefundReplacementConfigState
    >(RefundReplacementConfigController.new);

/// State for the Setup → Refund / Replacement request window configuration.
class RefundReplacementConfigState {
  const RefundReplacementConfigState({
    this.configuration,
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
    this.savedMessage,
  });

  final RefundReplacementConfiguration? configuration;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;
  final Map<String, String> fieldErrors;
  final String? savedMessage;

  RefundReplacementConfigState copyWith({
    RefundReplacementConfiguration? configuration,
    bool clearConfiguration = false,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    Map<String, String>? fieldErrors,
    bool clearError = false,
    String? savedMessage,
    bool clearSaved = false,
  }) => RefundReplacementConfigState(
    configuration: clearConfiguration
        ? null
        : configuration ?? this.configuration,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    fieldErrors: clearError
        ? const <String, String>{}
        : fieldErrors ?? this.fieldErrors,
    savedMessage: clearSaved ? null : savedMessage ?? this.savedMessage,
  );
}

class RefundReplacementConfigController
    extends Notifier<RefundReplacementConfigState> {
  String? _activeUserId;

  RefundReplacementConfigRepository get _repository =>
      ref.read(refundReplacementConfigRepositoryProvider);

  SessionState get _session => ref.read(sessionControllerProvider);

  String? get _token => _session.session?.accessToken;

  @override
  RefundReplacementConfigState build() {
    ref.listen<SessionState>(sessionControllerProvider, (previous, next) {
      final userId = next.publicUserId;
      if (!next.isAuthenticated ||
          (_activeUserId != null && _activeUserId != userId)) {
        state = const RefundReplacementConfigState();
      }
      _activeUserId = userId;
    }, fireImmediately: true);
    return const RefundReplacementConfigState();
  }

  /// Loads the current refund/replacement request window configuration.
  Future<void> load() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final configuration = await _repository.get(token);
      if (token != _token) return;
      state = state.copyWith(configuration: configuration, isLoading: false);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage:
            'Unable to load the refund/replacement window settings. Check '
            'your connection and try again.',
      );
    }
  }

  /// Saves the refund/replacement request window configuration. Returns true
  /// on success.
  ///
  /// An omitted (null) `windowHours` in [request] leaves the stored value
  /// unchanged on the backend.
  Future<bool> save(UpdateRefundReplacementConfigurationRequest request) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true, clearSaved: true);
    try {
      final configuration = await _repository.update(token, request);
      if (token != _token) return false;
      state = state.copyWith(
        configuration: configuration,
        isSaving: false,
        savedMessage: 'Refund/replacement window settings saved.',
      );
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
      return false;
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage:
            'Unable to save the refund/replacement window settings. Check '
            'your connection and try again.',
      );
      return false;
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
