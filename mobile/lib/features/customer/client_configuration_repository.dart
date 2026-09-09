import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Client-safe runtime configuration fetched from the backend.
///
/// This deliberately carries ONLY the values the mobile/web client is allowed
/// to see. The backend's client-config endpoint never exposes secrets such as
/// the Google Maps Server-Side key, JWT signing key, SMTP credentials or
/// MSG91 AuthKey.
class ClientConfiguration {
  const ClientConfiguration({this.googleMapsWebClientKey});

  /// Browser/Flutter-Web Google Maps key. Null when the operator has not
  /// configured one (the UI then shows a clear configuration error).
  final String? googleMapsWebClientKey;

  factory ClientConfiguration.fromJson(Map<String, dynamic> json) {
    final webClientKey = json['googleMapsWebClientKey'];
    return ClientConfiguration(
      googleMapsWebClientKey: webClientKey is String && webClientKey.isNotEmpty
          ? webClientKey
          : null,
    );
  }
}

class ClientConfigurationRepository {
  ClientConfigurationRepository({required this.api});

  final ApiClient api;

  Future<ClientConfiguration> get(String token) async {
    final response = await api.get(
      '/api/v1/client-config',
      accessToken: token,
    );
    final data = response['data'];
    return ClientConfiguration.fromJson(
      data is Map<String, dynamic> ? data : const <String, dynamic>{},
    );
  }
}

final clientConfigurationRepositoryProvider = Provider<ClientConfigurationRepository>(
  (ref) => ClientConfigurationRepository(api: authenticatedApiClient(ref)),
);
