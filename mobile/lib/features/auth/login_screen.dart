import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

enum FoundationRoute { login, home }

class LoginScreen extends ConsumerWidget {
  const LoginScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => Scaffold(
        body: SafeArea(
          child: Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 440),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Icon(Icons.local_drink, size: 56, color: Theme.of(context).colorScheme.primary),
                    const SizedBox(height: 20),
                    Text('DoodhDirect', style: Theme.of(context).textTheme.headlineMedium, textAlign: TextAlign.center),
                    const SizedBox(height: 8),
                    Text('Phase 0 foundation', style: Theme.of(context).textTheme.titleMedium, textAlign: TextAlign.center),
                    const SizedBox(height: 32),
                    const Text('Authentication framework is configured in the API. Working OTP and login flows are scheduled for Phase 1.'),
                    const SizedBox(height: 24),
                    FilledButton.icon(
                      onPressed: () => _showRolePicker(context, ref),
                      icon: const Icon(Icons.developer_mode),
                      label: const Text('Open foundation session'),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      );

  Future<void> _showRolePicker(BuildContext context, WidgetRef ref) async {
    final role = await showModalBottomSheet<UserRole>(
      context: context,
      builder: (context) => SafeArea(
        child: Wrap(
          children: UserRole.values
              .map((role) => ListTile(
                    leading: const Icon(Icons.account_circle_outlined),
                    title: Text(role.label),
                    onTap: () => Navigator.of(context).pop(role),
                  ))
              .toList(),
        ),
      ),
    );
    if (role != null) ref.read(sessionControllerProvider.notifier).useFoundationSession(role);
  }
}
