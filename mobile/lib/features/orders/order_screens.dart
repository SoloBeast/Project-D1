import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'order_controller.dart';
import 'order_models.dart';

class CheckoutScreen extends ConsumerStatefulWidget {
  const CheckoutScreen({super.key, this.initialProduct, this.initialQuantity});

  final CatalogueProduct? initialProduct;
  final double? initialQuantity;

  @override
  ConsumerState<CheckoutScreen> createState() => _CheckoutScreenState();
}

class _CheckoutScreenState extends ConsumerState<CheckoutScreen> {
  String? _addressId;
  bool _initializedCart = false;

  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      ref.read(customerControllerProvider.notifier).load();
      if (!_initializedCart &&
          widget.initialProduct != null &&
          widget.initialQuantity != null) {
        ref
            .read(orderControllerProvider.notifier)
            .setCartItem(widget.initialProduct!, widget.initialQuantity!);
        _initializedCart = true;
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final orderState = ref.watch(orderControllerProvider);
    final customerState = ref.watch(customerControllerProvider);
    final addresses = customerState.addresses
        .where((address) => address.isActive)
        .toList();
    final selectedId =
        _addressId ??
        (addresses
                .where((address) => address.isDefault)
                .firstOrNull
                ?.publicId ??
            addresses.firstOrNull?.publicId);
    final preview = orderState.preview;

    return Scaffold(
      appBar: AppBar(title: const Text('Checkout')),
      body: orderState.cart.isEmpty
          ? const EmptyStatePanel(
              title: 'Your cart is empty',
              message: 'Choose a product from the catalogue to start an order.',
            )
          : RefreshIndicator(
              onRefresh: () =>
                  ref.read(customerControllerProvider.notifier).load(),
              child: ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  Text(
                    'Delivery address',
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                  const SizedBox(height: 8),
                  if (customerState.isLoading && addresses.isEmpty)
                    const LinearProgressIndicator()
                  else if (addresses.isEmpty)
                    Card(
                      child: ListTile(
                        leading: const Icon(Icons.location_off_outlined),
                        title: const Text('Add a delivery address'),
                        subtitle: const Text(
                          'An active address is required to preview your order.',
                        ),
                        trailing: const Icon(Icons.chevron_right),
                        onTap: () => context.push('/customer/addresses/new'),
                      ),
                    )
                  else
                    DropdownButtonFormField<String>(
                      initialValue: selectedId,
                      decoration: const InputDecoration(
                        border: OutlineInputBorder(),
                      ),
                      items: addresses
                          .map(
                            (address) => DropdownMenuItem(
                              value: address.publicId,
                              child: Text('${address.label} — ${address.city}'),
                            ),
                          )
                          .toList(growable: false),
                      onChanged: (value) {
                        setState(() => _addressId = value);
                        ref
                            .read(orderControllerProvider.notifier)
                            .clearPreview();
                      },
                    ),
                  const SizedBox(height: 24),
                  Text('Items', style: Theme.of(context).textTheme.titleLarge),
                  const SizedBox(height: 8),
                  ...orderState.cart.map(
                    (item) => Card(
                      child: ListTile(
                        title: Text(item.product.name),
                        subtitle: Text(
                          '${formatQuantity(item.quantity)} ${item.product.unitLabel}',
                        ),
                        trailing: IconButton(
                          tooltip: 'Remove item',
                          onPressed: () => ref
                              .read(orderControllerProvider.notifier)
                              .removeCartItem(item.product.publicId),
                          icon: const Icon(Icons.delete_outline),
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(height: 16),
                  if (orderState.errorMessage != null)
                    Text(
                      orderState.errorMessage!,
                      style: TextStyle(
                        color: Theme.of(context).colorScheme.error,
                      ),
                    ),
                  if (preview != null) _PreviewCard(preview: preview),
                  const SizedBox(height: 16),
                  FilledButton.icon(
                    onPressed: selectedId == null || orderState.isSaving
                        ? null
                        : () async {
                            final success = await ref
                                .read(orderControllerProvider.notifier)
                                .previewFor(selectedId);
                            if (!context.mounted || !success) return;
                          },
                    icon: orderState.isSaving && preview == null
                        ? const SizedBox(
                            width: 18,
                            height: 18,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.calculate_outlined),
                    label: const Text('Preview order'),
                  ),
                  if (preview != null) ...[
                    const SizedBox(height: 8),
                    FilledButton(
                      onPressed: orderState.isSaving
                          ? null
                          : () async {
                              final order = await ref
                                  .read(orderControllerProvider.notifier)
                                  .create(preview.addressId);
                              if (context.mounted && order != null) {
                                context.go(
                                  '/orders/${order.publicId}/payment',
                                  extra: order,
                                );
                              }
                            },
                      child: Text(
                        'Place order · ₹${preview.payableAmount.toStringAsFixed(2)}',
                      ),
                    ),
                  ],
                ],
              ),
            ),
    );
  }
}

class _PreviewCard extends StatelessWidget {
  const _PreviewCard({required this.preview});
  final CheckoutPreview preview;

  @override
  Widget build(BuildContext context) => Card(
    color: Theme.of(context).colorScheme.surfaceContainerHighest,
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Server quote', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 8),
          Text('Fulfilled by ${preview.branchName} (${preview.branchCode})'),
          Text(
            '${preview.distanceKm.toStringAsFixed(1)} km from delivery address',
          ),
          const Divider(),
          ...preview.items.map(
            (item) => Padding(
              padding: const EdgeInsets.symmetric(vertical: 3),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Expanded(
                    child: Text(
                      '${item.productName} × ${formatQuantity(item.quantity)}',
                    ),
                  ),
                  Text('₹${item.lineTotal.toStringAsFixed(2)}'),
                ],
              ),
            ),
          ),
          const Divider(),
          _AmountRow(label: 'Subtotal', amount: preview.subtotal),
          _AmountRow(label: 'Discount', amount: -preview.discountAmount),
          _AmountRow(
            label: 'Payable',
            amount: preview.payableAmount,
            emphasized: true,
          ),
        ],
      ),
    ),
  );
}

class _AmountRow extends StatelessWidget {
  const _AmountRow({
    required this.label,
    required this.amount,
    this.emphasized = false,
  });
  final String label;
  final double amount;
  final bool emphasized;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 3),
    child: Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(
          label,
          style: emphasized
              ? const TextStyle(fontWeight: FontWeight.bold)
              : null,
        ),
        Text(
          '₹${amount.toStringAsFixed(2)}',
          style: emphasized
              ? const TextStyle(fontWeight: FontWeight.bold)
              : null,
        ),
      ],
    ),
  );
}

class OrderHistoryScreen extends ConsumerStatefulWidget {
  const OrderHistoryScreen({super.key});

  @override
  ConsumerState<OrderHistoryScreen> createState() => _OrderHistoryScreenState();
}

class _OrderHistoryScreenState extends ConsumerState<OrderHistoryScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(orderControllerProvider.notifier).loadOrders(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(orderControllerProvider);
    return Scaffold(
      appBar: AppBar(title: const Text('My orders')),
      body: state.isLoading && state.orders.isEmpty
          ? const LoadingStatePanel(message: 'Loading your orders...')
          : state.errorMessage != null && state.orders.isEmpty
          ? ErrorStatePanel(
              message: state.errorMessage!,
              onRetry: () =>
                  ref.read(orderControllerProvider.notifier).loadOrders(),
            )
          : state.orders.isEmpty
          ? EmptyStatePanel(
              title: 'No orders yet',
              message: 'Your confirmed one-time orders will appear here.',
              action: FilledButton(
                onPressed: () => context.push('/catalogue'),
                child: const Text('Browse products'),
              ),
            )
          : RefreshIndicator(
              onRefresh: () =>
                  ref.read(orderControllerProvider.notifier).loadOrders(),
              child: ListView.builder(
                padding: const EdgeInsets.all(12),
                itemCount: state.orders.length,
                itemBuilder: (context, index) {
                  final order = state.orders[index];
                  return Card(
                    child: ListTile(
                      title: Text(order.orderNumber),
                      subtitle: Text(
                        '${formatOrderDate(order.createdAtUtc)} · ${order.status}\n${order.itemSummary}',
                      ),
                      isThreeLine: true,
                      trailing: Text(order.formattedTotal),
                      onTap: () => context.push('/orders/${order.publicId}'),
                    ),
                  );
                },
              ),
            ),
    );
  }
}

class OrderDetailScreen extends ConsumerStatefulWidget {
  const OrderDetailScreen({super.key, required this.orderId});
  final String orderId;

  @override
  ConsumerState<OrderDetailScreen> createState() => _OrderDetailScreenState();
}

class _OrderDetailScreenState extends ConsumerState<OrderDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () =>
          ref.read(orderControllerProvider.notifier).loadOrder(widget.orderId),
    );
  }

  Future<void> _cancel(OrderSummary order) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cancel order?'),
        content: const Text(
          'This confirmed order will be cancelled and cannot be restored.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Keep order'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Cancel order'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    await ref.read(orderControllerProvider.notifier).cancel(order.publicId);
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(orderControllerProvider);
    final order = state.selectedOrder;
    return Scaffold(
      appBar: AppBar(title: const Text('Order details')),
      body: state.isLoading && order == null
          ? const LoadingStatePanel(message: 'Loading order...')
          : order == null
          ? ErrorStatePanel(
              message: state.errorMessage ?? 'Order could not be loaded.',
              onRetry: () => ref
                  .read(orderControllerProvider.notifier)
                  .loadOrder(widget.orderId),
            )
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                Text(
                  order.orderNumber,
                  style: Theme.of(context).textTheme.headlineSmall,
                ),
                Text(
                  '${formatOrderDate(order.createdAtUtc)} · ${order.status}',
                ),
                const SizedBox(height: 16),
                Card(
                  child: ListTile(
                    leading: const Icon(Icons.store_outlined),
                    title: Text(order.branchName),
                    subtitle: Text('${order.addressLabel}, ${order.city}'),
                  ),
                ),
                const SizedBox(height: 8),
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Column(
                      children: [
                        ...order.items.map(
                          (item) => _AmountRow(
                            label:
                                '${item.productName} × ${formatQuantity(item.quantity)}',
                            amount: item.lineTotal,
                          ),
                        ),
                        const Divider(),
                        _AmountRow(label: 'Subtotal', amount: order.subtotal),
                        _AmountRow(
                          label: 'Discount',
                          amount: -order.discountAmount,
                        ),
                        _AmountRow(
                          label: 'Payable',
                          amount: order.payableAmount,
                          emphasized: true,
                        ),
                      ],
                    ),
                  ),
                ),
                if (state.errorMessage != null) ...[
                  const SizedBox(height: 12),
                  Text(
                    state.errorMessage!,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ],
                if (order.status == 'PendingPayment') ...[
                  const SizedBox(height: 20),
                  FilledButton.icon(
                    onPressed: () => context.push(
                      '/orders/${order.publicId}/payment',
                      extra: order,
                    ),
                    icon: const Icon(Icons.payments_outlined),
                    label: Text('Pay ${order.formattedTotal}'),
                  ),
                ],
                if (order.canCancel) ...[
                  const SizedBox(height: 20),
                  OutlinedButton.icon(
                    onPressed: state.isSaving ? null : () => _cancel(order),
                    icon: const Icon(Icons.cancel_outlined),
                    label: const Text('Cancel order'),
                  ),
                ],
              ],
            ),
    );
  }
}

extension _IterableFirstOrNull<T> on Iterable<T> {
  T? get firstOrNull => isEmpty ? null : first;
}
