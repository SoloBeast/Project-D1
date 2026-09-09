/// MSG91 OTP provider environment. Values match the backend contract
/// (`OtpProviderConfiguration.EnvironmentTest` / `EnvironmentProduction`).
enum OtpProviderEnvironment {
  test,
  production;

  static OtpProviderEnvironment? fromJson(Object? value) {
    switch (value) {
      case 'Test':
      case 'test':
        return OtpProviderEnvironment.test;
      case 'Production':
      case 'production':
        return OtpProviderEnvironment.production;
      default:
        return null;
    }
  }

  /// Backend value as serialized over the wire.
  String get apiValue {
    switch (this) {
      case OtpProviderEnvironment.test:
        return 'Test';
      case OtpProviderEnvironment.production:
        return 'Production';
    }
  }

  String get label => apiValue;
}

/// OTP provider configuration visible to Setup → OTP Provider.
///
/// The MSG91 AuthKey is write-only: the backend NEVER returns it, so this
/// model has no authKey field and the screen never renders one on load.
class OtpProviderConfiguration {
  const OtpProviderConfiguration({
    required this.provider,
    required this.enabled,
    this.widgetId,
    this.environment,
    required this.configured,
    required this.status,
  });

  factory OtpProviderConfiguration.fromJson(Map<String, dynamic> json) =>
      OtpProviderConfiguration(
        provider: json['provider'] as String? ?? 'MSG91',
        enabled: json['enabled'] as bool? ?? false,
        widgetId: _nullableString(json['widgetId']),
        environment: OtpProviderEnvironment.fromJson(json['environment']),
        configured: json['configured'] as bool? ?? false,
        status: json['status'] as String? ?? 'Not Configured',
      );

  final String provider;
  final bool enabled;
  final String? widgetId;
  final OtpProviderEnvironment? environment;
  final bool configured;
  final String status;

  static String? _nullableString(Object? value) {
    if (value == null) return null;
    final string = value as String;
    return string.isEmpty ? null : string;
  }
}

/// Payload for updating the OTP provider configuration. Every field is
/// optional; an omitted field keeps its current value on the backend.
///
/// `authKey` is write-only — send it only when the user typed a new key. An
/// omitted (null) authKey preserves the stored key.
class UpdateOtpProviderConfigurationRequest {
  const UpdateOtpProviderConfigurationRequest({
    this.enabled,
    this.widgetId,
    this.authKey,
    this.environment,
  });

  final bool? enabled;
  final String? widgetId;
  final String? authKey;
  final OtpProviderEnvironment? environment;

  Map<String, dynamic> toJson() => {
    if (enabled != null) 'enabled': enabled,
    if (widgetId != null && widgetId!.trim().isNotEmpty)
      'widgetId': widgetId!.trim(),
    if (authKey != null && authKey!.trim().isNotEmpty)
      'authKey': authKey!.trim(),
    if (environment != null) 'environment': environment!.apiValue,
  };
}

/// Result of sending a test OTP through the configured provider.
class OtpProviderTestResult {
  const OtpProviderTestResult({required this.success, required this.message});

  factory OtpProviderTestResult.fromJson(Map<String, dynamic> json) =>
      OtpProviderTestResult(
        success: json['success'] as bool? ?? false,
        message: json['message'] as String? ?? '',
      );

  final bool success;
  final String message;
}
