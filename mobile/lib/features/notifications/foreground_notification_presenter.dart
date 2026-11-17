import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';

/// Android channel reused for foreground heads-up notifications. Must stay
/// identical to the channel created natively in `MainActivity` and the
/// `default_notification_channel_id` manifest metadata so backgrounded/
/// terminated OS notifications and foreground local notifications share one
/// user-visible channel.
const foregroundChannelId = 'doodh_direct_default';
const _foregroundChannelName = 'Delivery updates';
const _foregroundChannelDescription =
    'Order, delivery, and subscription updates from DoodhDirect.';

/// Delivery OTP events never carry the code on the push transport (the
/// backend substitutes generic text), and this client enforces the same rule
/// again: an OTP event always renders these constants, never incoming text.
/// Mirrors `DeliveryOtpPushPrivacy` on the backend.
const otpFallbackTitle = 'Delivery update';
const otpFallbackBody =
    'Your delivery update is ready. Open DoodhDirect to view it securely.';

const _otpEventType = 'DELIVERY_OTP_ISSUED';

/// Pure presentation model resolved from an incoming foreground FCM message.
/// Kept free of plugin types so resolution is unit-testable.
class ForegroundDisplay {
  const ForegroundDisplay({
    required this.title,
    required this.body,
    required this.notificationId,
    this.deepLink,
  });

  final String title;
  final String body;

  /// Stable Android notification id derived from the backend notification id
  /// (or event type as fallback) so a foreground banner never accidentally
  /// replaces an unrelated notification.
  final int notificationId;
  final String? deepLink;

  /// False when there is nothing user-visible to render.
  bool get shouldDisplay =>
      title.trim().isNotEmpty || body.trim().isNotEmpty;
}

/// Resolves what to display for a foreground FCM message described by plain
/// maps (message `notification` title/body plus `data`). The backend always
/// sends a `notification` payload; data carries `notificationId`,
/// `eventType`, and optional `deepLink`/`deep_link` entries.
ForegroundDisplay resolveForegroundDisplay({
  required String? title,
  required String? body,
  required Map<String, String> data,
}) {
  final eventType = data['eventType']?.trim();
  if (eventType != null &&
      eventType.toUpperCase() == _otpEventType) {
    return ForegroundDisplay(
      title: otpFallbackTitle,
      body: otpFallbackBody,
      notificationId: _stableNotificationId(data),
      deepLink: _rawDeepLink(data),
    );
  }
  return ForegroundDisplay(
    title: title?.trim() ?? '',
    body: body?.trim() ?? '',
    notificationId: _stableNotificationId(data),
    deepLink: _rawDeepLink(data),
  );
}

int _stableNotificationId(Map<String, String> data) {
  final seed = data['notificationId']?.trim().isNotEmpty == true
      ? data['notificationId']!.trim()
      : data['eventType']?.trim().isNotEmpty == true
          ? data['eventType']!.trim()
          : 'doodh-direct-foreground';
  return seed.hashCode & 0x7fffffff;
}

String? _rawDeepLink(Map<String, String> data) {
  final value = data['deepLink'] ?? data['deep_link'];
  final link = value?.trim();
  return link == null || link.isEmpty ? null : link;
}

/// Presents foreground FCM messages as native Android heads-up notifications.
/// Background/terminated handling is untouched: the OS still renders the FCM
/// `notification` payload itself, and no background display is added here.
abstract interface class ForegroundNotificationPresenter {
  /// Idempotent plugin initialization. Returns true when foreground
  /// presentation is available (Android only; iOS/web keep existing
  /// behavior). Never requests permissions itself.
  Future<bool> initialize();

  /// Shows a heads-up notification. No-ops when unavailable or when there is
  /// nothing to display. Never throws: foreground display must never break
  /// the inbox refresh path.
  Future<void> show(ForegroundDisplay display);

  /// Raw deep links from notification taps (unnormalized; the controller
  /// validates them through the existing allowlist before navigating).
  Stream<String> get tapDeepLinks;
}

class LocalForegroundNotificationPresenter
    implements ForegroundNotificationPresenter {
  LocalForegroundNotificationPresenter({FlutterLocalNotificationsPlugin? plugin})
      : _plugin = plugin ?? FlutterLocalNotificationsPlugin();

  final FlutterLocalNotificationsPlugin _plugin;
  final _taps = StreamController<String>.broadcast();
  bool _initialized = false;
  bool _available = false;

  @override
  Stream<String> get tapDeepLinks => _taps.stream;

  @override
  Future<bool> initialize() async {
    if (_initialized) return _available;
    _initialized = true;
    if (defaultTargetPlatform != TargetPlatform.android) return false;
    try {
      const settings = InitializationSettings(
        android: AndroidInitializationSettings('@mipmap/ic_launcher'),
      );
      await _plugin.initialize(
        settings: settings,
        onDidReceiveNotificationResponse: (response) {
          final payload = response.payload?.trim();
          if (payload != null && payload.isNotEmpty) {
            _taps.add(payload);
          }
        },
      );
      await _plugin
          .resolvePlatformSpecificImplementation<
              AndroidFlutterLocalNotificationsPlugin>()
          ?.createNotificationChannel(
            const AndroidNotificationChannel(
              foregroundChannelId,
              _foregroundChannelName,
              description: _foregroundChannelDescription,
              importance: Importance.high,
            ),
          );
      _available = true;
    } on Object {
      _available = false;
    }
    return _available;
  }

  @override
  Future<void> show(ForegroundDisplay display) async {
    if (!_available || !display.shouldDisplay) return;
    try {
      const details = NotificationDetails(
        android: AndroidNotificationDetails(
          foregroundChannelId,
          _foregroundChannelName,
          channelDescription: _foregroundChannelDescription,
          importance: Importance.high,
          priority: Priority.high,
          visibility: NotificationVisibility.public,
        ),
      );
      await _plugin.show(
        id: display.notificationId,
        title: display.title,
        body: display.body,
        notificationDetails: details,
        payload: display.deepLink,
      );
    } on Object {
      // Foreground display is best-effort (e.g. permission revoked after
      // grant): never let it break inbox handling.
    }
  }
}
