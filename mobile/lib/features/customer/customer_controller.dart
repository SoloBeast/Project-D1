import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'customer_models.dart';
import 'customer_repository.dart';

final customerRepositoryProvider = Provider<CustomerRepository>(
  (ref) => CustomerRepository(api: authenticatedApiClient(ref)),
);

final customerControllerProvider =
    NotifierProvider<CustomerController, CustomerState>(CustomerController.new);

class CustomerState {
  const CustomerState({
    this.profile,
    this.addresses = const <CustomerAddress>[],
    this.isLoading = false,
    this.isSaving = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
  });

  final CustomerProfile? profile;
  final List<CustomerAddress> addresses;
  final bool isLoading;
  final bool isSaving;
  final String? errorMessage;
  final Map<String, String> fieldErrors;

  CustomerState copyWith({
    CustomerProfile? profile,
    List<CustomerAddress>? addresses,
    bool? isLoading,
    bool? isSaving,
    String? errorMessage,
    Map<String, String>? fieldErrors,
    bool clearError = false,
  }) => CustomerState(
    profile: profile ?? this.profile,
    addresses: addresses ?? this.addresses,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    fieldErrors: clearError
        ? const <String, String>{}
        : fieldErrors ?? this.fieldErrors,
  );
}

class CustomerController extends Notifier<CustomerState> {
  CustomerRepository get _repository => ref.read(customerRepositoryProvider);

  @override
  CustomerState build() => const CustomerState();

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  Future<void> load() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, clearError: true);
    try {
      final results = await Future.wait([
        _repository.getProfile(token),
        _repository.getAddresses(token),
      ]);
      state = state.copyWith(
        profile: results[0] as CustomerProfile,
        addresses: results[1] as List<CustomerAddress>,
        isLoading: false,
      );
    } on ApiException catch (error) {
      state = state.copyWith(isLoading: false, errorMessage: error.message);
    } on Object {
      state = state.copyWith(
        isLoading: false,
        errorMessage:
            'Unable to reach DoodhDirect. Check your connection and try again.',
      );
    }
  }

  Future<bool> saveProfile(UpdateCustomerProfile request) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      final profile = await _repository.updateProfile(token, request);
      state = state.copyWith(profile: profile, isSaving: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage:
            'Unable to save your profile. Check your connection and try again.',
      );
    }
    return false;
  }

  Future<bool> saveAddress(AddressDraft draft, {String? addressId}) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      if (addressId == null) {
        await _repository.createAddress(token, draft);
      } else {
        await _repository.updateAddress(token, addressId, draft);
      }
      await _reloadAddresses(token);
      state = state.copyWith(isSaving: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage:
            'Unable to save the address. Check your connection and try again.',
      );
    }
    return false;
  }

  Future<bool> deactivateAddress(CustomerAddress address) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, clearError: true);
    try {
      await _repository.deactivateAddress(token, address.publicId);
      await _reloadAddresses(token);
      state = state.copyWith(isSaving: false);
      return true;
    } on ApiException catch (error) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: error.message,
        fieldErrors: _fieldErrors(error),
      );
    } on Object {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'Unable to deactivate the address. Check your connection and try again.',
      );
    }
    return false;
  }

  Future<AddressLookup?> reverseLookup(
    double latitude,
    double longitude,
  ) async {
    final token = _token;
    if (token == null) return null;

    try {
      return await _repository.reverseLookup(token, latitude, longitude);
    } on Object {
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

  Future<void> _reloadAddresses(String token) async {
    state = state.copyWith(addresses: await _repository.getAddresses(token));
  }
}
