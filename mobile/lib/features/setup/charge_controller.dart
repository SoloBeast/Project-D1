import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'charge_models.dart';
import 'charge_repository.dart';

final chargeRepositoryProvider = Provider<ChargeRepository>(
  (ref) => ChargeRepository(api: authenticatedApiClient(ref)),
);

final chargeControllerProvider =
    NotifierProvider<ChargeController, ChargeState>(ChargeController.new);

class ChargeState {
  const ChargeState({
    this.charges = const <Charge>[],
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
    this.savedMessage,
  });

  final List<Charge> charges;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;
  final Map<String, String> fieldErrors;
  final String? savedMessage;

  ChargeState copyWith({
    List<Charge>? charges,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    bool clearError = false,
    Map<String, String>? fieldErrors,
    String? savedMessage,
    bool clearSaved = false,
  }) => ChargeState(
    charges: charges ?? this.charges,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    fieldErrors: clearError
        ? const <String, String>{}
        : fieldErrors ?? this.fieldErrors,
    savedMessage: clearSaved ? null : savedMessage ?? this.savedMessage,
  );
}

class ChargeController extends Notifier<ChargeState> {
  String? _activeUserId;

  ChargeRepository get _repository => ref.read(chargeRepositoryProvider);

  SessionState get _session => ref.read(sessionControllerProvider);

  String? get _token => _session.session?.accessToken;

  @override
  ChargeState build() {
    ref.listen<SessionState>(sessionControllerProvider, (previous, next) {
      final userId = next.publicUserId;
      if (!next.isAuthenticated ||
          (_activeUserId != null && _activeUserId != userId)) {
        state = const ChargeState();
      }
      _activeUserId = userId;
    }, fireImmediately: true);
    return const ChargeState();
  }

  Future<void> load() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final charges = await _repository.list(token);
      if (token != _token) return;
      state = state.copyWith(charges: charges, isLoading: false);
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage: 'Unable to load Tax & Charges. Check your connection and try again.',
      );
    }
  }

  /// Creates a charge and refreshes the list. Returns the created charge or
  /// null on failure (server validation errors land in `fieldErrors`).
  Future<Charge?> create(CreateChargeRequest request) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final created = await _repository.create(token, request);
      if (token != _token) return null;
      final charges = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        charges: charges,
        isSaving: false,
        savedMessage: 'Charge ${created.chargeCode} created.',
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
            'Unable to create the charge. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Updates a charge and refreshes the list. Returns the updated charge or
  /// null on failure (e.g. immutable ChargeType once used).
  Future<Charge?> update(String publicId, UpdateChargeRequest request) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final updated = await _repository.update(token, publicId, request);
      if (token != _token) return null;
      final charges = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        charges: charges,
        isSaving: false,
        savedMessage: 'Charge ${updated.chargeCode} updated.',
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
            'Unable to update the charge. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Activates or deactivates via the dedicated endpoints. Returns the updated
  /// charge or null on failure.
  Future<Charge?> setActive(String publicId, bool isActive) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final updated = await _repository.setActive(token, publicId, isActive);
      if (token != _token) return null;
      final charges = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        charges: charges,
        isSaving: false,
        savedMessage: isActive
            ? 'Charge ${updated.chargeCode} activated.'
            : 'Charge ${updated.chargeCode} deactivated.',
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
            'Unable to update the charge. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Toggles the applicability mode through the dedicated endpoint. Returns the
  /// updated charge or null on failure — e.g. enabling "Applicable on All" is
  /// refused while the charge is still assigned to any product.
  Future<Charge?> setApplicableOnAll(
    String publicId,
    bool applicableOnAll,
  ) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final updated = await _repository.setApplicability(
        token,
        publicId,
        applicableOnAll,
      );
      if (token != _token) return null;
      final charges = await _repository.list(token);
      if (token != _token) return null;
      state = state.copyWith(
        charges: charges,
        isSaving: false,
        savedMessage: applicableOnAll
            ? 'Charge ${updated.chargeCode} now applies to all products.'
            : 'Charge ${updated.chargeCode} can now be assigned to products.',
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
            'Unable to update the charge. Check your connection and try again.',
      );
      return null;
    }
  }

  /// Permanently deletes an unused charge. Returns true on success; on failure
  /// the server business rule (e.g. referenced by historical orders) is shown.
  Future<bool> delete(String publicId) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      await _repository.delete(token, publicId);
      if (token != _token) return false;
      final charges = await _repository.list(token);
      if (token != _token) return false;
      state = state.copyWith(
        charges: charges,
        isSaving: false,
        savedMessage: 'Charge deleted.',
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
            'Unable to delete the charge. Check your connection and try again.',
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
