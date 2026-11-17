import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_web_plugins/url_strategy.dart';
import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'firebase_options.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Initialize Firebase
  await Firebase.initializeApp(
    options: DefaultFirebaseOptions.currentPlatform,
  );

  // Single authoritative background handler: the top-level
  // firebaseMessagingBackgroundHandler in push_notification_gateway.dart owns
  // background/terminated wakes. Registering it here (main isolate) covers
  // cold start; FirebasePushNotificationGateway.initialize() re-registers the
  // same entry point after hot restarts so there is exactly one handler.
  FirebaseMessaging.onBackgroundMessage(firebaseMessagingBackgroundHandler);

  usePathUrlStrategy();
  runApp(const ProviderScope(child: DoodhDirectApp()));
}
