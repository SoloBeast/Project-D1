import 'dart:convert';

import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_models.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('notification models', () {
    test('parses notification fields and normalizes optional values', () {
      final notification = AppNotification.fromJson({
        'notificationId': 'notification-1',
        'eventType': 'ORDER_CONFIRMED',
        'title': 'Order confirmed',
        'body': 'Your order has been confirmed.',
        'deepLink': '  /orders/order-1  ',
        'isRead': true,
        'createdAtUtc': '2026-08-17T10:00:00+05:30',
        'readAtUtc': '2026-08-17T05:00:30Z',
      });

      expect(notification.notificationId, 'notification-1');
      expect(notification.eventType, 'ORDER_CONFIRMED');
      expect(notification.deepLink, '/orders/order-1');
      expect(notification.createdAtUtc, DateTime.utc(2026, 8, 17, 4, 30));
      expect(notification.readAtUtc, DateTime.utc(2026, 8, 17, 5, 0, 30));
    });

    test(
      'treats blank optional fields as absent and marks unread item read',
      () {
        final notification = AppNotification.fromJson({
          ..._notificationJson('notification-1'),
          'deepLink': '   ',
          'readAtUtc': null,
        });

        expect(notification.deepLink, isNull);
        expect(notification.readAtUtc, isNull);
        expect(notification.isRead, isFalse);

        final readAt = DateTime.parse('2026-08-17T12:30:00+05:30');
        final read = notification.markRead(readAt);
        expect(read.isRead, isTrue);
        expect(read.readAtUtc, DateTime.utc(2026, 8, 17, 7));
        expect(read.notificationId, notification.notificationId);
      },
    );

    test('parses pages and calculates whether another page exists', () {
      final firstPage = NotificationPage.fromJson({
        'items': [_notificationJson('notification-1')],
        'page': 1,
        'pageSize': 20,
        'totalCount': 21,
      });
      final finalPage = NotificationPage.fromJson({
        'items': <Map<String, dynamic>>[],
        'page': 2,
        'pageSize': 20,
        'totalCount': 21,
      });

      expect(firstPage.items.single.notificationId, 'notification-1');
      expect(firstPage.hasMore, isTrue);
      expect(finalPage.hasMore, isFalse);
    });
  });

  group('notification repository', () {
    test(
      'lists notifications with filters and parses the data envelope',
      () async {
        final client = MockClient((request) async {
          expect(request.method, 'GET');
          expect(request.url.path, '/api/v1/notifications');
          expect(request.url.queryParameters, {
            'page': '2',
            'pageSize': '10',
            'isRead': 'false',
          });
          expect(request.headers['Authorization'], 'Bearer customer-token');
          return _jsonResponse({
            'success': true,
            'data': {
              'items': [_notificationJson('notification-2')],
              'page': 2,
              'pageSize': 10,
              'totalCount': 25,
            },
            'errors': [],
          });
        });
        final repository = _repository(client);

        final page = await repository.getNotifications(
          'customer-token',
          page: 2,
          pageSize: 10,
          isRead: false,
        );

        expect(page.items.single.notificationId, 'notification-2');
        expect(page.page, 2);
        expect(page.hasMore, isTrue);
      },
    );

    test('gets unread count from the authenticated route', () async {
      final client = MockClient((request) async {
        expect(request.method, 'GET');
        expect(request.url.path, '/api/v1/notifications/unread-count');
        expect(request.headers['Authorization'], 'Bearer customer-token');
        return _jsonResponse({
          'success': true,
          'data': {'unreadCount': 7},
          'errors': [],
        });
      });

      expect(await _repository(client).getUnreadCount('customer-token'), 7);
    });

    test('marks a notification read using the notification route', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/notifications/notification-1/read');
        expect(request.headers['Authorization'], 'Bearer customer-token');
        return _jsonResponse({'success': true, 'data': null, 'errors': []});
      });

      await _repository(client).markRead('customer-token', 'notification-1');
    });

    test('registers stable device metadata and parses the device', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/devices');
        expect(request.headers['Authorization'], 'Bearer customer-token');
        expect(jsonDecode(request.body), {
          'deviceIdentifier': 'installation-1',
          'deviceName': 'Test phone',
          'platform': 'android',
          'pushToken': 'push-token-1',
        });
        return _jsonResponse({
          'success': true,
          'data': {
            'deviceId': 'device-1',
            'platform': 'android',
            'deviceName': 'Test phone',
            'isActive': true,
            'lastSeenAtUtc': '2026-08-17T09:00:00Z',
          },
          'errors': [],
        });
      });

      final device = await _repository(client).registerDevice(
        token: 'customer-token',
        device: const DeviceMetadata(
          deviceIdentifier: 'installation-1',
          deviceName: 'Test phone',
          platform: 'android',
        ),
        pushToken: 'push-token-1',
      );

      expect(device.deviceId, 'device-1');
      expect(device.platform, 'android');
      expect(device.deviceName, 'Test phone');
      expect(device.isActive, isTrue);
      expect(device.lastSeenAtUtc, DateTime.utc(2026, 8, 17, 9));
    });
  });
}

NotificationRepository _repository(http.Client client) =>
    NotificationRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

http.Response _jsonResponse(Map<String, dynamic> body) => http.Response(
  jsonEncode(body),
  200,
  headers: {'content-type': 'application/json'},
);

Map<String, dynamic> _notificationJson(String notificationId) => {
  'notificationId': notificationId,
  'eventType': 'ORDER_CONFIRMED',
  'title': 'Order confirmed',
  'body': 'Your order has been confirmed.',
  'deepLink': '/orders/order-1',
  'isRead': false,
  'createdAtUtc': '2026-08-17T05:00:00Z',
  'readAtUtc': null,
};
