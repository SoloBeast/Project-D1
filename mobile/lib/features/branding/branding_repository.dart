import 'dart:async';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Repository for the branding API.
///
/// Public methods are anonymous by design — startup happens before Login and
/// the backend serves `/api/v1/branding` with [AllowAnonymous]. Admin methods
/// require an authenticated Owner/SystemAdmin token (the backend enforces
/// `SETUP.BRANDING.READ` / `SETUP.BRANDING.MANAGE`).
class BrandingRepository {
  BrandingRepository({required this._api});

  final ApiClient _api;

  static const String _publicBasePath = '/api/v1/branding';
  static const String _adminBasePath = '/api/v1/admin/setup/branding';

  /// Loads the active public branding. Returns a configuration with null
  /// members when nothing is configured.
  Future<BrandingConfiguration> getPublic() async {
    final response = await _api.get(_publicBasePath);
    return BrandingConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  /// Fetches the active logo (or animation) bytes. Throws
  /// [ApiException] with statusCode 404 when the asset is not configured —
  /// callers treat that as "use the fallback", not as an error.
  Future<ApiByteResponse> getAssetBytes(String relativeUrl) =>
      _api.getBytes(relativeUrl);

  Future<BrandingConfiguration> getForAdministration(String accessToken) async {
    final response = await _api.get(_adminBasePath, accessToken: accessToken);
    return BrandingConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<BrandingConfiguration> uploadLogo(
    String accessToken, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    final response = await _api.putMultipart(
      '$_adminBasePath/logo',
      fieldName: 'File',
      bytes: bytes,
      fileName: fileName,
      contentType: contentType,
      accessToken: accessToken,
    );
    return BrandingConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<BrandingConfiguration> uploadStartupAnimation(
    String accessToken, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    final response = await _api.putMultipart(
      '$_adminBasePath/startup-animation',
      fieldName: 'File',
      bytes: bytes,
      fileName: fileName,
      contentType: contentType,
      accessToken: accessToken,
    );
    return BrandingConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<BrandingConfiguration> removeLogo(String accessToken) async {
    final response = await _api.delete(
      '$_adminBasePath/logo',
      accessToken: accessToken,
    );
    return BrandingConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }

  Future<BrandingConfiguration> removeStartupAnimation(
    String accessToken,
  ) async {
    final response = await _api.delete(
      '$_adminBasePath/startup-animation',
      accessToken: accessToken,
    );
    return BrandingConfiguration.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }
}

final brandingRepositoryProvider = Provider<BrandingRepository>(
  (ref) => BrandingRepository(api: authenticatedApiClient(ref)),
);
