import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:go_router/go_router.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Display label for a branch id: the server-resolved branch name (e.g.
/// "Dabua"/"NIT3") when the authenticated session carries it, otherwise the
/// numeric fallback used by legacy cached sessions that predate the enriched
/// branchDetails metadata.
String branchDisplayName(List<AuthUserBranchInfo> details, int id) {
  for (final branch in details) {
    if (branch.id == id) return branch.name;
  }
  return 'Branch $id';
}

class RoleHomeScreen extends ConsumerWidget {
  const RoleHomeScreen({super.key, required this.role});

  final UserRole role;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (role == UserRole.customer) return const _CustomerHomeActions();
    final user = ref.watch(sessionControllerProvider).session?.user;
    return Scaffold(
      appBar: AppBar(
        title: Text('${role.label} workspace'),
        actions: [
          IconButton(
            tooltip: 'Login & security',
            onPressed: () => context.push('/security'),
            icon: const Icon(Icons.account_circle_outlined),
          ),
          const _NotificationButton(),
          const _SignOutButton(),
        ],
      ),
      body: switch (role) {
        UserRole.customer => const _CustomerHomeActions(),
        UserRole.delivery => _DeliveryHomeActions(
          roles: user?.roles ?? const [],
          permissions: user?.permissions ?? const [],
          branchIds: user?.branchIds ?? const [],
          branchDetails: user?.branchDetails ?? const [],
        ),
        UserRole.dairy => const _DairyHomeActions(),
        UserRole.owner || UserRole.admin => _AdminHomeActions(
          permissions: user?.permissions ?? const [],
          branchIds: user?.branchIds ?? const [],
          branchDetails: user?.branchDetails ?? const [],
        ),
        UserRole.support => const _SupportHomeActions(),
        UserRole.accountant => const StatePanel(
          icon: Icons.account_balance_outlined,
          title: 'Accounting workspace ready',
          message: 'Accounting workflows remain outside the Identity and RBAC phase.',
        ),
      },
    );
  }
}

class _SignOutButton extends ConsumerWidget {
  const _SignOutButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) => IconButton(
    tooltip: 'Sign out',
    onPressed: () => ref.read(sessionControllerProvider.notifier).signOut(),
    icon: const Icon(Icons.logout),
  );
}

class _NotificationButton extends ConsumerWidget {
  const _NotificationButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unreadCount = ref.watch(
      notificationControllerProvider.select((state) => state.unreadCount),
    );
    return SizedBox.square(
      dimension: 48,
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          Positioned.fill(
            child: IconButton(
              tooltip: 'Notifications',
              onPressed: () => context.push('/notifications'),
              icon: const Icon(Icons.notifications_outlined),
            ),
          ),
          if (unreadCount > 0)
            Positioned(
              right: 2,
              top: 3,
              child: IgnorePointer(
                child: Container(
                  constraints: const BoxConstraints(
                    minWidth: 18,
                    minHeight: 18,
                  ),
                  padding: const EdgeInsets.symmetric(horizontal: 4),
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: Theme.of(context).colorScheme.error,
                    borderRadius: BorderRadius.circular(9),
                    border: Border.all(
                      color: Theme.of(context).colorScheme.surface,
                      width: 1.5,
                    ),
                  ),
                  child: Text(
                    unreadCount > 99 ? '99+' : '$unreadCount',
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.onError,
                      fontSize: 10,
                      fontWeight: FontWeight.w700,
                      height: 1,
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _CartButton extends ConsumerWidget {
  const _CartButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final cartCount = ref.watch(
      orderControllerProvider.select((state) => state.cart.length),
    );
    return IconButton(
      key: const ValueKey('customer-cart-action'),
      tooltip: 'Cart',
      onPressed: () {
        ScaffoldMessenger.of(context).hideCurrentSnackBar();
        context.go('/checkout');
      },
      icon: Badge(
        isLabelVisible: cartCount > 0,
        label: Text(cartCount > 99 ? '99+' : '$cartCount'),
        child: const Icon(Icons.shopping_cart_outlined),
      ),
    );
  }
}

class _CustomerHomeActions extends ConsumerStatefulWidget {
  const _CustomerHomeActions();

  @override
  ConsumerState<_CustomerHomeActions> createState() =>
      _CustomerHomeActionsState();
}

class _CustomerHomeActionsState extends ConsumerState<_CustomerHomeActions> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      ref.read(customerControllerProvider.notifier).load();
      ref.read(catalogueControllerProvider.notifier).load();
      ref.read(orderControllerProvider.notifier).loadOrders();
      ref.read(walletControllerProvider.notifier).load();
    });
  }

  @override
  Widget build(BuildContext context) => CustomerShell(
    currentPath: '/home',
    actions: const [_CartButton(), _SignOutButton()],
    child: DoodhPage(
      child: RefreshIndicator(
        onRefresh: () async {
          await Future.wait([
            ref.read(customerControllerProvider.notifier).load(),
            ref.read(catalogueControllerProvider.notifier).load(),
            ref.read(orderControllerProvider.notifier).loadOrders(),
            ref.read(walletControllerProvider.notifier).load(),
          ]);
        },
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          children: [
            _GreetingHeader(),
            const SizedBox(height: 20),
            DoodhHeroCard(onBuy: () => context.push('/catalogue')),
            const SizedBox(height: 24),
            const DoodhSectionHeader(title: 'Your shortcuts'),
            const SizedBox(height: 12),
            LayoutBuilder(
              builder: (context, constraints) {
                final columns = constraints.maxWidth >= 720 ? 4 : 2;
                return GridView.count(
                  crossAxisCount: columns,
                  shrinkWrap: true,
                  physics: const NeverScrollableScrollPhysics(),
                  mainAxisSpacing: 12,
                  crossAxisSpacing: 12,
                  childAspectRatio: constraints.maxWidth >= 720 ? 1.7 : 1.35,
                  children: [
                    _QuickAction(
                      icon: Icons.shopping_bag_outlined,
                      label: 'Shop',
                      onTap: () => context.push('/catalogue'),
                    ),
                    _QuickAction(
                      icon: Icons.receipt_long_outlined,
                      label: 'My orders',
                      onTap: () => context.go('/orders'),
                    ),
                    _QuickAction(
                      icon: Icons.event_repeat_outlined,
                      label: 'Subscribe',
                      onTap: () => context.go('/subscriptions'),
                    ),
                    _QuickAction(
                      icon: Icons.account_balance_wallet_outlined,
                      label: 'Wallet',
                      onTap: () => context.go('/wallet'),
                    ),
                  ],
                );
              },
            ),
            const SizedBox(height: 24),
            _HomeContextCards(),
          ],
        ),
      ),
    ),
  );
}

class _GreetingHeader extends ConsumerWidget {
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final name = ref
        .watch(sessionControllerProvider)
        .session
        ?.user
        .displayName
        ?.trim();
    final firstName = name == null || name.isEmpty
        ? 'there'
        : name.split(' ').first;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Good day, $firstName',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
        const SizedBox(height: 4),
        const Text('Fresh dairy, delivered with care.'),
      ],
    );
  }
}

class _QuickAction extends StatelessWidget {
  const _QuickAction({
    required this.icon,
    required this.label,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Card(
    child: InkWell(
      borderRadius: DoodhRadii.md,
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(icon, color: DoodhColors.teal, size: 28),
            const SizedBox(height: 10),
            Flexible(
              child: Text(
                label,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.titleMedium,
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

class _HomeContextCards extends ConsumerWidget {
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final orders = ref.watch(orderControllerProvider).orders;
    final wallet = ref.watch(walletControllerProvider).wallet;
    final customer = ref.watch(customerControllerProvider);
    final latestOrder = orders.isEmpty ? null : orders.first;
    return Column(
      children: [
        if (latestOrder != null) ...[
          DoodhSectionHeader(
            title: 'Latest order',
            action: TextButton(
              onPressed: () => context.go('/orders'),
              child: const Text('View all'),
            ),
          ),
          const SizedBox(height: 12),
          DoodhActionTile(
            icon: Icons.local_shipping_outlined,
            title: latestOrder.orderNumber,
            subtitle:
                '${latestOrder.itemSummary}\n${latestOrder.formattedTotal}',
            onTap: () => context.push('/orders/${latestOrder.publicId}'),
            color: const Color(0xFFE6F1FA),
          ),
          const SizedBox(height: 24),
        ],
        DoodhSectionHeader(
          title: 'Stay on track',
          action: TextButton(
            onPressed: () => context.push('/deliveries'),
            child: const Text('Deliveries'),
          ),
        ),
        const SizedBox(height: 12),
        DoodhActionTile(
          icon: Icons.location_on_outlined,
          title: customer.addresses.isEmpty
              ? 'Add a delivery address'
              : 'Your delivery address',
          subtitle: customer.addresses.isEmpty
              ? 'Set your preferred doorstep before ordering'
              : customer.addresses.first.label,
          onTap: () => context.push('/customer/account'),
        ),
        const SizedBox(height: 12),
        DoodhActionTile(
          icon: Icons.account_balance_wallet_outlined,
          title: wallet == null
              ? 'Wallet'
              : 'Wallet · ₹${wallet.balance.toStringAsFixed(2)}',
          subtitle: 'Review balance and recent transactions',
          onTap: () => context.go('/wallet'),
          color: const Color(0xFFFFF2D2),
        ),
      ],
    );
  }
}

class _DeliveryHomeActions extends StatelessWidget {
  const _DeliveryHomeActions({
    required this.roles,
    required this.permissions,
    required this.branchIds,
    required this.branchDetails,
  });

  final List<String> roles;
  final List<String> permissions;
  final List<int> branchIds;
  final List<AuthUserBranchInfo> branchDetails;

  bool get _canManage =>
      roles.contains('DELIVERY_MANAGER') ||
      permissions.contains('DELIVERIES.READ_BRANCH');

  @override
  Widget build(BuildContext context) {
    final branchId = branchIds.isEmpty ? null : branchIds.first;
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text(
          _canManage ? 'Delivery management' : 'Delivery route',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
        const SizedBox(height: 16),
        if (!_canManage)
          Card(
            child: ListTile(
              leading: const Icon(Icons.route_outlined),
              title: const Text("Today's deliveries"),
              subtitle: const Text('Operate deliveries assigned to you'),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/delivery'),
            ),
          ),
        if (_canManage && branchId != null)
          Card(
            child: ListTile(
              leading: const Icon(Icons.local_shipping_outlined),
              title: Text(
                '${branchDisplayName(branchDetails, branchId)} deliveries',
              ),
              subtitle: const Text(
                'Assign staff and monitor delivery progress',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () =>
                  context.push('/delivery-management/branch/$branchId'),
            ),
          ),
        if (_canManage && branchId == null)
          const StatePanel(
            icon: Icons.location_off_outlined,
            title: 'No branch assigned',
            message: 'A branch assignment is required to manage deliveries.',
          ),
      ],
    );
  }
}

class _SupportHomeActions extends StatelessWidget {
  const _SupportHomeActions();

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text(
          'Customer support',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
        const SizedBox(height: 8),
        const Text(
          'Review customer care workflows and branch operations requests.',
        ),
        const SizedBox(height: 16),
        Card(
          child: ListTile(
            leading: const Icon(Icons.assignment_return_outlined),
            title: const Text('Refund / replacement requests'),
            subtitle: const Text(
              'Review customer requests, approve, reject, or complete them',
            ),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => context.push('/staff/refund-replacements'),
          ),
        ),
      ],
    );
  }
}

class _DairyHomeActions extends ConsumerWidget {
  const _DairyHomeActions();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final user = ref.watch(sessionControllerProvider).session?.user;
    final branchIds = user?.branchIds ?? const <int>[];
    final branchId = branchIds.isEmpty ? null : branchIds.first;
    final branchName = branchId == null ? null : user?.branchName(branchId);
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text(
          'Dairy operations',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
        const SizedBox(height: 8),
        const Text(
          'Record production and manage operational milk availability.',
        ),
        const SizedBox(height: 16),
        if (branchId == null)
          const StatePanel(
            icon: Icons.location_off_outlined,
            title: 'No branch assigned',
            message:
                'A branch assignment is required to manage dairy operations and deliveries.',
          )
        else ...[
          Card(
            child: ListTile(
              leading: const Icon(Icons.agriculture_outlined),
              title: Text(
                '${branchName ?? 'Branch $branchId'} dairy dashboard',
              ),
              subtitle: const Text(
                'Production, batches, availability, and usage',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/dairy/dashboard'),
            ),
          ),
          const SizedBox(height: 12),
          Card(
            child: ListTile(
              leading: const Icon(Icons.local_shipping_outlined),
              title: const Text('Delivery Management'),
              subtitle: const Text(
                'Manage deliveries, generate subscription deliveries, and assign deliveries to delivery staff.',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () =>
                  context.push('/delivery-management/branch/$branchId'),
            ),
          ),
          const SizedBox(height: 12),
          Card(
            child: ListTile(
              leading: const Icon(Icons.assignment_return_outlined),
              title: const Text('Refund / replacement requests'),
              subtitle: const Text(
                'Review customer requests, approve, reject, or complete them',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(
                '/staff/refund-replacements?branchId=$branchId',
              ),
            ),
          ),
        ],
      ],
    );
  }
}

class _AdminHomeActions extends ConsumerWidget {
  const _AdminHomeActions({
    required this.permissions,
    required this.branchIds,
    required this.branchDetails,
  });

  final List<String> permissions;
  final List<int> branchIds;
  final List<AuthUserBranchInfo> branchDetails;

  bool get _canReadCameras =>
      permissions.contains('CAMERAS.READ') ||
      permissions.contains('CAMERAS.MANAGE');

  bool get _canReadNumberSeries =>
      permissions.contains('SETUP.NUMBER_SERIES.READ') ||
      permissions.contains('SETUP.NUMBER_SERIES.MANAGE');

  bool get _canReadOtpProvider =>
      permissions.contains('SETUP.OTP_PROVIDER.READ') ||
      permissions.contains('SETUP.OTP_PROVIDER.MANAGE');

  bool get _canReadIntegrations =>
      permissions.contains('SETUP.INTEGRATIONS.READ') ||
      permissions.contains('SETUP.INTEGRATIONS.MANAGE');

  bool get _canReadRefundReplacementConfig =>
      permissions.contains('SETUP.REFUND_REPLACEMENT.READ') ||
      permissions.contains('SETUP.REFUND_REPLACEMENT.MANAGE');

  bool get _canManageEmployees =>
      permissions.contains('EMPLOYEES.READ') ||
      permissions.contains('EMPLOYEES.MANAGE');

  bool get _canReadBranches =>
      permissions.contains('BRANCHES.READ') ||
      permissions.contains('BRANCHES.MANAGE');

  bool get _canReadRefundReplacements =>
      permissions.contains('REFUND_REPLACEMENT.READ_BRANCH') ||
      permissions.contains('REFUND_REPLACEMENT.MANAGE_BRANCH');

  bool get _canReadReports => permissions.any(
    const {
      'REPORTS.ADMINISTRATION.READ',
      'REPORTS.FINANCIAL.READ',
      'REPORTS.OPERATIONS.READ',
      'REPORTS.MILK_TESTS.READ',
      'REPORTS.AUDIT.READ',
    }.contains,
  );

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        if (_canReadReports) ...[
          Text(
            'Administration',
            style: Theme.of(context).textTheme.headlineSmall,
          ),
          const SizedBox(height: 8),
          const Text('Review operational metrics and authorized reports.'),
          const SizedBox(height: 8),
        ],
        if (_canManageEmployees) ...[
          const _AdminSectionHeader('User & Access'),
          _AdminTileGrid(
            items: [
              _AdminTileData(
                icon: Icons.group_outlined,
                label: 'Employees',
                subtitle: 'Staff accounts & roles',
                onTap: () => context.push('/admin/employees'),
              ),
            ],
          ),
        ],
        if (_canReadBranches) ...[
          const _AdminSectionHeader('Master Data'),
          _AdminTileGrid(
            items: [
              _AdminTileData(
                icon: Icons.storefront_outlined,
                label: 'Branches',
                subtitle: 'Add, edit, activate, deactivate',
                onTap: () => context.push('/admin/branches'),
              ),
              _AdminTileData(
                icon: Icons.inventory_2_outlined,
                label: 'Catalogue',
                subtitle: 'Products & availability',
                onTap: () => context.push('/admin/catalogue'),
              ),
              _AdminTileData(
                icon: Icons.local_drink_outlined,
                label: 'Preview catalogue',
                subtitle: 'Customer view',
                onTap: () => context.push('/catalogue'),
              ),
            ],
          ),
        ],
        if (_canReadNumberSeries ||
            _canReadOtpProvider ||
            _canReadIntegrations ||
            _canReadRefundReplacementConfig) ...[
          const _AdminSectionHeader('System Setup'),
          _AdminTileGrid(
            items: [
              if (_canReadNumberSeries)
                _AdminTileData(
                  icon: Icons.numbers_outlined,
                  label: 'Number Series',
                  subtitle: 'Templates & reset policies',
                  onTap: () => context.push('/admin/setup/number-series'),
                ),
              if (_canReadOtpProvider)
                _AdminTileData(
                  icon: Icons.sms_outlined,
                  label: 'OTP Provider',
                  subtitle: 'MSG91 widget & auth key',
                  onTap: () => context.push('/admin/setup/otp-provider'),
                ),
              if (_canReadIntegrations)
                _AdminTileData(
                  icon: Icons.plumbing_outlined,
                  label: 'Integrations',
                  subtitle: 'SMTP, Razorpay & Maps keys',
                  onTap: () => context.push('/admin/setup/integrations'),
                ),
              if (_canReadRefundReplacementConfig)
                _AdminTileData(
                  icon: Icons.timer_outlined,
                  label: 'Refund / Replacement Window',
                  subtitle: 'Request window in hours',
                  onTap: () => context.push('/admin/setup/refund-replacement'),
                ),
            ],
          ),
        ],
        if (_canReadCameras ||
            _canReadReports ||
            _canReadRefundReplacements ||
            branchIds.isNotEmpty) ...[
          const _AdminSectionHeader('Monitoring & Operations'),
          _AdminTileGrid(
            items: [
              if (_canReadReports)
                _AdminTileData(
                  icon: Icons.dashboard_outlined,
                  label: 'Dashboard & Reports',
                  subtitle: 'Metrics, filters, exports',
                  onTap: () => context.push('/admin'),
                ),
              if (_canReadCameras)
                _AdminTileData(
                  icon: Icons.video_settings_outlined,
                  label: 'Cameras',
                  subtitle: 'Visibility & stream status',
                  onTap: () => context.push('/admin/cameras'),
                ),
              if (_canReadRefundReplacements)
                _AdminTileData(
                  icon: Icons.assignment_return_outlined,
                  label: 'Refund / replacement requests',
                  subtitle: branchIds.isEmpty
                      ? 'Review customer requests'
                      : 'Review requests for ${branchDisplayName(branchDetails, branchIds.first)}',
                  onTap: () => context.push(
                    branchIds.isEmpty
                        ? '/staff/refund-replacements'
                        : '/staff/refund-replacements?branchId=${branchIds.first}',
                  ),
                ),
              if (branchIds.isNotEmpty)
                _AdminTileData(
                  icon: Icons.agriculture_outlined,
                  label: 'Dairy operations',
                  subtitle:
                      '${branchDisplayName(branchDetails, branchIds.first)} activity',
                  onTap: () => context.push('/dairy/dashboard'),
                ),
              if (branchIds.isNotEmpty)
                _AdminTileData(
                  icon: Icons.local_shipping_outlined,
                  label: 'Deliveries',
                  subtitle:
                      'Assign & monitor ${branchDisplayName(branchDetails, branchIds.first)}',
                  onTap: () => context.push(
                    '/delivery-management/branch/${branchIds.first}',
                  ),
                ),
            ],
          ),
        ],
      ],
    );
  }
}

/// Compact section heading with a small accent bar.
class _AdminSectionHeader extends StatelessWidget {
  const _AdminSectionHeader(this.title);

  final String title;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 22, bottom: 12),
    child: Row(
      children: [
        Container(
          width: 4,
          height: 18,
          decoration: BoxDecoration(
            color: DoodhColors.teal,
            borderRadius: BorderRadius.circular(2),
          ),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            title,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: Theme.of(context).textTheme.titleLarge,
          ),
        ),
      ],
    ),
  );
}

/// Responsive icon-tile grid: 2 columns on phones, 3 on tablets, 4 on wide
/// screens. Tiles use the shared DoodhDirect Card theme and icon wells.
class _AdminTileGrid extends StatelessWidget {
  const _AdminTileGrid({required this.items});

  final List<_AdminTileData> items;

  @override
  Widget build(BuildContext context) {
    if (items.isEmpty) return const SizedBox.shrink();
    return LayoutBuilder(
      builder: (context, constraints) {
        final width = constraints.maxWidth;
        final columns = width >= 900 ? 4 : (width >= 600 ? 3 : 2);
        final tileHeight = width >= 600 ? 140.0 : 136.0;
        return GridView(
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
            crossAxisCount: columns,
            crossAxisSpacing: 12,
            mainAxisSpacing: 12,
            mainAxisExtent: tileHeight,
          ),
          children: [for (final item in items) _AdminTile(data: item)],
        );
      },
    );
  }
}

/// Compact tappable tile: icon well, short title, optional short subtitle.
class _AdminTile extends StatelessWidget {
  const _AdminTile({required this.data});

  final _AdminTileData data;

  @override
  Widget build(BuildContext context) => Card(
    child: InkWell(
      borderRadius: DoodhRadii.md,
      onTap: data.onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: DoodhColors.mint.withValues(alpha: .8),
                borderRadius: DoodhRadii.sm,
              ),
              child: Icon(data.icon, color: DoodhColors.tealDark, size: 22),
            ),
            const SizedBox(height: 6),
            Flexible(
              child: Text(
                data.label,
                maxLines: 2,
                textAlign: TextAlign.center,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(
                  context,
                ).textTheme.titleMedium?.copyWith(fontSize: 13, height: 1.2),
              ),
            ),
            if (data.subtitle != null) ...[
              const SizedBox(height: 2),
              Flexible(
                child: Text(
                  data.subtitle!,
                  maxLines: 1,
                  textAlign: TextAlign.center,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(
                    context,
                  ).textTheme.bodySmall?.copyWith(fontSize: 11, height: 1.2),
                ),
              ),
            ],
          ],
        ),
      ),
    ),
  );
}

class _AdminTileData {
  const _AdminTileData({
    required this.icon,
    required this.label,
    required this.onTap,
    this.subtitle,
  });

  final IconData icon;
  final String label;
  final String? subtitle;
  final VoidCallback onTap;
}

/// The browsing-only home shown to guests. It exposes only the public
/// storefront (home, catalogue, product details, cart, checkout review) and a
/// contextual sign-in prompt. No customer-specific data is fetched or shown —
/// protected quick actions simply route to the sign-in flow with a
/// return-intent, so the guest is never shown fabricated account state.
class GuestHomeScreen extends ConsumerWidget {
  const GuestHomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('DoodhDirect'),
        actions: const [_GuestSignInButton(), _CartButton()],
      ),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            const _GuestGreetingHeader(),
            const SizedBox(height: 20),
            DoodhHeroCard(onBuy: () => context.push('/catalogue')),
            const SizedBox(height: 24),
            const DoodhSectionHeader(title: 'Explore'),
            const SizedBox(height: 12),
            LayoutBuilder(
              builder: (context, constraints) {
                final columns = constraints.maxWidth >= 720 ? 4 : 2;
                return GridView.count(
                  crossAxisCount: columns,
                  shrinkWrap: true,
                  physics: const NeverScrollableScrollPhysics(),
                  mainAxisSpacing: 12,
                  crossAxisSpacing: 12,
                  childAspectRatio: constraints.maxWidth >= 720 ? 1.7 : 1.35,
                  children: [
                    _QuickAction(
                      icon: Icons.shopping_bag_outlined,
                      label: 'Browse',
                      onTap: () => context.push('/catalogue'),
                    ),
                    _QuickAction(
                      icon: Icons.shopping_cart_outlined,
                      label: 'My cart',
                      onTap: () => context.go('/checkout'),
                    ),
                    _QuickAction(
                      icon: Icons.lock_outline,
                      label: 'Orders',
                      onTap: () => context.go('/orders'),
                    ),
                    _QuickAction(
                      icon: Icons.account_balance_wallet_outlined,
                      label: 'Wallet',
                      onTap: () => context.go('/wallet'),
                    ),
                  ],
                );
              },
            ),
            const SizedBox(height: 24),
            const _GuestLoginPromptCard(),
          ],
        ),
      ),
    );
  }
}

class _GuestSignInButton extends ConsumerWidget {
  const _GuestSignInButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) => TextButton.icon(
    onPressed: () => context.go('/login'),
    icon: const Icon(Icons.login),
    label: const Text('Sign in'),
  );
}

class _GuestGreetingHeader extends StatelessWidget {
  const _GuestGreetingHeader();

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        'Welcome to DoodhDirect',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: 4),
      const Text(
        'Fresh dairy, delivered with care. Browse now — sign in when you '
        'are ready to order.',
      ),
    ],
  );
}

class _GuestLoginPromptCard extends StatelessWidget {
  const _GuestLoginPromptCard();

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Sign in for orders, payments and more',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 8),
          const Text(
            'Your cart is saved on this device and will be there after you '
            'sign in.',
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              FilledButton(
                onPressed: () => context.go('/login'),
                child: const Text('Sign in'),
              ),
            ],
          ),
        ],
      ),
    ),
  );
}
