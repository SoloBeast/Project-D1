import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
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
                padding: DoodhSpacing.pagePadding,
                children: [
                  Center(
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(
                        maxWidth: DoodhContentMax.form,
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          _BalancePanel(
                            wallet: wallet,
                            busy: state.isSaving,
                            onAddMoney: () => _showTopUpDialog(context),
                          ),
                          const SizedBox(height: DoodhSpacing.md),
                          const DoodhInfoBanner(
                            tone: DoodhTone.info,
                            message:
                                'Money appears in your available balance only after the '
                                'payment is confirmed by our servers. Recharges still '
                                'being verified are listed below and are not yet spendable.',
                          ),
                          if (state.errorMessage != null) ...[
                            const SizedBox(height: DoodhSpacing.md),
                            DoodhErrorBanner(message: state.errorMessage!),
                          ],
                          const SizedBox(height: DoodhSpacing.lg),
                          const DoodhSectionHeader(title: 'Transactions'),
                          const SizedBox(height: DoodhSpacing.sm),
                          if (state.transactions.isEmpty)
                            const EmptyStatePanel(
                              title: 'No transactions',
                              message: 'Wallet activity will appear here.',
                            )
                          else
                            for (final transaction in state.transactions)
                              _TransactionTile(transaction: transaction),
                        ],
                      ),
                    ),
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
      builder: (dialogContext) => _TopUpDialog(controller: controller),
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

/// Recharge dialog. Quick-amount chips are a pure input convenience — the
/// chosen value still goes through the existing server-confirmed top-up flow
/// (create payment → Razorpay → verify → refresh). Nothing is credited here.
class _TopUpDialog extends StatefulWidget {
  const _TopUpDialog({required this.controller});

  final TextEditingController controller;

  @override
  State<_TopUpDialog> createState() => _TopUpDialogState();
}

class _TopUpDialogState extends State<_TopUpDialog> {
  double? _pendingAmount;

  static const _quickAmounts = [100.0, 250.0, 500.0, 1000.0];

  void _selectQuickAmount(double value) {
    setState(() {
      _pendingAmount = value;
      widget.controller.text = DoodhPriceTag.format(value);
    });
  }

  @override
  Widget build(BuildContext context) {
    final hasInput = double.tryParse(widget.controller.text.trim()) != null;
    return AlertDialog(
      title: const Text('Add money to wallet'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          DoodhField(
            label: 'Amount',
            controller: widget.controller,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            prefixIcon: const Padding(
              padding: EdgeInsets.only(
                left: DoodhSpacing.md,
                right: DoodhSpacing.sm,
              ),
              child: Text('₹'),
            ),
            onChanged: (_) => setState(() => _pendingAmount = null),
          ),
          const SizedBox(height: DoodhSpacing.md),
          Wrap(
            spacing: DoodhSpacing.sm,
            runSpacing: DoodhSpacing.sm,
            children: [
              for (final quickAmount in _quickAmounts)
                ChoiceChip(
                  label: Text('₹${DoodhPriceTag.format(quickAmount)}'),
                  selected: _pendingAmount == quickAmount,
                  onSelected: (_) => _selectQuickAmount(quickAmount),
                ),
            ],
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: hasInput
              ? () => Navigator.pop(
                  context,
                  double.tryParse(widget.controller.text.trim()),
                )
              : null,
          child: const Text('Add money'),
        ),
      ],
    );
  }
}

/// Primary balance surface: current (server-authoritative) balance plus the
/// recharge entry point. Never implies money was added before the backend
/// confirms it — the credited amount is the only balance shown here.
class _BalancePanel extends StatelessWidget {
  const _BalancePanel({
    required this.wallet,
    required this.busy,
    required this.onAddMoney,
  });

  final WalletDetails wallet;
  final bool busy;
  final VoidCallback onAddMoney;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return DoodhCard(
      padding: const EdgeInsets.all(DoodhSpacing.xl),
      color: DoodhColors.tealDark,
      semanticLabel: 'Available wallet balance ${wallet.formattedBalance}',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(
                Icons.account_balance_wallet_outlined,
                color: Colors.white70,
              ),
              const SizedBox(width: DoodhSpacing.sm),
              Expanded(
                child: Text(
                  'Available balance',
                  style: theme.textTheme.titleMedium?.copyWith(
                    color: Colors.white,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.md),
          Text(
            wallet.formattedBalance,
            style: theme.textTheme.displayMedium?.copyWith(
              fontWeight: FontWeight.w800,
              color: Colors.white,
            ),
          ),
          const SizedBox(height: DoodhSpacing.xs),
          Text(
            wallet.currency,
            style: theme.textTheme.bodyMedium?.copyWith(color: Colors.white70),
          ),
          const SizedBox(height: DoodhSpacing.lg),
          FilledButton.icon(
            onPressed: busy ? null : onAddMoney,
            icon: busy
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.add_card_outlined),
            label: Text(busy ? 'Processing...' : 'Add money'),
            style: FilledButton.styleFrom(
              backgroundColor: DoodhColors.mint,
              foregroundColor: DoodhColors.tealDark,
              minimumSize: const Size(double.infinity, 48),
            ),
          ),
        ],
      ),
    );
  }
}

/// A single ledger entry. Presents credit/debit direction, amount, date/time,
/// confirmation state and the payment/order reference when the server supplies
/// one — all read-only, with no change to the wallet API contract.
class _TransactionTile extends StatelessWidget {
  const _TransactionTile({required this.transaction});

  final WalletTransaction transaction;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final credit = transaction.isCredit;
    final reference = transaction.paymentId ?? transaction.orderId;
    return Padding(
      padding: const EdgeInsets.only(bottom: DoodhSpacing.sm),
      child: DoodhCard(
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: credit
                    ? DoodhColors.successSurface
                    : DoodhColors.neutralSurface,
                shape: BoxShape.circle,
              ),
              child: Icon(
                credit ? Icons.south_west : Icons.north_east,
                size: 20,
                color: credit
                    ? DoodhColors.successForeground
                    : DoodhColors.neutralForeground,
              ),
            ),
            const SizedBox(width: DoodhSpacing.md),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    transaction.description,
                    style: theme.textTheme.bodyLarge?.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                  const SizedBox(height: DoodhSpacing.xs),
                  Text(
                    formatWalletDate(transaction.occurredAt),
                    style: theme.textTheme.bodyMedium,
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                  Wrap(
                    spacing: DoodhSpacing.sm,
                    runSpacing: DoodhSpacing.xs,
                    children: [
                      DoodhStatusPill(
                        label: credit ? 'Credit' : 'Debit',
                        tone: credit
                            ? DoodhStatusTone.success
                            : DoodhStatusTone.neutral,
                      ),
                      DoodhStatusPill(
                        label: transaction.isReconciled ? 'Confirmed' : 'Pending',
                        tone: transaction.isReconciled
                            ? DoodhStatusTone.success
                            : DoodhStatusTone.warning,
                      ),
                    ],
                  ),
                  if (reference != null) ...[
                    const SizedBox(height: DoodhSpacing.xs),
                    Text(
                      'Ref: $reference',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: DoodhColors.muted,
                      ),
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(width: DoodhSpacing.sm),
            Text(
              transaction.formattedAmount,
              style: theme.textTheme.titleMedium?.copyWith(
                color: credit ? DoodhColors.successForeground : DoodhColors.ink,
                fontWeight: FontWeight.w700,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
