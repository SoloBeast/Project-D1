import 'package:doodh_direct_mobile/core/config/app_config.dart';
import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'payment_controller.dart';
import 'payment_models.dart';

class PaymentMethodScreen extends ConsumerStatefulWidget {
  const PaymentMethodScreen({
    super.key,
    required this.orderId,
    this.initialOrder,
  });

  final String orderId;
  final OrderSummary? initialOrder;

  @override
  ConsumerState<PaymentMethodScreen> createState() =>
      _PaymentMethodScreenState();
}

class _PaymentMethodScreenState extends ConsumerState<PaymentMethodScreen> {
  OrderSummary? _order;

  @override
  void initState() {
    super.initState();
    _order = widget.initialOrder;
    Future.microtask(() {
      ref.read(paymentControllerProvider.notifier).loadCapabilities();
      ref.read(walletControllerProvider.notifier).load();
      if (_order == null) _loadOrder();
    });
  }

  Future<void> _loadOrder() async {
    await ref.read(orderControllerProvider.notifier).loadOrder(widget.orderId);
    if (!mounted) return;
    final loaded = ref.read(orderControllerProvider).selectedOrder;
    if (loaded?.publicId == widget.orderId) {
      setState(() => _order = loaded);
    }
  }

  @override
  Widget build(BuildContext context) {
    final paymentState = ref.watch(paymentControllerProvider);
    final orderState = ref.watch(orderControllerProvider);
    final walletState = ref.watch(walletControllerProvider);
    final order = _order;
    return Scaffold(
      appBar: AppBar(title: const Text('Payment')),
      body: order == null
          ? orderState.isLoading
                ? const LoadingStatePanel(message: 'Loading order...')
                : ErrorStatePanel(
                    message:
                        orderState.errorMessage ?? 'Order could not be loaded.',
                    onRetry: _loadOrder,
                  )
          : ListView(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
              children: [
                Card(
                  color: DoodhColors.tealDark,
                  child: Padding(
                    padding: const EdgeInsets.all(18),
                    child: Row(
                      children: [
                        const Icon(
                          Icons.lock_outline,
                          color: Colors.white,
                          size: 28,
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'Ready to pay',
                                style: Theme.of(context).textTheme.titleMedium
                                    ?.copyWith(color: Colors.white),
                              ),
                              const SizedBox(height: 4),
                              Text(
                                order.orderNumber,
                                style: const TextStyle(color: Colors.white70),
                              ),
                              const SizedBox(height: 2),
                              Text(
                                'Amount due: ${order.formattedTotal}',
                                style: const TextStyle(color: Colors.white70),
                              ),
                            ],
                          ),
                        ),
                        Text(
                          order.formattedTotal,
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: 20,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 20),
                DoodhSectionHeader(title: 'Choose how to pay'),
                const SizedBox(height: 8),
                if (paymentState.capabilities.isEmpty &&
                    paymentState.errorMessage == null)
                  const LinearProgressIndicator(),
                if (paymentState.selectedMethod == PaymentMethod.wallet &&
                    walletState.wallet != null)
                  Card(
                    color: DoodhColors.mint,
                    child: ListTile(
                      leading: const Icon(
                        Icons.account_balance_wallet_outlined,
                        color: DoodhColors.tealDark,
                      ),
                      title: const Text('DoodhDirect Wallet balance'),
                      trailing: Text(
                        walletState.wallet!.formattedBalance,
                        style: const TextStyle(
                          fontWeight: FontWeight.w800,
                          color: DoodhColors.tealDark,
                        ),
                      ),
                    ),
                  ),
                RadioGroup<PaymentMethod>(
                  groupValue: paymentState.selectedMethod,
                  onChanged: (value) {
                    if (!paymentState.isLoading && value != null) {
                      ref
                          .read(paymentControllerProvider.notifier)
                          .selectMethod(value);
                    }
                  },
                  child: Column(
                    children: paymentState.capabilities
                        .where((capability) => capability.isAvailable)
                        .map(
                          (capability) => _PaymentMethodTile(
                            method: capability.method,
                            label: capability.label,
                            balance: capability.method == PaymentMethod.wallet
                                ? walletState.wallet?.formattedBalance
                                : null,
                          ),
                        )
                        .toList(growable: false),
                  ),
                ),
                if (paymentState.errorMessage != null) ...[
                  const SizedBox(height: 16),
                  Text(
                    paymentState.errorMessage!,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ],
                const SizedBox(height: 24),
                FilledButton.icon(
                  onPressed: paymentState.isLoading
                      ? null
                      : () async {
                          final created = await ref
                              .read(paymentControllerProvider.notifier)
                              .createForOrder(order.publicId);
                          if (!context.mounted || !created) return;

                          final payment = ref
                              .read(paymentControllerProvider)
                              .payment;
                          if (payment == null) return;
                          if (payment.usesRazorpay &&
                              payment.status.isPending) {
                            await ref
                                .read(paymentControllerProvider.notifier)
                                .openRazorpayAndVerify();
                          }
                          if (context.mounted) {
                            context.go('/payments/${payment.publicId}/result');
                          }
                        },
                  icon: paymentState.isLoading
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.lock_outline),
                  label: Text(
                    paymentState.isLoading
                        ? 'Processing...'
                        : 'Pay ${order.formattedTotal}',
                  ),
                ),
              ],
            ),
    );
  }
}

class _PaymentMethodTile extends StatelessWidget {
  const _PaymentMethodTile({
    required this.method,
    required this.label,
    this.balance,
  });

  final PaymentMethod method;
  final String label;
  final String? balance;

  @override
  Widget build(BuildContext context) => RadioListTile<PaymentMethod>(
    value: method,
    secondary: Icon(switch (method) {
      PaymentMethod.wallet => Icons.account_balance_wallet_outlined,
      PaymentMethod.razorpay => Icons.payments_outlined,
      PaymentMethod.development => Icons.developer_mode_outlined,
    }),
    title: Text(label),
    subtitle: Text(switch (method) {
      PaymentMethod.wallet =>
        balance == null
            ? 'Pay securely from your DoodhDirect balance'
            : 'Available balance: $balance',
      PaymentMethod.razorpay => 'UPI, cards, netbanking, and supported wallets',
      PaymentMethod.development => 'Complete a local Development payment',
    }),
  );
}

class PaymentResultScreen extends ConsumerStatefulWidget {
  const PaymentResultScreen({super.key, required this.paymentId});

  final String paymentId;

  @override
  ConsumerState<PaymentResultScreen> createState() =>
      _PaymentResultScreenState();
}

class _PaymentResultScreenState extends ConsumerState<PaymentResultScreen> {
  bool _cartClearedForSuccess = false;

  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      final current = ref.read(paymentControllerProvider).payment;
      if (current?.publicId != widget.paymentId) {
        ref.read(paymentControllerProvider.notifier).load(widget.paymentId);
      } else {
        ref.read(paymentControllerProvider.notifier).refresh();
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(paymentControllerProvider);
    final payment = state.payment?.publicId == widget.paymentId
        ? state.payment
        : null;
    if (payment?.status.isSuccessful == true && !_cartClearedForSuccess) {
      _cartClearedForSuccess = true;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted) return;
        ref
            .read(orderControllerProvider.notifier)
            .clearCartAfterSuccessfulPayment();
      });
    }
    return Scaffold(
      appBar: AppBar(title: const Text('Payment status')),
      body: state.isLoading && payment == null
          ? const LoadingStatePanel(message: 'Checking payment status...')
          : payment == null
          ? ErrorStatePanel(
              message: state.errorMessage ?? 'Payment could not be loaded.',
              onRetry: () => ref
                  .read(paymentControllerProvider.notifier)
                  .load(widget.paymentId),
            )
          : _PaymentResultBody(
              payment: payment,
              isLoading: state.isLoading,
              errorMessage: state.errorMessage,
            ),
    );
  }
}

class _PaymentResultBody extends ConsumerWidget {
  const _PaymentResultBody({
    required this.payment,
    required this.isLoading,
    required this.errorMessage,
  });

  final PaymentDetails payment;
  final bool isLoading;
  final String? errorMessage;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = payment.status;
    final targetRoute = payment.isSubscriptionPayment
        ? '/subscriptions/${payment.subscriptionId}'
        : '/orders/${payment.orderId}';
    final targetName = payment.isSubscriptionPayment ? 'subscription' : 'order';
    final (icon, title, message) = status.isSuccessful
        ? (
            Icons.check_circle_outline,
            'Payment successful',
            '${payment.formattedAmount} was verified for ${payment.targetLabel}.',
          )
        : status.isTerminalFailure
        ? (
            Icons.cancel_outlined,
            status == PaymentStatus.expired
                ? 'Payment expired'
                : 'Payment failed',
            payment.failureMessage ??
                'This payment was not completed. You can retry from the $targetName.',
          )
        : (
            Icons.hourglass_top_outlined,
            'Verification pending',
            'The $targetName remains payment pending until DoodhDirect verifies the payment.',
          );

    final tone = status.isSuccessful
        ? DoodhStatusTone.success
        : status.isTerminalFailure
        ? DoodhStatusTone.error
        : DoodhStatusTone.warning;
    return RefreshIndicator(
      onRefresh: () => ref.read(paymentControllerProvider.notifier).refresh(),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(20, 24, 20, 32),
        children: [
          Center(
            child: DoodhStatusPill(label: title, tone: tone),
          ),
          const SizedBox(height: 18),
          Center(
            child: Icon(
              icon,
              size: 64,
              color: Theme.of(context).colorScheme.primary,
            ),
          ),
          const SizedBox(height: 16),
          Text(
            title,
            style: Theme.of(context).textTheme.headlineSmall,
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 8),
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 24),
          _PaymentSummary(payment: payment),
          if (errorMessage != null) ...[
            const SizedBox(height: 16),
            Text(
              errorMessage!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
              textAlign: TextAlign.center,
            ),
          ],
          if (status.isPending) ...[
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: isLoading
                  ? null
                  : () =>
                        ref.read(paymentControllerProvider.notifier).refresh(),
              icon: const Icon(Icons.refresh),
              label: const Text('Check status'),
            ),
            if (payment.usesRazorpay)
              OutlinedButton.icon(
                onPressed: isLoading
                    ? null
                    : () => ref
                          .read(paymentControllerProvider.notifier)
                          .openRazorpayAndVerify(),
                icon: const Icon(Icons.open_in_new),
                label: const Text('Continue Razorpay payment'),
              ),
            if (devToolsEnabled && payment.usesDevelopmentMock)
              OutlinedButton.icon(
                onPressed: isLoading
                    ? null
                    : () => ref
                          .read(paymentControllerProvider.notifier)
                          .completeDevelopment(),
                icon: const Icon(Icons.developer_mode_outlined),
                label: const Text('Complete development payment'),
              ),
          ],
          if (status.isTerminalFailure) ...[
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: payment.hasValidTarget
                  ? () => context.go(targetRoute)
                  : null,
              icon: const Icon(Icons.replay),
              label: Text('Return to $targetName'),
            ),
          ],
          if (status.isSuccessful) ...[
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: payment.hasValidTarget
                  ? () => context.go(targetRoute)
                  : null,
              icon: Icon(
                payment.isSubscriptionPayment
                    ? Icons.event_repeat_outlined
                    : Icons.receipt_long_outlined,
              ),
              label: Text(
                'View ${targetName[0].toUpperCase()}${targetName.substring(1)}',
              ),
            ),
            const SizedBox(height: 8),
            OutlinedButton.icon(
              onPressed: () => context.go('/home'),
              icon: const Icon(Icons.home_outlined),
              label: const Text('Go to Home'),
            ),
          ] else ...[
            const SizedBox(height: 8),
            TextButton(
              onPressed: payment.hasValidTarget
                  ? () => context.go(targetRoute)
                  : null,
              child: Text('View $targetName'),
            ),
          ],
        ],
      ),
    );
  }
}

class _PaymentSummary extends StatelessWidget {
  const _PaymentSummary({required this.payment});

  final PaymentDetails payment;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        children: [
          _SummaryRow(
            label: payment.isSubscriptionPayment ? 'Subscription' : 'Order',
            value: payment.isSubscriptionPayment
                ? payment.subscriptionId ?? 'Unavailable'
                : payment.orderNumber ?? 'Unavailable',
          ),
          _SummaryRow(label: 'Method', value: payment.method.label),
          _SummaryRow(label: 'Amount', value: payment.formattedAmount),
          _SummaryRow(label: 'Status', value: payment.status.name),
        ],
      ),
    ),
  );
}

class _SummaryRow extends StatelessWidget {
  const _SummaryRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Row(
      children: [
        Expanded(child: Text(label)),
        Flexible(
          child: Text(
            value,
            textAlign: TextAlign.end,
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        ),
      ],
    ),
  );
}
