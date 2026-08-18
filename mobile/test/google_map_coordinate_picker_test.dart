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
            StateError('Google Maps API key is not configured for this Web build.'),
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
}
