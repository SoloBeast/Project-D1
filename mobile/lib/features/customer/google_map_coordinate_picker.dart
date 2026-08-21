import 'package:doodh_direct_mobile/features/customer/current_location_provider.dart';
import 'package:doodh_direct_mobile/features/customer/maps_script_loader.dart';
import 'package:flutter/material.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

const _googleMapsApiKey = String.fromEnvironment(
  'DOOHDIRECT_GOOGLE_MAPS_API_KEY',
);

const addressSelectionFallback = LatLng(28.367, 77.317);
const addressSelectionZoom = 15.0;

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
    this.currentLocationProvider = getCurrentLocation,
    this.mapsLoader,
    this.mapBuilder,
  });

  final LatLng? initialLocation;
  final ValueChanged<LatLng> onLocationSelected;
  final CurrentLocationProvider currentLocationProvider;
  final Future<void> Function()? mapsLoader;
  final CoordinateMapBuilder? mapBuilder;

  @override
  State<GoogleMapCoordinatePicker> createState() =>
      _GoogleMapCoordinatePickerState();
}

class _GoogleMapCoordinatePickerState extends State<GoogleMapCoordinatePicker> {
  static int _mapInstanceCount = 0;
  static int _mapBuildCount = 0;

  late final Future<void> _mapsReady;
  late LatLng _selectedLocation;
  final _mapHostKey = GlobalKey();
  GoogleMapController? _mapController;
  bool _mapReadyForLayout = false;
  bool _mapDiagnosticsScheduled = false;
  bool _isLocating = false;
  String? _locationError;

  @override
  void initState() {
    super.initState();
    _selectedLocation = widget.initialLocation ?? addressSelectionFallback;

    final mapsLoader = widget.mapsLoader;
    if (mapsLoader != null) {
      _mapsReady = mapsLoader();
    } else {
      _diagnostic(
        'maps key present=${_googleMapsApiKey.trim().isNotEmpty}',
      );
      _mapsReady = loadGoogleMapsScript(_googleMapsApiKey);
    }
  }

  @override
  Widget build(BuildContext context) {
    _diagnostic('picker build started');
    return SizedBox(
      key: _mapHostKey,
      height: 300,
      child: LayoutBuilder(
        builder: (context, constraints) {
          _diagnostic(
            'picker constraints width=${constraints.maxWidth} '
            'height=${constraints.maxHeight} '
            'boundedWidth=${constraints.hasBoundedWidth} '
            'boundedHeight=${constraints.hasBoundedHeight}',
          );
          return FutureBuilder<void>(
            future: _mapsReady,
            builder: (context, snapshot) {
              if (snapshot.connectionState != ConnectionState.done) {
                _diagnostic('maps future state=${snapshot.connectionState}');
                return const Center(child: CircularProgressIndicator());
              }
              if (snapshot.hasError) {
                _diagnostic('maps future error=${snapshot.error}');
                return _MapStatePanel(message: '${snapshot.error}');
              }
              if (!_mapReadyForLayout) {
                _scheduleMapWhenHostIsReady();
                return const Center(child: CircularProgressIndicator());
              }
              _diagnostic('map widget creation started');
              _diagnostic(
                'marker creation completed count=1 '
                'position=${_selectedLocation.latitude},${_selectedLocation.longitude}',
              );
              _mapBuildCount++;
              _diagnostic('map widget build count=$_mapBuildCount');
              final map = widget.mapBuilder?.call(
                    _selectedLocation,
                    _selectLocation,
                    _moveCamera,
                  ) ??
                  GoogleMap(
                    initialCameraPosition: CameraPosition(
                      target: _selectedLocation,
                      zoom: addressSelectionZoom,
                    ),
                    markers: {
                      Marker(
                        markerId: const MarkerId('selected-address'),
                        position: _selectedLocation,
                      ),
                    },
                    onMapCreated: _restoreInitialCamera,
                    onTap: _selectLocation,
                    onCameraMove: _moveCamera,
                    zoomControlsEnabled: true,
                    mapToolbarEnabled: false,
                  );
              return Stack(
                children: [
                  Positioned.fill(child: map),
                  Positioned(
                    top: 8,
                    left: 8,
                    right: 8,
                    child: Align(
                      alignment: Alignment.topRight,
                      child: OutlinedButton.icon(
                        onPressed: _isLocating ? null : _useCurrentLocation,
                        icon: _isLocating
                            ? const SizedBox.square(
                                dimension: 16,
                                child: CircularProgressIndicator(strokeWidth: 2),
                              )
                            : const Icon(Icons.my_location_outlined),
                        label: const Text('Use my current location'),
                      ),
                    ),
                  ),
                  if (_locationError != null)
                    Positioned(
                      left: 8,
                      right: 8,
                      bottom: 8,
                      child: Material(
                        color: Theme.of(context).colorScheme.errorContainer,
                        borderRadius: BorderRadius.circular(8),
                        child: Padding(
                          padding: const EdgeInsets.all(10),
                          child: Text(
                            _locationError!,
                            textAlign: TextAlign.center,
                            style: TextStyle(
                              color: Theme.of(context)
                                  .colorScheme
                                  .onErrorContainer,
                            ),
                          ),
                        ),
                      ),
                    ),
                ],
              );
            },
          );
        },
      ),
    );
  }

  void _scheduleMapWhenHostIsReady() {
    if (_mapDiagnosticsScheduled) return;
    _mapDiagnosticsScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _mapDiagnosticsScheduled = false;
      if (!mounted || _mapReadyForLayout) return;
      final renderObject = _mapHostKey.currentContext?.findRenderObject();
      if (renderObject is RenderBox) {
        _diagnostic(
          'Flutter host attached=${renderObject.attached} '
          'hasSize=${renderObject.hasSize} '
          'width=${renderObject.hasSize ? renderObject.size.width : 0} '
          'height=${renderObject.hasSize ? renderObject.size.height : 0}',
        );
        if (renderObject.attached &&
            renderObject.hasSize &&
            renderObject.size.width > 0 &&
            renderObject.size.height > 0) {
          setState(() => _mapReadyForLayout = true);
          _diagnostic('Flutter host became layout-ready');
          return;
        }
      } else {
        _diagnostic('Flutter host render object unavailable');
      }
      _scheduleMapWhenHostIsReady();
    });
  }

  void _restoreInitialCamera(GoogleMapController controller) {
    _mapController = controller;
    _mapInstanceCount++;
    _diagnostic(
      'map creation completed instanceCount=$_mapInstanceCount '
      'center=${_selectedLocation.latitude},${_selectedLocation.longitude} '
      'zoom=$addressSelectionZoom',
    );
    controller.moveCamera(
      CameraUpdate.newCameraPosition(
        CameraPosition(
          target: _selectedLocation,
          zoom: addressSelectionZoom,
        ),
      ),
    );
    _diagnostic('initial camera restoration requested');
  }

  void _selectLocation(LatLng location) {
    if (!isValidCoordinate(location)) {
      _showLocationError(
        const CurrentLocationException(CurrentLocationFailure.invalid),
      );
      return;
    }
    _diagnostic(
      'map tap location=${location.latitude},${location.longitude}',
    );
    setState(() {
      _selectedLocation = location;
      _locationError = null;
    });
    widget.onLocationSelected(location);
  }

  Future<void> _useCurrentLocation() async {
    setState(() {
      _isLocating = true;
      _locationError = null;
    });
    try {
      final location = await widget.currentLocationProvider();
      if (!isValidCoordinate(location)) {
        throw const CurrentLocationException(CurrentLocationFailure.invalid);
      }
      if (!mounted) return;
      _selectLocation(location);
      await _mapController?.animateCamera(
        CameraUpdate.newCameraPosition(
          CameraPosition(target: location, zoom: addressSelectionZoom),
        ),
      );
    } on CurrentLocationException catch (error) {
      if (mounted) _showLocationError(error);
    } catch (_) {
      if (mounted) {
        _showLocationError(
          const CurrentLocationException(CurrentLocationFailure.general),
        );
      }
    } finally {
      if (mounted) setState(() => _isLocating = false);
    }
  }

  void _showLocationError(CurrentLocationException error) {
    setState(() => _locationError = error.friendlyMessage);
  }

  void _moveCamera(CameraPosition position) {
    _selectedLocation = position.target;
    _diagnostic(
      'camera move center=${position.target.latitude},${position.target.longitude} '
      'zoom=${position.zoom}',
    );
  }

  void _diagnostic(String message) {
    assert(() {
      debugPrint('[GoogleMapDiagnostic] $message');
      return true;
    }());
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
