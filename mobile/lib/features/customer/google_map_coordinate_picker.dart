import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/features/customer/current_location_provider.dart';
import 'package:doodh_direct_mobile/features/customer/location_permission.dart';
import 'package:flutter/material.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

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
    required this.mapsLoader,
    this.currentLocationProvider = getCurrentLocation,
    this.mapBuilder,
    this.locationPermissionService =
        const GeolocatorLocationPermissionService(),
  });

  final LatLng? initialLocation;
  final ValueChanged<LatLng> onLocationSelected;

  /// Loads the Google Maps JavaScript API (or resolves the platform default).
  ///
  /// The map is no longer bound to a compile-time `--dart-define` key. The
  /// owner supplies the runtime loader (see [maps_script_loader]) so the web
  /// client key can be fetched from the server at runtime and changed without
  /// rebuilding the app.
  final Future<void> Function() mapsLoader;
  final CurrentLocationProvider currentLocationProvider;
  final CoordinateMapBuilder? mapBuilder;

  /// Reads/requests foreground location access for the permission gate.
  /// Injected so tests can script every gate status without platform
  /// channels. The default uses Geolocator with foreground access only.
  final LocationPermissionService locationPermissionService;

  @override
  State<GoogleMapCoordinatePicker> createState() =>
      _GoogleMapCoordinatePickerState();
}

class _GoogleMapCoordinatePickerState extends State<GoogleMapCoordinatePicker>
    with WidgetsBindingObserver {
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

  /// Feature-triggered gate: checked (never requested) when the picker
  /// opens. The interactive map is built only while granted.
  LocationGateStatus _gateStatus = LocationGateStatus.checking;
  bool _permissionRequestInFlight = false;
  int _permissionAttempts = 0;

  @override
  void initState() {
    super.initState();
    _selectedLocation = widget.initialLocation ?? addressSelectionFallback;
    _mapsReady = widget.mapsLoader();
    WidgetsBinding.instance.addObserver(this);
    _refreshGateStatus();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Returning from Settings re-checks the OS status: access granted there
    // makes the interactive map available without another prompt.
    if (state == AppLifecycleState.resumed) _refreshGateStatus();
  }

  /// Reads the OS status without prompting. Permission is requested only
  /// from [_requestPermissionAccess], which runs solely after an explicit
  /// allow/retry tap.
  Future<void> _refreshGateStatus() async {
    final status = await widget.locationPermissionService.checkStatus();
    if (!mounted) return;
    setState(() => _gateStatus = status);
  }

  /// Requests foreground access after an explicit user action. A denial
  /// keeps the map unavailable with a non-blocking explanation and a retry
  /// action; the OS dialog is never re-triggered automatically.
  Future<void> _requestPermissionAccess() async {
    if (_permissionRequestInFlight) return;
    setState(() => _permissionRequestInFlight = true);
    try {
      final status = await widget.locationPermissionService.requestAccess();
      if (!mounted) return;
      setState(() {
        _gateStatus = status;
        _permissionAttempts++;
      });
    } finally {
      if (mounted) {
        setState(() => _permissionRequestInFlight = false);
      }
    }
  }

  Future<void> _openSettings() async {
    await widget.locationPermissionService.openSettings();
    // Platforms without a lifecycle pause never leave the app, so re-check
    // now as well (resumed re-checks also run via the observer).
    await _refreshGateStatus();
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
              // The interactive map (taps, drags, camera moves, coordinate
              // selection) is built only while permission is granted. Every
              // other status keeps a non-blocking gate panel so the rest of
              // the address form stays fully usable.
              switch (_gateStatus) {
                case LocationGateStatus.checking:
                  return const Center(child: CircularProgressIndicator());
                case LocationGateStatus.denied:
                  return _PermissionGatePanel(
                    icon: Icons.location_on_outlined,
                    title: _permissionAttempts == 0
                        ? 'Location access needed'
                        : 'Map unavailable without location access',
                    message: _permissionAttempts == 0
                        ? 'Allow location access to show the interactive map '
                              'and pick your delivery point. Your address '
                              'fields work without it.'
                        : 'Location access is still off, so the map stays '
                              'unavailable and your saved location is '
                              'unchanged. Retry whenever you are ready.',
                    primaryLabel: _permissionAttempts == 0
                        ? 'Allow location access'
                        : 'Try again',
                    busy: _permissionRequestInFlight,
                    onPrimary: _requestPermissionAccess,
                  );
                case LocationGateStatus.permanentlyDenied:
                  return _PermissionGatePanel(
                    icon: Icons.location_off_outlined,
                    title: 'Location is turned off',
                    message:
                        'System settings are blocking location for '
                        'DoodhDirect, so the map is unavailable. Open '
                        'settings to enable it, or keep filling your address '
                        'details manually.',
                    primaryLabel: 'Open settings',
                    busy: false,
                    onPrimary: _openSettings,
                    secondaryLabel: 'Check again',
                    onSecondary: _refreshGateStatus,
                  );
                case LocationGateStatus.unavailable:
                  return _PermissionGatePanel(
                    icon: Icons.location_off_outlined,
                    title: 'Location unavailable',
                    message:
                        'Current location is not available on this device or '
                        'browser, so the map cannot be shown. Your address '
                        'details are unaffected.',
                    primaryLabel: 'Check again',
                    busy: false,
                    onPrimary: _refreshGateStatus,
                  );
                case LocationGateStatus.granted:
                  break;
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
    // Rechecked on every tap: permission may have been revoked while the
    // map was open, and location services are reported separately from
    // permission denial.
    bool serviceEnabled;
    try {
      serviceEnabled =
          await widget.locationPermissionService.isServiceEnabled();
    } on Object {
      serviceEnabled = false;
    }
    if (!serviceEnabled) {
      if (!mounted) return;
      setState(() {
        _locationError =
            'Location services are turned off on this device. Turn them on, '
            'then try again.';
      });
      return;
    }
    final status = await widget.locationPermissionService.checkStatus();
    if (!mounted) return;
    if (status != LocationGateStatus.granted) {
      setState(() => _gateStatus = status);
      return;
    }
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
      // The provider never requests permission itself; a denial here means
      // access was revoked mid-use, so fall back to the gate.
      if (error.failure == CurrentLocationFailure.permissionDenied) {
        await _refreshGateStatus();
        if (!mounted) return;
      }
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

/// Non-blocking gate shown while the interactive map is unavailable.
/// The panel only explains and offers explicit actions; it never triggers
/// an OS prompt by itself and never blocks the surrounding address form.
class _PermissionGatePanel extends StatelessWidget {
  const _PermissionGatePanel({
    required this.icon,
    required this.title,
    required this.message,
    required this.primaryLabel,
    required this.onPrimary,
    required this.busy,
    this.secondaryLabel,
    this.onSecondary,
  });

  final IconData icon;
  final String title;
  final String message;
  final String primaryLabel;
  final Future<void> Function() onPrimary;
  final bool busy;
  final String? secondaryLabel;
  final Future<void> Function()? onSecondary;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: colors.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Center(
        child: SingleChildScrollView(
          padding: DoodhSpacing.cardPadding,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, size: 32, color: colors.onSurfaceVariant),
              const SizedBox(height: 8),
              Text(
                title,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.titleMedium,
              ),
              const SizedBox(height: 4),
              Text(
                message,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodyMedium,
              ),
              const SizedBox(height: 12),
              FilledButton(
                onPressed: busy ? null : () => onPrimary(),
                child: busy
                    ? const SizedBox.square(
                        dimension: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(primaryLabel),
              ),
              if (secondaryLabel != null && onSecondary != null)
                TextButton(
                  onPressed: () => onSecondary!(),
                  child: Text(secondaryLabel!),
                ),
            ],
          ),
        ),
      ),
    );
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
        padding: DoodhSpacing.cardPadding,
        child: Text(message, textAlign: TextAlign.center),
      ),
    ),
  );
}
