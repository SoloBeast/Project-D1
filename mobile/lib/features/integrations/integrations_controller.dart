import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'integrations_models.dart';
import 'integrations_repository.dart';

final integrationsRepositoryProvider = Provider<IntegrationsRepository>(
  (ref) => IntegrationsRepository(api: authenticatedApiClient(ref)),
);

final integrationsControllerProvider =
    NotifierProvider<IntegrationsController, IntegrationsState>(
      IntegrationsController.new,
    );

class IntegrationsState {
  const IntegrationsState({
    this.configuration,
    this.isLoading = false,
    this.isSaving = false,
    this.isTesting = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
    this.savedMessage,
    this.testMessage,
  });

  final IntegrationConfiguration? configuration;
  final bool isLoading;
  final bool isSaving;
  final bool isTesting;
  final String? errorMessage;
  final Map<String, String> fieldErrors;
  final String? savedMessage;

  /// Human-readable outcome of the last "Send test email" call. The backend
  /// decides success/failure and the message is safe to show the admin.
  final String? testMessage;

  IntegrationsState copyWith({
    IntegrationConfiguration? configuration,
    bool clearConfiguration = false,
    bool? isLoading,
    bool? isSaving,
    bool? isTesting,
    String? errorMessage,
    Map<String, String>? fieldErrors,
    bool clearError = false,
    String? savedMessage,
    bool clearSaved = false,
    String? testMessage,
    bool clearTest = false,
  }) => IntegrationsState(
    configuration: clearConfiguration
        ? null
        : configuration ?? this.configuration,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isTesting: isTesting ?? this.isTesting,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    fieldErrors: clearError
        ? const <String, String>{}
        : fieldErrors ?? this.fieldErrors,
    savedMessage: clearSaved ? null : savedMessage ?? this.savedMessage,
    testMessage: clearTest ? null : testMessage ?? this.testMessage,
  );
}

class IntegrationsController extends Notifier<IntegrationsState> {
  String? _activeUserId;

  IntegrationsRepository get _repository =>
      ref.read(integrationsRepositoryProvider);

  SessionState get _session => ref.read(sessionControllerProvider);

  String? get _token => _session.session?.accessToken;

  @override
  IntegrationsState build() {
    ref.listen<SessionState>(sessionControllerProvider, (previous, next) {
      final userId = next.publicUserId;
      if (!next.isAuthenticated ||
          (_activeUserId != null && _activeUserId != userId)) {
        state = const IntegrationsState();
      }
      _activeUserId = userId;
    }, fireImmediately: true);
    return const IntegrationsState();
  }

  /// Loads the current integration configuration.
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
            'Unable to load the integration settings. Check your connection '
            'and try again.',
      );
    }
  }

  /// Saves the integration settings. Returns true on success.
  ///
  /// The [request] may contain only the fields the admin changed; omitted
  /// fields keep their current values on the backend. Empty strings clear the
  /// database override; omitted secrets preserve the stored ones.
  Future<bool> save(UpdateIntegrationConfigurationRequest request) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final configuration = await _repository.update(token, request);
      if (token != _token) return false;
      state = state.copyWith(
        configuration: configuration,
        isSaving: false,
        savedMessage: 'Integration settings saved.',
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
            'Unable to save the integration settings. Check your connection '
            'and try again.',
      );
      return false;
    }
  }

  /// Sends a test email through the configured SMTP relay.
  Future<void> test() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isTesting: true, clearError: true, clearTest: true);
    try {
      final result = await _repository.test(token);
      if (token != _token) return;
      state = state.copyWith(isTesting: false, testMessage: result.message);
    } on ApiException catch (error) {
      state = state.copyWith(
        isTesting: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
    } on Object {
      state = state.copyWith(
        isTesting: false,
        errorMessage:
            'Unable to send a test email. Check your connection and try again.',
      );
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
