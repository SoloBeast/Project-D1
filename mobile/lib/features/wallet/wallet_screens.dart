import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'wallet_controller.dart';
import 'wallet_models.dart';

const developmentWalletTopUpEnabled = kDebugMode;

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

  Future<void> _showTopUpDialog() async {
    final controller = TextEditingController(text: '500');
    final amount = await showDialog<double>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Development wallet top-up'),
        content: TextField(
          controller: controller,
          autofocus: true,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: const InputDecoration(
            labelText: 'Amount',
            prefixText: '₹',
            border: OutlineInputBorder(),
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              final value = double.tryParse(controller.text.trim());
              if (value != null && value > 0) Navigator.pop(context, value);
            },
            child: const Text('Add balance'),
          ),
        ],
      ),
    );
    controller.dispose();
    if (amount == null || !mounted) return;

    final success = await ref
        .read(walletControllerProvider.notifier)
        .topUp(amount);
    if (success && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Development balance added.')),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(walletControllerProvider);
    final wallet = state.wallet;
    return Scaffold(
      appBar: AppBar(
        title: const Text('Wallet'),
        actions: [
          IconButton(
            tooltip: 'Refresh wallet',
            onPressed: state.isLoading
                ? null
                : () => ref.read(walletControllerProvider.notifier).load(),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: state.isLoading && wallet == null
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
                  if (developmentWalletTopUpEnabled) ...[
                    const SizedBox(height: 12),
                    OutlinedButton.icon(
                      onPressed: state.isSaving ? null : _showTopUpDialog,
                      icon: state.isSaving
                          ? const SizedBox(
                              width: 18,
                              height: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.add_card_outlined),
                      label: const Text('Development top-up'),
                    ),
                  ],
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
      subtitle: Text(formatWalletDate(transaction.occurredAtUtc)),
      trailing: Text(
        transaction.formattedAmount,
        style: TextStyle(color: color, fontWeight: FontWeight.w700),
      ),
    );
  }
}
