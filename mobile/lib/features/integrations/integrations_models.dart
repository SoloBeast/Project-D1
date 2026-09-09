/// Effective integration configuration visible to System Setup → INTEGRATIONS.
///
/// This screen owns the mail ID used to send transactional emails (SMTP), the
/// Razorpay runtime credentials, and the two Google Maps keys plus base URL.
/// Secrets (SMTP password, Razorpay key secret/webhook secret, Google Maps
/// server-side API key) are WRITE-ONLY: the backend never returns them, so this
/// model exposes only their `...Configured` flags and the screen renders blank
/// secret fields on load. The Google Maps WEB CLIENT key is client-visible by
/// design: it is stored plainly, returned by the backend, and rendered in the
/// field so the web app can load maps at runtime.
class IntegrationConfiguration {
  const IntegrationConfiguration({
    this.emailFromAddress,
    this.emailFromName,
    this.emailHost,
    this.emailPort = 587,
    this.emailUserName,
    this.emailUseSsl = true,
    required this.emailPasswordConfigured,
    required this.emailIsConfigured,
    this.inviteUrlBase,
    this.razorpayKeyId,
    required this.razorpayKeySecretConfigured,
    required this.razorpayWebhookSecretConfigured,
    required this.razorpayIsConfigured,
    this.googleMapsBaseUrl,
    required this.googleMapsApiKeyConfigured,
    required this.googleMapsIsConfigured,
    this.googleMapsWebClientKey,
  });

  factory IntegrationConfiguration.fromJson(Map<String, dynamic> json) =>
      IntegrationConfiguration(
        emailFromAddress: _nullableString(json['emailFromAddress']),
        emailFromName: _nullableString(json['emailFromName']),
        emailHost: _nullableString(json['emailHost']),
        emailPort: json['emailPort'] as int? ?? 587,
        emailUserName: _nullableString(json['emailUserName']),
        emailUseSsl: json['emailUseSsl'] as bool? ?? true,
        emailPasswordConfigured:
            json['emailPasswordConfigured'] as bool? ?? false,
        emailIsConfigured: json['emailIsConfigured'] as bool? ?? false,
        inviteUrlBase: _nullableString(json['inviteUrlBase']),
        razorpayKeyId: _nullableString(json['razorpayKeyId']),
        razorpayKeySecretConfigured:
            json['razorpayKeySecretConfigured'] as bool? ?? false,
        razorpayWebhookSecretConfigured:
            json['razorpayWebhookSecretConfigured'] as bool? ?? false,
        razorpayIsConfigured: json['razorpayIsConfigured'] as bool? ?? false,
        googleMapsBaseUrl: _nullableString(json['googleMapsBaseUrl']),
        googleMapsApiKeyConfigured:
            json['googleMapsApiKeyConfigured'] as bool? ?? false,
        googleMapsIsConfigured: json['googleMapsIsConfigured'] as bool? ?? false,
        googleMapsWebClientKey: _nullableString(json['googleMapsWebClientKey']),
      );

  final String? emailFromAddress;
  final String? emailFromName;
  final String? emailHost;
  final int emailPort;
  final String? emailUserName;
  final bool emailUseSsl;
  final bool emailPasswordConfigured;
  final bool emailIsConfigured;
  final String? inviteUrlBase;
  final String? razorpayKeyId;
  final bool razorpayKeySecretConfigured;
  final bool razorpayWebhookSecretConfigured;
  final bool razorpayIsConfigured;
  final String? googleMapsBaseUrl;
  final bool googleMapsApiKeyConfigured;
  final bool googleMapsIsConfigured;

  /// The client-visible Google Maps web key used by Flutter Web to load maps.
  /// Unlike the server-side key it is NOT write-only: the backend returns it
  /// so the admin can see (and edit) the exact value the browser will use.
  final String? googleMapsWebClientKey;

  /// The email section is ready to deliver when the backend resolved a
  /// from-address + host + valid port (from the database override or the
  /// environment fallback).
  bool get isEmailConfigured => emailIsConfigured;

  /// The Razorpay section is ready when keyId + key secret are available.
  bool get isRazorpayConfigured => razorpayIsConfigured;

  /// The Google Maps section is ready when an API key + absolute base URL are
  /// available.
  bool get isGoogleMapsConfigured => googleMapsIsConfigured;

  static String? _nullableString(Object? value) {
    if (value == null) return null;
    final string = value as String;
    return string.isEmpty ? null : string;
  }
}

/// Payload for updating the integration settings.
///
/// Every field is optional. An omitted (null) field keeps the current value on
/// the backend. A provided EMPTY string CLEARS the database override so the
/// environment/appsettings fallback applies again — so empty strings ARE sent,
/// unlike nulls. Secrets are write-only: they are sent only when the admin
/// typed a new value; an omitted secret preserves the stored one.
class UpdateIntegrationConfigurationRequest {
  const UpdateIntegrationConfigurationRequest({
    this.emailFromAddress,
    this.emailFromName,
    this.emailHost,
    this.emailPort,
    this.emailUserName,
    this.emailPassword,
    this.emailUseSsl,
    this.inviteUrlBase,
    this.razorpayKeyId,
    this.razorpayKeySecret,
    this.razorpayWebhookSecret,
    this.googleMapsApiKey,
    this.googleMapsBaseUrl,
    this.googleMapsWebClientKey,
  });

  final String? emailFromAddress;
  final String? emailFromName;
  final String? emailHost;
  final int? emailPort;
  final String? emailUserName;
  final String? emailPassword;
  final bool? emailUseSsl;
  final String? inviteUrlBase;
  final String? razorpayKeyId;
  final String? razorpayKeySecret;
  final String? razorpayWebhookSecret;
  final String? googleMapsApiKey;
  final String? googleMapsBaseUrl;
  final String? googleMapsWebClientKey;

  Map<String, dynamic> toJson() {
    final json = <String, dynamic>{};
    final fields = <String, String?>{
      'emailFromAddress': emailFromAddress,
      'emailFromName': emailFromName,
      'emailHost': emailHost,
      'emailUserName': emailUserName,
      'emailPassword': emailPassword,
      'inviteUrlBase': inviteUrlBase,
      'razorpayKeyId': razorpayKeyId,
      'razorpayKeySecret': razorpayKeySecret,
      'razorpayWebhookSecret': razorpayWebhookSecret,
      'googleMapsApiKey': googleMapsApiKey,
      'googleMapsBaseUrl': googleMapsBaseUrl,
      'googleMapsWebClientKey': googleMapsWebClientKey,
    };
    fields.forEach((key, value) {
      if (value != null) json[key] = value.trim();
    });
    if (emailPort != null) json['emailPort'] = emailPort;
    if (emailUseSsl != null) json['emailUseSsl'] = emailUseSsl;
    return json;
  }
}

/// Result of sending a test email through the configured SMTP relay.
class IntegrationTestResult {
  const IntegrationTestResult({required this.success, required this.message});

  factory IntegrationTestResult.fromJson(Map<String, dynamic> json) =>
      IntegrationTestResult(
        success: json['success'] as bool? ?? false,
        message: json['message'] as String? ?? '',
      );

  final bool success;
  final String message;
}
