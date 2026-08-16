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
      UserRole.delivery => const StatePanel(
        icon: Icons.route_outlined,
        title: 'Delivery workspace ready',
        message: 'Delivery operations are reserved for the delivery phase.',
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
    ],
  );
}

class _AdminHomeActions extends StatelessWidget {
  const _AdminHomeActions();

  @override
  Widget build(BuildContext context) => ListView(
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
          subtitle: const Text('Create, update, activate, and assign products'),
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
    ],
  );
}
