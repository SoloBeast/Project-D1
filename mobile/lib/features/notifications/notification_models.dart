class AppNotification {
  const AppNotification({
    required this.notificationId,
    required this.eventType,
    required this.title,
    required this.body,
    required this.deepLink,
    required this.isRead,
    required this.createdAt,
    required this.readAt,
  });

  factory AppNotification.fromJson(Map<String, dynamic> json) =>
      AppNotification(
        notificationId: json['notificationId'] as String,
        eventType: json['eventType'] as String,
        title: json['title'] as String,
        body: json['body'] as String,
        deepLink: _optionalString(json['deepLink']),
        isRead: json['isRead'] as bool,
        createdAt: DateTime.parse(json['createdAt'] as String),
        readAt: _optionalDateTime(json['readAt']),
      );

  final String notificationId;
  final String eventType;
  final String title;
  final String body;
  final String? deepLink;
  final bool isRead;
  final DateTime createdAt;
  final DateTime? readAt;

  AppNotification markRead(DateTime readAt) => AppNotification(
    notificationId: notificationId,
    eventType: eventType,
    title: title,
    body: body,
    deepLink: deepLink,
    isRead: true,
    createdAt: createdAt,
    readAt: readAt,
  );
}

class NotificationPage {
  const NotificationPage({
    required this.items,
    required this.page,
    required this.pageSize,
    required this.totalCount,
  });

  factory NotificationPage.fromJson(Map<String, dynamic> json) =>
      NotificationPage(
        items: (json['items'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(AppNotification.fromJson)
            .toList(growable: false),
        page: (json['page'] as num).toInt(),
        pageSize: (json['pageSize'] as num).toInt(),
        totalCount: (json['totalCount'] as num).toInt(),
      );

  final List<AppNotification> items;
  final int page;
  final int pageSize;
  final int totalCount;

  bool get hasMore => page * pageSize < totalCount;
}

class RegisteredNotificationDevice {
  const RegisteredNotificationDevice({
    required this.deviceId,
    required this.platform,
    required this.deviceName,
    required this.isActive,
    required this.registeredAt,
    required this.lastSeenAt,
  });

  factory RegisteredNotificationDevice.fromJson(Map<String, dynamic> json) =>
      RegisteredNotificationDevice(
        deviceId: json['deviceId'] as String,
        platform: json['platform'] as String,
        deviceName: _optionalString(json['deviceName']),
        isActive: json['isActive'] as bool,
        registeredAt: DateTime.parse(json['registeredAt'] as String),
        lastSeenAt: DateTime.parse(json['lastSeenAt'] as String),
      );

  final String deviceId;
  final String platform;
  final String? deviceName;
  final bool isActive;
  final DateTime registeredAt;
  final DateTime lastSeenAt;
}

String? _optionalString(Object? value) {
  final text = value as String?;
  return text == null || text.trim().isEmpty ? null : text.trim();
}

DateTime? _optionalDateTime(Object? value) {
  final text = _optionalString(value);
  return text == null ? null : DateTime.parse(text);
}
