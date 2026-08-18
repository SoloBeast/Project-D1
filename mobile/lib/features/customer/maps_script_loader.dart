import 'dart:async';

import 'maps_script_loader_stub.dart'
    if (dart.library.js_interop) 'maps_script_loader_web.dart';

Future<void> loadGoogleMapsScript(String apiKey) => loadMapsScript(apiKey);
