import 'dart:convert';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Last-known-good branding persistence.
///
/// Startup must never depend on the network: when the branding API is
/// unreachable (backend offline, first run offline, request timeout), the
/// most recently fetched configuration and the animation bytes are replayed
/// from this store. Corrupt or unknown-version payloads are discarded safely
/// so a malformed cache can never block startup.
///
/// Everything is kept in [FlutterSecureStorage] alongside the session — the
/// same device-local primitive the auth and guest-cart features use. No new
/// package is required.
class BrandingCache {
  BrandingCache({FlutterSecureStorage? storage})
    : _storage = storage ?? const FlutterSecureStorage();

  static const _metadataKey = 'branding.config.v1';
  static const _logoBytesKey = 'branding.logo.bytes.v1';
  static const _animationBytesKey = 'branding.animation.bytes.v1';
  static const _version = 1;

  final FlutterSecureStorage _storage;

  Future<BrandingConfiguration?> readConfiguration() async {
    final encoded = await _storage.read(key: _metadataKey);
    if (encoded == null || encoded.isEmpty) return null;
    try {
      final root = jsonDecode(encoded);
      if (root is! Map<String, dynamic> || root['version'] != _version) {
        return null;
      }
      final data = root['data'];
      if (data is! Map<String, dynamic>) return null;
      return BrandingConfiguration.fromJson(data);
    } on FormatException {
      return null;
    } on Object {
      // A damaged cache must never break startup — treat as empty.
      return null;
    }
  }

  Future<void> writeConfiguration(BrandingConfiguration configuration) async {
    final encoded = jsonEncode(<String, dynamic>{
      'version': _version,
      'data': configuration.toJson(),
    });
    await _storage.write(key: _metadataKey, value: encoded);
  }

  Future<Uint8List?> readLogoBytes() => _readBytes(_logoBytesKey);

  Future<Uint8List?> readAnimationBytes() => _readBytes(_animationBytesKey);

  Future<void> writeLogoBytes(Uint8List bytes) =>
      _storage.write(key: _logoBytesKey, value: base64Encode(bytes));

  Future<void> writeAnimationBytes(Uint8List bytes) =>
      _storage.write(key: _animationBytesKey, value: base64Encode(bytes));

  /// Drops the cached asset bytes (used when the server reports the asset was
  /// removed). The metadata row is kept because it still describes the other
  /// asset.
  Future<void> clearLogoBytes() => _storage.delete(key: _logoBytesKey);

  Future<void> clearAnimationBytes() =>
      _storage.delete(key: _animationBytesKey);

  Future<Uint8List?> _readBytes(String key) async {
    final encoded = await _storage.read(key: key);
    if (encoded == null || encoded.isEmpty) return null;
    try {
      return base64Decode(encoded);
    } on FormatException {
      return null;
    }
  }
}

final brandingCacheProvider = Provider<BrandingCache>(
  (ref) => BrandingCache(),
);
