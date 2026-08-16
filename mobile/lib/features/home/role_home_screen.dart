import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:go_router/go_router.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

class RoleHomeScreen extends ConsumerWidget {
  const RoleHomeScreen({super.key, required this.role});

  final UserRole role;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Scaffold(
    appBar: AppBar(
      title: Text('${role.label} workspace'),
      actions: [
        IconButton(
          tooltip: 'Sign out',
          onPressed: () async {
            await ref.read(sessionControllerProvider.notifier).signOut();
          },
          icon: const Icon(Icons.logout),
        ),
      ],
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
      UserRole.dairy => const StatePanel(
        icon: Icons.agriculture_outlined,
        title: 'Dairy workspace ready',
        message:
            'Dairy operations are reserved for the dairy operations phase.',
      ),
      UserRole.owner || UserRole.admin => const _AdminHomeActions(),
      UserRole.support => const StatePanel(
        icon: Icons.support_agent_outlined,
        title: 'Customer support workspace ready',
        message: 'Customer support workflows remain outside the Identity and RBAC phase.',
      ),
      UserRole.accountant => const StatePanel(
        icon: Icons.account_balance_outlined,
        title: 'Accounting workspace ready',
        message:
            'Accounting workflows remain outside the Identity and RBAC phase.',
      ),
    },
  );
}

class _CustomerHomeActions extends StatelessWidget {
  const _CustomerHomeActions();

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text('Account', style: Theme.of(context).textTheme.headlineSmall),
      const SizedBox(height: 8),
      const Text('Manage your profile and delivery locations.'),
      const SizedBox(height: 16),
      Card(
        child: ListTile(
          leading: const Icon(Icons.person_outline),
          title: const Text('Profile and addresses'),
          subtitle: const Text(
            'Update customer details and delivery locations',
          ),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => context.push('/customer/account'),
        ),
      ),
      Card(
        child: ListTile(
          leading: const Icon(Icons.account_balance_wallet_outlined),
          title: const Text('Wallet'),
          subtitle: const Text('View your balance and wallet transactions'),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => context.push('/wallet'),
        ),
      ),
      const SizedBox(height: 20),
      Card(
        child: ListTile(
          leading: const Icon(Icons.local_drink_outlined),
          title: const Text('Browse products'),
          subtitle: const Text('View active dairy products and prices'),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => context.push('/catalogue'),
        ),
      ),
      Card(
        child: ListTile(
          leading: const Icon(Icons.receipt_long_outlined),
          title: const Text('My orders'),
          subtitle: const Text(
            'View order history and manage confirmed orders',
          ),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => context.push('/orders'),
        ),
      ),
      Card(
        child: ListTile(
          leading: const Icon(Icons.local_shipping_outlined),
          title: const Text('My deliveries'),
          subtitle: const Text('Track active and completed deliveries'),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => context.push('/deliveries'),
        ),
      ),
      Card(
        child: ListTile(
          leading: const Icon(Icons.event_repeat_outlined),
          title: const Text('My subscriptions'),
          subtitle: const Text(
            'Manage prepaid plans and scheduled delivery days',
          ),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => context.push('/subscriptions'),
        ),
      ),
    ],
  );
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

class _AdminHomeActions extends ConsumerWidget {
  const _AdminHomeActions();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final branchIds =
        ref.watch(sessionControllerProvider).session?.user.branchIds ??
        const <int>[];
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
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
