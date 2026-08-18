import 'dart:async';

Future<void> loadMapsScript(String apiKey) async {
  if (apiKey.trim().isEmpty) {
    throw const MapsScriptLoaderException(
      'Google Maps API key is not configured for this Web build.',
    );
  }
  throw const MapsScriptLoaderException(
    'Google Maps is available only in the Flutter Web build.',
  );
}

class MapsScriptLoaderException implements Exception {
  const MapsScriptLoaderException(this.message);

  final String message;

  @override
  String toString() => message;
}
