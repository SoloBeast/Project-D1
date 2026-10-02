import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/branding/doodh_brand_mark.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
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
        constraints: const BoxConstraints(maxWidth: DoodhContentMax.wide),
        child: padding
            ? Padding(
                padding: EdgeInsets.symmetric(
                  horizontal: constraints.maxWidth < DoodhBreakpoints.compact
                      ? DoodhSpacing.md
                      : 28,
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
        child: Semantics(
          header: true,
          child: Text(title, style: Theme.of(context).textTheme.titleLarge),
        ),
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
            ExcludeSemantics(
              child: Container(
                width: 44,
                height: 44,
                decoration: BoxDecoration(
                  color: (color ?? DoodhColors.mint).withValues(alpha: .8),
                  borderRadius: DoodhRadii.sm,
                ),
                child: Icon(icon, color: DoodhColors.tealDark),
              ),
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
            const ExcludeSemantics(
              child: Icon(Icons.arrow_forward_ios_rounded, size: 16),
            ),
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
    return Semantics(
      label: label,
      container: true,
      child: ExcludeSemantics(
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: colors.$1,
            borderRadius: BorderRadius.circular(DoodhRadii.pillValue),
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
    this.showBrandInAppBar = true,
    this.header,
  });

  final Widget child;
  final String currentPath;
  final String? title;
  final List<Widget> actions;
  final Widget? floatingActionButton;

  /// Whether the AppBar carries the brand mark (+ name). Surfaces that draw
  /// their own large brand header (customer home) pass `false` so the name is
  /// not shown twice on one screen.
  final bool showBrandInAppBar;

  /// Rich leading widget replacing the AppBar title entirely (customer home:
  /// brand row + tagline + notification + wallet, all sharing the cart-icon
  /// row so no empty bar is wasted above the content).
  final Widget? header;

  static const _items = [
    (
      icon: Icons.home_outlined,
      selected: Icons.home_rounded,
      label: 'Home',
      path: '/home',
    ),
    (
      icon: Icons.storefront_outlined,
      selected: Icons.storefront_rounded,
      label: 'Products',
      path: '/catalogue',
    ),
    (
      icon: Icons.event_repeat_outlined,
      selected: Icons.event_repeat_rounded,
      label: 'Subscription',
      path: '/subscriptions',
    ),
    (
      icon: Icons.shopping_cart_outlined,
      selected: Icons.shopping_cart_rounded,
      label: 'Cart',
      path: '/checkout',
    ),
    (
      icon: Icons.more_horiz_outlined,
      selected: Icons.more_horiz_rounded,
      label: 'More',
      path: '/more',
    ),
  ];

  int get _selectedIndex {
    if (currentPath.startsWith('/orders') ||
        currentPath.startsWith('/wallet') ||
        currentPath.startsWith('/notifications') ||
        currentPath.startsWith('/customer/account')) {
      return 4;
    }
    final index = _items.indexWhere(
      (item) => currentPath.startsWith(item.path),
    );
    return index >= 0 && index < 4 ? index : 0;
  }

  void _openMore(BuildContext context, WidgetRef ref) {
    showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      backgroundColor: Colors.white,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(
          top: Radius.circular(DoodhRadii.lgValue),
        ),
      ),
      builder: (sheetContext) {
        void go(String path) {
          Navigator.of(sheetContext).pop();
          context.go(path);
        }

        void push(String path) {
          Navigator.of(sheetContext).pop();
          context.push(path);
        }

        return SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(
              DoodhSpacing.md,
              DoodhSpacing.sm,
              DoodhSpacing.md,
              DoodhSpacing.md,
            ),
            child: ListView(
              shrinkWrap: true,
              children: [
                const ListTile(
                  contentPadding: EdgeInsets.symmetric(
                    horizontal: DoodhSpacing.sm,
                  ),
                  title: Text(
                    'More',
                    style: TextStyle(fontWeight: FontWeight.w800),
                  ),
                  subtitle: Text('Manage your DoodhDirect account'),
                ),
                _MoreDestinationTile(
                  icon: Icons.receipt_long_outlined,
                  label: 'Orders',
                  onTap: () => go('/orders'),
                ),
                _MoreDestinationTile(
                  icon: Icons.account_balance_wallet_outlined,
                  label: 'Wallet',
                  onTap: () => go('/wallet'),
                ),
                _MoreDestinationTile(
                  icon: Icons.notifications_none,
                  label: 'Notifications',
                  onTap: () => push('/notifications'),
                ),
                _MoreDestinationTile(
                  icon: Icons.person_outline,
                  label: 'Profile / Account',
                  onTap: () => go('/customer/account'),
                ),
                const Divider(height: DoodhSpacing.lg),
                _MoreDestinationTile(
                  key: const ValueKey('customer-more-sign-out'),
                  icon: Icons.logout_rounded,
                  label: 'Sign out',
                  destructive: true,
                  onTap: () async {
                    Navigator.of(sheetContext).pop();
                    await ref
                        .read(sessionControllerProvider.notifier)
                        .signOut();
                  },
                ),
              ],
            ),
          ),
        );
      },
    );
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final cartCount = ref.watch(
      orderControllerProvider.select((state) => state.cart.length),
    );
    return Scaffold(
      appBar: AppBar(
        // The customer home passes a rich two-row header (brand block +
        // icons, wallet chip below) — give the toolbar room for it. All
        // other shells keep the standard height.
        toolbarHeight: header != null ? 104 : kToolbarHeight,
        titleSpacing: 20,
        title:
            header ??
            Row(
              children: [
                if (showBrandInAppBar) ...[
                  // Business ask: logo BESIDE the name — the business-uploaded
                  // logo when branding is configured, the bundled drop icon
                  // otherwise.
                  const DoodhBrandMark(showWordmark: false, height: 26),
                  // When a screen title is present the brand NAME is dropped
                  // (the logo mark stays): brand + title together squeezed
                  // both into ellipsis ("DoodhD..." / cramped titles) on
                  // phones, and the name is redundant next to the title.
                  if (title == null) ...[
                    const SizedBox(width: DoodhSpacing.sm),
                    Flexible(
                      child: Text(
                        'DoodhDirect',
                        overflow: TextOverflow.ellipsis,
                        style: Theme.of(context).textTheme.titleLarge,
                      ),
                    ),
                  ],
                ],
                if (title != null) ...[
                  SizedBox(
                    width: showBrandInAppBar ? DoodhSpacing.sm : 12,
                  ),
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
        actions: [...actions],
      ),
      body: child,
      floatingActionButton: floatingActionButton,
      bottomNavigationBar: NavigationBar(
        selectedIndex: _selectedIndex,
        onDestinationSelected: (index) {
          if (index == 4) {
            _openMore(context, ref);
          } else {
            context.go(_items[index].path);
          }
        },
        destinations: [
          for (var index = 0; index < _items.length; index++)
            NavigationDestination(
              icon: index == 3
                  ? Badge(
                      isLabelVisible: cartCount > 0,
                      label: Text(cartCount > 99 ? '99+' : '$cartCount'),
                      child: Icon(_items[index].icon),
                    )
                  : Icon(_items[index].icon),
              selectedIcon: index == 3
                  ? Badge(
                      isLabelVisible: cartCount > 0,
                      label: Text(cartCount > 99 ? '99+' : '$cartCount'),
                      child: Icon(_items[index].selected),
                    )
                  : Icon(_items[index].selected),
              label: _items[index].label,
              key: index == 4
                  ? const ValueKey('customer-more-navigation')
                  : null,
            ),
        ],
      ),
    );
  }
}

class _MoreDestinationTile extends StatelessWidget {
  const _MoreDestinationTile({
    super.key,
    required this.icon,
    required this.label,
    required this.onTap,
    this.destructive = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final bool destructive;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: const EdgeInsets.symmetric(horizontal: DoodhSpacing.sm),
    shape: RoundedRectangleBorder(borderRadius: DoodhRadii.md),
    leading: Icon(
      icon,
      color: destructive ? DoodhColors.coral : DoodhColors.teal,
    ),
    title: Text(
      label,
      style: TextStyle(
        color: destructive ? DoodhColors.coral : DoodhColors.ink,
        fontWeight: FontWeight.w700,
      ),
    ),
    trailing: const Icon(Icons.chevron_right_rounded),
    onTap: onTap,
  );
}

class DoodhHeroCard extends StatelessWidget {
  const DoodhHeroCard({super.key, required this.onBuy});

  final VoidCallback onBuy;

  @override
  Widget build(BuildContext context) => Card(
    clipBehavior: Clip.antiAlias,
    color: DoodhColors.teal,
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
                label: const Text('Shop'),
                style: FilledButton.styleFrom(
                  backgroundColor: Colors.white,
                  foregroundColor: DoodhColors.teal,
                ),
              ),
            ],
          );
          if (compact) return content;
          return Row(
            children: [
              Expanded(child: content),
              const SizedBox(width: 12),
              const ExcludeSemantics(
                child: Icon(
                  Icons.local_drink_rounded,
                  size: 82,
                  color: Colors.white24,
                ),
              ),
            ],
          );
        },
      ),
    ),
  );
}
