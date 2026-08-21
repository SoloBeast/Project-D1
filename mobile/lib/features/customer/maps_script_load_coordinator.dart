import 'dart:async';

final class MapsScriptLoadCoordinator {
  Future<void>? _inFlight;

  Future<void> load(Future<void> Function() startLoading) {
    final inFlight = _inFlight;
    if (inFlight != null) {
      return inFlight;
    }

    late final Future<void> started;
    started = Future<void>.sync(startLoading);
    _inFlight = started;

    started.then<void>(
      (_) => _clearIfCurrent(started),
      onError: (Object error, StackTrace stackTrace) {
        _clearIfCurrent(started);
      },
    );
    return started;
  }

  void _clearIfCurrent(Future<void> completed) {
    if (identical(_inFlight, completed)) {
      _inFlight = null;
    }
  }
}
