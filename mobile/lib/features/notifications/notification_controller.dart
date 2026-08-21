import 'dart:async';

import 'package:doodh_direct_mobile/core/device/device_metadata_service.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'notification_models.dart';
import 'notification_repository.dart';
import 'push_notification_gateway.dart';

final deviceMetadataServiceProvider = Provider<DeviceMetadataService>(
  (ref) => DeviceMetadataService(),
);

final notificationRepositoryProvider = Provider<NotificationRepository>(
  (ref) => NotificationRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final pushNotificationGatewayProvider = Provider<PushNotificationGateway>(
  (ref) => FirebasePushNotificationGateway(),
);

final notificationControllerProvider =
    NotifierProvider<NotificationController, NotificationState>(
      NotificationController.new,
    );

class NotificationState {
  const NotificationState({
    this.items = const [],
    this.unreadCount = 0,
    this.page = 0,
    this.hasMore = false,
    this.isLoading = false,
    this.isRefreshing = false,
    this.isLoadingMore = false,
    this.isRequestingPermission = false,
    this.isOffline = false,
    this.permissionStatus = PushPermissionStatus.unavailable,
    this.pendingDeepLink,
    this.errorMessage,
  });

  final List<AppNotification> items;
  final int unreadCount;
  final int page;
  final bool hasMore;
  final bool isLoading;
  final bool isRefreshing;
  final bool isLoadingMore;
  final bool isRequestingPermission;
  final bool isOffline;
  final PushPermissionStatus permissionStatus;
  final String? pendingDeepLink;
  final String? errorMessage;

  NotificationState copyWith({
    List<AppNotification>? items,
    int? unreadCount,
    int? page,
    bool? hasMore,
    bool? isLoading,
    bool? isRefreshing,
    bool? isLoadingMore,
    bool? isRequestingPermission,
    bool? isOffline,
    PushPermissionStatus? permissionStatus,
    String? pendingDeepLink,
    bool clearPendingDeepLink = false,
    String? errorMessage,
    bool clearError = false,
  }) => NotificationState(
    items: items ?? this.items,
    unreadCount: unreadCount ?? this.unreadCount,
    page: page ?? this.page,
    hasMore: hasMore ?? this.hasMore,
    isLoading: isLoading ?? this.isLoading,
    isRefreshing: isRefreshing ?? this.isRefreshing,
    isLoadingMore: isLoadingMore ?? this.isLoadingMore,
    isRequestingPermission:
        isRequestingPermission ?? this.isRequestingPermission,
    isOffline: isOffline ?? this.isOffline,
    permissionStatus: permissionStatus ?? this.permissionStatus,
    pendingDeepLink: clearPendingDeepLink
        ? null
        : pendingDeepLink ?? this.pendingDeepLink,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class NotificationController extends Notifier<NotificationState> {
  static const _pageSize = 20;

  StreamSubscription<String>? _tokenRefreshSubscription;
  StreamSubscription<String>? _openedDeepLinkSubscription;
  StreamSubscription<void>? _foregroundMessageSubscription;
  String? _activeUserId;
  String? _registeredPushToken;
  bool _pushStreamsStarted = false;
  int _sessionGeneration = 0;

  NotificationRepository get _repository =>
      ref.read(notificationRepositoryProvider);
  DeviceMetadataService get _deviceMetadata =>
      ref.read(deviceMetadataServiceProvider);
  PushNotificationGateway get _pushGateway =>
      ref.read(pushNotificationGatewayProvider);
  String? get _accessToken =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  NotificationState build() {
    ref.listen<SessionState>(
      sessionControllerProvider,
      (previous, next) => unawaited(_synchronizeSession(next)),
      fireImmediately: true,
    );
    ref.onDispose(_disposePushSubscriptions);
    return const NotificationState();
  }

  Future<void> loadInitial() => _loadFirstPage(refreshing: false);

  Future<void> refresh() => _loadFirstPage(refreshing: true);

  Future<void> loadMore() async {
    final token = _accessToken;
    final userId = _activeUserId;
    if (token == null ||
        userId == null ||
        state.isLoading ||
        state.isRefreshing ||
        state.isLoadingMore ||
        !state.hasMore) {
      return;
    }

    state = state.copyWith(
      isLoadingMore: true,
      isOffline: false,
      clearError: true,
    );
    try {
      final page = await _repository.getNotifications(
        token,
        page: state.page + 1,
        pageSize: _pageSize,
      );
      if (!_isActive(userId)) return;
      state = state.copyWith(
        items: _mergeNotifications(state.items, page.items),
        page: page.page,
        hasMore: page.hasMore,
        isLoadingMore: false,
      );
    } on Object catch (error) {
      if (_isActive(userId)) _setFailure(error, loadingMore: true);
    }
  }

  Future<String?> markRead(AppNotification notification) async {
    final deepLink = normalizeNotificationDeepLink(notification.deepLink);
    final token = _accessToken;
    final userId = _activeUserId;
    if (token == null || userId == null || notification.isRead) return deepLink;

    try {
      await _repository.markRead(token, notification.notificationId);
      if (!_isActive(userId)) return deepLink;
      final readAt = indiaNow();
      state = state.copyWith(
        items: [
          for (final item in state.items)
            if (item.notificationId == notification.notificationId)
              item.markRead(readAt)
            else
              item,
        ],
        unreadCount: state.unreadCount > 0 ? state.unreadCount - 1 : 0,
        isOffline: false,
        clearError: true,
      );
    } on Object catch (error) {
      if (_isActive(userId)) _setFailure(error);
      return null;
    }
    return deepLink;
  }

  Future<PushPermissionStatus> requestPushPermission() async {
    final userId = _activeUserId;
    if (userId == null || state.isRequestingPermission) {
      return state.permissionStatus;
    }

    state = state.copyWith(isRequestingPermission: true, clearError: true);
    try {
      final status = await _pushGateway.requestPermission();
      if (!_isActive(userId)) return status;
      state = state.copyWith(
        permissionStatus: status,
        isRequestingPermission: false,
      );
      if (_canReceivePush(status)) await _synchronizePushToken(userId);
      return status;
    } on Object {
      if (_isActive(userId)) {
        state = state.copyWith(
          isRequestingPermission: false,
          errorMessage: 'Unable to update notification permission.',
        );
      }
      return PushPermissionStatus.unavailable;
    }
  }

  String? takePendingDeepLink() {
    final link = state.pendingDeepLink;
    if (link != null) state = state.copyWith(clearPendingDeepLink: true);
    return link;
  }

  void clearError() => state = state.copyWith(clearError: true);

  Future<void> _synchronizeSession(SessionState session) async {
    final userId = session.publicUserId;
    if (!session.isAuthenticated || userId == null) {
      _sessionGeneration++;
      _activeUserId = null;
      _registeredPushToken = null;
      state = const NotificationState();
      return;
    }

    final isNewUser = userId != _activeUserId;
    _activeUserId = userId;
    final generation = ++_sessionGeneration;
    if (isNewUser) {
      _registeredPushToken = null;
      state = const NotificationState(isLoading: true);
    }

    await _initializePush(userId, generation);
    if (!_isCurrent(userId, generation)) return;
    await _loadFirstPage(refreshing: !isNewUser);
  }

  Future<void> _initializePush(String userId, int generation) async {
    try {
      if (!await _pushGateway.initialize() || !_isCurrent(userId, generation)) {
        if (_isCurrent(userId, generation)) {
          state = state.copyWith(
            permissionStatus: PushPermissionStatus.unavailable,
          );
        }
        return;
      }

      _startPushSubscriptions();
      final status = await _pushGateway.permissionStatus();
      if (!_isCurrent(userId, generation)) return;
      state = state.copyWith(permissionStatus: status);

      final initialLink = normalizeNotificationDeepLink(
        await _pushGateway.initialDeepLink(),
      );
      if (!_isCurrent(userId, generation)) return;
      if (initialLink != null) {
        state = state.copyWith(pendingDeepLink: initialLink);
      }
      if (_canReceivePush(status)) await _synchronizePushToken(userId);
    } on Object {
      if (_isCurrent(userId, generation)) {
        state = state.copyWith(
          permissionStatus: PushPermissionStatus.unavailable,
        );
      }
    }
  }

  void _startPushSubscriptions() {
    if (_pushStreamsStarted) return;
    _pushStreamsStarted = true;
    _tokenRefreshSubscription = _pushGateway.tokenRefreshes.listen(
      (token) => unawaited(_registerPushToken(token)),
    );
    _openedDeepLinkSubscription = _pushGateway.openedDeepLinks.listen((link) {
      final normalized = normalizeNotificationDeepLink(link);
      if (_activeUserId != null && normalized != null) {
        state = state.copyWith(pendingDeepLink: normalized);
      }
    });
    _foregroundMessageSubscription = _pushGateway.foregroundMessages.listen(
      (_) => unawaited(refresh()),
    );
  }

  Future<void> _synchronizePushToken(String userId) async {
    final pushToken = await _pushGateway.token();
    if (_isActive(userId) && pushToken != null && pushToken.trim().isNotEmpty) {
      await _registerPushToken(pushToken);
    }
  }

  Future<void> _registerPushToken(String pushToken) async {
    final normalizedToken = pushToken.trim();
    final token = _accessToken;
    final userId = _activeUserId;
    if (normalizedToken.isEmpty ||
        normalizedToken == _registeredPushToken ||
        token == null ||
        userId == null) {
      return;
    }

    try {
      final device = await _deviceMetadata.get();
      if (!_isActive(userId)) return;
      await _repository.registerDevice(
        token: token,
        device: device,
        pushToken: normalizedToken,
      );
      if (_isActive(userId)) _registeredPushToken = normalizedToken;
    } on Object {
      // Inbox access remains available when push registration is unavailable.
    }
  }

  Future<void> _loadFirstPage({required bool refreshing}) async {
    final token = _accessToken;
    final userId = _activeUserId;
    if (token == null || userId == null) return;

    state = state.copyWith(
      isLoading: !refreshing,
      isRefreshing: refreshing,
      isOffline: false,
      clearError: true,
    );
    try {
      final page = await _repository.getNotifications(
        token,
        pageSize: _pageSize,
      );
      final unreadCount = await _repository.getUnreadCount(token);
      if (!_isActive(userId)) return;
      state = state.copyWith(
        items: page.items,
        unreadCount: unreadCount,
        page: page.page,
        hasMore: page.hasMore,
        isLoading: false,
        isRefreshing: false,
      );
    } on Object catch (error) {
      if (_isActive(userId)) _setFailure(error, refreshing: refreshing);
    }
  }

  void _setFailure(
    Object error, {
    bool refreshing = false,
    bool loadingMore = false,
  }) {
    final isApiError = error is ApiException;
    state = state.copyWith(
      isLoading: false,
      isRefreshing: refreshing ? false : state.isRefreshing,
      isLoadingMore: loadingMore ? false : state.isLoadingMore,
      isOffline: !isApiError,
      errorMessage: isApiError ? error.message : _offlineMessage,
    );
  }

  bool _isActive(String userId) => _activeUserId == userId;

  bool _isCurrent(String userId, int generation) =>
      _isActive(userId) && _sessionGeneration == generation;

  void _disposePushSubscriptions() {
    unawaited(_tokenRefreshSubscription?.cancel());
    unawaited(_openedDeepLinkSubscription?.cancel());
    unawaited(_foregroundMessageSubscription?.cancel());
  }
}

bool _canReceivePush(PushPermissionStatus status) =>
    status == PushPermissionStatus.authorized ||
    status == PushPermissionStatus.provisional;

String? normalizeNotificationDeepLink(String? value) {
  final candidate = value?.trim();
  if (candidate == null || candidate.isEmpty || !candidate.startsWith('/')) {
    return null;
  }

  final uri = Uri.tryParse(candidate);
  if (uri == null ||
      uri.hasScheme ||
      uri.hasAuthority ||
      candidate.startsWith('//')) {
    return null;
  }

  const allowedRoots = {
    '/home',
    '/customer',
    '/catalogue',
    '/checkout',
    '/orders',
    '/subscriptions',
    '/payments',
    '/wallet',
    '/admin',
    '/deliveries',
    '/delivery',
    '/delivery-management',
    '/cameras',
    '/dairy',
  };
  final path = uri.path;
  final isAllowed = allowedRoots.any(
    (root) => path == root || path.startsWith('$root/'),
  );
  return isAllowed ? uri.toString() : null;
}

List<AppNotification> _mergeNotifications(
  List<AppNotification> current,
  List<AppNotification> incoming,
) {
  final seen = current.map((item) => item.notificationId).toSet();
  return [
    ...current,
    ...incoming.where((item) => seen.add(item.notificationId)),
  ];
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
