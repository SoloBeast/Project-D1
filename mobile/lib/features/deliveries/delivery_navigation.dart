import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

abstract interface class DeliveryNavigationLauncher {
  Future<bool> open(Uri destination);
}

final deliveryNavigationLauncherProvider = Provider<DeliveryNavigationLauncher>(
  (ref) => const UrlDeliveryNavigationLauncher(),
);

class UrlDeliveryNavigationLauncher implements DeliveryNavigationLauncher {
  const UrlDeliveryNavigationLauncher();

  @override
  Future<bool> open(Uri destination) =>
      launchUrl(destination, mode: LaunchMode.externalApplication);
}

Uri? deliveryNavigationUri({
  required double latitude,
  required double longitude,
  required String address,
}) {
  final hasValidCoordinates =
      latitude.isFinite &&
      longitude.isFinite &&
      latitude >= -90 &&
      latitude <= 90 &&
      longitude >= -180 &&
      longitude <= 180;
  if (hasValidCoordinates) {
    return Uri.https('www.google.com', '/maps/dir/', <String, String>{
      'api': '1',
      'destination': '$latitude,$longitude',
    });
  }

  final normalizedAddress = address.trim();
  if (normalizedAddress.isEmpty) {
    return null;
  }
  return Uri.https('www.google.com', '/maps/search/', <String, String>{
    'api': '1',
    'query': normalizedAddress,
  });
}
