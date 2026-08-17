import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'notification_controller.dart';
import 'notification_models.dart';
import 'push_notification_gateway.dart';

class NotificationInboxScreen extends ConsumerStatefulWidget {
  const NotificationInboxScreen({super.key});

  @override
  ConsumerState<NotificationInboxScreen> createState() =>
      _NotificationInboxScreenState();
}

class _NotificationInboxScreenState
    extends ConsumerState<NotificationInboxScreen> {
  late final ScrollController _scrollController;

  @override
  void initState() {
    super.initState();
    _scrollController = ScrollController()..addListener(_onScroll);
  }

  @override
  void dispose() {
    _scrollController
      ..removeListener(_onScroll)
      ..dispose();
    super.dispose();
  }

  void _onScroll() {
    if (_scrollController.position.extentAfter < 320) {
      ref.read(notificationControllerProvider.notifier).loadMore();
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(notificationControllerProvider);
    ref.listen<String?>(
      notificationControllerProvider.select((value) => value.errorMessage),
      (previous, next) {
        if (next != null && state.items.isNotEmpty && next != previous) {
          ScaffoldMessenger.of(context)
            ..hideCurrentSnackBar()
            ..showSnackBar(SnackBar(content: Text(next)));
        }
      },
    );

    return Scaffold(
      appBar: AppBar(
        title: const Text('Notifications'),
        actions: [
          IconButton(
            tooltip: 'Refresh notifications',
            onPressed: state.isRefreshing
                ? null
                : () => ref
                      .read(notificationControllerProvider.notifier)
                      .refresh(),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: Column(
        children: [
          _PermissionPanel(state: state),
          Expanded(child: _buildContent(state)),
        ],
      ),
    );
  }

  Widget _buildContent(NotificationState state) {
    final controller = ref.read(notificationControllerProvider.notifier);
    if (state.isLoading && state.items.isEmpty) {
      return const LoadingStatePanel(message: 'Loading notifications');
    }
    if (state.items.isEmpty && state.isOffline) {
      return OfflineStatePanel(onRetry: controller.loadInitial);
    }
    if (state.items.isEmpty && state.errorMessage != null) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: controller.loadInitial,
      );
    }
    if (state.items.isEmpty) {
      return EmptyStatePanel(
        title: 'No notifications',
        message: 'Updates about your orders and deliveries will appear here.',
        action: OutlinedButton.icon(
          onPressed: controller.refresh,
          icon: const Icon(Icons.refresh),
          label: const Text('Refresh'),
        ),
      );
    }

    return RefreshIndicator(
      onRefresh: controller.refresh,
      child: ListView.separated(
        controller: _scrollController,
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.symmetric(vertical: 8),
        itemCount: state.items.length + (state.hasMore ? 1 : 0),
        separatorBuilder: (context, index) => const Divider(height: 1),
        itemBuilder: (context, index) {
          if (index == state.items.length) {
            return const SizedBox(
              height: 72,
              child: Center(child: CircularProgressIndicator()),
            );
          }
          final notification = state.items[index];
          return _NotificationTile(
            notification: notification,
            onTap: () => _openNotification(notification),
          );
        },
      ),
    );
  }

  Future<void> _openNotification(AppNotification notification) async {
    final link = await ref
        .read(notificationControllerProvider.notifier)
        .markRead(notification);
    if (mounted && link != null) context.push(link);
  }
}

class _PermissionPanel extends ConsumerWidget {
  const _PermissionPanel({required this.state});

  final NotificationState state;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = state.permissionStatus;
    if (status == PushPermissionStatus.authorized ||
        status == PushPermissionStatus.provisional ||
        status == PushPermissionStatus.unavailable) {
      return const SizedBox.shrink();
    }

    final canRequest = status == PushPermissionStatus.notDetermined;
    return Material(
      color: Theme.of(context).colorScheme.surfaceContainerLow,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 12, 12),
        child: Row(
          children: [
            const Icon(Icons.notifications_off_outlined),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                canRequest
                    ? 'Enable push notifications for timely updates.'
                    : 'Push notifications are disabled in system settings.',
              ),
            ),
            if (canRequest)
              TextButton(
                onPressed: state.isRequestingPermission
                    ? null
                    : () => ref
                          .read(notificationControllerProvider.notifier)
                          .requestPushPermission(),
                child: state.isRequestingPermission
                    ? const SizedBox.square(
                        dimension: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Enable'),
              ),
          ],
        ),
      ),
    );
  }
}

class _NotificationTile extends StatelessWidget {
  const _NotificationTile({required this.notification, required this.onTap});

  final AppNotification notification;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Material(
      color: notification.isRead
          ? Colors.transparent
          : colors.primaryContainer.withValues(alpha: 0.28),
      child: ListTile(
        contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        leading: SizedBox.square(
          dimension: 40,
          child: Icon(
            notification.isRead
                ? Icons.notifications_none
                : Icons.notifications_active_outlined,
            color: notification.isRead
                ? colors.onSurfaceVariant
                : colors.primary,
          ),
        ),
        title: Text(
          notification.title,
          style: TextStyle(
            fontWeight: notification.isRead ? FontWeight.w500 : FontWeight.w700,
          ),
        ),
        subtitle: Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(notification.body),
              const SizedBox(height: 6),
              Text(
                _formatTimestamp(notification.createdAtUtc),
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ],
          ),
        ),
        trailing: notification.deepLink == null
            ? null
            : const Icon(Icons.chevron_right),
        onTap: onTap,
      ),
    );
  }
}

String _formatTimestamp(DateTime utcValue) {
  final value = utcValue.toLocal();
  final now = DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  final date = DateTime(value.year, value.month, value.day);
  final hour = value.hour == 0
      ? 12
      : value.hour > 12
      ? value.hour - 12
      : value.hour;
  final minute = value.minute.toString().padLeft(2, '0');
  final period = value.hour >= 12 ? 'PM' : 'AM';
  final time = '$hour:$minute $period';
  if (date == today) return 'Today, $time';
  if (date == today.subtract(const Duration(days: 1))) {
    return 'Yesterday, $time';
  }
  return '${value.day.toString().padLeft(2, '0')}/'
      '${value.month.toString().padLeft(2, '0')}/${value.year}, $time';
}
