import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

class DeviceMetadata {
  const DeviceMetadata({
    required this.deviceIdentifier,
    required this.deviceName,
    required this.platform,
  });

  final String deviceIdentifier;
  final String deviceName;
  final String platform;

  Map<String, dynamic> toJson() => {
    'deviceIdentifier': deviceIdentifier,
    'deviceName': deviceName,
    'platform': platform,
  };
}

class DeviceMetadataService {
  DeviceMetadataService({FlutterSecureStorage? storage})
    : _storage = storage ?? const FlutterSecureStorage();

  static const deviceIdentifierStorageKey = 'identity.device-id.v1';

  final FlutterSecureStorage _storage;

  Future<DeviceMetadata> get() async {
    var identifier = await _storage.read(key: deviceIdentifierStorageKey);
    if (identifier == null) {
      final random = Random.secure();
      identifier = List.generate(
        32,
        (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
      ).join();
      await _storage.write(key: deviceIdentifierStorageKey, value: identifier);
    }

    return DeviceMetadata(
      deviceIdentifier: identifier,
      deviceName: 'DoodhDirect Flutter',
      platform: kIsWeb ? 'web' : defaultTargetPlatform.name,
    );
  }
}
