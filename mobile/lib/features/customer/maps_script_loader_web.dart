import 'dart:async';
import 'dart:js_interop';
import 'dart:js_interop_unsafe';

import 'package:flutter/foundation.dart';
import 'package:web/web.dart' as web;

import 'maps_script_load_coordinator.dart';

final _mapsScriptLoad = MapsScriptLoadCoordinator();
int _scriptLoadAttemptCount = 0;
String? _lastPlatformViewSignature;
Timer? _platformViewMonitor;

Future<void> loadMapsScript(String apiKey) {
  _diagnostic('maps loader key present=${apiKey.trim().isNotEmpty}');

  if (apiKey.trim().isEmpty) {
    return Future.error(
      const MapsScriptLoaderException(
        'Google Maps API key is not configured for this Web build.',
      ),
    );
  }

  _scriptLoadAttemptCount++;
  _diagnostic(
    'script load requested attempt=$_scriptLoadAttemptCount '
    'keyConfigured=true',
  );
  return _mapsScriptLoad.load(() => _loadMapsScript(apiKey));
}

Future<void> _loadMapsScript(String apiKey) {
  final existing = web.document.querySelector(
    'script[data-doodhdirect-google-maps="true"]',
  );
  final existingScript = existing?.isA<web.HTMLScriptElement>() == true
      ? existing as web.HTMLScriptElement
      : null;
  _diagnostic(
    'script lookup existing=${existingScript != null} '
    'loadedMarker=${existingScript?.getAttribute('data-doodhdirect-google-maps-loaded') == 'true'} '
    'isConnected=${existingScript?.isConnected}',
  );
  if (existingScript?.getAttribute('data-doodhdirect-google-maps-loaded') ==
      'true') {
    _diagnostic('reusing previously loaded script');
    _reportGoogleReadiness('previously loaded script');
    _startPlatformViewMonitor();
    return Future<void>.value();
  }

  final script =
      existingScript ??
      (web.HTMLScriptElement()
        ..src = Uri.https('maps.googleapis.com', '/maps/api/js', {
          'key': apiKey,
        }).toString()
        ..async = true
        ..defer = true
        ..setAttribute('data-doodhdirect-google-maps', 'true'));

  _diagnostic(
    existingScript == null
        ? 'injecting new Google Maps script'
        : 'reusing in-flight existing Google Maps script',
  );
  final completer = Completer<void>();
  script.onLoad.listen((_) {
    script.setAttribute('data-doodhdirect-google-maps-loaded', 'true');
    _diagnostic('script load event completed isConnected=${script.isConnected}');
    _reportGoogleReadiness('script load event');
    _startPlatformViewMonitor();
    if (!completer.isCompleted) completer.complete();
  });
  script.onError.listen((_) {
    _diagnostic('script error event completed isConnected=${script.isConnected}');
    _reportGoogleReadiness('script error event');
    if (!completer.isCompleted) {
      completer.completeError(
        const MapsScriptLoaderException(
          'Google Maps JavaScript API failed to load. Check the API key, Maps JavaScript API enablement, billing, network connection, and allowed Web origin.',
        ),
      );
    }
  });

  if (existingScript == null) {
    web.document.head?.append(script);
    _diagnostic('script appended isConnected=${script.isConnected}');
  }
  return completer.future;
}

void _reportGoogleReadiness(String stage) {
  final googleExists = globalContext.hasProperty('google'.toJS).toDart;
  final google = googleExists ? globalContext['google'] : null;
  final mapsExists = google?.isA<JSObject>() == true &&
      (google as JSObject).hasProperty('maps'.toJS).toDart;
  _diagnostic(
    'JavaScript readiness stage="$stage" googleExists=$googleExists '
    'googleMapsExists=$mapsExists',
  );
}

void _startPlatformViewMonitor() {
  assert(() {
    _reportPlatformViews('monitor started');
    _platformViewMonitor ??= Timer.periodic(
      const Duration(seconds: 2),
      (_) => _reportPlatformViews('monitor tick'),
    );
    return true;
  }());
}

void _reportPlatformViews(String stage) {
  final elements = web.document.querySelectorAll('flt-platform-view');
  final descriptions = <String>[];
  for (var index = 0; index < elements.length; index++) {
    final node = elements.item(index);
    if (node == null || !node.isA<web.Element>()) continue;
    final element = node as web.Element;
    final rect = element.getBoundingClientRect();
    final style = web.window.getComputedStyle(element);
    descriptions.add(
      '#$index tag=${element.tagName} isConnected=${element.isConnected} '
      'rectWidth=${rect.width} rectHeight=${rect.height} '
      'computedWidth=${style.width} computedHeight=${style.height} '
      'display=${style.display} visibility=${style.visibility} '
      'opacity=${style.opacity}',
    );
  }
  final signature = descriptions.join(' | ');
  final changed = _lastPlatformViewSignature != null &&
      _lastPlatformViewSignature != signature;
  _diagnostic(
    'platform views stage="$stage" count=${elements.length} '
    'replacedOrDetached=$changed details=[$signature]',
  );
  _lastPlatformViewSignature = signature;
}

void _diagnostic(String message) {
  assert(() {
    debugPrint('[GoogleMapsWebDiagnostic] $message');
    return true;
  }());
}

class MapsScriptLoaderException implements Exception {
  const MapsScriptLoaderException(this.message);

  final String message;

  @override
  String toString() => message;
}
