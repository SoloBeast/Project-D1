import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
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
    if (_order == null) {
      Future.microtask(_loadOrder);
    }
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
              padding: const EdgeInsets.all(16),
              children: [
                Text(
                  order.orderNumber,
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 4),
                Text('Amount due: ${order.formattedTotal}'),
                const SizedBox(height: 24),
                Text(
                  'Payment method',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 8),
                RadioGroup<PaymentMethod>(
                  groupValue: paymentState.selectedMethod,
                  onChanged: (value) {
                    if (!paymentState.isLoading && value != null) {
                      ref
                          .read(paymentControllerProvider.notifier)
                          .selectMethod(value);
                    }
                  },
                  child: const Column(
                    children: [
                      _PaymentMethodTile(
                        method: PaymentMethod.wallet,
                        icon: Icons.account_balance_wallet_outlined,
                        subtitle: 'Pay securely from your DoodhDirect balance',
                      ),
                      _PaymentMethodTile(
                        method: PaymentMethod.razorpay,
                        icon: Icons.payments_outlined,
                        subtitle:
                            'UPI, cards, netbanking, and supported wallets',
                      ),
                    ],
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
                          if (payment.method == PaymentMethod.razorpay &&
                              !payment.usesMockGateway &&
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
    required this.icon,
    required this.subtitle,
  });

  final PaymentMethod method;
  final IconData icon;
  final String subtitle;

  @override
  Widget build(BuildContext context) => Card(
    child: RadioListTile<PaymentMethod>(
      value: method,
      secondary: Icon(icon),
      title: Text(method.label),
      subtitle: Text(subtitle),
    ),
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
    final (icon, title, message) = status.isSuccessful
        ? (
            Icons.check_circle_outline,
            'Payment successful',
            '${payment.formattedAmount} was verified for order ${payment.orderNumber}.',
          )
        : status.isTerminalFailure
        ? (
            Icons.cancel_outlined,
            status == PaymentStatus.expired
                ? 'Payment expired'
                : 'Payment failed',
            payment.failureMessage ??
                'This payment was not completed. You can retry from the order.',
          )
        : (
            Icons.hourglass_top_outlined,
            'Verification pending',
            'The order remains unpaid until DoodhDirect verifies the payment.',
          );

    return RefreshIndicator(
      onRefresh: () => ref.read(paymentControllerProvider.notifier).refresh(),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(24),
        children: [
          Icon(icon, size: 64, color: Theme.of(context).colorScheme.primary),
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
            if (payment.usesMockGateway)
              OutlinedButton.icon(
                onPressed: isLoading
                    ? null
                    : () => ref
                          .read(paymentControllerProvider.notifier)
                          .verifyDevelopmentMock(),
                icon: const Icon(Icons.developer_mode_outlined),
                label: const Text('Complete development payment'),
              ),
          ],
          if (status.isTerminalFailure) ...[
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: () => context.go('/orders/${payment.orderId}'),
              icon: const Icon(Icons.replay),
              label: const Text('Return to order'),
            ),
          ],
          const SizedBox(height: 8),
          TextButton(
            onPressed: () => context.go('/orders/${payment.orderId}'),
            child: const Text('View order'),
          ),
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
          _SummaryRow(label: 'Order', value: payment.orderNumber),
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
