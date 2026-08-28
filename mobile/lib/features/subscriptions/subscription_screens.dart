import 'package:doodh_direct_mobile/core/config/app_config.dart';
import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
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

List<CatalogueProduct> deduplicateAvailableSubscriptionProducts(
  Iterable<CatalogueProduct> products,
) {
  final uniqueProducts = <String, CatalogueProduct>{};
  for (final product in products) {
    if (product.isActive &&
        product.branchAvailability.any((branch) => branch.isAvailable)) {
      uniqueProducts.putIfAbsent(product.publicId, () => product);
    }
  }
  return uniqueProducts.values.toList(growable: false);
}

String? resolveAvailableSubscriptionProductId(
  List<CatalogueProduct> products,
  String? selectedProductId,
) => products.any((product) => product.publicId == selectedProductId)
    ? selectedProductId
    : products.firstOrNull?.publicId;

List<CustomerAddress> deduplicateActiveSubscriptionAddresses(
  Iterable<CustomerAddress> addresses,
) {
  final uniqueAddresses = <String, CustomerAddress>{};
  for (final address in addresses) {
    if (address.isActive) {
      uniqueAddresses.putIfAbsent(address.publicId, () => address);
    }
  }
  return uniqueAddresses.values.toList(growable: false);
}

String? resolveActiveSubscriptionAddressId(
  List<CustomerAddress> addresses,
  String? selectedAddressId,
) {
  if (addresses.any((address) => address.publicId == selectedAddressId)) {
    return selectedAddressId;
  }
  return addresses
          .where((address) => address.isDefault)
          .firstOrNull
          ?.publicId ??
      addresses.firstOrNull?.publicId;
}

class SubscriptionSetupScreen extends ConsumerStatefulWidget {
  const SubscriptionSetupScreen({super.key});

  @override
  ConsumerState<SubscriptionSetupScreen> createState() =>
      _SubscriptionSetupScreenState();
}

class _SubscriptionSetupScreenState
    extends ConsumerState<SubscriptionSetupScreen> {
  String? _productId;
  String? _addressId;
  double _quantity = 1;
  int _entitlement = 30;
  DateTime _startDate = _dateOnly(indiaNow());
  final Set<DeliveryWeekday> _days = {
    DeliveryWeekday.monday,
    DeliveryWeekday.wednesday,
    DeliveryWeekday.friday,
  };
  PaymentMethod _paymentMethod = PaymentMethod.wallet;
  SubscriptionDeliverySlot _slot = SubscriptionDeliverySlot.morning;

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
    final products = deduplicateAvailableSubscriptionProducts(
      catalogue.products,
    );
    final addresses = deduplicateActiveSubscriptionAddresses(
      customer.addresses,
    );
    _productId = resolveAvailableSubscriptionProductId(products, _productId);
    _addressId = resolveActiveSubscriptionAddressId(addresses, _addressId);
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
    final product = products.firstWhere((item) => item.publicId == _productId);
    final estimate = product.price * _quantity * _entitlement;
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        DropdownButtonFormField<String>(
          initialValue: product.publicId,
          decoration: const InputDecoration(labelText: 'Product'),
          items: products
              .map(
                (item) => DropdownMenuItem(
                  value: item.publicId,
                  child: Text(item.name),
                ),
              )
              .toList(),
          onChanged: (value) => setState(() => _productId = value),
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<String>(
          initialValue: _addressId,
          decoration: const InputDecoration(labelText: 'Delivery address'),
          items: addresses
              .map(
                (item) => DropdownMenuItem(
                  value: item.publicId,
                  child: Text('${item.label} - ${item.city}'),
                ),
              )
              .toList(),
          onChanged: (value) => setState(() => _addressId = value),
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
        DropdownButtonFormField<SubscriptionDeliverySlot>(
          initialValue: _slot,
          decoration: const InputDecoration(labelText: 'Delivery slot'),
          items: SubscriptionDeliverySlot.values
              .map(
                (slot) =>
                    DropdownMenuItem(value: slot, child: Text(slot.apiValue)),
              )
              .toList(),
          onChanged: (value) => setState(() => _slot = value!),
        ),
        const SizedBox(height: 12),
        RadioGroup<PaymentMethod>(
          groupValue: _paymentMethod,
          onChanged: (value) => setState(() => _paymentMethod = value!),
          child: Column(
            children: [
              const RadioListTile(
                value: PaymentMethod.wallet,
                title: Text('DoodhDirect Wallet'),
              ),
              const RadioListTile(
                value: PaymentMethod.razorpay,
                title: Text('Razorpay'),
              ),
              if (devToolsEnabled)
                const RadioListTile(
                  value: PaymentMethod.development,
                  title: Text('Development payment'),
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
    final today = _dateOnly(indiaNow());
    final selected = await showDatePicker(
      context: context,
      initialDate: _startDate,
      firstDate: today,
      lastDate: today.add(const Duration(days: 366)),
    );
    if (selected != null && mounted) {
      setState(() => _startDate = _dateOnly(selected));
    }
  }

  Future<void> _create() async {
    final scaledQuantity = _quantity * 1000;
    final quantityHasTooManyDecimals =
        (scaledQuantity - scaledQuantity.round()).abs() > 0.000001;
    if (_productId == null ||
        _addressId == null ||
        _quantity <= 0 ||
        quantityHasTooManyDecimals ||
        _entitlement < 1 ||
        _entitlement > 366 ||
        _days.isEmpty ||
        _startDate.isBefore(_dateOnly(indiaNow()))) {
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
            productId: _productId!,
            addressId: _addressId!,
            quantity: _quantity,
            startDate: _startDate,
            deliveryDays: _days,
            slot: _slot,
            totalEntitlement: _entitlement,
            paymentMethod: _paymentMethod,
          ),
        );
    if (!mounted || created == null) return;
    final payment = created.payment;
    if (payment.usesRazorpay && payment.status.isPending) {
      final verified = await ref
          .read(paymentControllerProvider.notifier)
          .openRazorpayAndVerify();
      if (!mounted || !verified) return;
    }
    if (!mounted) return;
    context.go('/payments/${payment.publicId}/result');
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
    return CustomerShell(
      currentPath: '/subscriptions',
      title: 'Subscriptions',
      actions: [
        IconButton(
          tooltip: 'Refresh subscriptions',
          onPressed: state.isLoading ? null : _refresh,
          icon: const Icon(Icons.refresh),
        ),
      ],
      floatingActionButton: state.subscriptions.isEmpty
          ? null
          : FloatingActionButton(
              tooltip: 'Create subscription',
              onPressed: () => context.push('/subscriptions/new'),
              child: const Icon(Icons.add),
            ),
      child: body,
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
  PaymentMethod _retryPaymentMethod = PaymentMethod.wallet;
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
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Text(
                subscription.productName,
                style: Theme.of(context).textTheme.headlineSmall,
              ),
            ),
            _SubscriptionStatusPill(status: subscription.status),
          ],
        ),
        const SizedBox(height: 16),
        Card(
          color: DoodhColors.mint,
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${subscription.remainingEntitlement} deliveries remaining',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 4),
                Text(
                  '${subscription.usedEntitlement} of ${subscription.totalEntitlement} deliveries used',
                ),
                const SizedBox(height: 12),
                LinearProgressIndicator(
                  value: subscription.entitlementProgress,
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        DoodhSectionHeader(
          title: 'Your plan',
          action: Text(
            subscription.formattedPayableAmount,
            style: const TextStyle(fontWeight: FontWeight.w800),
          ),
        ),
        const SizedBox(height: 8),
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
        const SizedBox(height: 16),
        DoodhSectionHeader(title: 'Delivery address'),
        const SizedBox(height: 8),
        Card(
          child: ListTile(
            leading: const Icon(Icons.location_on_outlined),
            title: Text(subscription.address),
            subtitle: const Text('Used for upcoming deliveries'),
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
        if (subscription.status == SubscriptionStatus.paymentPending ||
            subscription.status == SubscriptionStatus.paymentFailed) ...[
          const SizedBox(height: 8),
          Text(
            'Status: ${subscription.status.label}',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          Text('Amount Due: ${subscription.formattedPayableAmount}'),
          const SizedBox(height: 8),
          Text(
            subscription.status == SubscriptionStatus.paymentPending
                ? 'Complete Payment'
                : 'Retry Payment',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          RadioGroup<PaymentMethod>(
            groupValue: _retryPaymentMethod,
            onChanged: (value) {
              if (!state.isSaving && value != null) {
                setState(() => _retryPaymentMethod = value);
              }
            },
            child: Column(
              children: [
                const RadioListTile(
                  value: PaymentMethod.wallet,
                  title: Text('DoodhDirect Wallet'),
                ),
                const RadioListTile(
                  value: PaymentMethod.razorpay,
                  title: Text('Razorpay'),
                ),
                if (devToolsEnabled)
                  const RadioListTile(
                    value: PaymentMethod.development,
                    title: Text('Development payment'),
                  ),
              ],
            ),
          ),
          FilledButton.icon(
            onPressed: state.isSaving ? null : _retryPayment,
            icon: state.isSaving
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : Icon(
                    subscription.status == SubscriptionStatus.paymentPending
                        ? Icons.payment
                        : Icons.replay,
                  ),
            label: Text(
              state.isSaving
                  ? 'Processing...'
                  : subscription.status == SubscriptionStatus.paymentPending
                  ? 'Complete Payment'
                  : 'Retry Payment',
            ),
          ),
        ],
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

  Future<void> _retryPayment() async {
    final created = await ref
        .read(subscriptionControllerProvider.notifier)
        .retryPayment(widget.subscriptionId, _retryPaymentMethod);
    if (!mounted || created == null) return;

    final payment = created.payment;
    if (payment.usesRazorpay && payment.status.isPending) {
      final verified = await ref
          .read(paymentControllerProvider.notifier)
          .openRazorpayAndVerify();
      if (!mounted || !verified) return;
    }
    if (!mounted) return;
    context.go('/payments/${payment.publicId}/result');
  }

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
    final addresses = deduplicateActiveSubscriptionAddresses(
      ref.read(customerControllerProvider).addresses,
    );
    if (addresses.isEmpty) return;
    final quantityController = TextEditingController(
      text: subscription.quantity.toString(),
    );
    var addressId = resolveActiveSubscriptionAddressId(
      addresses,
      subscription.addressId,
    )!;
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
                DropdownButtonFormField<String>(
                  initialValue: addressId,
                  decoration: const InputDecoration(
                    labelText: 'Delivery address',
                  ),
                  items: addresses
                      .map(
                        (item) => DropdownMenuItem(
                          value: item.publicId,
                          child: Text(item.label),
                        ),
                      )
                      .toList(),
                  onChanged: (value) {
                    if (value != null) {
                      setDialogState(() => addressId = value);
                    }
                  },
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
                        addressId: addressId,
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
                      '${formatQuantity(delivery.quantity)} · ${delivery.address}',
                    ),
                    trailing: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        _SubscriptionDeliveryStatus(status: delivery.status),
                        if (delivery.status.canSkip)
                          IconButton(
                            tooltip: 'Skip delivery',
                            onPressed: state.isSaving
                                ? null
                                : () => _skip(delivery),
                            icon: const Icon(Icons.event_busy_outlined),
                          ),
                      ],
                    ),
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

class _SubscriptionStatusPill extends StatelessWidget {
  const _SubscriptionStatusPill({required this.status});

  final SubscriptionStatus status;

  @override
  Widget build(BuildContext context) => DoodhStatusPill(
    label: status.label,
    tone: switch (status) {
      SubscriptionStatus.active => DoodhStatusTone.success,
      SubscriptionStatus.paymentPending ||
      SubscriptionStatus.paymentFailed => DoodhStatusTone.warning,
      SubscriptionStatus.cancelled => DoodhStatusTone.error,
      _ => DoodhStatusTone.neutral,
    },
  );
}

class _SubscriptionDeliveryStatus extends StatelessWidget {
  const _SubscriptionDeliveryStatus({required this.status});

  final SubscriptionDeliveryStatus status;

  @override
  Widget build(BuildContext context) => DoodhStatusPill(
    label: status.label,
    tone: switch (status) {
      SubscriptionDeliveryStatus.failed ||
      SubscriptionDeliveryStatus.cancelled => DoodhStatusTone.error,
      SubscriptionDeliveryStatus.delivered => DoodhStatusTone.success,
      SubscriptionDeliveryStatus.scheduled ||
      SubscriptionDeliveryStatus.skipped => DoodhStatusTone.neutral,
      SubscriptionDeliveryStatus.unknown => DoodhStatusTone.warning,
    },
  );
}

DateTime _dateOnly(DateTime value) =>
    DateTime(value.year, value.month, value.day);

String _formatDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/'
    '${value.month.toString().padLeft(2, '0')}/'
    '${value.year.toString().padLeft(4, '0')}';
