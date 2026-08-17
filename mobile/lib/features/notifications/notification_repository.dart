import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'notification_models.dart';

class NotificationRepository {
  NotificationRepository({required this.api});

  final ApiClient api;

  Future<NotificationPage> getNotifications(
    String token, {
    int page = 1,
    int pageSize = 20,
    bool? isRead,
  }) async {
    final query = <String, String>{
      'page': '$page',
      'pageSize': '$pageSize',
      if (isRead != null) 'isRead': '$isRead',
    };
    final path = Uri(
      path: '/api/v1/notifications',
      queryParameters: query,
    ).toString();
    final response = await api.get(path, accessToken: token);
    return NotificationPage.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<int> getUnreadCount(String token) async {
    final response = await api.get(
      '/api/v1/notifications/unread-count',
      accessToken: token,
    );
    final data = response['data'] as Map<String, dynamic>;
    return (data['unreadCount'] as num).toInt();
  }

  Future<void> markRead(String token, String notificationId) async {
    await api.post(
      '/api/v1/notifications/$notificationId/read',
      accessToken: token,
    );
  }

  Future<RegisteredNotificationDevice> registerDevice({
    required String token,
    required DeviceMetadata device,
    required String pushToken,
  }) async {
    final response = await api.post(
      '/api/v1/devices',
      accessToken: token,
      body: {...device.toJson(), 'pushToken': pushToken},
    );
    return RegisteredNotificationDevice.fromJson(
      response['data'] as Map<String, dynamic>,
    );
  }
}
