import 'dart:async';

import 'package:web/web.dart' as web;

Future<void> loadMapsScript(String apiKey) {
  if (apiKey.trim().isEmpty) {
    return Future.error(
      const MapsScriptLoaderException(
        'Google Maps API key is not configured for this Web build.',
      ),
    );
  }

  final existing = web.document.querySelector(
    'script[data-doodhdirect-google-maps="true"]',
  );
  if (existing != null) return Future<void>.value();

  final script = web.HTMLScriptElement()
    ..src = Uri.https('maps.googleapis.com', '/maps/api/js', {
      'key': apiKey,
    }).toString()
    ..async = true
    ..defer = true
    ..setAttribute('data-doodhdirect-google-maps', 'true');

  final completer = Completer<void>();
  script.onLoad.listen((_) {
    if (!completer.isCompleted) completer.complete();
  });
  script.onError.listen((_) {
    if (!completer.isCompleted) {
      completer.completeError(
        const MapsScriptLoaderException(
          'Google Maps JavaScript API failed to load. Check the API key, Maps JavaScript API enablement, billing, and localhost:51482 restriction.',
        ),
      );
    }
  });
  web.document.head?.append(script);
  return completer.future;
}

class MapsScriptLoaderException implements Exception {
  const MapsScriptLoaderException(this.message);

  final String message;

  @override
  String toString() => message;
}
