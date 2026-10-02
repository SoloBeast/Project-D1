import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
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
    // On tablets/web the setup becomes a two-column composition (plan on the
    // left, address + payment on the right) on a constrained reading width;
    // phones keep the single guided column.
    final useTwoColumn = MediaQuery.sizeOf(context).width >=
        DoodhBreakpoints.medium;
    Widget planSections() => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        DoodhSectionCard(
          icon: Icons.local_drink_outlined,
          title: '1. Choose your product',
          subtitle: 'Fresh delivery from an available branch',
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(
                  width: 84,
                  child: DoodhProductImage(
                    height: 84,
                    imageUrl: product.usableImageUrl,
                    icon: doodhProductUnitIcon(product.unitOfMeasure),
                    semanticLabel: '${product.name} product image',
                    borderRadius: DoodhRadii.mdRadius,
                  ),
                ),
                const SizedBox(width: DoodhSpacing.md),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(product.name, style: Theme.of(context).textTheme.titleLarge),
                      const SizedBox(height: DoodhSpacing.xs),
                      DoodhPriceTag(amount: product.price),
                      const SizedBox(height: DoodhSpacing.sm),
                      DropdownButtonFormField<String>(
                        initialValue: product.publicId,
                        decoration: const InputDecoration(labelText: 'Change product'),
                        items: products.map((item) => DropdownMenuItem(value: item.publicId, child: Text(item.name))).toList(),
                        onChanged: (value) => setState(() => _productId = value),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ],
        ),
        const SizedBox(height: DoodhSpacing.md),
        DoodhSectionCard(
          icon: Icons.water_drop_outlined,
          title: '2. Set quantity and prepaid coverage',
          subtitle: 'Your quantity is applied to each generated delivery',
          children: [
            TextFormField(
              initialValue: _quantity.toString(),
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: InputDecoration(labelText: 'Quantity per delivery (${product.unitLabel})'),
              onChanged: (value) => _quantity = double.tryParse(value) ?? 0,
            ),
            const SizedBox(height: DoodhSpacing.md),
            TextFormField(
              initialValue: _entitlement.toString(),
              keyboardType: TextInputType.number,
              decoration: const InputDecoration(
                labelText: 'Total deliveries',
                helperText: 'Choose between 1 and 366 prepaid deliveries',
              ),
              onChanged: (value) => _entitlement = int.tryParse(value) ?? 0,
            ),
          ],
        ),
        const SizedBox(height: DoodhSpacing.md),
        DoodhSectionCard(
          icon: Icons.event_repeat_outlined,
          title: '3. Choose your delivery rhythm',
          subtitle: 'Select at least one day and a preferred time window',
          children: [
            Semantics(
              container: true,
              label: 'Delivery days, ${_days.length} selected',
              child: Wrap(
                spacing: DoodhSpacing.xs,
                runSpacing: DoodhSpacing.xs,
                children: DeliveryWeekday.values.map((day) => FilterChip(
                  label: Text(day.shortLabel),
                  tooltip: day.apiValue,
                  selected: _days.contains(day),
                  onSelected: (selected) => setState(() => selected ? _days.add(day) : _days.remove(day)),
                )).toList(),
              ),
            ),
            const SizedBox(height: DoodhSpacing.sm),
            DropdownButtonFormField<SubscriptionDeliverySlot>(
              initialValue: _slot,
              decoration: const InputDecoration(labelText: 'Delivery slot'),
              items: SubscriptionDeliverySlot.values.map((slot) => DropdownMenuItem(value: slot, child: Text(slot.apiValue))).toList(),
              onChanged: (value) => setState(() => _slot = value!),
            ),
          ],
        ),
      ],
    );

    Widget logisticsSections() => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        DoodhSectionCard(
          icon: Icons.location_on_outlined,
          title: '4. Delivery address and start date',
          children: [
            DropdownButtonFormField<String>(
              initialValue: _addressId,
              decoration: const InputDecoration(labelText: 'Delivery address'),
              items: addresses.map((item) => DropdownMenuItem(value: item.publicId, child: Text('${item.label} - ${item.city}'))).toList(),
              onChanged: (value) => setState(() => _addressId = value),
            ),
            const SizedBox(height: DoodhSpacing.xs),
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Start date'),
              subtitle: Text(_formatDate(_startDate)),
              trailing: const Icon(Icons.calendar_today_outlined),
              onTap: _pickStartDate,
            ),
          ],
        ),
        const SizedBox(height: DoodhSpacing.md),
        DoodhSectionCard(
          icon: Icons.lock_outline,
          title: '5. Review and pay',
          subtitle: 'Payment and activation are confirmed by DoodhDirect',
          children: [
            DoodhCard(
              color: DoodhColors.mint,
              semanticLabel: 'Prepaid estimate for ${formatQuantity(_quantity)} per delivery and $_entitlement deliveries',
              child: Row(
                children: [
                  const Icon(Icons.receipt_long_outlined),
                  const SizedBox(width: DoodhSpacing.sm),
                  Expanded(child: Text('${formatQuantity(_quantity)} ${product.unitLabel} × $_entitlement deliveries\nEstimate only; final payable amount is confirmed by the server.')),
                  DoodhPriceTag(amount: estimate),
                ],
              ),
            ),
            const SizedBox(height: DoodhSpacing.md),
            RadioGroup<PaymentMethod>(
              groupValue: _paymentMethod,
              onChanged: (value) => setState(() => _paymentMethod = value!),
              child: const Column(children: [
                RadioListTile(value: PaymentMethod.wallet, title: Text('DoodhDirect Wallet'), contentPadding: EdgeInsets.zero),
                RadioListTile(value: PaymentMethod.razorpay, title: Text('Razorpay'), contentPadding: EdgeInsets.zero),
              ]),
            ),
            if (state.errorMessage != null) ...[
              const SizedBox(height: DoodhSpacing.sm),
              DoodhErrorBanner(message: state.errorMessage!),
            ],
          ],
        ),
      ],
    );

    // The primary CTA stays sticky at the bottom on phones so the pay action
    // is always reachable; on wide layouts it flows inline after the form.
    final cta = DoodhButton(
      label: state.isSaving ? 'Processing...' : 'Continue to payment',
      icon: state.isSaving ? null : Icons.lock_outline,
      busy: state.isSaving,
      expand: true,
      onPressed: state.isSaving ? null : _create,
    );
    return Stack(
      children: [
        ListView(
          padding: EdgeInsets.fromLTRB(
            DoodhSpacing.md,
            DoodhSpacing.md,
            DoodhSpacing.md,
            useTwoColumn ? DoodhSpacing.xl : 96,
          ),
          children: [
            Text('Build your recurring plan', style: Theme.of(context).textTheme.headlineSmall),
            const SizedBox(height: DoodhSpacing.xs),
            const Text('Choose what you need, when it arrives, and how much prepaid coverage to keep.'),
            const SizedBox(height: DoodhSpacing.lg),
            Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: DoodhContentMax.form),
                child: useTwoColumn
                    ? Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Expanded(child: planSections()),
                          const SizedBox(width: DoodhSpacing.lg),
                          Expanded(child: logisticsSections()),
                        ],
                      )
                    : Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          planSections(),
                          const SizedBox(height: DoodhSpacing.md),
                          logisticsSections(),
                        ],
                      ),
              ),
            ),
            if (useTwoColumn) ...[
              const SizedBox(height: DoodhSpacing.lg),
              cta,
            ],
          ],
        ),
        if (!useTwoColumn)
          Align(
            alignment: Alignment.bottomCenter,
            child: Container(
              width: double.infinity,
              decoration: const BoxDecoration(
                color: Colors.white,
                border: Border(top: BorderSide(color: DoodhColors.line)),
              ),
              child: SafeArea(
                top: false,
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(
                    DoodhSpacing.md,
                    DoodhSpacing.sm,
                    DoodhSpacing.md,
                    DoodhSpacing.sm,
                  ),
                  child: cta,
                ),
              ),
            ),
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
            child: ListView(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: DoodhSpacing.pagePadding,
              children: [
                Text('Your recurring plans', style: Theme.of(context).textTheme.headlineSmall),
                const SizedBox(height: DoodhSpacing.xs),
                const Text('See your delivery rhythm, prepaid balance, and current plan status at a glance.'),
                const SizedBox(height: DoodhSpacing.lg),
                DoodhGrid(
                  minItemWidth: 320,
                  maxColumns: 2,
                  children: state.subscriptions.map((subscription) => _SubscriptionPlanCard(
                    subscription: subscription,
                    onTap: () => context.push('/subscriptions/${subscription.publicId}'),
                  )).toList(),
                ),
              ],
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

}

class _SubscriptionPlanCard extends StatelessWidget {
  const _SubscriptionPlanCard({required this.subscription, required this.onTap});

  final SubscriptionDetails subscription;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => DoodhCard(
    onTap: onTap,
    semanticLabel: '${subscription.productName}, ${subscription.formattedQuantity}, ${subscription.status.label}, ${subscription.remainingEntitlement} deliveries remaining',
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(
              width: 72,
              child: DoodhProductImage(
                height: 72,
                icon: doodhProductUnitIcon(subscription.unitOfMeasure),
                semanticLabel: '${subscription.productName} product image',
                borderRadius: DoodhRadii.mdRadius,
              ),
            ),
            const SizedBox(width: DoodhSpacing.md),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(subscription.productName, style: Theme.of(context).textTheme.titleLarge),
                  const SizedBox(height: DoodhSpacing.xs),
                  Text(subscription.formattedQuantity),
                ],
              ),
            ),
            const SizedBox(width: DoodhSpacing.xs),
            _SubscriptionStatusPill(status: subscription.status),
          ],
        ),
        const SizedBox(height: DoodhSpacing.md),
        Row(
          children: [
            const Icon(Icons.event_repeat_outlined, size: 20),
            const SizedBox(width: DoodhSpacing.sm),
            Expanded(child: Text(subscription.scheduleLabel)),
          ],
        ),
        const SizedBox(height: DoodhSpacing.md),
        Semantics(
          container: true,
          label: '${subscription.usedEntitlement} of ${subscription.totalEntitlement} prepaid deliveries used, ${subscription.remainingEntitlement} deliveries remaining',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      '${subscription.remainingEntitlement} deliveries remaining',
                      style: Theme.of(context).textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                  Text(
                    '${subscription.usedEntitlement}/${subscription.totalEntitlement}',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: DoodhColors.muted,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: DoodhSpacing.sm),
              LinearProgressIndicator(
                value: subscription.entitlementProgress,
                minHeight: 6,
                borderRadius: BorderRadius.circular(DoodhRadii.pillValue),
              ),
            ],
          ),
        ),
        const Divider(height: DoodhSpacing.lg),
        Row(
          children: [
            const Icon(Icons.visibility_outlined, size: 18),
            const SizedBox(width: DoodhSpacing.sm),
            const Expanded(child: Text('View details')),
            const Icon(Icons.chevron_right),
          ],
        ),
      ],
    ),
  );
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
    child: DoodhResponsive(
      builder: (context, size) {
        final useTwoColumn = size != DoodhWindowSize.compact;
        Widget heroCard() => DoodhCard(
          color: DoodhColors.tealDark,
          semanticLabel:
              '${subscription.productName}, ${subscription.formattedQuantity}, ${subscription.status.label}',
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              ExcludeSemantics(
                child: Container(
                  width: 64,
                  height: 64,
                  decoration: BoxDecoration(
                    color: DoodhColors.mint,
                    borderRadius: DoodhRadii.mdRadius,
                  ),
                  child: Icon(
                    doodhProductUnitIcon(subscription.unitOfMeasure),
                    color: DoodhColors.tealDark,
                  ),
                ),
              ),
              const SizedBox(width: DoodhSpacing.md),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      subscription.productName,
                      style: Theme.of(context).textTheme.titleLarge?.copyWith(
                        color: Colors.white,
                      ),
                    ),
                    const SizedBox(height: DoodhSpacing.xs),
                    Text(
                      subscription.formattedQuantity,
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: Colors.white70,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: DoodhSpacing.xs),
              _SubscriptionStatusPill(status: subscription.status),
            ],
          ),
        );

        Widget statusAndPlanColumn() => Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            heroCard(),
            const SizedBox(height: DoodhSpacing.md),
            // Prepaid entitlement: the trust anchor of the plan.
            DoodhCard(
              color: DoodhColors.mint,
              semanticLabel:
                  '${subscription.remainingEntitlement} deliveries remaining, ${subscription.usedEntitlement} of ${subscription.totalEntitlement} prepaid deliveries used',
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    '${subscription.remainingEntitlement} deliveries remaining',
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                  const SizedBox(height: DoodhSpacing.xs),
                  Text(
                    '${subscription.usedEntitlement} of ${subscription.totalEntitlement} deliveries used',
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                  Semantics(
                    container: true,
                    label:
                        '${subscription.usedEntitlement} of ${subscription.totalEntitlement} prepaid deliveries used',
                    child: LinearProgressIndicator(
                      value: subscription.entitlementProgress,
                      minHeight: 6,
                      borderRadius: BorderRadius.circular(
                        DoodhRadii.pillValue,
                      ),
                    ),
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                  Text(
                    'Prepaid plan value ${subscription.formattedPayableAmount}',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: DoodhColors.tealDark,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: DoodhSpacing.md),
            _planCard(context, subscription),
          ],
        );

        Widget addressAndActionsColumn() => Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            DoodhSectionCard(
              icon: Icons.location_on_outlined,
              title: 'Delivery address',
              children: [
                DoodhInfoTile(
                  icon: Icons.location_on_outlined,
                  title: subscription.address,
                  subtitle: 'Used for upcoming deliveries',
                ),
              ],
            ),
            if (state.errorMessage != null) ...[
              const SizedBox(height: DoodhSpacing.md),
              DoodhInfoBanner(
                tone: DoodhTone.error,
                message: state.errorMessage!,
              ),
            ],
            if (subscription.status == SubscriptionStatus.paymentPending ||
                subscription.status == SubscriptionStatus.paymentFailed) ...[
              const SizedBox(height: DoodhSpacing.md),
              DoodhSectionCard(
                icon: Icons.priority_high_outlined,
                title: subscription.status == SubscriptionStatus.paymentPending
                    ? 'Complete Payment'
                    : 'Retry Payment',
                subtitle: 'Status: ${subscription.status.label}',
                children: [
                  Text('Amount Due: ${subscription.formattedPayableAmount}'),
                  const SizedBox(height: DoodhSpacing.sm),
                  RadioGroup<PaymentMethod>(
                    groupValue: _retryPaymentMethod,
                    onChanged: (value) {
                      if (!state.isSaving && value != null) {
                        setState(() => _retryPaymentMethod = value);
                      }
                    },
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
                  const SizedBox(height: DoodhSpacing.sm),
                  FilledButton.icon(
                    onPressed: state.isSaving ? null : _retryPayment,
                    icon: state.isSaving
                        ? const SizedBox(
                            width: 18,
                            height: 18,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : Icon(
                            subscription.status ==
                                    SubscriptionStatus.paymentPending
                                ? Icons.payment
                                : Icons.replay,
                          ),
                    label: Text(
                      state.isSaving
                          ? 'Processing...'
                          : subscription.status ==
                                    SubscriptionStatus.paymentPending
                              ? 'Complete Payment'
                              : 'Retry Payment',
                    ),
                  ),
                ],
              ),
            ],
            const SizedBox(height: DoodhSpacing.md),
            DoodhSectionCard(
              icon: Icons.tune_outlined,
              title: 'Manage this plan',
              subtitle: 'Changes apply to upcoming deliveries on the server.',
              children: [
                // Schedule updates are disabled: after a subscription is
                // placed the customer may only hold (pause/resume) or
                // cancel it.
                if (subscription.status.canPause)
                  OutlinedButton.icon(
                    onPressed: state.isSaving
                        ? null
                        : () => _action(
                            'pause',
                            'Pause subscription?',
                            ref
                                .read(subscriptionControllerProvider.notifier)
                                .pause,
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
                            ref
                                .read(subscriptionControllerProvider.notifier)
                                .resume,
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
                            ref
                                .read(subscriptionControllerProvider.notifier)
                                .cancel,
                          ),
                    icon: const Icon(Icons.cancel_outlined),
                    label: const Text('Cancel subscription'),
                  ),
              ],
            ),
            const SizedBox(height: DoodhSpacing.md),
            DoodhButton(
              label: 'View delivery calendar',
              icon: Icons.event_note_outlined,
              variant: DoodhButtonVariant.secondary,
              expand: true,
              onPressed: () => context.push(
                '/subscriptions/${subscription.publicId}/calendar',
              ),
            ),
          ],
        );

        return ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: DoodhSpacing.pagePadding,
          children: [
            Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(
                  maxWidth: DoodhContentMax.form,
                ),
                child: useTwoColumn
                    ? Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Expanded(child: statusAndPlanColumn()),
                          const SizedBox(width: DoodhSpacing.lg),
                          Expanded(child: addressAndActionsColumn()),
                        ],
                      )
                    : Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          statusAndPlanColumn(),
                          const SizedBox(height: DoodhSpacing.md),
                          addressAndActionsColumn(),
                        ],
                      ),
              ),
            ),
          ],
        );
      },
    ),
  );

  Widget _planCard(BuildContext context, SubscriptionDetails subscription) {
    final nextDelivery = _nextDeliverySummary(subscription);
    return DoodhSectionCard(
      icon: Icons.receipt_long_outlined,
      title: 'Your plan',
      trailing: Text(
        subscription.formattedPayableAmount,
        style: const TextStyle(fontWeight: FontWeight.w800),
      ),
      children: [
        DoodhKeyValueRow(
          label: 'Product',
          value:
              '${subscription.formattedQuantity} · ${subscription.productSku}',
        ),
        DoodhKeyValueRow(
          label: 'Delivery days',
          value: subscription.scheduleLabel,
        ),
        DoodhKeyValueRow(
          label: 'Active window',
          value:
              '${_formatDate(subscription.startDate)} to ${_formatDate(subscription.endDate)}',
        ),
        DoodhKeyValueRow(
          label: 'Unit price',
          value: '₹${subscription.unitPrice.toStringAsFixed(2)} per unit',
        ),
        if (nextDelivery != null)
          DoodhKeyValueRow(label: 'Next delivery', value: nextDelivery),
      ],
    );
  }

  String? _nextDeliverySummary(SubscriptionDetails subscription) {
    final next = _nextScheduledOccurrence(subscription);
    if (next == null) return null;
    final slot = subscription.schedules.first.slot.apiValue;
    return '${_formatDate(next)} · $slot';
  }

  DateTime? _nextScheduledOccurrence(SubscriptionDetails subscription) {
    if (subscription.status != SubscriptionStatus.active) return null;
    final weekdays = subscription.schedules
        .map((schedule) => schedule.dayOfWeek)
        .toSet();
    if (weekdays.isEmpty) return null;
    final start = _dateOnly(subscription.startDate);
    final end = _dateOnly(subscription.endDate);
    var cursor = _dateOnly(indiaNow());
    if (cursor.isBefore(start)) cursor = start;
    for (var offset = 0; offset < 14; offset++) {
      final candidate = cursor.add(Duration(days: offset));
      if (candidate.isAfter(end)) return null;
      if (weekdays.contains(_weekdayOf(candidate))) return candidate;
    }
    return null;
  }

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
    final today = _dateOnly(indiaNow());
    final upcoming = state.calendar
        .where(
          (delivery) =>
              delivery.status == SubscriptionDeliveryStatus.scheduled &&
              !_dateOnly(delivery.scheduledDate).isBefore(today),
        )
        .toList()
      ..sort((a, b) => a.scheduledDate.compareTo(b.scheduledDate));
    final history = state.calendar
        .where(
          (delivery) =>
              !(delivery.status == SubscriptionDeliveryStatus.scheduled &&
                  !_dateOnly(delivery.scheduledDate).isBefore(today)),
        )
        .toList()
      ..sort((a, b) => b.scheduledDate.compareTo(a.scheduledDate));
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
            child: ListView(
              physics: const AlwaysScrollableScrollPhysics(),
              padding: DoodhSpacing.pagePadding,
              children: [
                if (upcoming.isNotEmpty) ...[
                  DoodhSectionHeader(title: 'Upcoming deliveries'),
                  const SizedBox(height: DoodhSpacing.sm),
                  for (final delivery in upcoming) ...[
                    _deliveryCard(delivery, state),
                    const SizedBox(height: DoodhSpacing.sm),
                  ],
                  const SizedBox(height: DoodhSpacing.sm),
                ],
                if (history.isNotEmpty) ...[
                  DoodhSectionHeader(title: 'Delivery history'),
                  const SizedBox(height: DoodhSpacing.sm),
                  for (final delivery in history) ...[
                    _deliveryCard(delivery, state),
                    const SizedBox(height: DoodhSpacing.sm),
                  ],
                ],
              ],
            ),
          );
    return Scaffold(
      appBar: AppBar(
        title: const Text('Delivery calendar'),
        actions: [
          IconButton(
            tooltip: 'Set Vacation',
            onPressed: () => context.push('/subscriptions/vacation'),
            icon: const Icon(Icons.beach_access_outlined),
          ),
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

  Widget _deliveryCard(SubscriptionDelivery delivery, SubscriptionState state) =>
      DoodhCard(
        semanticLabel:
            '${_formatDate(delivery.scheduledDate)}, ${delivery.status.label}, ${formatQuantity(delivery.quantity)}',
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Compact calendar tile keeps the date scannable down the list.
            ExcludeSemantics(
              child: Container(
                width: 52,
                height: 56,
                decoration: BoxDecoration(
                  color: delivery.status == SubscriptionDeliveryStatus.scheduled
                      ? DoodhColors.mint
                      : DoodhColors.neutralSurface,
                  borderRadius: DoodhRadii.mdRadius,
                  border: Border.all(color: DoodhColors.line),
                ),
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Text(
                      _weekdayOf(delivery.scheduledDate).shortLabel,
                      style: Theme.of(context).textTheme.labelSmall?.copyWith(
                        color: DoodhColors.muted,
                      ),
                    ),
                    Text(
                      '${delivery.scheduledDate.day}',
                      style: Theme.of(context).textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(width: DoodhSpacing.md),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    _formatDate(delivery.scheduledDate),
                    style: Theme.of(context).textTheme.titleSmall?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  const SizedBox(height: DoodhSpacing.xs),
                  Text(
                    '${delivery.slot.apiValue} · ${formatQuantity(delivery.quantity)} per delivery',
                  ),
                  const SizedBox(height: DoodhSpacing.xs),
                  Text(
                    delivery.address,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: DoodhColors.muted,
                    ),
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                  _SubscriptionDeliveryStatus(status: delivery.status),
                ],
              ),
            ),
            const SizedBox(width: DoodhSpacing.sm),
            if (delivery.status.canSkip)
              IconButton(
                tooltip: 'Skip delivery',
                onPressed: state.isSaving ? null : () => _skip(delivery),
                icon: const Icon(Icons.event_busy_outlined),
              ),
          ],
        ),
      );
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

DeliveryWeekday _weekdayOf(DateTime value) =>
    DeliveryWeekday.values[value.weekday - 1];

String _formatDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/'
    '${value.month.toString().padLeft(2, '0')}/'
    '${value.year.toString().padLeft(4, '0')}';
