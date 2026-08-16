import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'subscription_controller.dart';
import 'subscription_models.dart';

class SubscriptionSetupScreen extends ConsumerStatefulWidget {
  const SubscriptionSetupScreen({super.key});

  @override
  ConsumerState<SubscriptionSetupScreen> createState() =>
      _SubscriptionSetupScreenState();
}

class _SubscriptionSetupScreenState
    extends ConsumerState<SubscriptionSetupScreen> {
  CatalogueProduct? _product;
  CustomerAddress? _address;
  double _quantity = 1;
  int _entitlement = 30;
  DateTime _startDate = _dateOnly(DateTime.now());
  final Set<DeliveryWeekday> _days = {
    DeliveryWeekday.monday,
    DeliveryWeekday.wednesday,
    DeliveryWeekday.friday,
  };
  PaymentMethod _paymentMethod = PaymentMethod.wallet;

  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      ref.read(catalogueControllerProvider.notifier).load();
      ref.read(customerControllerProvider.notifier).load();
    });
  }

  @override
  Widget build(BuildContext context) {
    final catalogue = ref.watch(catalogueControllerProvider);
    final customer = ref.watch(customerControllerProvider);
    final subscription = ref.watch(subscriptionControllerProvider);
    final products = catalogue.products
        .where(
          (item) =>
              item.isActive &&
              item.branchAvailability.any((branch) => branch.isAvailable),
        )
        .toList(growable: false);
    final addresses = customer.addresses
        .where((item) => item.isActive)
        .toList(growable: false);
    _product ??= products.isEmpty ? null : products.first;
    _address ??= addresses.isEmpty
        ? null
        : addresses.firstWhere(
            (item) => item.isDefault,
            orElse: () => addresses.first,
          );
    return Scaffold(
      appBar: AppBar(title: const Text('New subscription')),
      body:
          (catalogue.isLoading || customer.isLoading) &&
              (products.isEmpty || addresses.isEmpty)
          ? const LoadingStatePanel(message: 'Loading subscription options...')
          : products.isEmpty || addresses.isEmpty
          ? ErrorStatePanel(
              message: products.isEmpty
                  ? 'No active products are available for subscription.'
                  : 'Add an active delivery address before subscribing.',
              onRetry: () {
                ref.read(catalogueControllerProvider.notifier).load();
                ref.read(customerControllerProvider.notifier).load();
              },
            )
          : _form(context, products, addresses, subscription),
    );
  }

  Widget _form(
    BuildContext context,
    List<CatalogueProduct> products,
    List<CustomerAddress> addresses,
    SubscriptionState state,
  ) {
    final product = _product!;
    final estimate = product.price * _quantity * _entitlement;
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        DropdownButtonFormField<CatalogueProduct>(
          initialValue: product,
          decoration: const InputDecoration(labelText: 'Product'),
          items: products
              .map(
                (item) => DropdownMenuItem(value: item, child: Text(item.name)),
              )
              .toList(),
          onChanged: (value) => setState(() => _product = value),
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<CustomerAddress>(
          initialValue: _address,
          decoration: const InputDecoration(labelText: 'Delivery address'),
          items: addresses
              .map(
                (item) => DropdownMenuItem(
                  value: item,
                  child: Text('${item.label} - ${item.city}'),
                ),
              )
              .toList(),
          onChanged: (value) => setState(() => _address = value),
        ),
        const SizedBox(height: 12),
        TextFormField(
          initialValue: _quantity.toString(),
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: InputDecoration(
            labelText: 'Quantity per delivery (${product.unitLabel})',
          ),
          onChanged: (value) => _quantity = double.tryParse(value) ?? 0,
        ),
        const SizedBox(height: 12),
        TextFormField(
          initialValue: _entitlement.toString(),
          keyboardType: TextInputType.number,
          decoration: const InputDecoration(labelText: 'Total deliveries'),
          onChanged: (value) => _entitlement = int.tryParse(value) ?? 0,
        ),
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Start date'),
          subtitle: Text(_formatDate(_startDate)),
          trailing: const Icon(Icons.calendar_today_outlined),
          onTap: _pickStartDate,
        ),
        Text('Delivery days', style: Theme.of(context).textTheme.titleMedium),
        Wrap(
          spacing: 4,
          children: DeliveryWeekday.values
              .map(
                (day) => FilterChip(
                  label: Text(day.shortLabel),
                  selected: _days.contains(day),
                  onSelected: (selected) => setState(
                    () => selected ? _days.add(day) : _days.remove(day),
                  ),
                ),
              )
              .toList(),
        ),
        RadioGroup<PaymentMethod>(
          groupValue: _paymentMethod,
          onChanged: (value) => setState(() => _paymentMethod = value!),
          child: const Column(
            children: [
              RadioListTile(
                value: PaymentMethod.wallet,
                title: Text('DoodhDirect Wallet'),
              ),
              RadioListTile(
                value: PaymentMethod.razorpay,
                title: Text('Razorpay'),
              ),
            ],
          ),
        ),
        Card(
          child: ListTile(
            leading: const Icon(Icons.receipt_long_outlined),
            title: const Text('Prepaid estimate'),
            subtitle: Text(
              '${formatQuantity(_quantity)} x $_entitlement deliveries',
            ),
            trailing: Text('Rs ${estimate.toStringAsFixed(2)}'),
          ),
        ),
        if (state.errorMessage != null)
          Padding(
            padding: const EdgeInsets.only(top: 12),
            child: Text(
              state.errorMessage!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        const SizedBox(height: 16),
        FilledButton.icon(
          onPressed: state.isSaving ? null : _create,
          icon: state.isSaving
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.lock_outline),
          label: Text(state.isSaving ? 'Processing...' : 'Continue to payment'),
        ),
      ],
    );
  }

  Future<void> _pickStartDate() async {
    final selected = await showDatePicker(
      context: context,
      initialDate: _startDate,
      firstDate: _dateOnly(DateTime.now()),
      lastDate: DateTime.now().add(const Duration(days: 366)),
    );
    if (selected != null && mounted) {
      setState(() => _startDate = _dateOnly(selected));
    }
  }

  Future<void> _create() async {
    final scaledQuantity = _quantity * 1000;
    final quantityHasTooManyDecimals =
        (scaledQuantity - scaledQuantity.round()).abs() > 0.000001;
    if (_product == null ||
        _address == null ||
        _quantity <= 0 ||
        quantityHasTooManyDecimals ||
        _entitlement < 1 ||
        _entitlement > 366 ||
        _days.isEmpty ||
        _startDate.isBefore(_dateOnly(DateTime.now()))) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'Check quantity, start date, delivery count, and selected delivery days.',
          ),
        ),
      );
      return;
    }
    final created = await ref
        .read(subscriptionControllerProvider.notifier)
        .create(
          CreateSubscriptionRequest(
            productId: _product!.publicId,
            addressId: _address!.publicId,
            quantity: _quantity,
            startDate: _startDate,
            deliveryDays: _days,
            totalEntitlement: _entitlement,
            paymentMethod: _paymentMethod,
          ),
        );
    if (!mounted || created == null) return;
    final payment = created.payment;
    if (payment.method == PaymentMethod.razorpay &&
        !payment.usesMockGateway &&
        payment.status.isPending) {
      await ref
          .read(paymentControllerProvider.notifier)
          .openRazorpayAndVerify();
    }
    if (mounted) context.go('/payments/${payment.publicId}/result');
  }
}

class SubscriptionListScreen extends ConsumerStatefulWidget {
  const SubscriptionListScreen({super.key});

  @override
  ConsumerState<SubscriptionListScreen> createState() =>
      _SubscriptionListScreenState();
}

class _SubscriptionListScreenState
    extends ConsumerState<SubscriptionListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () =>
          ref.read(subscriptionControllerProvider.notifier).loadSubscriptions(),
    );
  }

  Future<void> _refresh() =>
      ref.read(subscriptionControllerProvider.notifier).loadSubscriptions();

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(subscriptionControllerProvider);
    final body = state.isLoading && state.subscriptions.isEmpty
        ? const LoadingStatePanel(message: 'Loading subscriptions...')
        : state.isOffline && state.subscriptions.isEmpty
        ? OfflineStatePanel(onRetry: _refresh)
        : state.errorMessage != null && state.subscriptions.isEmpty
        ? ErrorStatePanel(message: state.errorMessage!, onRetry: _refresh)
        : state.subscriptions.isEmpty
        ? EmptyStatePanel(
            title: 'No subscriptions yet',
            message: 'Set up a prepaid delivery plan for your home.',
            action: FilledButton.icon(
              onPressed: () => context.push('/subscriptions/new'),
              icon: const Icon(Icons.add),
              label: const Text('Create subscription'),
            ),
          )
        : RefreshIndicator(
            onRefresh: _refresh,
            child: ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.all(16),
              itemCount: state.subscriptions.length,
              separatorBuilder: (_, index) => const SizedBox(height: 10),
              itemBuilder: (context, index) {
                final subscription = state.subscriptions[index];
                return Card(
                  child: ListTile(
                    leading: CircleAvatar(
                      child: Icon(_statusIcon(subscription.status)),
                    ),
                    title: Text(subscription.productName),
                    subtitle: Text(
                      '${subscription.formattedQuantity}\n'
                      '${subscription.scheduleLabel} | '
                      '${subscription.remainingEntitlement} deliveries remaining',
                    ),
                    isThreeLine: true,
                    trailing: const Icon(Icons.chevron_right),
                    onTap: () =>
                        context.push('/subscriptions/${subscription.publicId}'),
                  ),
                );
              },
            ),
          );
    return Scaffold(
      appBar: AppBar(
        title: const Text('Subscriptions'),
        actions: [
          IconButton(
            tooltip: 'Refresh subscriptions',
            onPressed: state.isLoading ? null : _refresh,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      floatingActionButton: state.subscriptions.isEmpty
          ? null
          : FloatingActionButton(
              tooltip: 'Create subscription',
              onPressed: () => context.push('/subscriptions/new'),
              child: const Icon(Icons.add),
            ),
      body: body,
    );
  }

  IconData _statusIcon(SubscriptionStatus status) => switch (status) {
    SubscriptionStatus.active => Icons.play_circle_outline,
    SubscriptionStatus.paused => Icons.pause_circle_outline,
    SubscriptionStatus.completed => Icons.check_circle_outline,
    SubscriptionStatus.cancelled => Icons.cancel_outlined,
    SubscriptionStatus.paymentPending => Icons.pending_outlined,
    SubscriptionStatus.paymentFailed => Icons.error_outline,
    SubscriptionStatus.unknown => Icons.help_outline,
  };
}

class SubscriptionDetailScreen extends ConsumerStatefulWidget {
  const SubscriptionDetailScreen({required this.subscriptionId, super.key});

  final String subscriptionId;

  @override
  ConsumerState<SubscriptionDetailScreen> createState() =>
      _SubscriptionDetailScreenState();
}

class _SubscriptionDetailScreenState
    extends ConsumerState<SubscriptionDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(subscriptionControllerProvider.notifier)
          .loadSubscription(widget.subscriptionId),
    );
  }

  Future<void> _reload() => ref
      .read(subscriptionControllerProvider.notifier)
      .loadSubscription(widget.subscriptionId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(subscriptionControllerProvider);
    final subscription =
        state.selectedSubscription?.publicId == widget.subscriptionId
        ? state.selectedSubscription
        : state.subscriptions
              .where((item) => item.publicId == widget.subscriptionId)
              .firstOrNull;
    final body = state.isLoading && subscription == null
        ? const LoadingStatePanel(message: 'Loading subscription...')
        : state.isOffline && subscription == null
        ? OfflineStatePanel(onRetry: _reload)
        : state.errorMessage != null && subscription == null
        ? ErrorStatePanel(message: state.errorMessage!, onRetry: _reload)
        : subscription == null
        ? ErrorStatePanel(
            message: 'Subscription could not be found.',
            onRetry: _reload,
          )
        : _content(context, subscription, state);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Subscription'),
        actions: [
          IconButton(
            tooltip: 'Refresh subscription',
            onPressed: state.isLoading ? null : _reload,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: body,
    );
  }

  Widget _content(
    BuildContext context,
    SubscriptionDetails subscription,
    SubscriptionState state,
  ) => RefreshIndicator(
    onRefresh: _reload,
    child: ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(16),
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                subscription.productName,
                style: Theme.of(context).textTheme.headlineSmall,
              ),
            ),
            Chip(label: Text(subscription.status.label)),
          ],
        ),
        const SizedBox(height: 12),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${subscription.usedEntitlement} of ${subscription.totalEntitlement} deliveries used',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 10),
                LinearProgressIndicator(
                  value: subscription.entitlementProgress,
                ),
                const SizedBox(height: 8),
                Text(
                  '${subscription.remainingEntitlement} deliveries remaining',
                ),
              ],
            ),
          ),
        ),
        Card(
          child: Column(
            children: [
              ListTile(
                leading: const Icon(Icons.local_drink_outlined),
                title: Text(subscription.formattedQuantity),
                subtitle: Text(subscription.productSku),
              ),
              ListTile(
                leading: const Icon(Icons.calendar_month_outlined),
                title: Text(subscription.scheduleLabel),
                subtitle: Text(
                  '${_formatDate(subscription.startDate)} to ${_formatDate(subscription.endDate)}',
                ),
              ),
              ListTile(
                leading: const Icon(Icons.payments_outlined),
                title: Text(subscription.formattedPayableAmount),
                subtitle: Text(
                  '₹${subscription.unitPrice.toStringAsFixed(2)} per unit',
                ),
              ),
            ],
          ),
        ),
        Card(
          child: Column(
            children: [
              const ListTile(
                leading: Icon(Icons.location_on_outlined),
                title: Text('Delivery address snapshot'),
              ),
              ListTile(
                title: Text(subscription.address),
                subtitle: Text(
                  'Branch: ${subscription.branchName} (${subscription.branchCode})',
                ),
              ),
            ],
          ),
        ),
        if (state.errorMessage != null)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 8),
            child: Text(
              state.errorMessage!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        const SizedBox(height: 8),
        OutlinedButton.icon(
          onPressed: subscription.status.canUpdate && !state.isSaving
              ? () => _edit(context, subscription)
              : null,
          icon: const Icon(Icons.edit_outlined),
          label: const Text('Update schedule'),
        ),
        if (subscription.status.canPause)
          OutlinedButton.icon(
            onPressed: state.isSaving
                ? null
                : () => _action(
                    'pause',
                    'Pause subscription?',
                    ref.read(subscriptionControllerProvider.notifier).pause,
                  ),
            icon: const Icon(Icons.pause),
            label: const Text('Pause subscription'),
          ),
        if (subscription.status.canResume)
          OutlinedButton.icon(
            onPressed: state.isSaving
                ? null
                : () => _action(
                    'resume',
                    'Resume subscription?',
                    ref.read(subscriptionControllerProvider.notifier).resume,
                  ),
            icon: const Icon(Icons.play_arrow),
            label: const Text('Resume subscription'),
          ),
        if (subscription.status.canCancel)
          TextButton.icon(
            onPressed: state.isSaving
                ? null
                : () => _action(
                    'cancel',
                    'Cancel subscription?',
                    ref.read(subscriptionControllerProvider.notifier).cancel,
                  ),
            icon: const Icon(Icons.cancel_outlined),
            label: const Text('Cancel subscription'),
          ),
        const SizedBox(height: 8),
        FilledButton.icon(
          onPressed: () =>
              context.push('/subscriptions/${subscription.publicId}/calendar'),
          icon: const Icon(Icons.event_note_outlined),
          label: const Text('View delivery calendar'),
        ),
      ],
    ),
  );

  Future<void> _action(
    String label,
    String title,
    Future<bool> Function(String) operation,
  ) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(title),
        content: Text(
          'This will change the subscription status on the server.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Keep it'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: Text(label),
          ),
        ],
      ),
    );
    if (confirmed == true) {
      await operation(widget.subscriptionId);
    }
  }

  Future<void> _edit(
    BuildContext context,
    SubscriptionDetails subscription,
  ) async {
    final customer = ref.read(customerControllerProvider);
    if (customer.addresses.isEmpty) {
      await ref.read(customerControllerProvider.notifier).load();
    }
    if (!mounted) return;
    final addresses = ref
        .read(customerControllerProvider)
        .addresses
        .where((item) => item.isActive)
        .toList(growable: false);
    if (addresses.isEmpty) return;
    final quantityController = TextEditingController(
      text: subscription.quantity.toString(),
    );
    var address = addresses.firstWhere(
      (item) => item.publicId == subscription.addressId,
      orElse: () => addresses.first,
    );
    var days = subscription.schedules.map((item) => item.dayOfWeek).toSet();
    final result = await showDialog<UpdateSubscriptionRequest>(
      context: this.context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) => AlertDialog(
          title: const Text('Update subscription'),
          content: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                TextField(
                  controller: quantityController,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  decoration: const InputDecoration(
                    labelText: 'Quantity per delivery',
                  ),
                ),
                DropdownButtonFormField<CustomerAddress>(
                  initialValue: address,
                  decoration: const InputDecoration(
                    labelText: 'Delivery address',
                  ),
                  items: addresses
                      .map(
                        (item) => DropdownMenuItem(
                          value: item,
                          child: Text(item.label),
                        ),
                      )
                      .toList(),
                  onChanged: (value) => setDialogState(() => address = value!),
                ),
                const SizedBox(height: 12),
                Align(
                  alignment: Alignment.centerLeft,
                  child: Text(
                    'Delivery days',
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                ),
                Wrap(
                  spacing: 4,
                  children: DeliveryWeekday.values
                      .map(
                        (day) => FilterChip(
                          label: Text(day.shortLabel),
                          selected: days.contains(day),
                          onSelected: (selected) => setDialogState(() {
                            if (selected) {
                              days = {...days, day};
                            } else {
                              days = {...days}..remove(day);
                            }
                          }),
                        ),
                      )
                      .toList(),
                ),
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Close'),
            ),
            FilledButton(
              onPressed: days.isEmpty
                  ? null
                  : () => Navigator.pop(
                      context,
                      UpdateSubscriptionRequest(
                        quantity: double.tryParse(quantityController.text),
                        addressId: address.publicId,
                        deliveryDays: days,
                      ),
                    ),
              child: const Text('Save'),
            ),
          ],
        ),
      ),
    );
    quantityController.dispose();
    if (result != null && mounted) {
      await ref
          .read(subscriptionControllerProvider.notifier)
          .update(widget.subscriptionId, result);
    }
  }
}

class SubscriptionCalendarScreen extends ConsumerStatefulWidget {
  const SubscriptionCalendarScreen({required this.subscriptionId, super.key});

  final String subscriptionId;

  @override
  ConsumerState<SubscriptionCalendarScreen> createState() =>
      _SubscriptionCalendarScreenState();
}

class _SubscriptionCalendarScreenState
    extends ConsumerState<SubscriptionCalendarScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(subscriptionControllerProvider.notifier)
          .loadCalendar(widget.subscriptionId),
    );
  }

  Future<void> _reload() => ref
      .read(subscriptionControllerProvider.notifier)
      .loadCalendar(widget.subscriptionId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(subscriptionControllerProvider);
    final body = state.isLoading && state.calendar.isEmpty
        ? const LoadingStatePanel(message: 'Loading delivery calendar...')
        : state.isOffline && state.calendar.isEmpty
        ? OfflineStatePanel(onRetry: _reload)
        : state.errorMessage != null && state.calendar.isEmpty
        ? ErrorStatePanel(message: state.errorMessage!, onRetry: _reload)
        : state.calendar.isEmpty
        ? EmptyStatePanel(
            title: 'No deliveries scheduled',
            message: 'Scheduled deliveries will appear here.',
            action: OutlinedButton.icon(
              onPressed: _reload,
              icon: const Icon(Icons.refresh),
              label: const Text('Refresh'),
            ),
          )
        : RefreshIndicator(
            onRefresh: _reload,
            child: ListView.separated(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: const EdgeInsets.all(16),
              itemCount: state.calendar.length,
              separatorBuilder: (_, index) => const SizedBox(height: 8),
              itemBuilder: (context, index) {
                final delivery = state.calendar[index];
                return Card(
                  child: ListTile(
                    leading: Icon(_deliveryIcon(delivery.status)),
                    title: Text(_formatDate(delivery.scheduledDate)),
                    subtitle: Text(
                      '${formatQuantity(delivery.quantity)} | ${delivery.status.label}\n'
                      '${delivery.branchName} (${delivery.branchCode})\n'
                      '${delivery.address}',
                    ),
                    isThreeLine: true,
                    trailing: delivery.status.canSkip
                        ? IconButton(
                            tooltip: 'Skip delivery',
                            onPressed: state.isSaving
                                ? null
                                : () => _skip(delivery),
                            icon: const Icon(Icons.event_busy_outlined),
                          )
                        : null,
                  ),
                );
              },
            ),
          );
    return Scaffold(
      appBar: AppBar(
        title: const Text('Delivery calendar'),
        actions: [
          IconButton(
            tooltip: 'Refresh calendar',
            onPressed: state.isLoading ? null : _reload,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: body,
    );
  }

  Future<void> _skip(SubscriptionDelivery delivery) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Skip this delivery?'),
        content: Text(
          'The delivery scheduled for ${_formatDate(delivery.scheduledDate)} will be skipped.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Keep delivery'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Skip'),
          ),
        ],
      ),
    );
    if (confirmed == true) {
      await ref
          .read(subscriptionControllerProvider.notifier)
          .skip(widget.subscriptionId, delivery.publicId);
    }
  }

  IconData _deliveryIcon(SubscriptionDeliveryStatus status) => switch (status) {
    SubscriptionDeliveryStatus.scheduled => Icons.event_outlined,
    SubscriptionDeliveryStatus.delivered => Icons.check_circle_outline,
    SubscriptionDeliveryStatus.skipped => Icons.event_busy_outlined,
    SubscriptionDeliveryStatus.failed => Icons.error_outline,
    SubscriptionDeliveryStatus.cancelled => Icons.cancel_outlined,
    SubscriptionDeliveryStatus.unknown => Icons.help_outline,
  };
}

DateTime _dateOnly(DateTime value) =>
    DateTime(value.year, value.month, value.day);

String _formatDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/'
    '${value.month.toString().padLeft(2, '0')}/'
    '${value.year.toString().padLeft(4, '0')}';
