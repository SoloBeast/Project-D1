import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('push receive path without Firebase configuration', () {
    test(
      'initialization fails closed without breaking startup',
      () async {
        final gateway = FirebasePushNotificationGateway();

        await expectLater(gateway.initialize(), completion(isFalse));

        // Initialization is idempotent: repeated calls keep failing closed
        // instead of throwing.
        await expectLater(gateway.initialize(), completion(isFalse));
      },
    );

    test('permission, token and deep-link accessors degrade safely', () async {
      final gateway = FirebasePushNotificationGateway();

      expect(
        await gateway.permissionStatus(),
        PushPermissionStatus.unavailable,
      );
      expect(
        await gateway.requestPermission(),
        PushPermissionStatus.unavailable,
      );
      expect(await gateway.token(), isNull);
      expect(await gateway.initialDeepLink(), isNull);
    });

    test('push streams are inert without Firebase', () async {
      final gateway = FirebasePushNotificationGateway();

      await expectLater(gateway.tokenRefreshes, emitsDone);
      await expectLater(gateway.openedDeepLinks, emitsDone);
      await expectLater(gateway.foregroundMessages, emitsDone);
    });

    test(
      'background handler survives a server-shaped payload without Firebase',
      () async {
        // Mirrors the Stage 1 backend payload: notification is rendered by
        // the OS; data carries the inbox linkage. The handler must never
        // throw, even when Firebase cannot initialize in the isolate.
        final message = RemoteMessage(
          messageId: 'projects/test-project/messages/msg-1',
          data: {
            'notificationId': '3fa85f64-5717-4562-b3fc-2c963f66afa6',
            'eventType': 'ORDER_CREATED',
            'deepLink': '/orders/100',
            'deep_link': '/orders/100',
          },
        );

        await expectLater(
          firebaseMessagingBackgroundHandler(message),
          completes,
        );
      },
    );

    test('background handler tolerates a data-only payload', () async {
      final message = RemoteMessage(
        data: {'eventType': 'DELIVERY_OTP_ISSUED'},
      );

      await expectLater(
        firebaseMessagingBackgroundHandler(message),
        completes,
      );
    });
  });
}
