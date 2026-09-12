import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_models.dart';

/// Admin repository for reading and updating the refund/replacement request
/// window configuration.
///
/// Only OWNER / SYSTEM_ADMIN roles are authorized (the backend enforces
/// `SETUP.REFUND_REPLACEMENT.READ` / `SETUP.REFUND_REPLACEMENT.MANAGE`). The
/// window is server-authoritative and expressed in whole hours.
class RefundReplacementConfigRepository {
  RefundReplacementConfigRepository({required this._api});

  final ApiClient _api;

  static const String _basePath = '/api/v1/admin/setup/refund-replacement';

  /// Loads the current refund/replacement request window configuration.
  Future<RefundReplacementConfiguration> get(String accessToken) async {
    final response = await _api.get(_basePath, accessToken: accessToken);
    return RefundReplacementConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Updates the refund/replacement request window configuration.
  Future<RefundReplacementConfiguration> update(
    String accessToken,
    UpdateRefundReplacementConfigurationRequest request,
  ) async {
    final response = await _api.put(
      _basePath,
      accessToken: accessToken,
      body: request.toJson(),
    );
    return RefundReplacementConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }
}
