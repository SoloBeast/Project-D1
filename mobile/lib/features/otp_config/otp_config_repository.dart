import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/otp_config/otp_config_models.dart';

/// Admin repository for reading and updating the OTP provider configuration.
///
/// Only OWNER / SYSTEM_ADMIN roles are authorized (the backend enforces
/// `SETUP.OTP_PROVIDER.READ` / `SETUP.OTP_PROVIDER.MANAGE`). The MSG91
/// AuthKey is write-only: `get` never returns it and `update` only sends it
/// when the caller provides a new value.
class OtpConfigRepository {
  OtpConfigRepository({required this._api});

  final ApiClient _api;

  static const String _basePath = '/api/v1/admin/setup/otp-provider';

  /// Loads the current OTP provider configuration.
  ///
  /// The backend never returns the AuthKey, so the resulting
  /// [OtpProviderConfiguration] has no authKey field.
  Future<OtpProviderConfiguration> get(String accessToken) async {
    final response = await _api.get(_basePath, accessToken: accessToken);
    return OtpProviderConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Updates the OTP provider configuration.
  ///
  /// Every field in [request] is optional; omitted fields keep their current
  /// values on the backend. A non-null `authKey` in the request is the only
  /// time a key travels over the wire (write-only).
  Future<OtpProviderConfiguration> update(
    String accessToken,
    UpdateOtpProviderConfigurationRequest request,
  ) async {
    final response = await _api.put(
      _basePath,
      accessToken: accessToken,
      body: request.toJson(),
    );
    return OtpProviderConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Sends a test OTP through the configured provider.
  Future<OtpProviderTestResult> test(String accessToken) async {
    final response = await _api.post(
      '$_basePath/test',
      accessToken: accessToken,
    );
    return OtpProviderTestResult.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }
}
