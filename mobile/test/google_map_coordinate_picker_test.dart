import 'dart:async';

import 'package:doodh_direct_mobile/features/customer/current_location_provider.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
import 'package:flutter/material.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('shows a configuration error when Maps has no API key', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          onLocationSelected: (_) {},
          mapsLoader: () => Future.error(
            StateError(
              'Google Maps API key is not configured for this Web build.',
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Google Maps API key is not configured'),
      findsOneWidget,
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('shows a loading state while Maps is loading', (tester) async {
    final load = Completer<void>();
    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          onLocationSelected: (_) {},
          mapsLoader: () => load.future,
        ),
      ),
    );

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    load.complete();
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  testWidgets('passes a selected map coordinate to the address form callback', (
    tester,
  ) async {
    LatLng? selected;
    LatLng? initialCameraTarget;
    const pickedLocation = LatLng(28.4089, 77.3178);

    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          onLocationSelected: (location) => selected = location,
          mapsLoader: () async {},
          mapBuilder: (location, onLocationSelected, onCameraMove) {
            initialCameraTarget = location;
            return ElevatedButton(
              onPressed: () => onLocationSelected(pickedLocation),
              child: const Text('Select map location'),
            );
          },
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(initialCameraTarget, const LatLng(28.367, 77.317));
    await tester.tap(find.text('Select map location'));
    await tester.pump();

    expect(selected, pickedLocation);
    expect(tester.takeException(), isNull);
  });

  testWidgets('uses a valid saved coordinate and tracks camera movement', (
    tester,
  ) async {
    const savedLocation = LatLng(28.4595, 77.0266);
    LatLng? cameraLocation;
    LatLng? selected;
    var providerCalls = 0;

    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          initialLocation: savedLocation,
          onLocationSelected: (location) => selected = location,
          currentLocationProvider: () async {
            providerCalls++;
            return const LatLng(28.6, 77.4);
          },
          mapsLoader: () async {},
          mapBuilder: (location, onLocationSelected, onCameraMove) {
            cameraLocation = location;
            return ElevatedButton(
              onPressed: () => onCameraMove(
                const CameraPosition(target: LatLng(28.41, 77.32), zoom: 14),
              ),
              child: const Text('Move map'),
            );
          },
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(cameraLocation, savedLocation);
    expect(providerCalls, 0);
    await tester.tap(find.text('Move map'));
    await tester.pump();
    expect(selected, isNull);
    expect(providerCalls, 0);
    expect(tester.takeException(), isNull);
  });

  testWidgets('uses current location only after explicit action', (tester) async {
    const currentLocation = LatLng(28.6129, 77.2295);
    LatLng? selected;
    var providerCalls = 0;

    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          onLocationSelected: (location) => selected = location,
          currentLocationProvider: () async {
            providerCalls++;
            return currentLocation;
          },
          mapsLoader: () async {},
          mapBuilder: (_, _, _) => const SizedBox.expand(),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(providerCalls, 0);
    expect(selected, isNull);
    await tester.tap(find.text('Use my current location'));
    await tester.pumpAndSettle();

    expect(providerCalls, 1);
    expect(selected, currentLocation);
    expect(find.text('Use my current location'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('shows a friendly message when location permission is denied', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          onLocationSelected: (_) {},
          currentLocationProvider: () async {
            throw const CurrentLocationException(
              CurrentLocationFailure.permissionDenied,
            );
          },
          mapsLoader: () async {},
          mapBuilder: (_, _, _) => const SizedBox.expand(),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Use my current location'));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Location permission was denied'),
      findsOneWidget,
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('rejects invalid coordinates from the location provider', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          onLocationSelected: (_) {},
          currentLocationProvider: () async =>
              const LatLng(double.nan, 77.317),
          mapsLoader: () async {},
          mapBuilder: (_, _, _) => const SizedBox.expand(),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Use my current location'));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('The device returned an invalid location'),
      findsOneWidget,
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('keeps the picker within a narrow screen', (tester) async {
    await tester.binding.setSurfaceSize(const Size(320, 640));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(
        home: GoogleMapCoordinatePicker(
          onLocationSelected: (_) {},
          mapsLoader: () async {},
          mapBuilder: (_, _, _) => const SizedBox.expand(),
        ),
      ),
    );
    await tester.pumpAndSettle();

    final picker = tester.getSize(find.byType(GoogleMapCoordinatePicker));
    expect(picker.width, lessThanOrEqualTo(320));
    expect(picker.height, lessThanOrEqualTo(640));
    expect(tester.takeException(), isNull);
  });
}
