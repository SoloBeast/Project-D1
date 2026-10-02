import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/branding/branding_controller.dart';
import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:doodh_direct_mobile/features/branding/branding_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

final adminBrandingRepositoryProvider = Provider<BrandingRepository>(
  (ref) => BrandingRepository(api: authenticatedApiClient(ref)),
);

final adminBrandingControllerProvider =
    NotifierProvider<AdminBrandingController, AdminBrandingState>(
      AdminBrandingController.new,
    );

class AdminBrandingState {
  const AdminBrandingState({
    this.configuration,
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
    this.savedMessage,
  });

  final BrandingConfiguration? configuration;
  final bool isLoading;
  final bool isSaving;

  /// Set when the last operation failed (load, upload, remove, or a local
  /// validation rejection). The screen surfaces it as a SnackBar/banner.
  final String? errorMessage;

  /// Set when the last operation succeeded (e.g. "Logo updated.").
  final String? savedMessage;

  AdminBrandingState copyWith({
    BrandingConfiguration? configuration,
    bool clearConfiguration = false,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    bool clearError = false,
    String? savedMessage,
    bool clearSaved = false,
  }) => AdminBrandingState(
    configuration: clearConfiguration
        ? null
        : configuration ?? this.configuration,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    savedMessage: clearSaved ? null : savedMessage ?? this.savedMessage,
  );
}

/// Owner/SystemAdmin controller behind the Admin Centre Branding screen.
/// Mirrors the OtpConfigController conventions: load-on-init, per-operation
/// busy flags, and a single surfaced error/message per action. After every
/// successful mutation the shared startup [brandingControllerProvider] is
/// refreshed so the cached last-known-good branding stays consistent.
class AdminBrandingController extends Notifier<AdminBrandingState> {
  @override
  AdminBrandingState build() {
    Future.microtask(load);
    return const AdminBrandingState();
  }

  BrandingRepository get _repository =>
      ref.read(adminBrandingRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  Future<void> load() async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(
        isLoading: false,
        errorMessage: 'Sign in as Owner or System Admin to manage branding.',
        clearSaved: true,
      );
      return;
    }
    state = state.copyWith(isLoading: true, clearError: true, clearSaved: true);
    try {
      final configuration = await _repository.getForAdministration(token);
      state = state.copyWith(
        configuration: configuration,
        isLoading: false,
        clearError: true,
      );
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on ApiNetworkException {
      state = state.copyWith(
        isLoading: false,
        errorMessage: 'Unable to reach DoodhDirect. Check your connection.',
      );
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage: 'Unable to load branding. Try again.',
      );
    }
  }

  Future<void> uploadLogo({
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) =>
      _upload(
        kindLabel: 'Logo',
        upload: () => _repository.uploadLogo(
          _token ?? '',
          bytes: bytes,
          fileName: fileName,
          contentType: contentType,
        ),
      );

  Future<void> uploadStartupAnimation({
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) =>
      _upload(
        kindLabel: 'Startup animation',
        upload: () => _repository.uploadStartupAnimation(
          _token ?? '',
          bytes: bytes,
          fileName: fileName,
          contentType: contentType,
        ),
      );

  Future<void> removeLogo() => _remove(
    kindLabel: 'Logo',
    remove: () => _repository.removeLogo(_token ?? ''),
  );

  Future<void> removeStartupAnimation() => _remove(
    kindLabel: 'Startup animation',
    remove: () => _repository.removeStartupAnimation(_token ?? ''),
  );

  Future<void> _upload({
    required String kindLabel,
    required Future<BrandingConfiguration> Function() upload,
  }) async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(errorMessage: 'Your session expired. Sign in again.');
      return;
    }
    state = state.copyWith(isSaving: true, clearError: true, clearSaved: true);
    try {
      final configuration = await upload();
      state = state.copyWith(
        configuration: configuration,
        isSaving: false,
        clearError: true,
        savedMessage: '$kindLabel updated.',
      );
      await ref.read(brandingControllerProvider.notifier).refresh();
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
    } on ApiNetworkException {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'Unable to reach DoodhDirect. Check your connection.',
      );
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage: '$kindLabel upload failed. Try again.',
      );
    }
  }

  Future<void> _remove({
    required String kindLabel,
    required Future<BrandingConfiguration> Function() remove,
  }) async {
    final token = _token;
    if (token == null) {
      state = state.copyWith(errorMessage: 'Your session expired. Sign in again.');
      return;
    }
    state = state.copyWith(isSaving: true, clearError: true, clearSaved: true);
    try {
      final configuration = await remove();
      state = state.copyWith(
        configuration: configuration,
        isSaving: false,
        clearError: true,
        savedMessage: '$kindLabel removed.',
      );
      await ref.read(brandingControllerProvider.notifier).refresh();
    } on ApiException catch (error) {
      state = state.copyWith(isSaving: false, errorMessage: error.message);
    } on ApiNetworkException {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'Unable to reach DoodhDirect. Check your connection.',
      );
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage: '$kindLabel removal failed. Try again.',
      );
    }
  }
}
