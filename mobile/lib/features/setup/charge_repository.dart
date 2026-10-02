import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'charge_models.dart';

/// HTTP client for the Setup → Tax & Charges module.
class ChargeRepository {
  ChargeRepository({required this._api});

  final ApiClient _api;

  static const String _basePath = '/api/v1/admin/setup/tax-charges';

  Future<List<Charge>> list(String accessToken) async {
    final response = await _api.get(_basePath, accessToken: accessToken);
    final items = response['data'] as List<dynamic>;
    return items
        .map((item) => Charge.fromJson(item as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<Charge> get(String accessToken, String publicId) async {
    final response = await _api.get(
      '$_basePath/${Uri.encodeComponent(publicId)}',
      accessToken: accessToken,
    );
    return Charge.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<Charge> create(String accessToken, CreateChargeRequest request) async {
    final response = await _api.post(
      _basePath,
      accessToken: accessToken,
      body: request.toJson(),
    );
    return Charge.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<Charge> update(
    String accessToken,
    String publicId,
    UpdateChargeRequest request,
  ) async {
    final response = await _api.put(
      '$_basePath/${Uri.encodeComponent(publicId)}',
      accessToken: accessToken,
      body: request.toJson(),
    );
    return Charge.fromJson(response['data'] as Map<String, dynamic>);
  }

  /// Toggles applicability through the DEDICATED backend endpoints — never a
  /// generic PUT, mirroring the Number Series convention.
  Future<Charge> setActive(
    String accessToken,
    String publicId,
    bool isActive,
  ) async {
    final response = await _api.post(
      '$_basePath/${Uri.encodeComponent(publicId)}/'
      '${isActive ? 'activate' : 'deactivate'}',
      accessToken: accessToken,
    );
    return Charge.fromJson(response['data'] as Map<String, dynamic>);
  }

  /// Dedicated applicability toggle (PUT `{id}/applicability`). Switching an
  /// item charge back to global is refused by the server while any product
  /// assignment exists — the caller surfaces that business error.
  Future<Charge> setApplicability(
    String accessToken,
    String publicId,
    bool applicableOnAll,
  ) async {
    final response = await _api.put(
      '$_basePath/${Uri.encodeComponent(publicId)}/applicability',
      accessToken: accessToken,
      body: <String, dynamic>{'applicableOnAll': applicableOnAll},
    );
    return Charge.fromJson(response['data'] as Map<String, dynamic>);
  }

  /// Permanently deletes an unused charge. The backend refuses when any order
  /// snapshot references the charge — the caller surfaces that business error.
  Future<void> delete(String accessToken, String publicId) async {
    await _api.delete(
      '$_basePath/${Uri.encodeComponent(publicId)}',
      accessToken: accessToken,
    );
  }
}
