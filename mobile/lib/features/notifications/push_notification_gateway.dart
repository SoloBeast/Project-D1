import 'dart:async';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';

const _webApiKey = String.fromEnvironment('FIREBASE_WEB_API_KEY');
const _webAppId = String.fromEnvironment('FIREBASE_WEB_APP_ID');
const _webMessagingSenderId = String.fromEnvironment(
  'FIREBASE_WEB_MESSAGING_SENDER_ID',
);
const _webProjectId = String.fromEnvironment('FIREBASE_WEB_PROJECT_ID');
const _webAuthDomain = String.fromEnvironment('FIREBASE_WEB_AUTH_DOMAIN');
const _webStorageBucket = String.fromEnvironment('FIREBASE_WEB_STORAGE_BUCKET');
const _webMeasurementId = String.fromEnvironment('FIREBASE_WEB_MEASUREMENT_ID');
const _webVapidKey = String.fromEnvironment('FIREBASE_WEB_VAPID_KEY');

enum PushPermissionStatus {
  unavailable,
  notDetermined,
  denied,
  authorized,
  provisional,
}

/// Background/terminated message entry point for firebase_messaging.
///
/// Must stay top-level with the `vm:entry-point` pragma so the native host
/// can invoke it in a background isolate. The server sends
/// `notification` + `data` payloads, so the OS renders the system tray
/// notification itself; this handler only ensures the Firebase app is
/// initialized in the isolate and then returns. The inbox remains the source
/// of truth: it refreshes when the app resumes or the notification is tapped
/// via the existing `onMessage` / `getInitialMessage` / `onMessageOpenedApp`
/// flows. Navigation and mark-read always run in the main isolate through
/// the validated deep-link allowlist — never here. Logs nothing identifying
/// (no device tokens; [RemoteMessage] carries none).
@pragma('vm:entry-point')
Future<void> firebaseMessagingBackgroundHandler(RemoteMessage message) async {
  try {
    if (Firebase.apps.isEmpty) {
      await Firebase.initializeApp();
    }
  } on Object {
    // Firebase unconfigured on this installation: the OS cannot display
    // anything for us, and there is no authenticated context to refresh.
    // Swallow so a background wake never crashes the isolate.
  }
}

abstract interface class PushNotificationGateway {
  Stream<String> get tokenRefreshes;
  Stream<String> get openedDeepLinks;
  Stream<void> get foregroundMessages;

  Future<bool> initialize();
  Future<PushPermissionStatus> permissionStatus();
  Future<PushPermissionStatus> requestPermission();
  Future<String?> token();
  Future<String?> initialDeepLink();
}

class FirebasePushNotificationGateway implements PushNotificationGateway {
  bool _initialized = false;
  bool _unavailable = false;

  FirebaseMessaging? get _messaging =>
      _initialized ? FirebaseMessaging.instance : null;

  @override
  Stream<String> get tokenRefreshes =>
      _messaging?.onTokenRefresh ?? const Stream.empty();

  @override
  Stream<String> get openedDeepLinks => _initialized
      ? FirebaseMessaging.onMessageOpenedApp
            .map(_deepLinkFromMessage)
            .where((link) => link != null)
            .cast<String>()
      : const Stream.empty();

  @override
  Stream<void> get foregroundMessages => _initialized
      ? FirebaseMessaging.onMessage.map<void>((_) {})
      : const Stream.empty();

  /// Foreground policy: Firebase messages received while the app is open
  /// only trigger a silent inbox refresh (see the controller's
  /// `foregroundMessages` subscription). No heads-up banner is shown: the
  /// inbox is the source of truth, and a foreground banner would risk a
  /// duplicate user-visible notification next to the inbox row. The OS
  /// renders the tray notification itself when the app is backgrounded or
  /// terminated because the server sends `notification` + `data` payloads.
  @override
  Future<bool> initialize() async {
    if (_initialized) return true;
    if (_unavailable || !_isSupportedPlatform) return false;
    if (kIsWeb && !_hasWebConfiguration) return false;

    try {
      if (Firebase.apps.isEmpty) {
        await Firebase.initializeApp(options: kIsWeb ? _webOptions : null);
      }
      FirebaseMessaging.onBackgroundMessage(
        firebaseMessagingBackgroundHandler,
      );
      _initialized = true;
      return true;
    } on Object {
      _unavailable = true;
      return false;
    }
  }

  @override
  Future<PushPermissionStatus> permissionStatus() async {
    if (!await initialize()) return PushPermissionStatus.unavailable;
    final settings = await _messaging!.getNotificationSettings();
    return _mapAuthorizationStatus(settings.authorizationStatus);
  }

  @override
  Future<PushPermissionStatus> requestPermission() async {
    if (!await initialize()) return PushPermissionStatus.unavailable;
    final settings = await _messaging!.requestPermission(
      alert: true,
      badge: true,
      sound: true,
      provisional: false,
    );
    return _mapAuthorizationStatus(settings.authorizationStatus);
  }

  @override
  Future<String?> token() async {
    if (!await initialize()) return null;
    return _messaging!.getToken(
      vapidKey: kIsWeb && _webVapidKey.isNotEmpty ? _webVapidKey : null,
    );
  }

  @override
  Future<String?> initialDeepLink() async {
    if (!await initialize()) return null;
    return _deepLinkFromMessage(await _messaging!.getInitialMessage());
  }

  bool get _isSupportedPlatform =>
      kIsWeb ||
      defaultTargetPlatform == TargetPlatform.android ||
      defaultTargetPlatform == TargetPlatform.iOS;

  bool get _hasWebConfiguration =>
      _webApiKey.isNotEmpty &&
      _webAppId.isNotEmpty &&
      _webMessagingSenderId.isNotEmpty &&
      _webProjectId.isNotEmpty;

  FirebaseOptions get _webOptions => FirebaseOptions(
    apiKey: _webApiKey,
    appId: _webAppId,
    messagingSenderId: _webMessagingSenderId,
    projectId: _webProjectId,
    authDomain: _optional(_webAuthDomain),
    storageBucket: _optional(_webStorageBucket),
    measurementId: _optional(_webMeasurementId),
  );

  static PushPermissionStatus _mapAuthorizationStatus(
    AuthorizationStatus status,
  ) => switch (status) {
    AuthorizationStatus.authorized => PushPermissionStatus.authorized,
    AuthorizationStatus.provisional => PushPermissionStatus.provisional,
    AuthorizationStatus.denied => PushPermissionStatus.denied,
    AuthorizationStatus.notDetermined => PushPermissionStatus.notDetermined,
  };

  static String? _deepLinkFromMessage(RemoteMessage? message) {
    final value = message?.data['deepLink'] ?? message?.data['deep_link'];
    final link = value?.trim();
    return link == null || link.isEmpty ? null : link;
  }

  static String? _optional(String value) => value.isEmpty ? null : value;
}
