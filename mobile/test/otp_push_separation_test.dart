import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/push_notification_gateway.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('OTP push/in-app separation', () {
    test('in-app inbox preserves the OTP body for authenticated display', () {
      const otpBody =
          'Your delivery OTP is 731946. Share it only with the assigned delivery staff.';
      final notification = AppNotification(
        notificationId: 'notification-otp-1',
        eventType: 'DELIVERY_OTP_ISSUED',
        title: 'Delivery Otp Issued',
        body: otpBody,
        deepLink: '/deliveries/test',
        isRead: false,
        createdAt: DateTime.utc(2026, 8, 17, 12),
        readAt: null,
      );

      // The authenticated inbox is the only surface that may show the code.
      expect(notification.body, contains('731946'));
      expect(
        normalizeNotificationDeepLink(notification.deepLink),
        '/deliveries/test',
      );
    });

    test('push deep links stay app-relative (external OTP lures rejected)', () {
      expect(
        normalizeNotificationDeepLink('https://example.test/deliveries/1'),
        isNull,
      );
      expect(
        normalizeNotificationDeepLink('//example.test/deliveries/1'),
        isNull,
      );
      expect(
        normalizeNotificationDeepLink('/deliveries/test'),
        '/deliveries/test',
      );
    });

    test('background handler tolerates the OTP event without throwing', () async {
      final message = RemoteMessage(data: {'eventType': 'DELIVERY_OTP_ISSUED'});

      await expectLater(
        firebaseMessagingBackgroundHandler(message),
        completes,
      );
    });

    test('foreground/background push streams stay silent without Firebase', () async {
      final gateway = FirebasePushNotificationGateway();

      await expectLater(gateway.foregroundMessages, emitsDone);
      await expectLater(gateway.foregroundMessagePayloads, emitsDone);
      await expectLater(gateway.openedDeepLinks, emitsDone);
      await expectLater(gateway.tokenRefreshes, emitsDone);
    });
  });
}
