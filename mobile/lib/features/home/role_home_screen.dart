import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:go_router/go_router.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

class RoleHomeScreen extends ConsumerWidget {
  const RoleHomeScreen({super.key, required this.role});

  final UserRole role;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (role == UserRole.customer) return const _CustomerHomeActions();
    return Scaffold(
      appBar: AppBar(
        title: Text('${role.label} workspace'),
        actions: [const _NotificationButton(), const _SignOutButton()],
      ),
      body: switch (role) {
        UserRole.customer => const _CustomerHomeActions(),
        UserRole.delivery => _DeliveryHomeActions(
          roles:
              ref.watch(sessionControllerProvider).session?.user.roles ??
              const [],
          permissions:
              ref.watch(sessionControllerProvider).session?.user.permissions ??
              const [],
          branchIds:
              ref.watch(sessionControllerProvider).session?.user.branchIds ??
              const [],
        ),
        UserRole.dairy => const _DairyHomeActions(),
        UserRole.owner || UserRole.admin => _AdminHomeActions(
          permissions:
              ref.watch(sessionControllerProvider).session?.user.permissions ??
              const [],
          branchIds:
              ref.watch(sessionControllerProvider).session?.user.branchIds ??
              const [],
        ),
        UserRole.support => const StatePanel(
          icon: Icons.support_agent_outlined,
          title: 'Customer support workspace ready',
          message: 'Customer support workflows remain outside the Identity and RBAC phase.',
        ),
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
  });

  final List<String> roles;
  final List<String> permissions;
  final List<int> branchIds;

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
              title: Text('Branch $branchId deliveries'),
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

class _DairyHomeActions extends ConsumerWidget {
  const _DairyHomeActions();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final branchIds =
        ref.watch(sessionControllerProvider).session?.user.branchIds ??
        const <int>[];
    final branchId = branchIds.isEmpty ? null : branchIds.first;
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
              title: Text('Branch $branchId dairy dashboard'),
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
        ],
      ],
    );
  }
}

class _AdminHomeActions extends ConsumerWidget {
  const _AdminHomeActions({required this.permissions, required this.branchIds});

  final List<String> permissions;
  final List<int> branchIds;

  bool get _canReadCameras =>
      permissions.contains('CAMERAS.READ') ||
      permissions.contains('CAMERAS.MANAGE');

  bool get _canReadNumberSeries =>
      permissions.contains('SETUP.NUMBER_SERIES.READ') ||
      permissions.contains('SETUP.NUMBER_SERIES.MANAGE');

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
          const SizedBox(height: 16),
          Card(
            child: ListTile(
              leading: const Icon(Icons.dashboard_outlined),
              title: const Text('Dashboard and reports'),
              subtitle: const Text(
                'Open administration metrics, filters, and exports',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/admin'),
            ),
          ),
          const SizedBox(height: 20),
        ],
        Text('Catalogue', style: Theme.of(context).textTheme.headlineSmall),
        const SizedBox(height: 8),
        const Text('Maintain products, categories, and branch availability.'),
        const SizedBox(height: 16),
        Card(
          child: ListTile(
            leading: const Icon(Icons.inventory_2_outlined),
            title: const Text('Manage catalogue'),
            subtitle: const Text(
              'Create, update, activate, and assign products',
            ),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => context.push('/admin/catalogue'),
          ),
        ),
        Card(
          child: ListTile(
            leading: const Icon(Icons.local_drink_outlined),
            title: const Text('Preview customer catalogue'),
            subtitle: const Text('View the active public product list'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => context.push('/catalogue'),
          ),
        ),
        if (_canReadCameras)
          Card(
            child: ListTile(
              leading: const Icon(Icons.video_settings_outlined),
              title: const Text('Manage cameras'),
              subtitle: const Text(
                'Review visibility, status, ordering, and stream metadata',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/admin/cameras'),
            ),
          ),
        if (_canReadNumberSeries)
          Card(
            child: ListTile(
              leading: const Icon(Icons.numbers_outlined),
              title: const Text('Number series'),
              subtitle: const Text(
                'Configure document number templates and reset policies',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/admin/setup/number-series'),
            ),
          ),
        if (branchIds.isNotEmpty)
          Card(
            child: ListTile(
              leading: const Icon(Icons.agriculture_outlined),
              title: const Text('Manage dairy operations'),
              subtitle: Text(
                'Record and review branch ${branchIds.first} dairy activity',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/dairy/dashboard'),
            ),
          ),
        if (branchIds.isNotEmpty)
          Card(
            child: ListTile(
              leading: const Icon(Icons.local_shipping_outlined),
              title: const Text('Manage deliveries'),
              subtitle: Text('Assign and monitor branch ${branchIds.first}'),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push(
                '/delivery-management/branch/${branchIds.first}',
              ),
            ),
          ),
      ],
    );
  }
}
