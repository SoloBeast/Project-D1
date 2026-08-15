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
      UserRole.owner || UserRole.admin => const StatePanel(
        icon: Icons.dashboard_outlined,
        title: 'Administration workspace ready',
        message: 'Management, reports, and settings are reserved for the admin phase.',
      ),
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
      const SizedBox(height: 20),
      const StatePanel(
        icon: Icons.local_drink_outlined,
        title: 'Ordering is coming next',
        message: 'Products, orders, subscriptions, and wallet workflows arrive in their roadmap phases.',
      ),
    ],
  );
}
