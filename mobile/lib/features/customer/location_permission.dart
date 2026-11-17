import 'package:geolocator/geolocator.dart';

/// Gate status for the feature-triggered map/current-location flow.
///
/// Location permission is NEVER requested at app launch, session restore,
/// login, registration, guest access or home loading. It is only checked
/// (never requested) when the user opens a map-bearing screen, and it is
/// requested only after the user taps an explicit allow/retry action in the
/// map permission gate.
enum LocationGateStatus {
  /// The OS status has not been read yet.
  checking,

  /// `whileInUse` or `always`: the interactive map may be shown.
  granted,

  /// Retryable denial: show an explanation with an explicit retry action.
  /// Never re-trigger the OS dialog automatically.
  denied,

  /// OS-level permanent denial (or equivalent): the map stays unavailable,
  /// explain that Settings is required and offer an Open Settings action.
  permanentlyDenied,

  /// Location is unavailable on this device/browser (services off where the
  /// platform reports it as an error, unsupported web context, or an
  /// unreadable OS state). The map stays unavailable with a graceful message.
  unavailable,
}

/// Maps an OS permission value to a gate status.
///
/// `unableToDetermine` is treated as a retryable denial: the user gets an
/// explicit retry action instead of an automatic re-prompt.
LocationGateStatus mapLocationPermission(LocationPermission permission) =>
    switch (permission) {
      LocationPermission.whileInUse || LocationPermission.always =>
        LocationGateStatus.granted,
      LocationPermission.deniedForever => LocationGateStatus.permanentlyDenied,
      LocationPermission.denied || LocationPermission.unableToDetermine =>
        LocationGateStatus.denied,
    };

/// Reads and requests foreground location access for the map gate.
///
/// The implementation is injectable so widget tests can script every status
/// without touching platform channels. Only foreground access is ever
/// requested (`Geolocator.requestPermission` resolves `whileInUse`); there
/// is intentionally no API for background/Always access.
abstract class LocationPermissionService {
  /// Reads the current OS status. Never shows an OS prompt.
  Future<LocationGateStatus> checkStatus();

  /// Requests foreground access. Call only after an explicit user action.
  Future<LocationGateStatus> requestAccess();

  /// Whether device location services are enabled. Checked separately from
  /// permission so the current-location action can explain the two cases
  /// distinctly.
  Future<bool> isServiceEnabled();

  /// Opens the OS app-settings page. Never throws.
  Future<void> openSettings();
}

/// Geolocator-backed [LocationPermissionService].
class GeolocatorLocationPermissionService
    implements LocationPermissionService {
  const GeolocatorLocationPermissionService();

  @override
  Future<LocationGateStatus> checkStatus() async {
    try {
      // No timeout wrapper: a hung probe must stay timer-free so widget
      // tests pumping the picker without platform channels behave exactly
      // as before (a pending future, never a pending timer). On devices
      // the OS answers immediately.
      final permission = await Geolocator.checkPermission();
      return mapLocationPermission(permission);
    } on Object {
      // Unsupported web context, missing platform implementation, or any
      // other unreadable OS state: stay gated with a graceful message.
      return LocationGateStatus.unavailable;
    }
  }

  @override
  Future<LocationGateStatus> requestAccess() async {
    try {
      final permission = await Geolocator.requestPermission();
      return mapLocationPermission(permission);
    } on Object {
      return LocationGateStatus.unavailable;
    }
  }

  @override
  Future<bool> isServiceEnabled() async {
    try {
      return await Geolocator.isLocationServiceEnabled();
    } on Object {
      return false;
    }
  }

  @override
  Future<void> openSettings() async {
    try {
      await Geolocator.openAppSettings();
    } on Object {
      // No settings surface available: callers always present a manual
      // alternative alongside, so this must never break the flow.
    }
  }
}
