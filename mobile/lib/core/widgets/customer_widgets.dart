import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

class DoodhPage extends StatelessWidget {
  const DoodhPage({super.key, required this.child, this.padding = true});

  final Widget child;
  final bool padding;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => Align(
      alignment: Alignment.topCenter,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 1180),
        child: padding
            ? Padding(
                padding: EdgeInsets.symmetric(
                  horizontal: constraints.maxWidth < 600 ? 16 : 28,
                  vertical: 20,
                ),
                child: child,
              )
            : child,
      ),
    ),
  );
}

class DoodhSectionHeader extends StatelessWidget {
  const DoodhSectionHeader({super.key, required this.title, this.action});

  final String title;
  final Widget? action;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      Expanded(
        child: Text(title, style: Theme.of(context).textTheme.titleLarge),
      ),
      ?action,
    ],
  );
}

class DoodhActionTile extends StatelessWidget {
  const DoodhActionTile({
    super.key,
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
    this.color,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;
  final Color? color;

  @override
  Widget build(BuildContext context) => Card(
    child: InkWell(
      borderRadius: DoodhRadii.md,
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Row(
          children: [
            Container(
              width: 44,
              height: 44,
              decoration: BoxDecoration(
                color: (color ?? DoodhColors.mint).withValues(alpha: .8),
                borderRadius: DoodhRadii.sm,
              ),
              child: Icon(icon, color: DoodhColors.tealDark),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(title, style: Theme.of(context).textTheme.titleMedium),
                  const SizedBox(height: 3),
                  Text(subtitle, maxLines: 2, overflow: TextOverflow.ellipsis),
                ],
              ),
            ),
            const Icon(Icons.arrow_forward_ios_rounded, size: 16),
          ],
        ),
      ),
    ),
  );
}

class DoodhStatusPill extends StatelessWidget {
  const DoodhStatusPill({
    super.key,
    required this.label,
    this.tone = DoodhStatusTone.neutral,
  });

  final String label;
  final DoodhStatusTone tone;

  @override
  Widget build(BuildContext context) {
    final colors = switch (tone) {
      DoodhStatusTone.success => (DoodhColors.mint, DoodhColors.tealDark),
      DoodhStatusTone.warning => (
        const Color(0xFFFFF2D2),
        const Color(0xFF855D00),
      ),
      DoodhStatusTone.error => (const Color(0xFFFCE5E0), DoodhColors.coral),
      DoodhStatusTone.neutral => (const Color(0xFFEFF2F0), DoodhColors.muted),
    };
    return DecoratedBox(
      decoration: BoxDecoration(
        color: colors.$1,
        borderRadius: BorderRadius.circular(30),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
        child: Text(
          label,
          style: TextStyle(
            color: colors.$2,
            fontSize: 12,
            fontWeight: FontWeight.w700,
          ),
        ),
      ),
    );
  }
}

enum DoodhStatusTone { success, warning, error, neutral }

class CustomerShell extends ConsumerWidget {
  const CustomerShell({
    super.key,
    required this.child,
    required this.currentPath,
    this.title,
    this.actions = const [],
    this.floatingActionButton,
  });

  final Widget child;
  final String currentPath;
  final String? title;
  final List<Widget> actions;
  final Widget? floatingActionButton;

  static const _items = [
    (
      icon: Icons.home_outlined,
      selected: Icons.home,
      label: 'Home',
      path: '/home',
    ),
    (
      icon: Icons.receipt_long_outlined,
      selected: Icons.receipt_long,
      label: 'Orders',
      path: '/orders',
    ),
    (
      icon: Icons.event_repeat_outlined,
      selected: Icons.event_repeat,
      label: 'Subscribe',
      path: '/subscriptions',
    ),
    (
      icon: Icons.account_balance_wallet_outlined,
      selected: Icons.account_balance_wallet,
      label: 'Wallet',
      path: '/wallet',
    ),
    (
      icon: Icons.person_outline,
      selected: Icons.person,
      label: 'Profile',
      path: '/customer/account',
    ),
  ];

  int get _selectedIndex {
    final index = _items.indexWhere(
      (item) => currentPath.startsWith(item.path),
    );
    return index < 0 ? 0 : index;
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final session = ref.watch(sessionControllerProvider).session;
    final displayName = session?.user.displayName?.trim();
    final initials = displayName == null || displayName.isEmpty
        ? 'D'
        : displayName.substring(0, 1).toUpperCase();
    return Scaffold(
      appBar: AppBar(
        titleSpacing: 20,
        title: Row(
          children: [
            const Icon(Icons.water_drop_rounded, color: DoodhColors.teal),
            const SizedBox(width: 8),
            Flexible(
              child: Text(
                'DoodhDirect',
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.titleLarge,
              ),
            ),
            if (title != null) ...[
              const SizedBox(width: 12),
              Flexible(
                child: Text(
                  title!,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
            ],
          ],
        ),
        actions: [
          ...actions,
          const _ShellNotificationButton(),
          Padding(
            padding: const EdgeInsets.only(right: 16, left: 4),
            child: CircleAvatar(
              radius: 17,
              backgroundColor: DoodhColors.mint,
              foregroundColor: DoodhColors.tealDark,
              child: Text(
                initials,
                style: const TextStyle(fontWeight: FontWeight.w800),
              ),
            ),
          ),
        ],
      ),
      body: child,
      floatingActionButton: floatingActionButton,
      bottomNavigationBar: NavigationBar(
        selectedIndex: _selectedIndex,
        onDestinationSelected: (index) => context.go(_items[index].path),
        destinations: [
          for (final item in _items)
            NavigationDestination(
              icon: Icon(item.icon),
              selectedIcon: Icon(item.selected),
              label: item.label,
            ),
        ],
      ),
    );
  }
}

class _ShellNotificationButton extends ConsumerWidget {
  const _ShellNotificationButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final count = ref.watch(
      notificationControllerProvider.select((state) => state.unreadCount),
    );
    return IconButton(
      tooltip: 'Notifications',
      onPressed: () => context.push('/notifications'),
      icon: Badge(
        isLabelVisible: count > 0,
        label: Text(count > 99 ? '99+' : '$count'),
        child: const Icon(Icons.notifications_none),
      ),
    );
  }
}

class DoodhHeroCard extends StatelessWidget {
  const DoodhHeroCard({super.key, required this.onBuy});

  final VoidCallback onBuy;

  @override
  Widget build(BuildContext context) => Card(
    clipBehavior: Clip.antiAlias,
    color: DoodhColors.tealDark,
    child: Padding(
      padding: const EdgeInsets.all(22),
      child: LayoutBuilder(
        builder: (context, constraints) {
          final compact = constraints.maxWidth < 420;
          final content = Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const DoodhStatusPill(
                label: 'FRESH FROM THE DAIRY',
                tone: DoodhStatusTone.success,
              ),
              const SizedBox(height: 14),
              Text(
                'Fresh Buffalo Milk',
                style: Theme.of(context).textTheme.headlineSmall
                    ?.copyWith(color: Colors.white),
              ),
              const SizedBox(height: 6),
              Text(
                'Delivered to your doorstep, when you need it.',
                style: Theme.of(context).textTheme.bodyMedium
                    ?.copyWith(color: Colors.white70),
              ),
              const SizedBox(height: 16),
              FilledButton.icon(
                onPressed: onBuy,
                icon: const Icon(Icons.shopping_bag_outlined),
                label: const Text('Buy milk'),
                style: FilledButton.styleFrom(
                  backgroundColor: Colors.white,
                  foregroundColor: DoodhColors.tealDark,
                ),
              ),
            ],
          );
          if (compact) return content;
          return Row(
            children: [
              Expanded(child: content),
              const SizedBox(width: 12),
              const Icon(
                Icons.local_drink_rounded,
                size: 82,
                color: Colors.white24,
              ),
            ],
          );
        },
      ),
    ),
  );
}
