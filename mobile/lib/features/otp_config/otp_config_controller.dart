import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'otp_config_models.dart';
import 'otp_config_repository.dart';

final otpConfigRepositoryProvider = Provider<OtpConfigRepository>(
  (ref) => OtpConfigRepository(api: authenticatedApiClient(ref)),
);

final otpConfigControllerProvider =
    NotifierProvider<OtpConfigController, OtpConfigState>(
      OtpConfigController.new,
    );

class OtpConfigState {
  const OtpConfigState({
    this.configuration,
    this.isLoading = false,
    this.isSaving = false,
    this.isTesting = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
    this.savedMessage,
    this.testMessage,
  });

  final OtpProviderConfiguration? configuration;
  final bool isLoading;
  final bool isSaving;
  final bool isTesting;
  final String? errorMessage;
  final Map<String, String> fieldErrors;
  final String? savedMessage;

  /// Human-readable outcome of the last "Send test OTP" call. The backend
  /// decides success/failure and the message is safe to show the admin.
  final String? testMessage;

  OtpConfigState copyWith({
    OtpProviderConfiguration? configuration,
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
  }) => OtpConfigState(
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

class OtpConfigController extends Notifier<OtpConfigState> {
  String? _activeUserId;

  OtpConfigRepository get _repository =>
      ref.read(otpConfigRepositoryProvider);

  SessionState get _session => ref.read(sessionControllerProvider);

  String? get _token => _session.session?.accessToken;

  @override
  OtpConfigState build() {
    ref.listen<SessionState>(sessionControllerProvider, (previous, next) {
      final userId = next.publicUserId;
      if (!next.isAuthenticated ||
          (_activeUserId != null && _activeUserId != userId)) {
        state = const OtpConfigState();
      }
      _activeUserId = userId;
    }, fireImmediately: true);
    return const OtpConfigState();
  }

  /// Loads the current OTP provider configuration.
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
            'Unable to load the OTP provider configuration. Check your '
            'connection and try again.',
      );
    }
  }

  /// Saves the OTP provider configuration. Returns true on success.
  ///
  /// The [request] may contain only the fields the admin changed; omitted
  /// fields keep their current values on the backend. An omitted `authKey`
  /// preserves the stored key.
  Future<bool> save(UpdateOtpProviderConfigurationRequest request) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final configuration = await _repository.update(token, request);
      if (token != _token) return false;
      state = state.copyWith(
        configuration: configuration,
        isSaving: false,
        savedMessage: 'OTP provider settings saved.',
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
            'Unable to save the OTP provider settings. Check your '
            'connection and try again.',
      );
      return false;
    }
  }

  /// Sends a test OTP through the configured provider.
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
            'Unable to send a test OTP. Check your connection and try again.',
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
