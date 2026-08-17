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

  @override
  Future<bool> initialize() async {
    if (_initialized) return true;
    if (_unavailable || !_isSupportedPlatform) return false;
    if (kIsWeb && !_hasWebConfiguration) return false;

    try {
      await Firebase.initializeApp(options: kIsWeb ? _webOptions : null);
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
