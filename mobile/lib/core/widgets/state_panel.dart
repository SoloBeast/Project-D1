import 'package:flutter/material.dart';

class StatePanel extends StatelessWidget {
  const StatePanel({
    super.key,
    required this.icon,
    required this.title,
    required this.message,
    this.action,
  });

  final IconData icon;
  final String title;
  final String message;
  final Widget? action;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => SingleChildScrollView(
      child: ConstrainedBox(
        constraints: BoxConstraints(
          minHeight: constraints.hasBoundedHeight ? constraints.maxHeight : 0,
        ),
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 420),
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(icon, size: 48, color: Theme.of(context).colorScheme.primary),
                  const SizedBox(height: 16),
                  Text(
                    title,
                    style: Theme.of(context).textTheme.titleLarge,
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 8),
                  Text(message, textAlign: TextAlign.center),
                  if (action != null) ...[const SizedBox(height: 20), action!],
                ],
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

class LoadingStatePanel extends StatelessWidget {
  const LoadingStatePanel({super.key, this.message = 'Loading...'});

  final String message;

  @override
  Widget build(BuildContext context) => Semantics(
    label: message,
    child: const Center(child: CircularProgressIndicator()),
  );
}

class EmptyStatePanel extends StatelessWidget {
  const EmptyStatePanel({
    super.key,
    required this.title,
    required this.message,
    this.action,
  });

  final String title;
  final String message;
  final Widget? action;

  @override
  Widget build(BuildContext context) => StatePanel(
    icon: Icons.inbox_outlined,
    title: title,
    message: message,
    action: action,
  );
}

class ErrorStatePanel extends StatelessWidget {
  const ErrorStatePanel({
    super.key,
    required this.message,
    required this.onRetry,
  });

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => StatePanel(
    icon: Icons.error_outline,
    title: 'Something went wrong',
    message: message,
    action: FilledButton.icon(
      onPressed: onRetry,
      icon: const Icon(Icons.refresh),
      label: const Text('Retry'),
    ),
  );
}

class UnauthorizedStatePanel extends StatelessWidget {
  const UnauthorizedStatePanel({super.key});

  @override
  Widget build(BuildContext context) => const StatePanel(
    icon: Icons.lock_outline,
    title: 'Access denied',
    message: 'Your account does not have permission to view this content.',
  );
}

class OfflineStatePanel extends StatelessWidget {
  const OfflineStatePanel({super.key, required this.onRetry});

  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) => StatePanel(
    icon: Icons.cloud_off_outlined,
    title: 'You are offline',
    message: 'Check your connection and try again.',
    action: FilledButton.icon(
      onPressed: onRetry,
      icon: const Icon(Icons.refresh),
      label: const Text('Retry'),
    ),
  );
}
