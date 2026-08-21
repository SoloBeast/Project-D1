import 'dart:async';

import 'package:geolocator/geolocator.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

enum CurrentLocationFailure {
  permissionDenied,
  unavailable,
  timeout,
  invalid,
  general,
}

class CurrentLocationException implements Exception {
  const CurrentLocationException(this.failure);

  final CurrentLocationFailure failure;

  String get friendlyMessage => switch (failure) {
    CurrentLocationFailure.permissionDenied =>
      'Location permission was denied. You can still select your location on the map.',
    CurrentLocationFailure.unavailable =>
      'Current location is unavailable on this device or browser.',
    CurrentLocationFailure.timeout =>
      'Finding your current location took too long. Please try again.',
    CurrentLocationFailure.invalid =>
      'The device returned an invalid location. Please select a point on the map.',
    CurrentLocationFailure.general =>
      'Current location could not be found. Please try again or use the map.',
  };

  @override
  String toString() => friendlyMessage;
}

typedef CurrentLocationProvider = Future<LatLng> Function();

bool isValidCoordinate(LatLng location) =>
    location.latitude.isFinite &&
    location.longitude.isFinite &&
    location.latitude >= -90 &&
    location.latitude <= 90 &&
    location.longitude >= -180 &&
    location.longitude <= 180;

Future<LatLng> getCurrentLocation() async {
  try {
    if (!await Geolocator.isLocationServiceEnabled()) {
      throw const CurrentLocationException(CurrentLocationFailure.unavailable);
    }

    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied ||
        permission == LocationPermission.deniedForever) {
      throw const CurrentLocationException(
        CurrentLocationFailure.permissionDenied,
      );
    }

    final position = await Geolocator.getCurrentPosition(
      locationSettings: const LocationSettings(
        accuracy: LocationAccuracy.high,
        timeLimit: Duration(seconds: 12),
      ),
    );
    final location = LatLng(position.latitude, position.longitude);
    if (!isValidCoordinate(location)) {
      throw const CurrentLocationException(CurrentLocationFailure.invalid);
    }
    return location;
  } on CurrentLocationException {
    rethrow;
  } on TimeoutException {
    throw const CurrentLocationException(CurrentLocationFailure.timeout);
  } on PermissionDeniedException {
    throw const CurrentLocationException(
      CurrentLocationFailure.permissionDenied,
    );
  } on LocationServiceDisabledException {
    throw const CurrentLocationException(CurrentLocationFailure.unavailable);
  } catch (_) {
    throw const CurrentLocationException(CurrentLocationFailure.general);
  }
}
