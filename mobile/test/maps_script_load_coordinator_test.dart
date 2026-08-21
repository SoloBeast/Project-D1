import 'dart:async';

import 'package:doodh_direct_mobile/features/customer/maps_script_load_coordinator.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('shares one in-flight load with concurrent callers', () async {
    final coordinator = MapsScriptLoadCoordinator();
    final load = Completer<void>();
    var starts = 0;

    final first = coordinator.load(() {
      starts++;
      return load.future;
    });
    final second = coordinator.load(() {
      starts++;
      return Future<void>.value();
    });

    expect(identical(first, second), isTrue);
    expect(starts, 1);
    load.complete();
    await Future.wait([first, second]);
  });

  test('propagates failure and allows a later retry', () async {
    final coordinator = MapsScriptLoadCoordinator();
    var starts = 0;

    final failure = coordinator.load(() {
      starts++;
      return Future<void>.error(StateError('script failed'));
    });

    await expectLater(failure, throwsA(isA<StateError>()));

    final retry = coordinator.load(() {
      starts++;
      return Future<void>.value();
    });
    await retry;

    expect(starts, 2);
  });
}
