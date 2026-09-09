import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'wallet_controller.dart';
import 'wallet_models.dart';

class WalletScreen extends ConsumerStatefulWidget {
  const WalletScreen({super.key});

  @override
  ConsumerState<WalletScreen> createState() => _WalletScreenState();
}

class _WalletScreenState extends ConsumerState<WalletScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(walletControllerProvider.notifier).load());
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(walletControllerProvider);
    final wallet = state.wallet;
    return CustomerShell(
      currentPath: '/wallet',
      title: 'Wallet',
      actions: [
        IconButton(
          tooltip: 'Add money',
          icon: const Icon(Icons.add_card_outlined),
          onPressed: state.isSaving ? null : () => _showTopUpDialog(context),
        ),
        IconButton(
          tooltip: 'Refresh wallet',
          onPressed: state.isLoading
              ? null
              : () => ref.read(walletControllerProvider.notifier).load(),
          icon: const Icon(Icons.refresh),
        ),
      ],
      child: state.isLoading && wallet == null
          ? const LoadingStatePanel(message: 'Loading wallet...')
          : wallet == null
          ? ErrorStatePanel(
              message: state.errorMessage ?? 'Wallet could not be loaded.',
              onRetry: () => ref.read(walletControllerProvider.notifier).load(),
            )
          : RefreshIndicator(
              onRefresh: () =>
                  ref.read(walletControllerProvider.notifier).load(),
              child: ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.all(16),
                children: [
                  _BalancePanel(wallet: wallet),
                  if (state.errorMessage != null) ...[
                    const SizedBox(height: 12),
                    Text(
                      state.errorMessage!,
                      style: TextStyle(
                        color: Theme.of(context).colorScheme.error,
                      ),
                    ),
                  ],
                  const SizedBox(height: 24),
                  Text(
                    'Transactions',
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                  const SizedBox(height: 8),
                  if (state.transactions.isEmpty)
                    const EmptyStatePanel(
                      title: 'No transactions',
                      message: 'Wallet activity will appear here.',
                    )
                  else
                    ...state.transactions.map(
                      (transaction) =>
                          _TransactionTile(transaction: transaction),
                    ),
                ],
              ),
            ),
    );
  }

  Future<void> _showTopUpDialog(BuildContext context) async {
    final controller = TextEditingController();
    final amount = await showDialog<double>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Add money to wallet'),
        content: TextField(
          controller: controller,
          autofocus: true,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: const InputDecoration(labelText: 'Amount', prefixText: '₹ '),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(
              dialogContext,
              double.tryParse(controller.text.trim()),
            ),
            child: const Text('Add money'),
          ),
        ],
      ),
    );
    controller.dispose();
    if (amount == null || amount <= 0) return;
    if (!context.mounted) return;

    // Keep the checkout in progress visible: Razorpay opens on top of the app
    // and a blocking indicator covers the moment before it appears.
    final navigator = Navigator.of(context);
    showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (_) => const Center(
        child: CircularProgressIndicator(),
      ),
    );
    final message =
        await ref.read(walletControllerProvider.notifier).topUp(amount);
    if (!context.mounted) return;
    navigator.pop(); // Dismiss the progress indicator.
    if (message == null) return;
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(message),
          behavior: SnackBarBehavior.floating,
        ),
      );
  }
}

class _BalancePanel extends StatelessWidget {
  const _BalancePanel({required this.wallet});

  final WalletDetails wallet;

  @override
  Widget build(BuildContext context) => Card(
    color: Theme.of(context).colorScheme.primaryContainer,
    child: Padding(
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Available balance',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 8),
          Text(
            wallet.formattedBalance,
            style: Theme.of(context).textTheme.headlineMedium
                ?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 4),
          Text(wallet.currency),
        ],
      ),
    ),
  );
}

class _TransactionTile extends StatelessWidget {
  const _TransactionTile({required this.transaction});

  final WalletTransaction transaction;

  @override
  Widget build(BuildContext context) {
    final color = transaction.isCredit
        ? Theme.of(context).colorScheme.primary
        : Theme.of(context).colorScheme.onSurface;
    return ListTile(
      contentPadding: EdgeInsets.zero,
      leading: CircleAvatar(
        child: Icon(transaction.isCredit ? Icons.south_west : Icons.north_east),
      ),
      title: Text(transaction.description),
      subtitle: Text(formatWalletDate(transaction.occurredAt)),
      trailing: Text(
        transaction.formattedAmount,
        style: TextStyle(color: color, fontWeight: FontWeight.w700),
      ),
    );
  }
}
