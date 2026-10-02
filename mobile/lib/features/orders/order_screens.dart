import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
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
  bool _initializedCart = false;

  CheckoutAddressSelection? _selection(
    OrderState state,
    List<CustomerAddress> addresses,
  ) {
    final current = state.checkoutAddress;
    if (current?.manualAddress != null) return current;
    if (current?.addressId != null &&
        addresses.any((address) => address.publicId == current!.addressId)) {
      return current;
    }
    final address =
        addresses.where((item) => item.isDefault).firstOrNull ??
        addresses.firstOrNull;
    if (address == null) return null;
    return CheckoutAddressSelection.saved(address.publicId);
  }

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
    final session = ref.watch(sessionControllerProvider);
    final theme = Theme.of(context);
    final isAuthenticated = session.isAuthenticated;
    // A single stack on phones and a two-column checkout on tablet/web. The
    // shared breakpoint token keeps this aligned with the rest of the app.
    final useTwoColumn =
        isAuthenticated &&
        MediaQuery.sizeOf(context).width >= DoodhBreakpoints.medium;
    final addresses = customerState.addresses
        .where((address) => address.isActive)
        .toList();
    final selection = _selection(orderState, addresses);
    final selectedId = selection?.addressId;
    final preview = orderState.preview;
    if (selection != null && orderState.checkoutAddress == null) {
      Future.microtask(
        () => ref
            .read(orderControllerProvider.notifier)
            .selectSavedAddress(selection.addressId!),
      );
    }

    const pagePadding = EdgeInsets.fromLTRB(
      DoodhSpacing.md,
      DoodhSpacing.md,
      DoodhSpacing.md,
      DoodhSpacing.xl,
    );

    Widget orderSection() => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _CheckoutSectionLabel(
          step: '1',
          title: 'Your order',
          subtitle: 'Fresh products selected for this delivery',
          trailing: DoodhChip(
            label:
                '${orderState.cart.length} '
                '${orderState.cart.length == 1 ? 'line' : 'lines'}',
            icon: Icons.shopping_basket_outlined,
          ),
        ),
        const SizedBox(height: DoodhSpacing.sm),
        ...orderState.cart.map(
          (item) => Padding(
            padding: const EdgeInsets.only(bottom: DoodhSpacing.sm),
            child: _CartItemCard(
              item: item,
              onDecrease: () => ref
                  .read(orderControllerProvider.notifier)
                  .decrementCartItem(item.product.publicId),
              onIncrease: () => ref
                  .read(orderControllerProvider.notifier)
                  .incrementCartItem(item.product.publicId),
              onRemove: () => ref
                  .read(orderControllerProvider.notifier)
                  .removeCartItem(item.product.publicId),
            ),
          ),
        ),
        const SizedBox(height: DoodhSpacing.xs),
        DoodhButton(
          label: 'Continue Shopping',
          icon: Icons.add_shopping_cart_outlined,
          variant: DoodhButtonVariant.secondary,
          expand: true,
          onPressed: () {
            ScaffoldMessenger.of(context).hideCurrentSnackBar();
            context.go('/catalogue');
          },
        ),
      ],
    );

    Widget deliverySection() => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const _CheckoutSectionLabel(
          step: '2',
          title: 'Delivery address',
          subtitle: 'Where should we deliver your order?',
        ),
        const SizedBox(height: DoodhSpacing.sm),
        if (!isAuthenticated)
          const _CheckoutLoginRequiredCard()
        else if (customerState.isLoading && addresses.isEmpty)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: DoodhSpacing.md),
            child: LinearProgressIndicator(),
          )
        else ...[
          if (addresses.isNotEmpty) ...[
            _SavedAddressCard(
              addresses: addresses,
              selectedId: selectedId,
              onChanged: (value) {
                if (value != null) {
                  ref
                      .read(orderControllerProvider.notifier)
                      .selectSavedAddress(value);
                }
              },
            ),
            const SizedBox(height: DoodhSpacing.sm),
          ],
          if (orderState.checkoutAddress?.manualAddress != null) ...[
            _ManualAddressCard(
              address: orderState.checkoutAddress!.manualAddress!,
              onEdit: _enterNewAddress,
            ),
            const SizedBox(height: DoodhSpacing.sm),
          ],
          DoodhButton(
            label: 'Enter New Address',
            icon: Icons.add_location_alt_outlined,
            variant: DoodhButtonVariant.secondary,
            expand: true,
            onPressed: _enterNewAddress,
          ),
        ],
        if (isAuthenticated && preview != null) ...[
          const SizedBox(height: DoodhSpacing.md),
          _CheckoutDeliveryInfoCard(preview: preview),
        ],
      ],
    );

    Widget summarySection() => Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const _CheckoutSectionLabel(
          step: '3',
          title: 'Order summary',
          subtitle: 'Your final total is calculated securely',
        ),
        const SizedBox(height: DoodhSpacing.sm),
        if (orderState.errorMessage != null) ...[
          DoodhErrorBanner(message: orderState.errorMessage!),
          const SizedBox(height: DoodhSpacing.sm),
        ],
        if (preview != null)
          _CheckoutOrderSummaryCard(preview: preview)
        else
          DoodhCard(
            child: Text(
              'Preview your order to see the exact, server-calculated total '
              'before you pay.',
              style: theme.textTheme.bodyMedium,
            ),
          ),
      ],
    );

    final progressStrip = _CheckoutStepStrip(
      steps: const ['Cart', 'Address', 'Pay'],
      currentIndex: selection != null ? 2 : 1,
    );

    // Sticky, backend-authoritative primary action. The wording only ever
    // reflects the current (never assumed) state of the checkout flow.
    Widget stickyBar() => DecoratedBox(
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
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              progressStrip,
              const SizedBox(height: DoodhSpacing.sm),
              if (!isAuthenticated)
                DoodhButton(
                  label: 'Login to continue',
                  icon: Icons.lock_outline,
                  expand: true,
                  onPressed: () => context.go('/login?redirectTo=/checkout'),
                )
              else if (useTwoColumn)
                Row(
                  children: [
                    Expanded(child: _StickyTotal(preview: preview)),
                    const SizedBox(width: DoodhSpacing.md),
                    SizedBox(
                      width: 300,
                      child: _primaryAction(
                        context,
                        orderState,
                        selection,
                        preview,
                      ),
                    ),
                  ],
                )
              else ...[
                _StickyTotal(preview: preview),
                const SizedBox(height: DoodhSpacing.sm),
                _primaryAction(context, orderState, selection, preview),
              ],
            ],
          ),
        ),
      ),
    );

    final isEmpty = orderState.cart.isEmpty;

    return Scaffold(
      appBar: AppBar(title: const Text('Checkout')),
      body: isEmpty
          ? EmptyStatePanel(
              title: 'Your cart is empty',
              message: 'Choose a product from the catalogue to start an order.',
              action: FilledButton.icon(
                onPressed: () {
                  ScaffoldMessenger.of(context).hideCurrentSnackBar();
                  context.go('/catalogue');
                },
                icon: const Icon(Icons.shopping_bag_outlined),
                label: const Text('Continue Shopping'),
              ),
            )
          : RefreshIndicator(
              onRefresh: () =>
                  ref.read(customerControllerProvider.notifier).load(),
              child: useTwoColumn
                  ? ListView(
                      padding: pagePadding,
                      children: [
                        Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Expanded(
                              flex: 3,
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.stretch,
                                children: [
                                  orderSection(),
                                  const SizedBox(height: DoodhSpacing.lg),
                                  deliverySection(),
                                ],
                              ),
                            ),
                            const SizedBox(width: DoodhSpacing.lg),
                            Expanded(
                              flex: 2,
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.stretch,
                                children: [summarySection()],
                              ),
                            ),
                          ],
                        ),
                      ],
                    )
                  : ListView(
                      padding: pagePadding,
                      children: [
                        orderSection(),
                        const SizedBox(height: DoodhSpacing.lg),
                        deliverySection(),
                        if (isAuthenticated) ...[
                          const SizedBox(height: DoodhSpacing.lg),
                          summarySection(),
                        ],
                      ],
                    ),
            ),
      bottomNavigationBar: isEmpty ? null : stickyBar(),
    );
  }

  /// Builds the sticky primary action for the current checkout state.
  ///
  /// - Before a server quote exists the only valid next step is a preview.
  /// - Once the authoritative quote exists the user can create the order.
  ///
  /// The label never claims success: an order is only considered placed after
  /// the backend returns it, and payment confirmation always happens later on
  /// the payment screens.
  Widget _primaryAction(
    BuildContext context,
    OrderState orderState,
    CheckoutAddressSelection? selection,
    CheckoutPreview? preview,
  ) {
    final busy = orderState.isSaving;
    if (preview == null) {
      return DoodhButton(
        label: 'Preview order',
        icon: Icons.calculate_outlined,
        busy: busy,
        expand: true,
        onPressed: selection == null || busy
            ? null
            : () => ref
                  .read(orderControllerProvider.notifier)
                  .previewFor(selection),
      );
    }
    return DoodhButton(
      label: 'Place order · ₹${preview.payableAmount.toStringAsFixed(2)}',
      icon: Icons.lock_outline,
      busy: busy,
      expand: true,
      onPressed: busy
          ? null
          : () async {
              final order = await ref
                  .read(orderControllerProvider.notifier)
                  .create(selection ?? orderState.checkoutAddress!);
              if (context.mounted && order != null) {
                context.go('/orders/${order.publicId}/payment', extra: order);
              }
            },
    );
  }

  Future<void> _enterNewAddress() async {
    final draft = await context.push<AddressDraft>('/checkout/address/new');
    if (!mounted || draft == null) return;

    final checkoutDraft = CheckoutAddressDraft.fromCustomerDraft(draft);
    final save = await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Save this address to your profile?'),
        content: const Text(
          'You can use this address just for this order, or save it for future deliveries.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text("Don't Save"),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Save Address'),
          ),
        ],
      ),
    );
    if (!mounted) return;

    if (save != true) {
      ref
          .read(orderControllerProvider.notifier)
          .selectManualAddress(checkoutDraft);
      return;
    }

    final label = await _askForAddressLabel();
    if (!mounted || label == null) return;
    final previousAddressIds = ref
        .read(customerControllerProvider)
        .addresses
        .map((address) => address.publicId)
        .toSet();
    final profileDraft = AddressDraft(
      label: label,
      addressLine1: checkoutDraft.addressLine1,
      addressLine2: checkoutDraft.addressLine2,
      locality: checkoutDraft.locality,
      city: checkoutDraft.city,
      state: checkoutDraft.state,
      pinCode: checkoutDraft.pinCode,
      landmark: checkoutDraft.landmark,
      deliveryInstructions: checkoutDraft.deliveryInstructions,
      contactName: checkoutDraft.contactName,
      contactMobile: checkoutDraft.contactMobile,
      latitude: checkoutDraft.latitude,
      longitude: checkoutDraft.longitude,
      isDefault: false,
    );
    final saved = await ref
        .read(customerControllerProvider.notifier)
        .saveAddress(profileDraft);
    if (!mounted || !saved) return;

    final savedAddress = ref
        .read(customerControllerProvider)
        .addresses
        .where((address) => !previousAddressIds.contains(address.publicId))
        .firstOrNull;
    if (savedAddress != null) {
      ref
          .read(orderControllerProvider.notifier)
          .selectSavedAddress(savedAddress.publicId);
    } else {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('The saved address could not be selected. Try again.'),
        ),
      );
    }
  }

  Future<String?> _askForAddressLabel() {
    return showDialog<String>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) => const _AddressLabelDialog(),
    );
  }
}

/// Collects a profile label for a newly saved address.
///
/// The dialog owns its own [TextEditingController] so the controller is
/// disposed only when the dialog route itself is removed from the tree.
/// Disposing it as soon as `showDialog` resolves is unsafe because the route
/// keeps listening to the controller during its exit transition.
class _AddressLabelDialog extends StatefulWidget {
  const _AddressLabelDialog();

  @override
  State<_AddressLabelDialog> createState() => _AddressLabelDialogState();
}

class _AddressLabelDialogState extends State<_AddressLabelDialog> {
  final _controller = TextEditingController();
  final _formKey = GlobalKey<FormState>();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _submit() {
    if (_formKey.currentState?.validate() ?? false) {
      Navigator.of(context).pop(_controller.text.trim());
    }
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Name this address'),
      content: Form(
        key: _formKey,
        child: TextFormField(
          controller: _controller,
          autofocus: true,
          decoration: const InputDecoration(labelText: 'Label'),
          textCapitalization: TextCapitalization.words,
          textInputAction: TextInputAction.done,
          onFieldSubmitted: (_) => _submit(),
          validator: (value) =>
              value == null || value.trim().isEmpty ? 'Enter a label' : null,
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _submit, child: const Text('Continue')),
      ],
    );
  }
}

class _CartItemCard extends StatelessWidget {
  const _CartItemCard({
    required this.item,
    required this.onDecrease,
    required this.onIncrease,
    required this.onRemove,
  });

  final OrderCartItem item;
  final VoidCallback onDecrease;
  final VoidCallback onIncrease;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final product = item.product;
    final quantity = formatQuantity(item.quantity);
    final lineTotal = product.price * item.quantity;
    return Semantics(
      container: true,
      label:
          '${product.name}, $quantity ${product.unitLabel}, '
          '₹${product.price.toStringAsFixed(2)} each, '
          'estimated line total ₹${lineTotal.toStringAsFixed(2)}',
      child: Card(
        clipBehavior: Clip.antiAlias,
        child: Padding(
          padding: const EdgeInsets.all(DoodhSpacing.md),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(
                    width: 76,
                    child: DoodhProductImage(
                      imageUrl: product.usableImageUrl,
                      icon: doodhProductUnitIcon(product.unitOfMeasure),
                      height: 76,
                      borderRadius: BorderRadius.circular(DoodhRadii.mdValue),
                      semanticLabel: '${product.name} product image',
                    ),
                  ),
                  const SizedBox(width: DoodhSpacing.md),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Expanded(
                              child: Text(
                                product.name,
                                style: theme.textTheme.titleMedium,
                              ),
                            ),
                            IconButton(
                              tooltip: 'Remove ${product.name}',
                              onPressed: onRemove,
                              visualDensity: VisualDensity.compact,
                              icon: const Icon(Icons.delete_outline, size: 20),
                            ),
                          ],
                        ),
                        Text(
                          '₹${product.price.toStringAsFixed(2)} per '
                          '${product.unitLabel}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: DoodhColors.muted,
                          ),
                        ),
                        Text(
                          '$quantity ${product.unitLabel} · '
                          '₹${product.price.toStringAsFixed(2)} each',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: DoodhColors.muted,
                          ),
                        ),
                        const SizedBox(height: DoodhSpacing.sm),
                        Text(
                          'Estimated line total',
                          style: theme.textTheme.labelSmall?.copyWith(
                            color: DoodhColors.muted,
                          ),
                        ),
                        Text(
                          '₹${lineTotal.toStringAsFixed(2)}',
                          style: theme.textTheme.titleMedium?.copyWith(
                            fontWeight: FontWeight.w800,
                            color: DoodhColors.tealDark,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const Divider(height: DoodhSpacing.lg),
              Row(
                children: [
                  // The label yields space to the fixed-width stepper controls
                  // so the row never overflows on compact phones.
                  Expanded(
                    child: Text(
                      'Quantity',
                      style: theme.textTheme.labelLarge,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  SizedBox(
                    width: 48,
                    height: 48,
                    child: IconButton.outlined(
                      tooltip: 'Decrease quantity',
                      onPressed: onDecrease,
                      icon: const Icon(Icons.remove, size: 18),
                    ),
                  ),
                  SizedBox(
                    width: 58,
                    child: Text(
                      quantity,
                      textAlign: TextAlign.center,
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ),
                  SizedBox(
                    width: 48,
                    height: 48,
                    child: IconButton.outlined(
                      tooltip: 'Increase quantity',
                      onPressed: onIncrease,
                      icon: const Icon(Icons.add, size: 18),
                    ),
                  ),
                  const SizedBox(width: DoodhSpacing.xs),
                  Text(product.unitLabel),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ManualAddressCard extends StatelessWidget {
  const _ManualAddressCard({required this.address, required this.onEdit});

  final CheckoutAddressDraft address;
  final VoidCallback onEdit;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Semantics(
      container: true,
      label: 'One-time delivery address for ${address.contactName}',
      child: Card(
        color: theme.colorScheme.surfaceContainerHighest,
        child: Padding(
          padding: DoodhSpacing.cardPadding,
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(
                Icons.location_on_outlined,
                color: theme.colorScheme.primary,
              ),
              const SizedBox(width: DoodhSpacing.md),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      address.label?.trim().isNotEmpty == true
                          ? address.label!.trim()
                          : 'One-time address',
                      style: theme.textTheme.titleMedium,
                    ),
                    const SizedBox(height: DoodhSpacing.xs),
                    Text(
                      '${address.addressLine1}, ${address.locality}, ${address.city}\n'
                      '${address.state} - ${address.pinCode}',
                    ),
                    const SizedBox(height: DoodhSpacing.xs),
                    Text(
                      'Contact: ${address.contactName} · ${address.contactMobile}',
                      style: theme.textTheme.bodySmall,
                    ),
                  ],
                ),
              ),
              IconButton(
                tooltip: 'Edit address',
                onPressed: onEdit,
                icon: const Icon(Icons.edit_outlined),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Shown to guests (and unauthenticated users) at the account-dependent
/// checkout boundary. The in-memory cart is left untouched so the exact items
/// carry over after sign in, and the [redirectTo] return-intent brings the
/// user straight back to this screen once authenticated.
class _CheckoutLoginRequiredCard extends StatelessWidget {
  const _CheckoutLoginRequiredCard();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      color: DoodhColors.mint,
      child: Padding(
        padding: DoodhSpacing.cardPadding,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.lock_outline, color: DoodhColors.tealDark),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    'Login required to continue to checkout.',
                    style: theme.textTheme.titleMedium,
                  ),
                ),
              ],
            ),
            const SizedBox(height: DoodhSpacing.sm),
            Text(
              'Sign in to choose a delivery address, preview your order and pay. '
              'Your cart is saved on this device and will still be here after you '
              'sign in.',
              style: theme.textTheme.bodyMedium,
            ),
            const SizedBox(height: DoodhSpacing.md),
            DoodhButton(
              label: 'Login',
              icon: Icons.login,
              expand: true,
              onPressed: () => context.go('/login?redirectTo=/checkout'),
            ),
          ],
        ),
      ),
    );
  }
}

/// The authoritative, server-calculated order summary. Every value shown here
/// comes straight from the backend quote — the UI never recomputes pricing.
class _CheckoutOrderSummaryCard extends StatelessWidget {
  const _CheckoutOrderSummaryCard({required this.preview});

  final CheckoutPreview preview;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Semantics(
      container: true,
      label:
          'Authoritative checkout quote for ${preview.addressLabel}. '
          'Final amount payable ₹${preview.payableAmount.toStringAsFixed(2)}',
      child: Card(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.all(DoodhSpacing.md),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Row(
                    children: [
                      Icon(
                        Icons.receipt_long_outlined,
                        color: theme.colorScheme.primary,
                      ),
                      const SizedBox(width: DoodhSpacing.sm),
                      Expanded(
                        child: Semantics(
                          header: true,
                          child: Text(
                            'Server quote',
                            style: theme.textTheme.titleMedium,
                          ),
                        ),
                      ),
                      const DoodhChip(
                        label: 'Server verified',
                        tone: DoodhTone.success,
                        icon: Icons.verified_outlined,
                      ),
                    ],
                  ),
                  const SizedBox(height: DoodhSpacing.md),
                  ...preview.items.map(
                    (item) => Padding(
                      padding: const EdgeInsets.symmetric(vertical: 3),
                      child: Row(
                        mainAxisAlignment: MainAxisAlignment.spaceBetween,
                        children: [
                          Expanded(
                            child: Text(
                              '${item.productName} × '
                              '${formatQuantity(item.quantity)}',
                            ),
                          ),
                          Text('₹${item.lineTotal.toStringAsFixed(2)}'),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ),
            const Divider(height: 1),
            Padding(
              padding: const EdgeInsets.all(DoodhSpacing.md),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _AmountRow(
                    label: 'Subtotal',
                    amount: preview.subtotal,
                    style: theme.textTheme.bodyLarge,
                  ),
                  if (preview.discountAmount > 0)
                    _AmountRow(
                      label: 'Discount',
                      amount: -preview.discountAmount,
                      style: theme.textTheme.bodyLarge,
                    ),
                  // Server-applied charges (taxes/fees): rendered exactly as the
                  // backend preview computed them — never recalculated here.
                  ...preview.charges.map(
                    (charge) => _ChargeAmountRow(charge: charge),
                  ),
                  const Divider(height: DoodhSpacing.lg),
                  _AmountRow(
                    label: 'Total payable',
                    amount: preview.payableAmount,
                    emphasized: true,
                    style: theme.textTheme.titleLarge?.copyWith(
                      color: DoodhColors.tealDark,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Delivery information that the backend quote actually provides. Nothing is
/// invented here — no delivery slots, guarantees or timing promises are shown
/// because the quote does not carry them.
class _CheckoutDeliveryInfoCard extends StatelessWidget {
  const _CheckoutDeliveryInfoCard({required this.preview});

  final CheckoutPreview preview;

  @override
  Widget build(BuildContext context) {
    final line2 = preview.addressLine2?.trim();
    final address = [
      preview.addressLine1,
      if (line2 != null && line2.isNotEmpty) line2,
      preview.locality,
      '${preview.city}, ${preview.state} - ${preview.pinCode}',
    ].join(', ');
    return DoodhSectionCard(
      icon: Icons.local_shipping_outlined,
      title: 'Delivery information',
      subtitle: 'Confirmed for this order',
      children: [
        DoodhKeyValueRow(label: 'Address', value: preview.addressLabel),
        DoodhKeyValueRow(
          label: 'Delivering to',
          value: address,
          stackBelow: 300,
        ),
        DoodhKeyValueRow(
          label: 'Contact',
          value: '${preview.contactName} · ${preview.contactMobile}',
          stackBelow: 300,
        ),
        DoodhKeyValueRow(
          label: 'Fulfilling branch',
          value: preview.branchName,
          stackBelow: 300,
        ),
        DoodhKeyValueRow(
          label: 'Distance',
          value: '${preview.distanceKm.toStringAsFixed(1)} km',
          stackBelow: 300,
        ),
      ],
    );
  }
}

/// One server-applied checkout charge: description (or type fallback) as the
/// label with the configured percentage, and the server-computed amount.
class _ChargeAmountRow extends StatelessWidget {
  const _ChargeAmountRow({required this.charge});

  final OrderChargeLine charge;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Expanded(
            child: Text(
              '${charge.displayLabel} (${charge.formattedPercentage})',
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.bodyLarge,
            ),
          ),
          Text(charge.formattedAmount, style: theme.textTheme.bodyLarge),
        ],
      ),
    );
  }
}

class _AmountRow extends StatelessWidget {
  const _AmountRow({
    required this.label,
    required this.amount,
    this.emphasized = false,
    this.style,
  });
  final String label;
  final double amount;
  final bool emphasized;

  /// Optional text style override. When omitted the legacy emphasis styling is
  /// kept so the order detail / staff inspection screens are unaffected.
  final TextStyle? style;

  @override
  Widget build(BuildContext context) {
    final resolved =
        style ??
        (emphasized ? const TextStyle(fontWeight: FontWeight.bold) : null);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Flexible(
            child: Text(
              label,
              overflow: TextOverflow.ellipsis,
              style: resolved,
            ),
          ),
          Text('₹${amount.toStringAsFixed(2)}', style: resolved),
        ],
      ),
    );
  }
}

/// Compact, always-visible checkout progress strip. State is conveyed with a
/// label ("complete" / "current" / "upcoming") as well as colour, so the
/// meaning never depends on colour alone.
class _CheckoutStepStrip extends StatelessWidget {
  const _CheckoutStepStrip({required this.steps, required this.currentIndex});

  final List<String> steps;
  final int currentIndex;

  @override
  Widget build(BuildContext context) {
    final parts = <String>[
      for (var i = 0; i < steps.length; i++)
        '${steps[i]} '
            '${i < currentIndex
                ? 'complete'
                : i == currentIndex
                ? 'current'
                : 'upcoming'}',
    ];
    return Semantics(
      container: true,
      label: 'Checkout progress. ${parts.join(', ')}.',
      child: ExcludeSemantics(
        child: Wrap(
          spacing: DoodhSpacing.sm,
          runSpacing: DoodhSpacing.xs,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            for (var i = 0; i < steps.length; i++)
              _StepPill(
                label: steps[i],
                done: i < currentIndex,
                current: i == currentIndex,
              ),
          ],
        ),
      ),
    );
  }
}

class _StepPill extends StatelessWidget {
  const _StepPill({
    required this.label,
    required this.done,
    required this.current,
  });

  final String label;
  final bool done;
  final bool current;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final Color background;
    final Color foreground;
    final IconData icon;
    if (done) {
      background = DoodhColors.mint;
      foreground = DoodhColors.tealDark;
      icon = Icons.check_circle_outline;
    } else if (current) {
      background = DoodhColors.tealDark;
      foreground = Colors.white;
      icon = Icons.radio_button_checked;
    } else {
      background = DoodhColors.neutralSurface;
      foreground = DoodhColors.muted;
      icon = Icons.radio_button_unchecked;
    }
    return DecoratedBox(
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(DoodhRadii.pillValue),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(
          horizontal: 10,
          vertical: DoodhSpacing.xs + 1,
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 14, color: foreground),
            const SizedBox(width: DoodhSpacing.xs),
            Text(
              label,
              style: theme.textTheme.labelSmall?.copyWith(
                color: foreground,
                fontWeight: FontWeight.w700,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Sticky total readout. It stays honest while no server quote exists yet.
class _StickyTotal extends StatelessWidget {
  const _StickyTotal({required this.preview});

  final CheckoutPreview? preview;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final value = preview;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          'Total payable',
          style: theme.textTheme.labelSmall?.copyWith(color: DoodhColors.muted),
        ),
        Text(
          value == null
              ? 'Awaiting preview'
              : '₹${value.payableAmount.toStringAsFixed(2)}',
          style: value == null
              ? theme.textTheme.titleMedium?.copyWith(
                  color: DoodhColors.muted,
                  fontWeight: FontWeight.w700,
                )
              : theme.textTheme.titleLarge?.copyWith(
                  color: DoodhColors.tealDark,
                  fontWeight: FontWeight.w800,
                ),
        ),
      ],
    );
  }
}

class _CheckoutSectionLabel extends StatelessWidget {
  const _CheckoutSectionLabel({
    required this.step,
    required this.title,
    required this.subtitle,
    this.trailing,
  });
  final String step;
  final String title;
  final String subtitle;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => Semantics(
    container: true,
    header: true,
    label: 'Step $step: $title. $subtitle',
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 30,
          height: 30,
          alignment: Alignment.center,
          decoration: const BoxDecoration(
            color: DoodhColors.tealDark,
            shape: BoxShape.circle,
          ),
          child: Text(
            step,
            style: const TextStyle(
              color: Colors.white,
              fontWeight: FontWeight.w800,
              fontSize: 14,
            ),
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: Theme.of(context).textTheme.titleMedium),
              Text(
                subtitle,
                style: Theme.of(context).textTheme.bodySmall
                    ?.copyWith(color: DoodhColors.muted),
              ),
            ],
          ),
        ),
        if (trailing != null) ...[
          const SizedBox(width: DoodhSpacing.sm),
          trailing!,
        ],
      ],
    ),
  );
}

/// Saved-address selector. The chosen address is shown in full so the
/// destination is obvious before the customer pays; the dropdown keeps the
/// exact existing selection behaviour (including the unsaved manual address
/// path, which is collapsed into [CheckoutAddressSelection] upstream).
class _SavedAddressCard extends StatelessWidget {
  const _SavedAddressCard({
    required this.addresses,
    required this.selectedId,
    required this.onChanged,
  });

  final List<CustomerAddress> addresses;
  final String? selectedId;
  final ValueChanged<String?> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final selected = addresses
        .where((address) => address.publicId == selectedId)
        .firstOrNull;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(DoodhSpacing.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (selected != null) ...[
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(
                    Icons.location_on_outlined,
                    color: theme.colorScheme.primary,
                  ),
                  const SizedBox(width: DoodhSpacing.sm),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            Flexible(
                              child: Text(
                                selected.label,
                                style: theme.textTheme.titleMedium,
                              ),
                            ),
                            if (selected.isDefault) ...[
                              const SizedBox(width: DoodhSpacing.xs),
                              const DoodhChip(
                                label: 'Default',
                                tone: DoodhTone.info,
                                icon: Icons.star_outline,
                              ),
                            ],
                          ],
                        ),
                        const SizedBox(height: DoodhSpacing.xs),
                        Text(
                          '${selected.addressLine1}, ${selected.locality}, '
                          '${selected.city}\n'
                          '${selected.state} - ${selected.pinCode}',
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: DoodhColors.muted,
                          ),
                        ),
                        const SizedBox(height: DoodhSpacing.xs),
                        Text(
                          'Contact: ${selected.contactName} · '
                          '${selected.contactMobile}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: DoodhColors.muted,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: DoodhSpacing.sm),
              const Divider(height: 1),
            ],
            DropdownButtonFormField<String>(
              initialValue: selectedId,
              isExpanded: true,
              decoration: const InputDecoration(
                labelText: 'Saved address',
                helperText: 'Choose where this order should arrive',
                border: InputBorder.none,
                prefixIcon: Icon(Icons.location_on_outlined),
              ),
              items: addresses
                  .map(
                    (address) => DropdownMenuItem(
                      value: address.publicId,
                      child: Text(
                        '${address.label} — ${address.city}',
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  )
                  .toList(growable: false),
              onChanged: onChanged,
            ),
          ],
        ),
      ),
    );
  }
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
    return CustomerShell(
      currentPath: '/orders',
      title: 'My orders',
      child: state.isLoading && state.orders.isEmpty
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
              child: DoodhPage(
                child: ListView.separated(
                  physics: const AlwaysScrollableScrollPhysics(),
                  padding: const EdgeInsets.only(bottom: DoodhSpacing.xl),
                  itemCount: state.orders.length,
                  separatorBuilder: (_, _) =>
                      const SizedBox(height: DoodhSpacing.md),
                  itemBuilder: (context, index) {
                    final order = state.orders[index];
                    return _CustomerOrderCard(
                      order: order,
                      onTap: () => context.push('/orders/${order.publicId}'),
                    );
                  },
                ),
              ),
            ),
    );
  }
}

class _CustomerOrderCard extends StatelessWidget {
  const _CustomerOrderCard({required this.order, required this.onTap});

  final OrderSummary order;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final visibleItems = order.items.take(3).toList(growable: false);
    return DoodhCard(
      semanticLabel:
          '${order.orderNumber}, order status ${order.status}, '
          'payment status ${_paymentLabel(order)}, '
          'delivery status ${_deliveryLabel(order)}, ${order.formattedTotal}',
      onTap: onTap,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      order.orderNumber,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    const SizedBox(height: DoodhSpacing.xs),
                    Text(
                      formatOrderDate(order.createdAt),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: DoodhColors.muted,
                      ),
                    ),
                  ],
                ),
              ),
              Text(
                order.formattedTotal,
                maxLines: 1,
                style: theme.textTheme.titleLarge?.copyWith(
                  fontSize: 18,
                  fontWeight: FontWeight.w800,
                ),
              ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.md),
          Wrap(
            spacing: DoodhSpacing.xs,
            runSpacing: DoodhSpacing.xs,
            children: [
              _LabeledStatusPill(label: 'Order', value: order.status),
              _LabeledStatusPill(label: 'Payment', value: _paymentLabel(order)),
              _LabeledStatusPill(
                label: 'Delivery',
                value: _deliveryLabel(order),
              ),
            ],
          ),
          if (visibleItems.isNotEmpty) ...[
            const SizedBox(height: DoodhSpacing.md),
            Row(
              children: [
                for (final item in visibleItems) ...[
                  SizedBox(
                    width: 52,
                    child: DoodhProductImage(
                      height: 52,
                      icon: doodhProductUnitIcon(item.unitOfMeasure),
                      semanticLabel: '${item.productName} product image',
                      borderRadius: BorderRadius.circular(DoodhRadii.smValue),
                    ),
                  ),
                  const SizedBox(width: DoodhSpacing.xs),
                ],
                Expanded(
                  child: Text(
                    order.itemSummary,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.bodyMedium,
                  ),
                ),
              ],
            ),
          ],
          const Divider(height: DoodhSpacing.lg),
          Row(
            children: [
              const Icon(Icons.location_on_outlined, size: 18),
              const SizedBox(width: DoodhSpacing.xs),
              Expanded(
                child: Text(
                  '${order.addressLabel}, ${order.city}',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              const SizedBox(width: DoodhSpacing.sm),
              Text(
                'View details',
                style: theme.textTheme.labelLarge?.copyWith(
                  color: theme.colorScheme.primary,
                ),
              ),
              const Icon(Icons.chevron_right),
            ],
          ),
        ],
      ),
    );
  }
}

class _LabeledStatusPill extends StatelessWidget {
  const _LabeledStatusPill({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) =>
      DoodhStatusPill(label: '$label: $value', tone: _statusTone(value));
}

String _paymentLabel(OrderSummary order) {
  final status = order.paymentStatus?.trim();
  if (status != null && status.isNotEmpty) return status;
  return order.status.toLowerCase() == 'pendingpayment'
      ? 'Pending'
      : 'Not available';
}

String _deliveryLabel(OrderSummary order) {
  final status = order.deliveryStatus?.trim();
  return status == null || status.isEmpty ? 'Not scheduled' : status;
}

DoodhStatusTone _statusTone(String status) {
  final normalized = status.toLowerCase();
  if (normalized.contains('cancel') ||
      normalized.contains('fail') ||
      normalized.contains('reject')) {
    return DoodhStatusTone.error;
  }
  if (normalized.contains('pending') ||
      normalized.contains('initiated') ||
      normalized.contains('outfordelivery') ||
      normalized.contains('out for delivery') ||
      normalized.contains('arrived')) {
    return DoodhStatusTone.warning;
  }
  if (normalized.contains('success') ||
      normalized.contains('deliver') ||
      normalized.contains('confirm') ||
      normalized.contains('complete')) {
    return DoodhStatusTone.success;
  }
  return DoodhStatusTone.neutral;
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
          : RefreshIndicator(
              onRefresh: () => ref
                  .read(orderControllerProvider.notifier)
                  .loadOrder(widget.orderId),
              child: DoodhPage(
                child: ListView(
                  physics: const AlwaysScrollableScrollPhysics(),
                  padding: const EdgeInsets.only(bottom: DoodhSpacing.xl),
                  children: [
                    _OrderTrustHeader(order: order),
                    const SizedBox(height: DoodhSpacing.md),
                    DoodhResponsive(
                      builder: (context, size) {
                        final primary = Column(
                          children: [
                            _OrderItemsSection(order: order),
                            const SizedBox(height: DoodhSpacing.md),
                            _OrderPaymentSection(order: order),
                          ],
                        );
                        final secondary = Column(
                          children: [
                            _OrderDeliverySection(order: order),
                            const SizedBox(height: DoodhSpacing.md),
                            _OrderSupportSection(order: order),
                            if (state.errorMessage != null) ...[
                              const SizedBox(height: DoodhSpacing.md),
                              DoodhInfoBanner(
                                message: state.errorMessage!,
                                icon: Icons.error_outline,
                              ),
                            ],
                            if (order.status == 'PendingPayment') ...[
                              const SizedBox(height: DoodhSpacing.md),
                              SizedBox(
                                width: double.infinity,
                                child: FilledButton.icon(
                                  onPressed: () => context.push(
                                    '/orders/${order.publicId}/payment',
                                    extra: order,
                                  ),
                                  icon: const Icon(Icons.payments_outlined),
                                  label: Text('Pay ${order.formattedTotal}'),
                                ),
                              ),
                            ],
                            if (order.canCancel) ...[
                              const SizedBox(height: DoodhSpacing.sm),
                              SizedBox(
                                width: double.infinity,
                                child: OutlinedButton.icon(
                                  onPressed: state.isSaving
                                      ? null
                                      : () => _cancel(order),
                                  icon: const Icon(Icons.cancel_outlined),
                                  label: const Text('Cancel order'),
                                ),
                              ),
                            ],
                          ],
                        );
                        if (size == DoodhWindowSize.compact) {
                          return Column(
                            children: [
                              primary,
                              const SizedBox(height: DoodhSpacing.md),
                              secondary,
                            ],
                          );
                        }
                        return Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Expanded(flex: 3, child: primary),
                            const SizedBox(width: DoodhSpacing.lg),
                            Expanded(flex: 2, child: secondary),
                          ],
                        );
                      },
                    ),
                  ],
                ),
              ),
            ),
    );
  }
}

class _OrderTrustHeader extends StatelessWidget {
  const _OrderTrustHeader({required this.order});
  final OrderSummary order;

  @override
  Widget build(BuildContext context) => DoodhSectionCard(
    title: order.orderNumber,
    subtitle: formatOrderDate(order.createdAt),
    icon: Icons.receipt_long_outlined,
    children: [
      Wrap(
        spacing: DoodhSpacing.xs,
        runSpacing: DoodhSpacing.xs,
        children: [
          _LabeledStatusPill(label: 'Order', value: order.status),
          _LabeledStatusPill(label: 'Payment', value: _paymentLabel(order)),
          _LabeledStatusPill(label: 'Delivery', value: _deliveryLabel(order)),
        ],
      ),
      const SizedBox(height: DoodhSpacing.md),
      DoodhInfoTile(
        icon: Icons.location_on_outlined,
        title: 'Delivery destination',
        subtitle: '${order.addressLabel}, ${order.city}',
      ),
    ],
  );
}

class _OrderItemsSection extends StatelessWidget {
  const _OrderItemsSection({required this.order});
  final OrderSummary order;

  @override
  Widget build(BuildContext context) => DoodhSectionCard(
    title: 'Your items',
    subtitle: '${order.items.length} products in this order',
    icon: Icons.shopping_bag_outlined,
    children: [
      for (final item in order.items) _OrderItemRow(item: item),
      const Divider(height: DoodhSpacing.lg),
      _AmountRow(label: 'Subtotal', amount: order.subtotal),
      if (order.discountAmount > 0)
        _AmountRow(label: 'Discount', amount: -order.discountAmount),
      // Frozen checkout snapshot — these rows come from the order itself and
      // never from the current Tax & Charges master.
      for (final charge in order.charges)
        _AmountRow(
          label: '${charge.displayLabel} (${charge.formattedPercentage})',
          amount: charge.amount,
        ),
      _AmountRow(
        label: 'Total paid / payable',
        amount: order.payableAmount,
        emphasized: true,
      ),
    ],
  );
}

class _OrderItemRow extends StatelessWidget {
  const _OrderItemRow({required this.item});
  final OrderItem item;

  @override
  Widget build(BuildContext context) => Semantics(
    container: true,
    label:
        '${item.productName}, ${formatQuantity(item.quantity)} ${item.unitOfMeasure}, '
        'unit price ₹${item.unitPrice.toStringAsFixed(2)}, '
        'line amount ₹${item.lineTotal.toStringAsFixed(2)}',
    child: Padding(
      padding: const EdgeInsets.symmetric(vertical: DoodhSpacing.sm),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 64,
            height: 88,
            child: DoodhProductImage(
              height: 88,
              icon: doodhProductUnitIcon(item.unitOfMeasure),
              semanticLabel: '${item.productName} product image',
              borderRadius: BorderRadius.circular(DoodhRadii.smValue),
            ),
          ),
          const SizedBox(width: DoodhSpacing.md),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  item.productName,
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                const SizedBox(height: DoodhSpacing.xs),
                Text('${formatQuantity(item.quantity)} ${item.unitOfMeasure}'),
                Text(
                  '₹${item.unitPrice.toStringAsFixed(2)} per ${item.unitOfMeasure}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ),
          Text(
            '₹${item.lineTotal.toStringAsFixed(2)}',
            style: Theme.of(context).textTheme.titleSmall
                ?.copyWith(fontWeight: FontWeight.w800),
          ),
        ],
      ),
    ),
  );
}

class _OrderPaymentSection extends StatelessWidget {
  const _OrderPaymentSection({required this.order});
  final OrderSummary order;

  @override
  Widget build(BuildContext context) => DoodhSectionCard(
    title: 'Payment',
    subtitle: 'Payment is shown separately from order fulfilment',
    icon: Icons.payments_outlined,
    trailing: _LabeledStatusPill(label: 'Payment', value: _paymentLabel(order)),
    children: [
      DoodhKeyValueRow(label: 'Amount', value: order.formattedTotal),
      if (order.gatewayPaymentId != null)
        DoodhKeyValueRow(
          label: 'Razorpay payment ID',
          value: order.gatewayPaymentId!,
        ),
    ],
  );
}

class _OrderDeliverySection extends StatelessWidget {
  const _OrderDeliverySection({required this.order});
  final OrderSummary order;

  @override
  Widget build(BuildContext context) => DoodhSectionCard(
    title: 'Delivery',
    subtitle: order.deliveryReferenceNumber ?? 'Delivery not scheduled yet',
    icon: Icons.local_shipping_outlined,
    trailing: _LabeledStatusPill(
      label: 'Delivery',
      value: _deliveryLabel(order),
    ),
    children: [
      order.deliveryPublicId == null
          ? const DoodhInfoBanner(
              message: 'Tracking will appear after a delivery is created.',
              icon: Icons.schedule_outlined,
            )
          : DoodhActionTile(
              icon: Icons.route_outlined,
              title: 'Track delivery',
              subtitle:
                  'View progress, delivery partner, OTP and support options',
              onTap: () =>
                  context.push('/deliveries/${order.deliveryPublicId}'),
            ),
    ],
  );
}

class _OrderSupportSection extends StatelessWidget {
  const _OrderSupportSection({required this.order});
  final OrderSummary order;

  @override
  Widget build(BuildContext context) => DoodhSectionCard(
    title: 'Support',
    subtitle: 'Requests use backend-confirmed eligibility and status',
    icon: Icons.support_agent_outlined,
    children: [
      if (order.deliveryPublicId != null)
        DoodhActionTile(
          icon: Icons.assignment_return_outlined,
          title: 'Check refund or replacement availability',
          subtitle: 'Open this delivery’s request workflow',
          onTap: () => context.push(
            '/deliveries/${order.deliveryPublicId}/refund-replacement',
          ),
        ),
      DoodhActionTile(
        icon: Icons.history_outlined,
        title: 'Your refund or replacement requests',
        subtitle: 'View request status and history',
        onTap: () => context.push('/refund-replacements'),
      ),
    ],
  );
}

class StaffOrderInspectionScreen extends ConsumerStatefulWidget {
  const StaffOrderInspectionScreen({super.key, required this.orderId});

  final String orderId;

  @override
  ConsumerState<StaffOrderInspectionScreen> createState() =>
      _StaffOrderInspectionScreenState();
}

class _StaffOrderInspectionScreenState
    extends ConsumerState<StaffOrderInspectionScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(orderControllerProvider.notifier)
          .loadStaffOrder(widget.orderId),
    );
  }

  Future<void> _load() =>
      ref.read(orderControllerProvider.notifier).loadStaffOrder(widget.orderId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(orderControllerProvider);
    final order = state.selectedOrder?.publicId == widget.orderId
        ? state.selectedOrder
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Order inspection')),
      body: state.isLoading && order == null
          ? const LoadingStatePanel(message: 'Loading order...')
          : order == null
          ? ErrorStatePanel(
              message: state.errorMessage ?? 'Order could not be loaded.',
              onRetry: _load,
            )
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              order.orderNumber,
                              style: Theme.of(context).textTheme.headlineSmall,
                            ),
                            const SizedBox(height: 4),
                            Text(formatOrderDate(order.createdAt)),
                          ],
                        ),
                      ),
                      _OrderStatusPill(status: order.status),
                    ],
                  ),
                  const SizedBox(height: 16),
                  Card(
                    color: DoodhColors.mint,
                    child: ListTile(
                      leading: const Icon(
                        Icons.local_shipping_outlined,
                        color: DoodhColors.tealDark,
                      ),
                      title: Text('Branch: ${order.branchName}'),
                      subtitle: Text('${order.addressLabel}, ${order.city}'),
                    ),
                  ),
                  const SizedBox(height: 16),
                  DoodhSectionHeader(title: 'Order items'),
                  const SizedBox(height: 8),
                  Card(
                    child: Padding(
                      padding: DoodhSpacing.cardPadding,
                      child: Column(
                        children: [
                          ...order.items.map(
                            (item) => Padding(
                              padding: const EdgeInsets.symmetric(vertical: 6),
                              child: Row(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  const Icon(Icons.local_drink_outlined),
                                  const SizedBox(width: 10),
                                  Expanded(
                                    child: Text(
                                      '${item.productName}\n${formatQuantity(item.quantity)}',
                                    ),
                                  ),
                                  Text('₹${item.lineTotal.toStringAsFixed(2)}'),
                                ],
                              ),
                            ),
                          ),
                          const Divider(height: 24),
                          _AmountRow(label: 'Subtotal', amount: order.subtotal),
                          if (order.discountAmount > 0)
                            _AmountRow(
                              label: 'Discount',
                              amount: -order.discountAmount,
                            ),
                          for (final charge in order.charges)
                            _AmountRow(
                              label:
                                  '${charge.displayLabel} (${charge.formattedPercentage})',
                              amount: charge.amount,
                            ),
                          _AmountRow(
                            label: 'Total',
                            amount: order.payableAmount,
                            emphasized: true,
                          ),
                        ],
                      ),
                    ),
                  ),
                  if (order.paymentStatus != null) ...[
                    const SizedBox(height: 16),
                    DoodhSectionHeader(title: 'Payment status'),
                    Card(
                      child: ListTile(
                        leading: const Icon(Icons.payment_outlined),
                        title: Text(order.paymentStatus!),
                        subtitle: Text(
                          order.gatewayPaymentId ?? 'Payment linked to order',
                        ),
                      ),
                    ),
                  ],
                  if (order.deliveryPublicId != null) ...[
                    const SizedBox(height: 16),
                    DoodhSectionHeader(title: 'Delivery'),
                    Card(
                      child: ListTile(
                        leading: const Icon(Icons.location_on_outlined),
                        title: Text(
                          order.deliveryReferenceNumber ?? 'Linked delivery',
                        ),
                        subtitle: Text(
                          order.deliveryStatus ?? 'Delivery linked to order',
                        ),
                      ),
                    ),
                  ],
                ],
              ),
            ),
    );
  }
}

class _OrderStatusPill extends StatelessWidget {
  const _OrderStatusPill({required this.status});
  final String status;

  @override
  Widget build(BuildContext context) {
    final normalized = status.toLowerCase();
    final tone = normalized.contains('cancel') || normalized.contains('fail')
        ? DoodhStatusTone.error
        : normalized.contains('pending')
        ? DoodhStatusTone.warning
        : normalized.contains('deliver') || normalized.contains('confirm')
        ? DoodhStatusTone.success
        : DoodhStatusTone.neutral;
    return DoodhStatusPill(label: status, tone: tone);
  }
}

extension _IterableFirstOrNull<T> on Iterable<T> {
  T? get firstOrNull => isEmpty ? null : first;
}
