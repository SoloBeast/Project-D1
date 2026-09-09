import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/integrations/integrations_models.dart';

/// Admin repository for reading and updating the INTEGRATION settings.
///
/// Only OWNER / SYSTEM_ADMIN roles are authorized (the backend enforces
/// `SETUP.INTEGRATIONS.READ` / `SETUP.INTEGRATIONS.MANAGE`). Every secret
/// (SMTP password, Razorpay key secret/webhook secret, Google Maps API key) is
/// write-only: `get` never returns it and `update` only sends a secret when
/// the caller provides a new value. An empty string in the request CLEARS the
/// database override so the environment fallback applies again.
class IntegrationsRepository {
  IntegrationsRepository({required this._api});

  final ApiClient _api;

  static const String _basePath = '/api/v1/admin/setup/integrations';

  /// Loads the current integration configuration.
  ///
  /// The backend never returns stored secrets, so the resulting
  /// [IntegrationConfiguration] only carries their `...Configured` flags.
  Future<IntegrationConfiguration> get(String accessToken) async {
    final response = await _api.get(_basePath, accessToken: accessToken);
    return IntegrationConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Updates the integration configuration.
  ///
  /// Every field in [request] is optional; omitted (null) fields keep their
  /// current values on the backend. Non-null secrets travel over the wire only
  /// when the admin typed a new value (write-only).
  Future<IntegrationConfiguration> update(
    String accessToken,
    UpdateIntegrationConfigurationRequest request,
  ) async {
    final response = await _api.put(
      _basePath,
      accessToken: accessToken,
      body: request.toJson(),
    );
    return IntegrationConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Sends a test email through the configured SMTP relay.
  Future<IntegrationTestResult> test(String accessToken) async {
    final response = await _api.post(
      '$_basePath/test',
      accessToken: accessToken,
    );
    return IntegrationTestResult.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }
}
