import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';

class CustomerRepository {
  CustomerRepository({required this._api});

  final ApiClient _api;

  Future<CustomerProfile> getProfile(String token) async {
    final response = await _api.get('/api/v1/customers/me', accessToken: token);
    return CustomerProfile.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<CustomerProfile> updateProfile(
    String token,
    UpdateCustomerProfile request,
  ) async {
    final response = await _api.patch(
      '/api/v1/customers/me',
      body: request.toJson(),
      accessToken: token,
    );
    return CustomerProfile.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<List<CustomerAddress>> getAddresses(String token) async {
    final response = await _api.get(
      '/api/v1/customers/me/addresses',
      accessToken: token,
    );
    final data = response['data'] as List<dynamic>? ?? const <dynamic>[];
    return data
        .cast<Map<String, dynamic>>()
        .map(CustomerAddress.fromJson)
        .toList(growable: false);
  }

  Future<CustomerAddress> createAddress(
    String token,
    AddressDraft request,
  ) async {
    final response = await _api.post(
      '/api/v1/customers/me/addresses',
      body: request.toJson(),
      accessToken: token,
    );
    return CustomerAddress.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<CustomerAddress> updateAddress(
    String token,
    String addressId,
    AddressDraft request,
  ) async {
    final response = await _api.patch(
      '/api/v1/customers/me/addresses/$addressId',
      body: request.toJson(),
      accessToken: token,
    );
    return CustomerAddress.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<void> deactivateAddress(String token, String addressId) async {
    await _api.delete(
      '/api/v1/customers/me/addresses/$addressId',
      accessToken: token,
    );
  }

  Future<AddressLookup?> reverseLookup(
    String token,
    double latitude,
    double longitude,
  ) async {
    final response = await _api.get(
      '/api/v1/customers/me/address-lookup/reverse'
      '?latitude=$latitude&longitude=$longitude',
      accessToken: token,
    );
    final data = response['data'];
    return data is Map<String, dynamic> ? AddressLookup.fromJson(data) : null;
  }
}
