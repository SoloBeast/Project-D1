import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart'
    show formatQuantity;
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
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
                            selected:
                                paymentState.selectedMethod ==
                                capability.method,
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
    required this.selected,
    this.balance,
  });

  final PaymentMethod method;
  final String label;
  final bool selected;
  final String? balance;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final icon = switch (method) {
      PaymentMethod.wallet => Icons.account_balance_wallet_outlined,
      PaymentMethod.razorpay => Icons.payments_outlined,
      PaymentMethod.development => Icons.developer_mode_outlined,
    };
    final description = switch (method) {
      PaymentMethod.wallet =>
        balance == null
            ? 'Pay securely from your DoodhDirect balance'
            : 'Available balance: $balance',
      PaymentMethod.razorpay => 'UPI, cards, netbanking, and supported wallets',
      PaymentMethod.development => 'Complete a local Development payment',
    };
    return Semantics(
      container: true,
      selected: selected,
      label: '$label. $description${selected ? '. Selected' : ''}',
      child: Card(
        color: selected
            ? theme.colorScheme.primaryContainer
            : theme.colorScheme.surface,
        child: RadioListTile<PaymentMethod>(
          value: method,
          secondary: Icon(icon),
          title: Text(
            label,
            style: const TextStyle(fontWeight: FontWeight.w700),
          ),
          subtitle: Text(description),
        ),
      ),
    );
  }
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
      appBar: AppBar(
        title: Text(
          payment?.status.isSuccessful == true &&
              payment?.isOrderPayment == true
              ? 'Order Success'
              : 'Payment status',
        ),
      ),
      body: state.isLoading && payment == null
          ? const LoadingStatePanel(message: 'Checking payment status...')
          : payment == null
          ? ErrorStatePanel(
              message: state.errorMessage ?? 'Payment could not be loaded.',
              onRetry: () => ref
                  .read(paymentControllerProvider.notifier)
                  .load(widget.paymentId),
            )
          : payment.status.isSuccessful
          ? _OrderSuccessBody(payment: payment, errorMessage: state.errorMessage)
          : _PaymentIssueBody(
              payment: payment,
              isLoading: state.isLoading,
              errorMessage: state.errorMessage,
            ),
    );
  }
}

// ---------------------------------------------------------------------------
// Confirmed success (backend-verified payments only)
// ---------------------------------------------------------------------------

/// Reference-styled confirmation for a backend-verified successful payment.
///
/// Rendered ONLY when [PaymentDetails.status.isSuccessful] (success, refunded,
/// or partially refunded per the existing authoritative semantics). Pending
/// and terminal-failure states keep their dedicated presentations in
/// [_PaymentIssueBody] below, so a pending or failed payment can never appear
/// visually successful.
class _OrderSuccessBody extends ConsumerStatefulWidget {
  const _OrderSuccessBody({required this.payment, required this.errorMessage});

  final PaymentDetails payment;
  final String? errorMessage;

  @override
  ConsumerState<_OrderSuccessBody> createState() => _OrderSuccessBodyState();
}

class _OrderSuccessBodyState extends ConsumerState<_OrderSuccessBody> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_loadOrderSummary);
  }

  /// Loads the confirmed order snapshot through the existing order
  /// controller. No new API contract is introduced: this is the same
  /// endpoint/detail the "View order" destination uses, and after checkout the
  /// controller usually already holds this exact order, making the fetch a
  /// no-op refresh of existing state.
  Future<void> _loadOrderSummary() async {
    final orderId = widget.payment.orderId;
    if (orderId == null) return;
    await ref.read(orderControllerProvider.notifier).loadOrder(orderId);
  }

  @override
  Widget build(BuildContext context) {
    final payment = widget.payment;
    final orderState = ref.watch(orderControllerProvider);
    final order = payment.orderId != null &&
            orderState.selectedOrder?.publicId == payment.orderId
        ? orderState.selectedOrder
        : null;
    final targetRoute = payment.isSubscriptionPayment
        ? '/subscriptions/${payment.subscriptionId}'
        : '/orders/${payment.orderId}';

    return RefreshIndicator(
      onRefresh: () => ref.read(paymentControllerProvider.notifier).refresh(),
      child: LayoutBuilder(
        builder: (context, constraints) {
          final compact = constraints.maxWidth < DoodhBreakpoints.compact;
          final horizontalPadding = compact
              ? DoodhSpacing.md
              : DoodhSpacing.lg;
          // Phones get the full-width reference composition; tablets and web
          // centre the same content on a comfortable reading width instead of
          // stretching cards edge to edge.
          return ListView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: EdgeInsets.fromLTRB(
              horizontalPadding,
              DoodhSpacing.lg,
              horizontalPadding,
              DoodhSpacing.xxl,
            ),
            children: [
              Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(
                    maxWidth: DoodhContentMax.form,
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      _SuccessHero(payment: payment),
                      const SizedBox(height: DoodhSpacing.md),
                      _SuccessOrderSummaryCard(payment: payment, order: order),
                      if (payment.isOrderPayment) ...[
                        const SizedBox(height: DoodhSpacing.md),
                        if (order != null)
                          _SuccessDeliveryCard(order: order)
                        else if (orderState.errorMessage != null)
                          DoodhInfoBanner(
                            tone: DoodhTone.warning,
                            message: orderState.errorMessage!,
                            action: TextButton(
                              onPressed: _loadOrderSummary,
                              child: const Text('Retry'),
                            ),
                          )
                        else
                          const _SuccessDeliveryPlaceholder(),
                      ],
                      const SizedBox(height: DoodhSpacing.lg),
                      if (widget.errorMessage != null) ...[
                        DoodhErrorBanner(message: widget.errorMessage!),
                        const SizedBox(height: DoodhSpacing.sm),
                      ],
                      ..._buildActions(context, payment, order, targetRoute),
                    ],
                  ),
                ),
              ),
            ],
          );
        },
      ),
    );
  }

  List<Widget> _buildActions(
    BuildContext context,
    PaymentDetails payment,
    OrderSummary? order,
    String targetRoute,
  ) {
    final targetName = payment.isSubscriptionPayment ? 'subscription' : 'order';
    final targetLabel =
        'View ${targetName[0].toUpperCase()}${targetName.substring(1)}';
    final deliveryPublicId = payment.isOrderPayment
        ? order?.deliveryPublicId
        : null;
    final canTrack =
        deliveryPublicId != null && deliveryPublicId.trim().isNotEmpty;
    return [
      // Single dominant primary CTA; secondary actions stay subordinate.
      DoodhButton(
        label: targetLabel,
        icon: payment.isSubscriptionPayment
            ? Icons.event_repeat_outlined
            : Icons.receipt_long_outlined,
        expand: true,
        onPressed: payment.hasValidTarget
            ? () => context.go(targetRoute)
            : null,
      ),
      const SizedBox(height: DoodhSpacing.md),
      // Reference-style quick-action chip row: the existing secondary actions
      // as compact pill buttons, centered and wrapped to a second line on
      // narrow screens. Routes and gating are unchanged.
      Wrap(
        key: const ValueKey('order-success-quick-actions'),
        spacing: DoodhSpacing.sm,
        runSpacing: DoodhSpacing.sm,
        alignment: WrapAlignment.center,
        children: [
          if (canTrack)
            DoodhButton(
              label: 'Track delivery',
              icon: Icons.local_shipping_outlined,
              variant: DoodhButtonVariant.secondary,
              // Same existing delivery-detail route as everywhere else in the
              // app; go keeps the terminal screen off the back stack.
              onPressed: () => context.go('/deliveries/$deliveryPublicId'),
            ),
          DoodhButton(
            label: 'Continue Shopping',
            icon: Icons.storefront_outlined,
            variant: DoodhButtonVariant.secondary,
            onPressed: () => context.go('/catalogue'),
          ),
        ],
      ),
      const SizedBox(height: DoodhSpacing.xs),
      Center(
        child: TextButton(
          onPressed: () => context.go('/home'),
          child: const Text('Go to Home'),
        ),
      ),
    ];
  }
}

/// The confirmation hero: brand-teal surface, check badge, verified message,
/// and the authoritative "Payment confirmed by DoodhDirect" pill.
class _SuccessHero extends StatelessWidget {
  const _SuccessHero({required this.payment});

  final PaymentDetails payment;

  @override
  Widget build(BuildContext context) {
    const title = 'Payment successful';
    const statusLabel = 'Payment confirmed by DoodhDirect';
    // Kept verbatim from the previous authoritative wording: it states only
    // that the amount was verified for the payment's target.
    final message =
        '${payment.formattedAmount} was verified for ${payment.targetLabel}.';
    return Semantics(
      container: true,
      liveRegion: true,
      label: '$statusLabel. $title. $message',
      child: DoodhCard(
        color: DoodhColors.tealDark,
        padding: const EdgeInsets.all(DoodhSpacing.xl),
        child: Column(
          children: [
            Container(
              width: 56,
              height: 56,
              decoration: const BoxDecoration(
                color: DoodhColors.mint,
                shape: BoxShape.circle,
              ),
              child: const ExcludeSemantics(
                child: Icon(
                  Icons.check_rounded,
                  size: 34,
                  color: DoodhColors.tealDark,
                ),
              ),
            ),
            const SizedBox(height: DoodhSpacing.md),
            Text(
              title,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                color: Colors.white,
              ),
            ),
            const SizedBox(height: DoodhSpacing.sm),
            Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: Colors.white70,
              ),
            ),
            const SizedBox(height: DoodhSpacing.md),
            const DoodhStatusPill(
              label: statusLabel,
              tone: DoodhStatusTone.success,
            ),
          ],
        ),
      ),
    );
  }
}

/// Confirmed order/payment facts. Only values that already exist on
/// [PaymentDetails] / [OrderSummary] are shown; totals are never recalculated.
class _SuccessOrderSummaryCard extends StatelessWidget {
  const _SuccessOrderSummaryCard({required this.payment, required this.order});

  final PaymentDetails payment;
  final OrderSummary? order;

  @override
  Widget build(BuildContext context) {
    final order = this.order;
    final theme = Theme.of(context);
    final items = order?.items ?? const <OrderItem>[];
    return DoodhSectionCard(
      icon: Icons.receipt_long_outlined,
      title: 'Order summary',
      children: [
        DoodhKeyValueRow(
          label: 'Reference',
          value: payment.orderNumber ?? payment.publicId,
        ),
        DoodhKeyValueRow(
          label: payment.isOrderPayment ? 'Placed on' : 'Date',
          value: _formatIndiaTimestamp(
            order?.createdAt ?? payment.createdAtUtc,
          ),
        ),
        // Item lines use the already-confirmed order snapshot only; amounts
        // are the server-provided line totals (never recalculated here).
        if (items.isNotEmpty) ...[
          const SizedBox(height: DoodhSpacing.sm),
          for (final item in items)
            MergeSemantics(
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  vertical: DoodhSpacing.xs,
                ),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            item.productName,
                            style: theme.textTheme.titleSmall,
                          ),
                          Text(
                            '${formatQuantity(item.quantity)} '
                            '${item.unitOfMeasure}',
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: DoodhColors.muted,
                            ),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: DoodhSpacing.sm),
                    Text(
                      '₹${item.lineTotal.toStringAsFixed(2)}',
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          const Divider(),
          DoodhKeyValueRow(
            label: 'Subtotal',
            value: '₹${order!.subtotal.toStringAsFixed(2)}',
          ),
          if (order.discountAmount > 0)
            DoodhKeyValueRow(
              label: 'Discount',
              value: '-₹${order.discountAmount.toStringAsFixed(2)}',
            ),
        ],
        DoodhKeyValueRow(label: 'Payment method', value: payment.method.label),
        const DoodhKeyValueRow(label: 'Payment status', value: 'Verified'),
        MergeSemantics(
          child: Padding(
            padding: const EdgeInsets.only(top: DoodhSpacing.sm),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    'Payment amount',
                    style: theme.textTheme.titleMedium,
                  ),
                ),
                Text(
                  payment.formattedAmount,
                  style: theme.textTheme.titleLarge?.copyWith(
                    color: DoodhColors.tealDark,
                    fontWeight: FontWeight.w800,
                  ),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

/// Delivery snapshot from the already-confirmed order. No ETA, slot, or
/// promise is invented: only the destination, fulfilling branch, and the
/// server-provided delivery status (when meaningful) are rendered.
class _SuccessDeliveryCard extends StatelessWidget {
  const _SuccessDeliveryCard({required this.order});

  final OrderSummary order;

  @override
  Widget build(BuildContext context) {
    final deliveryStatus = order.deliveryStatus?.trim();
    final showDeliveryStatus =
        deliveryStatus != null &&
        deliveryStatus.isNotEmpty &&
        deliveryStatus.toLowerCase() != 'pendingpayment';
    return DoodhSectionCard(
      icon: Icons.local_shipping_outlined,
      title: 'Delivery',
      trailing: showDeliveryStatus
          ? DoodhStatusPill(
              label: deliveryStatus,
              tone: _deliveryStatusTone(deliveryStatus),
            )
          : null,
      children: [
        DoodhKeyValueRow(
          label: 'Deliver to',
          value: '${order.addressLabel} · ${order.city}',
        ),
        DoodhKeyValueRow(label: 'Fulfilled by', value: order.branchName),
      ],
    );
  }
}

DoodhStatusTone _deliveryStatusTone(String status) => switch (
  status.toLowerCase()
) {
  'delivered' => DoodhStatusTone.success,
  'cancelled' || 'failed' => DoodhStatusTone.error,
  'outfordelivery' || 'intransit' || 'dispatched' => DoodhStatusTone.warning,
  _ => DoodhStatusTone.neutral,
};

/// Transient row while the order summary (delivery destination) is loading.
/// Deliberately static (no indeterminate animation) so scroll/list settles.
class _SuccessDeliveryPlaceholder extends StatelessWidget {
  const _SuccessDeliveryPlaceholder();

  @override
  Widget build(BuildContext context) => DoodhCard(
    child: Row(
      children: [
        const ExcludeSemantics(
          child: Icon(Icons.local_shipping_outlined, color: DoodhColors.muted),
        ),
        const SizedBox(width: DoodhSpacing.sm),
        Expanded(
          child: Text(
            'Loading delivery details…',
            style: Theme.of(context).textTheme.bodyMedium,
          ),
        ),
      ],
    ),
  );
}

String _formatIndiaTimestamp(DateTime utc) {
  const months = [
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ];
  final local = toIndiaTime(utc);
  final hour12 = local.hour % 12 == 0 ? 12 : local.hour % 12;
  final minute = local.minute.toString().padLeft(2, '0');
  final marker = local.hour < 12 ? 'AM' : 'PM';
  return '${local.day} ${months[local.month - 1]} ${local.year}, '
      '$hour12:$minute $marker';
}

// ---------------------------------------------------------------------------
// Pending / terminal-failure payments (unchanged behaviour)
// ---------------------------------------------------------------------------

/// Presentation for payments that are NOT yet verified: pending verification
/// and terminal failures. Success never reaches this body.
class _PaymentIssueBody extends ConsumerWidget {
  const _PaymentIssueBody({
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
    final (icon, title, message) = status.isTerminalFailure
        ? (
            Icons.cancel_outlined,
            status == PaymentStatus.expired ? 'Payment expired' : 'Payment failed',
            payment.failureMessage ??
                'This payment was not completed. You can retry from the $targetName.',
          )
        : (
            Icons.hourglass_top_outlined,
            'Verification pending',
            'The $targetName remains payment pending until DoodhDirect verifies the payment.',
          );

    final tone = status.isTerminalFailure
        ? DoodhStatusTone.error
        : DoodhStatusTone.warning;
    final theme = Theme.of(context);
    final statusLabel = status.isTerminalFailure
        ? 'Payment needs attention'
        : 'Payment status is still being verified';
    return RefreshIndicator(
      onRefresh: () => ref.read(paymentControllerProvider.notifier).refresh(),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(20, 24, 20, 32),
        children: [
          Semantics(
            container: true,
            liveRegion: true,
            label: '$statusLabel. $title. $message',
            child: Card(
              color: status.isTerminalFailure
                  ? DoodhColors.errorSurface
                  : DoodhColors.warningSurface,
              child: Padding(
                padding: DoodhSpacing.cardPadding,
                child: Column(
                  children: [
                    DoodhStatusPill(label: title, tone: tone),
                    const SizedBox(height: DoodhSpacing.md),
                    Icon(
                      icon,
                      size: 64,
                      color: status.isTerminalFailure
                          ? DoodhColors.errorForeground
                          : DoodhColors.warningForeground,
                    ),
                  ],
                ),
              ),
            ),
          ),
          const SizedBox(height: 16),
          const SizedBox(height: 16),
          Text(
            title,
            style: theme.textTheme.headlineSmall,
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
          const SizedBox(height: 8),
          TextButton(
            onPressed: payment.hasValidTarget
                ? () => context.go(targetRoute)
                : null,
            child: Text('View $targetName'),
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
      padding: DoodhSpacing.cardPadding,
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
