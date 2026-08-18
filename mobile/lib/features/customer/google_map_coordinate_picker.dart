import 'package:doodh_direct_mobile/features/customer/maps_script_loader.dart';
import 'package:flutter/material.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

const _googleMapsApiKey = String.fromEnvironment(
  'DOOHDIRECT_GOOGLE_MAPS_API_KEY',
);

const _faridabad = LatLng(28.367, 77.317);

typedef CoordinateMapBuilder = Widget Function(
  LatLng selectedLocation,
  ValueChanged<LatLng> onLocationSelected,
  ValueChanged<CameraPosition> onCameraMove,
);

class GoogleMapCoordinatePicker extends StatefulWidget {
  const GoogleMapCoordinatePicker({
    super.key,
    this.initialLocation,
    required this.onLocationSelected,
    this.mapsLoader,
    this.mapBuilder,
  });

  final LatLng? initialLocation;
  final ValueChanged<LatLng> onLocationSelected;
  final Future<void> Function()? mapsLoader;
  final CoordinateMapBuilder? mapBuilder;

  @override
  State<GoogleMapCoordinatePicker> createState() =>
      _GoogleMapCoordinatePickerState();
}

class _GoogleMapCoordinatePickerState extends State<GoogleMapCoordinatePicker> {
  late final Future<void> _mapsReady;
  late LatLng _selectedLocation;

  @override
  void initState() {
    super.initState();
    _selectedLocation = widget.initialLocation ?? _faridabad;
    _mapsReady =
        widget.mapsLoader?.call() ?? loadGoogleMapsScript(_googleMapsApiKey);
  }

  @override
  Widget build(BuildContext context) => SizedBox(
    height: 300,
    child: FutureBuilder<void>(
      future: _mapsReady,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError) {
          return _MapStatePanel(message: '${snapshot.error}');
        }
        return widget.mapBuilder?.call(
              _selectedLocation,
              _selectLocation,
              _moveCamera,
            ) ??
            GoogleMap(
              initialCameraPosition: CameraPosition(
                target: _selectedLocation,
                zoom: 14,
              ),
              markers: {
                Marker(
                  markerId: const MarkerId('selected-address'),
                  position: _selectedLocation,
                ),
              },
              onTap: _selectLocation,
              onCameraMove: _moveCamera,
              zoomControlsEnabled: true,
              mapToolbarEnabled: false,
            );
      },
    ),
  );

  void _selectLocation(LatLng location) {
    setState(() => _selectedLocation = location);
    widget.onLocationSelected(location);
  }

  void _moveCamera(CameraPosition position) {
    _selectedLocation = position.target;
  }
}

class _MapStatePanel extends StatelessWidget {
  const _MapStatePanel({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: Theme.of(context).colorScheme.surfaceContainerHighest,
      borderRadius: BorderRadius.circular(8),
    ),
    child: Center(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Text(message, textAlign: TextAlign.center),
      ),
    ),
  );
}
