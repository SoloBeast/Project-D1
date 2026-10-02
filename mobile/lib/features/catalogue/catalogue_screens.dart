import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/media_url.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/setup/charge_controller.dart';
import 'package:doodh_direct_mobile/features/setup/charge_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';

import 'catalogue_controller.dart';
import 'catalogue_models.dart';
import 'catalogue_product_card.dart';
import 'catalogue_repository.dart';

class ProductCatalogueScreen extends ConsumerStatefulWidget {
  const ProductCatalogueScreen({super.key});

  @override
  ConsumerState<ProductCatalogueScreen> createState() =>
      _ProductCatalogueScreenState();
}

class _ProductCatalogueScreenState
    extends ConsumerState<ProductCatalogueScreen> {
  final _searchController = TextEditingController();

  @override
  void initState() {
    super.initState();
    final existingQuery = ref.read(catalogueControllerProvider).searchQuery;
    if (existingQuery.isNotEmpty) {
      _searchController.text = existingQuery;
    }
    Future.microtask(
      () => ref.read(catalogueControllerProvider.notifier).load(),
    );
  }

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  CatalogueController get _controller =>
      ref.read(catalogueControllerProvider.notifier);

  Future<void> _reload() => _controller.load();

  void _openProduct(CatalogueProduct product) =>
      context.push('/catalogue/products/${product.publicId}');

  void _addToCart(CatalogueProduct product) {
    // No confirmation snackbar by design: the header cart badge reflects the
    // change immediately.
    ref.read(orderControllerProvider.notifier).setCartItem(product, 1);
  }

  void _clearFilters() {
    _controller.clearFilters();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(catalogueControllerProvider);
    if (_searchController.text != state.searchQuery) {
      _searchController.value = _searchController.value.copyWith(
        text: state.searchQuery,
        selection: TextSelection.collapsed(offset: state.searchQuery.length),
        composing: TextRange.empty,
      );
    }
    final cart = ref.watch(
      orderControllerProvider.select((orderState) => orderState.cart),
    );
    final cartQuantities = <String, double>{
      for (final item in cart) item.product.publicId: item.quantity,
    };

    return CustomerShell(
      title: 'Catalogue',
      currentPath: '/catalogue',
      actions: [
        IconButton(
          key: const ValueKey('catalogue-header-cart'),
          tooltip: cart.isEmpty
              ? 'Cart, empty'
              : 'Cart, ${cart.length} product lines',
          onPressed: () {
            ScaffoldMessenger.of(context).hideCurrentSnackBar();
            context.push('/checkout');
          },
          icon: Badge(
            isLabelVisible: cart.isNotEmpty,
            label: Text(cart.length > 99 ? '99+' : '${cart.length}'),
            child: const Icon(Icons.shopping_bag_outlined),
          ),
        ),
      ],
      child: state.isLoading && state.products.isEmpty
          ? const LoadingStatePanel(message: 'Loading products')
          : state.errorMessage != null && state.products.isEmpty
          ? ErrorStatePanel(message: state.errorMessage!, onRetry: _reload)
          : RefreshIndicator(
              onRefresh: _reload,
              child: CustomScrollView(
                slivers: [
                  SliverToBoxAdapter(
                    child: _CatalogueControls(
                      state: state,
                      searchController: _searchController,
                      onSearchChanged: (value) => ref
                          .read(catalogueControllerProvider.notifier)
                          .setSearchQuery(value),
                      onCategorySelected: (categoryId) => ref
                          .read(catalogueControllerProvider.notifier)
                          .selectCategory(categoryId),
                      onAvailabilityChanged: (value) => ref
                          .read(catalogueControllerProvider.notifier)
                          .setAvailableOnly(value: value),
                    ),
                  ),
                  if (state.visibleProducts.isEmpty)
                    SliverFillRemaining(
                      hasScrollBody: true,
                      child: state.hasActiveFilters
                          ? EmptyStatePanel(
                              title: 'No matching products',
                              message:
                                  'Try a different search term or clear your '
                                  'filters to see everything in the catalogue.',
                              action: DoodhButton(
                                label: 'Clear filters',
                                icon: Icons.filter_alt_off_outlined,
                                onPressed: _clearFilters,
                              ),
                            )
                          : const EmptyStatePanel(
                              title: 'No products available',
                              message:
                                  'There are no products in this category yet.',
                            ),
                    )
                  else
                    SliverPadding(
                      padding: const EdgeInsets.fromLTRB(
                        DoodhSpacing.md,
                        0,
                        DoodhSpacing.md,
                        DoodhSpacing.xxxl,
                      ),
                      sliver: SliverToBoxAdapter(
                        child: Center(
                          child: ConstrainedBox(
                            constraints: const BoxConstraints(
                              maxWidth: DoodhContentMax.wide,
                            ),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Semantics(
                                  header: true,
                                  child: Text(
                                    'Fresh picks for you',
                                    style: Theme.of(context)
                                        .textTheme
                                        .headlineSmall
                                        ?.copyWith(
                                          fontSize: 18,
                                          fontWeight: FontWeight.w800,
                                        ),
                                  ),
                                ),
                                const SizedBox(height: DoodhSpacing.xs),
                                Text(
                                  'Choose from the products available to your branch.',
                                  style: Theme.of(context).textTheme.bodyMedium
                                      ?.copyWith(color: DoodhColors.muted),
                                ),
                                const SizedBox(height: DoodhSpacing.md),
                                DoodhGrid(
                                  // 150px keeps 2 product cards side-by-side
                                  // on phone-width list views (~328px
                                  // content width); 4-up on wide screens.
                                  // minColumns guarantees 2-up even on
                                  // narrower phones where the estimate
                                  // would floor to 1.
                                  minItemWidth: 150,
                                  maxColumns: 4,
                                  minColumns: 2,
                                  children: [
                                    for (final product in state.visibleProducts)
                                      CatalogueProductCard(
                                        key: ValueKey(
                                          'catalogue-product-${product.publicId}',
                                        ),
                                        product: product,
                                        cartQuantity:
                                            cartQuantities[product.publicId],
                                        imageHeight: 120,
                                        onOpen: () => _openProduct(product),
                                        onSubscribe: () =>
                                            context.push('/subscriptions/new'),
                                        onAddToCart: () => _addToCart(product),
                                      ),
                                  ],
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ),
                ],
              ),
            ),
    );
  }
}

/// Search, category rail and availability filter shown above the product grid.
///
/// Category selection keeps using the server-side filter (unchanged API); the
/// search term and availability toggle are purely in-memory presentation
/// filters over the already-loaded list.
class _CatalogueControls extends StatelessWidget {
  const _CatalogueControls({
    required this.state,
    required this.searchController,
    required this.onSearchChanged,
    required this.onCategorySelected,
    required this.onAvailabilityChanged,
  });

  final CatalogueState state;
  final TextEditingController searchController;
  final ValueChanged<String> onSearchChanged;
  final ValueChanged<String?> onCategorySelected;
  final ValueChanged<bool> onAvailabilityChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final options = [
      for (final category in state.categories)
        DoodhCategoryOption(id: category.publicId, label: category.name),
    ];
    final categoryLabel = state.selectedCategoryId == null
        ? 'All categories'
        : state.categories
                  .where(
                    (category) => category.publicId == state.selectedCategoryId,
                  )
                  .map((category) => category.name)
                  .elementAtOrNull(0) ??
              'All categories';
    final count = state.visibleProducts.length;

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: DoodhContentMax.wide),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            DoodhSpacing.md,
            DoodhSpacing.lg,
            DoodhSpacing.md,
            DoodhSpacing.lg,
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Semantics(
                header: true,
                child: Text(
                  'What are you looking for?',
                  style: theme.textTheme.headlineMedium?.copyWith(
                    fontSize: 20,
                    color: DoodhColors.tealDark,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -.5,
                  ),
                ),
              ),
              const SizedBox(height: DoodhSpacing.xs),
              Text(
                'Fresh dairy essentials, ready when you are.',
                style: theme.textTheme.bodyLarge?.copyWith(
                  color: DoodhColors.muted,
                ),
              ),
              const SizedBox(height: DoodhSpacing.md),
              DoodhSearchBar(
                key: const ValueKey('catalogue-search'),
                controller: searchController,
                hint: 'Search products or categories',
                onChanged: onSearchChanged,
              ),
              if (options.isNotEmpty) ...[
                const SizedBox(height: DoodhSpacing.md),
                DoodhCategoryRail(
                  key: const ValueKey('catalogue-category-rail'),
                  options: options,
                  selectedId: state.selectedCategoryId,
                  onSelected: onCategorySelected,
                ),
              ],
              const SizedBox(height: DoodhSpacing.sm),
              Row(
                children: [
                  FilterChip(
                    key: const ValueKey('catalogue-availability-filter'),
                    selected: state.availableOnly,
                    onSelected: onAvailabilityChanged,
                    avatar: state.availableOnly
                        ? const Icon(Icons.check_rounded, size: 18)
                        : const Icon(Icons.inventory_2_outlined, size: 18),
                    label: const Text('Available now'),
                  ),
                  const Spacer(),
                  Semantics(
                    liveRegion: true,
                    container: true,
                    child: Text(
                      '$count product${count == 1 ? '' : 's'} · $categoryLabel',
                      style: theme.textTheme.labelLarge?.copyWith(
                        color: DoodhColors.muted,
                      ),
                    ),
                  ),
                ],
              ),
              if (state.isLoading) ...[
                const SizedBox(height: DoodhSpacing.sm),
                const LinearProgressIndicator(minHeight: 3),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class ProductDetailScreen extends ConsumerStatefulWidget {
  const ProductDetailScreen({super.key, required this.productId});

  final String productId;

  @override
  ConsumerState<ProductDetailScreen> createState() =>
      _ProductDetailScreenState();
}

class _ProductDetailScreenState extends ConsumerState<ProductDetailScreen> {
  CatalogueProduct? _product;
  String? _error;
  final _quantityController = TextEditingController(text: '1');

  @override
  void initState() {
    super.initState();
    // Keep the sticky total preview in sync while the quantity is typed.
    _quantityController.addListener(_handleQuantityChanged);
    Future.microtask(_load);
  }

  void _handleQuantityChanged() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    _quantityController.removeListener(_handleQuantityChanged);
    _quantityController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final repository = ref.read(catalogueRepositoryProvider);
      final product = await repository.getProduct(widget.productId);
      if (mounted) setState(() => _product = product);
    } on Object catch (error) {
      if (mounted) {
        setState(() => _error = error.toString());
      }
    }
  }

  /// Adds [product] to the cart.
  ///
  /// Pricing stays server-authoritative: this only records the requested
  /// quantity. When [quantity] is omitted the validated value typed into the
  /// quantity field is used (keeping the existing three-decimal rule).
  void _addToCart(CatalogueProduct product, {double? quantity}) {
    final validation = quantity == null
        ? _validateQuantity(_quantityController.text)
        : null;
    final resolved = quantity ?? double.tryParse(_quantityController.text);
    if (resolved == null || resolved <= 0 || validation != null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(validation ?? 'Enter a valid quantity')),
      );
      return;
    }
    // No confirmation snackbar by design: the header cart badge reflects the
    // change immediately.
    ref.read(orderControllerProvider.notifier).setCartItem(product, resolved);
  }

  @override
  Widget build(BuildContext context) {
    final product = _product;
    final cart = ref.watch(
      orderControllerProvider.select((state) => state.cart),
    );
    return Scaffold(
      appBar: AppBar(
        title: Text(product?.name ?? 'Product details'),
        actions: [
          IconButton(
            key: const ValueKey('product-detail-header-cart'),
            tooltip: cart.isEmpty
                ? 'Cart, empty'
                : 'Cart, ${cart.length} product lines',
            onPressed: () {
              ScaffoldMessenger.of(context).hideCurrentSnackBar();
              context.push('/checkout');
            },
            icon: Badge(
              isLabelVisible: cart.isNotEmpty,
              label: Text(cart.length > 99 ? '99+' : '${cart.length}'),
              child: const Icon(Icons.shopping_bag_outlined),
            ),
          ),
        ],
      ),
      body: product == null
          ? (_error == null
                ? const LoadingStatePanel(message: 'Loading product')
                : ErrorStatePanel(message: _error!, onRetry: _load))
          : _buildBody(product),
      bottomNavigationBar: product == null ? null : _buildActionBar(product),
    );
  }

  /// The sticky purchase bar. A compact total preview (unit price × validated
  /// quantity — the same input the "Add to cart" action submits) sits above
  /// the single primary "Add to cart" action and the existing "Subscribe"
  /// secondary action, reusing [DoodhButton].
  Widget _buildActionBar(CatalogueProduct product) {
    final theme = Theme.of(context);
    final quantity = double.tryParse(_quantityController.text);
    final validQuantity = quantity != null && quantity > 0 ? quantity : null;
    return DecoratedBox(
      decoration: const BoxDecoration(
        color: DoodhColors.cream,
        border: Border(top: BorderSide(color: DoodhColors.line)),
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            DoodhSpacing.md,
            DoodhSpacing.sm,
            DoodhSpacing.md,
            DoodhSpacing.md,
          ),
          // `heightFactor: 1` is required: `Scaffold` offers the bottom bar a
          // loose height constraint, and a plain [Center] would expand to it and
          // starve the scrollable body of all remaining height.
          child: Center(
            heightFactor: 1,
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: DoodhContentMax.wide),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Row(
                    children: [
                      Text(
                        'Total',
                        style: theme.textTheme.labelLarge?.copyWith(
                          color: DoodhColors.muted,
                        ),
                      ),
                      const Spacer(),
                      // Same presentation as the checkout sticky total
                      // ("₹X.YY") so the two sticky bars read identically.
                      Semantics(
                        container: true,
                        label:
                            'Sticky total \u20B9${(product.price * (validQuantity ?? 1)).toStringAsFixed(2)}',
                        child: Text(
                          key: const ValueKey('product-detail-sticky-total'),
                          '\u20B9${(product.price * (validQuantity ?? 1)).toStringAsFixed(2)}',
                          style: theme.textTheme.titleMedium?.copyWith(
                            color: DoodhColors.tealDark,
                            fontWeight: FontWeight.w900,
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                  Row(
                    children: [
                      Expanded(
                        child: DoodhButton(
                          label: 'Subscribe',
                          icon: Icons.event_repeat_outlined,
                          variant: DoodhButtonVariant.secondary,
                          onPressed: () => context.push('/subscriptions/new'),
                        ),
                      ),
                      const SizedBox(width: DoodhSpacing.sm),
                      Expanded(
                        flex: 2,
                        child: DoodhButton(
                          label: 'Add to cart',
                          icon: Icons.add_shopping_cart_outlined,
                          onPressed: () => _addToCart(product),
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildBody(CatalogueProduct product) {
    // Related products reuse the already-loaded catalogue list — no extra API.
    final related = ref
        .watch(catalogueControllerProvider)
        .products
        .where(
          (candidate) =>
              candidate.publicId != product.publicId &&
              candidate.category.publicId == product.category.publicId,
        )
        .take(4)
        .toList(growable: false);

    return DoodhResponsive(
      builder: (context, size) {
        final isCompact = size == DoodhWindowSize.compact;
        return CustomScrollView(
          slivers: [
            SliverToBoxAdapter(
              child: Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(
                    maxWidth: DoodhContentMax.wide,
                  ),
                  child: Padding(
                    padding: DoodhSpacing.pagePadding,
                    child: isCompact
                        ? Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              _heroImage(product, height: 260),
                              const SizedBox(height: DoodhSpacing.md),
                              _purchasePanel(product),
                              const SizedBox(height: DoodhSpacing.md),
                              _detailsPanel(product),
                            ],
                          )
                        : Row(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Expanded(
                                flex: 11,
                                child: _heroImage(product, height: 420),
                              ),
                              const SizedBox(width: DoodhSpacing.lg),
                              Expanded(
                                flex: 10,
                                child: Column(
                                  crossAxisAlignment:
                                      CrossAxisAlignment.stretch,
                                  children: [
                                    _purchasePanel(product),
                                    const SizedBox(height: DoodhSpacing.md),
                                    _detailsPanel(product),
                                  ],
                                ),
                              ),
                            ],
                          ),
                  ),
                ),
              ),
            ),
            if (related.isNotEmpty)
              SliverToBoxAdapter(
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(
                      maxWidth: DoodhContentMax.wide,
                    ),
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(
                        DoodhSpacing.md,
                        0,
                        DoodhSpacing.md,
                        DoodhSpacing.lg,
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          DoodhSectionHeader(
                            title: 'More in ${product.category.name}',
                          ),
                          const SizedBox(height: DoodhSpacing.md),
                          DoodhGrid(
                            // Match the catalogue grid density so related
                            // products also show 2-up on phones.
                            minItemWidth: 150,
                            maxColumns: 3,
                            minColumns: 2,
                            children: [
                              for (final item in related)
                                CatalogueProductCard(
                                  product: item,
                                  onOpen: () => context.push(
                                    '/catalogue/products/${item.publicId}',
                                  ),
                                  onSubscribe: () =>
                                      context.push('/subscriptions/new'),
                                  onAddToCart: () =>
                                      _addToCart(item, quantity: 1),
                                ),
                            ],
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
          ],
        );
      },
    );
  }

  /// Large, dominant product image that keeps the shared branded fallback.
  Widget _heroImage(CatalogueProduct product, {required double height}) {
    return DoodhProductImage(
      imageUrl: product.usableImageUrl,
      icon: doodhProductUnitIcon(product.unitOfMeasure),
      semanticLabel: '${product.name} product image',
      height: height,
      borderRadius: DoodhRadii.lg,
    );
  }

  /// Product information, price/unit hierarchy and the existing quantity field.
  Widget _purchasePanel(CatalogueProduct product) {
    final theme = Theme.of(context);
    return DoodhCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            product.category.name.toUpperCase(),
            style: theme.textTheme.labelSmall?.copyWith(
              color: DoodhColors.muted,
              fontWeight: FontWeight.w700,
              letterSpacing: .6,
            ),
          ),
          const SizedBox(height: DoodhSpacing.xs),
          Text(
            product.name,
            style: theme.textTheme.headlineSmall?.copyWith(
              fontWeight: FontWeight.w800,
            ),
          ),
          const SizedBox(height: DoodhSpacing.sm),
          DoodhPriceTag(
            amount: product.price,
            style: theme.textTheme.headlineSmall?.copyWith(
              color: DoodhColors.tealDark,
              fontWeight: FontWeight.w900,
            ),
          ),
          const SizedBox(height: DoodhSpacing.xs),
          Text(
            'per ${product.unitLabel}',
            style: theme.textTheme.bodySmall?.copyWith(
              color: DoodhColors.muted,
            ),
          ),
          const SizedBox(height: DoodhSpacing.md),
          Align(
            alignment: Alignment.centerLeft,
            child: product.isAvailable
                ? const DoodhChip(
                    label: 'Available',
                    tone: DoodhTone.success,
                    icon: Icons.check_circle_outline,
                  )
                : const DoodhChip(
                    label: 'Out of stock',
                    tone: DoodhTone.warning,
                    icon: Icons.error_outline,
                  ),
          ),
          if (!product.isAvailable) ...[
            const SizedBox(height: DoodhSpacing.md),
            const DoodhInfoBanner(
              tone: DoodhTone.warning,
              message:
                  'This product is currently out of stock. '
                  'See its branch availability below.',
            ),
          ],
          const SizedBox(height: DoodhSpacing.md),
          const Divider(height: 1),
          const SizedBox(height: DoodhSpacing.md),
          Text('Quantity', style: theme.textTheme.titleSmall),
          const SizedBox(height: DoodhSpacing.sm),
          TextFormField(
            controller: _quantityController,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: InputDecoration(
              labelText: 'Quantity (${product.unitLabel})',
              border: const OutlineInputBorder(),
              helperText: 'Up to three decimal places',
            ),
            validator: (value) => _validateQuantity(value),
          ),
        ],
      ),
    );
  }

  /// Supporting sections: the description (only when the API supplies one) and
  /// the branch availability already returned with the product.
  Widget _detailsPanel(CatalogueProduct product) {
    final description = product.description?.trim();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (description != null && description.isNotEmpty) ...[
          DoodhSectionCard(
            icon: Icons.info_outline,
            title: 'About this product',
            children: [Text(description)],
          ),
          const SizedBox(height: DoodhSpacing.md),
        ],
        _branchAvailability(product),
      ],
    );
  }

  Widget _branchAvailability(CatalogueProduct product) {
    final theme = Theme.of(context);
    final branches = product.branchAvailability;
    return DoodhSectionCard(
      icon: Icons.storefront_outlined,
      title: branches.isEmpty
          ? 'Branch availability'
          : branches.length == 1
          ? 'Available at 1 branch'
          : 'Available at ${branches.length} branches',
      children: [
        if (branches.isEmpty)
          Text(
            'Branch availability is confirmed when you place an order.',
            style: theme.textTheme.bodyMedium?.copyWith(
              color: DoodhColors.muted,
            ),
          )
        else
          for (var index = 0; index < branches.length; index++) ...[
            Row(
              children: [
                ExcludeSemantics(
                  child: Icon(
                    Icons.store_mall_directory_outlined,
                    size: 20,
                    color: DoodhColors.tealDark,
                  ),
                ),
                const SizedBox(width: DoodhSpacing.sm),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        branches[index].branchName,
                        style: theme.textTheme.titleSmall,
                      ),
                      Text(
                        branches[index].maxDailyQuantity == null
                            ? (branches[index].isAvailable
                                  ? 'Available'
                                  : 'Not available')
                            : 'Daily limit: '
                                  '${formatQuantity(branches[index].maxDailyQuantity!)} '
                                  '${product.unitLabel}',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: DoodhColors.muted,
                        ),
                      ),
                    ],
                  ),
                ),
                ExcludeSemantics(
                  child: Icon(
                    branches[index].isAvailable
                        ? Icons.check_circle_outline
                        : Icons.remove_circle_outline,
                    size: 20,
                    color: branches[index].isAvailable
                        ? DoodhColors.successForeground
                        : DoodhColors.muted,
                  ),
                ),
              ],
            ),
            if (index < branches.length - 1)
              const Divider(height: DoodhSpacing.lg),
          ],
      ],
    );
  }

  String? _validateQuantity(String? value) {
    final quantity = double.tryParse(value ?? '');
    if (quantity == null || quantity <= 0) return 'Enter a positive quantity';
    if ((value!.split('.').elementAtOrNull(1)?.length ?? 0) > 3) {
      return 'Use up to three decimal places';
    }
    return null;
  }
}

class AdminCatalogueScreen extends ConsumerStatefulWidget {
  const AdminCatalogueScreen({super.key, this.pickImage});

  /// Test seam overriding the platform image picker (milk-test convention).
  final CatalogueImagePicker? pickImage;

  @override
  ConsumerState<AdminCatalogueScreen> createState() =>
      _AdminCatalogueScreenState();
}

/// Web-style catalogue management dashboard (see Catalogue Screen.jpg):
/// brand header + Products/Categories tabs, white management cards with a
/// header row, filter bar, data table and pagination footer. Behaviour
/// (controller calls, menu keys, dialog contracts) is unchanged — only the
/// presentation is restyled.
class _AdminCatalogueScreenState extends ConsumerState<AdminCatalogueScreen> {
  static const _panelGreen = Color(0xFF198754);
  static const _pageGrey = Color(0xFFF8F9FA);
  // Wide web-style table (Product + SKU + Category + Unit + Price +
  // Description + Branches + Taxes + Status + Actions): phones get
  // horizontal scroll for the full grid, cards never overflow.
  static const _tableMinWidth = 1480.0;

  int _tab = 0;
  final _productSearch = TextEditingController();
  final _categorySearch = TextEditingController();
  String? _categoryFilter;
  String _statusFilter = 'All';
  int _productPage = 0;
  int _categoryPage = 0;
  static const _pageSize = 10;

  /// Blank in-grid draft rows for direct creation (no separate pages).
  bool _addingProduct = false;
  bool _addingCategory = false;

  void _startProductDraft() {
    if (_addingProduct) return;
    setState(() {
      _tab = 0;
      _addingProduct = true;
      _productPage = 0;
    });
  }

  void _startCategoryDraft() {
    if (_addingCategory) return;
    setState(() {
      _tab = 1;
      _addingCategory = true;
      _categoryPage = 0;
    });
  }

  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      ref.read(adminCatalogueControllerProvider.notifier).load();
      // Branch/tax multi-select columns need the Tax & Charges master;
      // no-op when already loaded, degrades to empty options otherwise.
      if (ref.read(chargeControllerProvider).charges.isEmpty) {
        ref.read(chargeControllerProvider.notifier).load();
      }
    });
  }

  @override
  void dispose() {
    _productSearch.dispose();
    _categorySearch.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(adminCatalogueControllerProvider);
    return Scaffold(
      backgroundColor: _pageGrey,
      appBar: AppBar(
        backgroundColor: _pageGrey,
        title: const Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.water_drop_rounded, color: _panelGreen, size: 20),
            SizedBox(width: 6),
            Flexible(
              child: Text(
                'DoodhDirect',
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  color: _panelGreen,
                  fontWeight: FontWeight.w800,
                ),
              ),
            ),
          ],
        ),
        actions: [
          IconButton(
            tooltip: 'Add category',
            onPressed: _startCategoryDraft,
            icon: const Icon(Icons.create_new_folder_outlined),
          ),
          IconButton(
            tooltip: 'Add product',
            onPressed: _startProductDraft,
            icon: const Icon(Icons.add_box_outlined),
          ),
        ],
      ),
      body: state.isLoading && state.products.isEmpty
          ? const LoadingStatePanel(message: 'Loading catalogue management')
          : state.errorMessage != null && state.products.isEmpty
          ? ErrorStatePanel(
              message: state.errorMessage!,
              onRetry: () =>
                  ref.read(adminCatalogueControllerProvider.notifier).load(),
            )
          : RefreshIndicator(
              onRefresh: () =>
                  ref.read(adminCatalogueControllerProvider.notifier).load(),
              child: Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(
                    maxWidth: DoodhContentMax.wide,
                  ),
                  child: ListView(
                    padding: DoodhSpacing.pagePadding,
                    children: [
                      _managementHeader(context),
                      const SizedBox(height: DoodhSpacing.sm),
                      _tabs(context),
                      const SizedBox(height: DoodhSpacing.md),
                      if (state.errorMessage != null)
                        Padding(
                          padding: const EdgeInsets.only(bottom: DoodhSpacing.sm),
                          child: Text(
                            state.errorMessage!,
                            style: TextStyle(
                              color: Theme.of(context).colorScheme.error,
                            ),
                          ),
                        ),
                      // Strict tab split: the Products tab shows only the
                      // products card, the Categories tab only categories.
                      if (_tab == 0)
                        _productsCard(context, state)
                      else
                        _categoriesCard(context, state, standalone: true),
                      const SizedBox(height: DoodhSpacing.xxxl),
                    ],
                  ),
                ),
              ),
            ),
    );
  }

  Widget _managementHeader(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Catalogue management',
          style: theme.textTheme.headlineSmall?.copyWith(
            fontSize: 24,
            fontWeight: FontWeight.w800,
          ),
        ),
        const SizedBox(height: 2),
        Text(
          'Manage your products and categories',
          style: theme.textTheme.bodyMedium?.copyWith(
            color: DoodhColors.muted,
            fontSize: 14,
          ),
        ),
      ],
    );
  }

  Widget _tabs(BuildContext context) {
    Widget tab({
      required int index,
      required IconData icon,
      required String label,
    }) {
      final selected = _tab == index;
      return Expanded(
        child: InkWell(
          borderRadius: BorderRadius.circular(10),
          onTap: () => setState(() {
            _tab = index;
            _productPage = 0;
            _categoryPage = 0;
          }),
          child: Container(
            padding: const EdgeInsets.symmetric(vertical: 10),
            decoration: BoxDecoration(
              color: selected ? Colors.white : Colors.transparent,
              borderRadius: BorderRadius.circular(10),
              border: Border.all(
                color: selected ? DoodhColors.line : Colors.transparent,
              ),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(
                  icon,
                  size: 16,
                  color: selected ? DoodhColors.ink : DoodhColors.muted,
                ),
                const SizedBox(width: 4),
                Flexible(
                  child: Text(
                    label,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontWeight: FontWeight.w700,
                      color: selected ? DoodhColors.ink : DoodhColors.muted,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      );
    }

    return Container(
      padding: const EdgeInsets.all(4),
      decoration: BoxDecoration(
        color: const Color(0xFFEFF2F0),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        children: [
          tab(
            index: 0,
            icon: Icons.inventory_2_outlined,
            label: 'Products',
          ),
          tab(index: 1, icon: Icons.folder_outlined, label: 'Categories'),
        ],
      ),
    );
  }

  List<CatalogueProduct> _filteredProducts(AdminCatalogueState state) {
    final query = _productSearch.text.trim().toLowerCase();
    return state.products.where((product) {
      if (_categoryFilter != null &&
          product.category.publicId != _categoryFilter) {
        return false;
      }
      if (_statusFilter == 'Active' && !product.isActive) return false;
      if (_statusFilter == 'Inactive' && product.isActive) return false;
      if (query.isEmpty) return true;
      return product.name.toLowerCase().contains(query) ||
          product.sku.toLowerCase().contains(query) ||
          product.category.name.toLowerCase().contains(query);
    }).toList(growable: false);
  }

  List<ProductCategory> _filteredCategories(AdminCatalogueState state) {
    final query = _categorySearch.text.trim().toLowerCase();
    if (query.isEmpty) return state.categories;
    return state.categories
        .where(
          (category) =>
              category.name.toLowerCase().contains(query) ||
              category.code.toLowerCase().contains(query) ||
              (category.description?.toLowerCase().contains(query) ?? false),
        )
        .toList(growable: false);
  }

  int _productCountFor(String categoryId, AdminCatalogueState state) => state
      .products
      .where((product) => product.category.publicId == categoryId)
      .length;

  Widget _card(Widget child) => Card(
    elevation: 1,
    color: Colors.white,
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(12),
      side: const BorderSide(color: Color(0xFFE9ECEF)),
    ),
    child: Padding(
      padding: const EdgeInsets.all(DoodhSpacing.md),
      child: child,
    ),
  );

  Widget _productsCard(BuildContext context, AdminCatalogueState state) {
    final filtered = _filteredProducts(state);
    final total = filtered.length;
    final pageCount = total == 0 ? 1 : ((total - 1) ~/ _pageSize) + 1;
    final page = _productPage.clamp(0, pageCount - 1);
    final start = page * _pageSize;
    final end = (start + _pageSize).clamp(0, total);
    final visible = total == 0
        ? const <CatalogueProduct>[]
        : filtered.sublist(start, end);
    return _card(
      Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Wrap (not Row+Spacer): the themed button padding makes the
          // actions ~300px wide, which overflowed narrow phone cards.
          Wrap(
            spacing: 8,
            runSpacing: 8,
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text(
                'Products ($total)',
                style: const TextStyle(
                  fontWeight: FontWeight.w800,
                  fontSize: 16,
                ),
              ),
              FilledButton.icon(
                onPressed: () => _startProductDraft(),
                style: FilledButton.styleFrom(
                  backgroundColor: _panelGreen,
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(8),
                  ),
                  minimumSize: const Size(0, 40),
                  padding: const EdgeInsets.symmetric(horizontal: 12),
                ),
                icon: const Icon(Icons.add_rounded, size: 18),
                label: const Text('Add product'),
              ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.md),
          LayoutBuilder(
            builder: (context, constraints) {
              final narrow = constraints.maxWidth < 640;
              final search = TextField(
                controller: _productSearch,
                decoration: const InputDecoration(
                  hintText: 'Search products by name, SKU or category...',
                  prefixIcon: Icon(Icons.search_rounded),
                  contentPadding: EdgeInsets.symmetric(vertical: 10),
                ),
                onChanged: (_) => setState(() => _productPage = 0),
              );
              final categoryDropdown = DropdownButtonFormField<String>(
                isExpanded: true,
                initialValue: _categoryFilter,
                decoration: const InputDecoration(
                  contentPadding: EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 10,
                  ),
                ),
                hint: const Text('All categories'),
                items: [
                  const DropdownMenuItem<String>(
                    value: null,
                    child: Text('All categories'),
                  ),
                  for (final category in state.categories)
                    DropdownMenuItem<String>(
                      value: category.publicId,
                      child: Text(category.name),
                    ),
                ],
                onChanged: (value) => setState(() {
                  _categoryFilter = value;
                  _productPage = 0;
                }),
              );
              final statusDropdown = DropdownButtonFormField<String>(
                isExpanded: true,
                initialValue: _statusFilter,
                decoration: const InputDecoration(
                  contentPadding: EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 10,
                  ),
                ),
                items: const ['All', 'Active', 'Inactive']
                    .map(
                      (s) => DropdownMenuItem<String>(
                        value: s,
                        child: Text(s == 'All' ? 'Status: All' : s),
                      ),
                    )
                    .toList(),
                onChanged: (value) => setState(() {
                  _statusFilter = value ?? 'All';
                  _productPage = 0;
                }),
              );
              if (narrow) {
                return Column(
                  children: [
                    search,
                    const SizedBox(height: 8),
                    categoryDropdown,
                    const SizedBox(height: 8),
                    statusDropdown,
                  ],
                );
              }
              return Row(
                children: [
                  Expanded(flex: 5, child: search),
                  const SizedBox(width: 8),
                  Expanded(flex: 2, child: categoryDropdown),
                  const SizedBox(width: 8),
                  Expanded(flex: 2, child: statusDropdown),
                ],
              );
            },
          ),
          const SizedBox(height: DoodhSpacing.md),
          // The draft renders even with zero products so creation never
          // strands the admin on the empty panel.
          if (visible.isEmpty && !_addingProduct)
            const EmptyStatePanel(
              title: 'No products',
              message: 'Create the first catalogue product.',
            )
          else ...[
            _tableScroll(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _productHeaderRow(),
                  const Divider(height: 1),
                  if (_addingProduct)
                    _ProductRow.draft(
                      key: const ValueKey('product-draft'),
                      categories: state.categories,
                      branches: state.branches,
                      charges: ref.watch(chargeControllerProvider).charges,
                      onCreate: _createProductFromRow,
                      onCancelDraft: () =>
                          setState(() => _addingProduct = false),
                      onOpenDetails: () => _editProduct(context),
                    ),
                  for (final product in visible)
                    _ProductRow.existing(
                      key: ValueKey('product-${product.publicId}'),
                      product: product,
                      categories: state.categories,
                      branches: state.branches,
                      charges: ref.watch(chargeControllerProvider).charges,
                      onSave: _updateProductFromRow,
                      onToggleActive: (value) => ref
                          .read(adminCatalogueControllerProvider.notifier)
                          .setProductActive(product.publicId, value),
                      onOpenDetails: () => _editProduct(context, product),
                      onOpenAvailability: () =>
                          _editAvailability(context, product),
                    ),
                ],
              ),
            ),
            const Divider(height: 1),
            _paginationFooter(
              start: total == 0 ? 0 : start + 1,
              end: end,
              total: total,
              page: page,
              pageCount: pageCount,
              onPage: (next) => setState(() => _productPage = next),
            ),
          ],
        ],
      ),
    );
  }

  Widget _tableScroll({required Widget child}) => SingleChildScrollView(
    scrollDirection: Axis.horizontal,
    child: ConstrainedBox(
      constraints: const BoxConstraints(minWidth: _tableMinWidth),
      child: SizedBox(width: _tableMinWidth, child: child),
    ),
  );

  /// Creates the product from the in-grid draft row (the draft always
  /// carries its branch selection — the server requires at least one).
  Future<bool> _createProductFromRow(ProductDraft draft) async {
    final saved = await ref
        .read(adminCatalogueControllerProvider.notifier)
        .saveProduct(null, draft);
    if (saved && mounted) setState(() => _addingProduct = false);
    return saved;
  }

  /// Saves in-grid edits (scalars plus description, unit, branches and
  /// taxes — the image still lives in the details dialog).
  Future<bool> _updateProductFromRow(
    String productId,
    ProductDraft draft,
  ) => ref
      .read(adminCatalogueControllerProvider.notifier)
      .saveProduct(productId, draft);

  /// Description, Branches and Taxes each own a column so long text in one
  /// never shifts the horizontal rhythm of the others; every header cell
  /// carries the same 4px outer gutter as the row cells below.
  Widget _productHeaderRow() => const Padding(
    padding: EdgeInsets.symmetric(vertical: 10),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        Expanded(flex: 3, child: _HeaderCell('Product')),
        Expanded(flex: 2, child: _HeaderCell('SKU')),
        Expanded(flex: 2, child: _HeaderCell('Category')),
        Expanded(flex: 1, child: _HeaderCell('Unit')),
        Expanded(flex: 1, child: _HeaderCell('Price')),
        Expanded(flex: 2, child: _HeaderCell('Description')),
        Expanded(flex: 2, child: _HeaderCell('Branches')),
        Expanded(flex: 2, child: _HeaderCell('Applicable Taxes')),
        Expanded(flex: 1, child: _HeaderCell('Status')),
        // Save/discard plus status-adjacent tools need the room; the table
        // scrolls horizontally, so this never overflows the card.
        SizedBox(width: 150, child: _HeaderCell('Actions')),
      ],
    ),
  );


  Widget _paginationFooter({
    required int start,
    required int end,
    required int total,
    required int page,
    required int pageCount,
    required ValueChanged<int> onPage,
  }) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 8),
    // The single-row layout overflows narrow phone cards, so small widths
    // stack the range row above a compact pager.
    child: LayoutBuilder(
      builder: (context, constraints) {
        Widget pageButton({
          required String tooltip,
          required IconData icon,
          required VoidCallback? onPressed,
        }) => IconButton(
          tooltip: tooltip,
          onPressed: onPressed,
          style: IconButton.styleFrom(
            minimumSize: const Size(36, 36),
            padding: EdgeInsets.zero,
            tapTargetSize: MaterialTapTargetSize.shrinkWrap,
          ),
          icon: Icon(icon, size: 20),
        );
        final pager = Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            pageButton(
              tooltip: 'First page',
              icon: Icons.first_page_rounded,
              onPressed: page > 0 ? () => onPage(0) : null,
            ),
            pageButton(
              tooltip: 'Previous page',
              icon: Icons.chevron_left_rounded,
              onPressed: page > 0 ? () => onPage(page - 1) : null,
            ),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
              decoration: BoxDecoration(
                border: Border.all(color: DoodhColors.line),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Text('${page + 1}'),
            ),
            pageButton(
              tooltip: 'Next page',
              icon: Icons.chevron_right_rounded,
              onPressed: page < pageCount - 1 ? () => onPage(page + 1) : null,
            ),
            pageButton(
              tooltip: 'Last page',
              icon: Icons.last_page_rounded,
              onPressed: page < pageCount - 1
                  ? () => onPage(pageCount - 1)
                  : null,
            ),
          ],
        );
        final rangeRow = Row(
          children: [
            const Flexible(
              child: Text('Rows per page:', overflow: TextOverflow.ellipsis),
            ),
            const SizedBox(width: 6),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              decoration: BoxDecoration(
                border: Border.all(color: DoodhColors.line),
                borderRadius: BorderRadius.circular(6),
              ),
              child: const Text('10'),
            ),
            const Spacer(),
            Text('$start-$end of $total'),
          ],
        );
        if (constraints.maxWidth < 520) {
          return Column(
            children: [rangeRow, const SizedBox(height: 4), pager],
          );
        }
        return Row(children: [Expanded(child: rangeRow), pager]);
      },
    ),
  );

  Widget _categoriesCard(
    BuildContext context,
    AdminCatalogueState state, {
    bool standalone = false,
  }) {
    final filtered = _filteredCategories(state);
    final total = filtered.length;
    final pageCount = total == 0 ? 1 : ((total - 1) ~/ _pageSize) + 1;
    final page = _categoryPage.clamp(0, pageCount - 1);
    final start = page * _pageSize;
    final end = (start + _pageSize).clamp(0, total);
    final visible = total == 0
        ? const <ProductCategory>[]
        : filtered.sublist(start, end);
    return _card(
      Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Wrap (not Row+Spacer): title + 220px search + button cannot fit
          // narrow phone cards on one row.
          Wrap(
            spacing: 8,
            runSpacing: 8,
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text(
                'Categories ($total)',
                style: const TextStyle(
                  fontWeight: FontWeight.w800,
                  fontSize: 16,
                ),
              ),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  if (!standalone)
                    SizedBox(
                      width: 220,
                      child: TextField(
                        controller: _categorySearch,
                        decoration: const InputDecoration(
                          hintText: 'Search categories...',
                          prefixIcon: Icon(Icons.search_rounded),
                          contentPadding: EdgeInsets.symmetric(vertical: 10),
                        ),
                        onChanged: (_) => setState(() => _categoryPage = 0),
                      ),
                    ),
                  FilledButton.icon(
                    onPressed: _addingCategory ? null : _startCategoryDraft,
                    style: FilledButton.styleFrom(
                      backgroundColor: _panelGreen,
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(8),
                      ),
                      minimumSize: const Size(0, 40),
                      padding: const EdgeInsets.symmetric(horizontal: 12),
                    ),
                    icon: const Icon(Icons.add_rounded, size: 18),
                    label: const Text('Add category'),
                  ),
                ],
              ),
            ],
          ),
          if (standalone) ...[
            const SizedBox(height: DoodhSpacing.sm),
            TextField(
              controller: _categorySearch,
              decoration: const InputDecoration(
                hintText: 'Search categories...',
                prefixIcon: Icon(Icons.search_rounded),
              ),
              onChanged: (_) => setState(() => _categoryPage = 0),
            ),
          ],
          const SizedBox(height: DoodhSpacing.md),
          _tableScroll(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 10),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.center,
                    children: [
                      Expanded(flex: 2, child: _HeaderCell('Category')),
                      Expanded(flex: 1, child: _HeaderCell('Code')),
                      Expanded(flex: 3, child: _HeaderCell('Description')),
                      Expanded(flex: 1, child: _HeaderCell('Status')),
                      Expanded(flex: 1, child: _HeaderCell('Products')),
                    ],
                  ),
                ),
                const Divider(height: 1),
                if (_addingCategory)
                  _CategoryRow.draft(
                    key: const ValueKey('category-draft'),
                    productCount: 0,
                    onCreate: _createCategoryFromRow,
                    onCancelDraft: () =>
                        setState(() => _addingCategory = false),
                  ),
                for (final category in visible)
                  _CategoryRow.existing(
                    key: ValueKey('category-${category.publicId}'),
                    category: category,
                    productCount: _productCountFor(
                      category.publicId,
                      state,
                    ),
                    onSave: _updateCategoryFromRow,
                    onToggleActive: (value) => ref
                        .read(adminCatalogueControllerProvider.notifier)
                        .setCategoryActive(category.publicId, value),
                  ),
              ],
            ),
          ),
          const Divider(height: 1),
          _paginationFooter(
            start: total == 0 ? 0 : start + 1,
            end: end,
            total: total,
            page: page,
            pageCount: pageCount,
            onPage: (next) => setState(() => _categoryPage = next),
          ),
        ],
      ),
    );
  }

  /// Creates the category from the in-grid draft row.
  Future<bool> _createCategoryFromRow(CategoryDraft draft) async {
    final saved = await ref
        .read(adminCatalogueControllerProvider.notifier)
        .saveCategory(null, draft);
    if (saved && mounted) setState(() => _addingCategory = false);
    return saved;
  }

  /// Saves in-grid category edits.
  Future<bool> _updateCategoryFromRow(
    String categoryId,
    CategoryDraft draft,
  ) => ref
      .read(adminCatalogueControllerProvider.notifier)
      .saveCategory(categoryId, draft);

  Future<void> _editProduct(
    BuildContext context, [
    CatalogueProduct? product,
  ]) async {
    final state = ref.read(adminCatalogueControllerProvider);
    final result =
        await showDialog<
          ({ProductDraft draft, ProductImageChange? imageChange})
        >(
          context: context,
          builder: (_) => _ProductDialog(
            categories: state.categories,
            branches: state.branches,
            product: product,
            pickImage: widget.pickImage,
          ),
        );
    if (result == null || !context.mounted) return;
    final controller = ref.read(adminCatalogueControllerProvider.notifier);
    // The product fields are saved first so a new product exists to attach
    // the image to; the image operation then runs against its public id.
    final saved = await controller.saveProduct(product?.publicId, result.draft);
    if (!saved || !context.mounted) return;

    final change = result.imageChange;
    if (change == null) return;
    if (change.remove) {
      await controller.removeProductImage(product!.publicId);
    } else if (change.bytes != null) {
      final targetId =
          product?.publicId ??
          ref
              .read(adminCatalogueControllerProvider)
              .products
              .where(
                (item) => item.sku == result.draft.sku.trim().toUpperCase(),
              )
              .map((item) => item.publicId)
              .firstOrNull;
      if (targetId != null) {
        await controller.upsertProductImage(
          targetId,
          bytes: change.bytes!,
          fileName: change.fileName!,
          contentType: change.contentType!,
        );
      }
    }
  }

  Future<void> _editAvailability(
    BuildContext context,
    CatalogueProduct product,
  ) async {
    final state = ref.read(adminCatalogueControllerProvider);
    if (state.branches.isEmpty) return;
    final mainBranch = state.branches
        .where((branch) => branch.code.toUpperCase() == 'MAIN')
        .firstOrNull;
    final branch =
        product.branchAvailability
            .where(
              (availability) =>
                  mainBranch != null &&
                  availability.branchId == mainBranch.publicId,
            )
            .firstOrNull ??
        product.branchAvailability.firstOrNull;
    final draft = await showDialog<BranchAvailabilityDraft>(
      context: context,
      builder: (_) =>
          _AvailabilityDialog(branches: state.branches, current: branch),
    );
    if (draft != null && context.mounted) {
      await ref
          .read(adminCatalogueControllerProvider.notifier)
          .setBranchAvailability(product.publicId, draft);
    }
  }
}

/// Units offered by the in-grid Unit dropdown (same set as the details
/// dialog so the two never disagree).
const _productUnits = ['litre', 'kilogram', 'gram', 'piece'];

/// One editable product grid row. Name, SKU, price, description edit inline;
/// category and unit are dropdowns; branches and applicable taxes are
/// multi-select dropdowns in their own columns. The status switch, details
/// entry (image) and branch-availability entry stay as row tools. The draft
/// variant pre-selects MAIN so creation satisfies the server's
/// at-least-one-branch rule.
class _ProductRow extends StatefulWidget {
  const _ProductRow.draft({
    super.key,
    required this.categories,
    required this.branches,
    this.charges = const [],
    required this.onCreate,
    required this.onCancelDraft,
    required this.onOpenDetails,
  }) : product = null,
       onSave = null,
       onToggleActive = null,
       onOpenAvailability = null;

  const _ProductRow.existing({
    super.key,
    required CatalogueProduct this.product,
    required this.categories,
    this.branches = const [],
    this.charges = const [],
    required this.onSave,
    required this.onToggleActive,
    required this.onOpenDetails,
    required this.onOpenAvailability,
  }) : onCreate = null,
       onCancelDraft = null;

  final CatalogueProduct? product;
  final List<ProductCategory> categories;
  final List<CatalogueBranch> branches;
  final List<Charge> charges;
  final Future<bool> Function(ProductDraft draft)? onCreate;
  final VoidCallback? onCancelDraft;
  final Future<bool> Function(String productId, ProductDraft draft)? onSave;
  final ValueChanged<bool>? onToggleActive;
  final VoidCallback? onOpenDetails;
  final VoidCallback? onOpenAvailability;

  @override
  State<_ProductRow> createState() => _ProductRowState();
}

class _ProductRowState extends State<_ProductRow> {
  late final TextEditingController _name = TextEditingController(
    text: widget.product?.name ?? '',
  );
  late final TextEditingController _sku = TextEditingController(
    text: widget.product?.sku ?? '',
  );
  late final TextEditingController _price = TextEditingController(
    text: widget.product == null ? '' : _format(widget.product!.price),
  );
  late final TextEditingController _description = TextEditingController(
    text: widget.product?.description ?? '',
  );
  late String? _categoryId =
      widget.product?.category.publicId ??
      widget.categories.firstOrNull?.publicId;
  late String _unit = widget.product?.unitOfMeasure ?? 'litre';
  late Set<String> _selectedBranchIds = _initialBranchIds();
  late Set<String> _selectedChargeIds = _initialChargeIds();
  bool _saving = false;

  CatalogueProduct? get _product => widget.product;

  bool get _isDraft => _product == null;

  Set<String> _initialBranchIds() {
    final product = widget.product;
    if (product != null) {
      return ProductDraft.fromProduct(product).branchIds.toSet();
    }
    final fallback = _defaultBranchId();
    return fallback == null ? <String>{} : {fallback};
  }

  Set<String> _initialChargeIds() {
    final product = widget.product;
    if (product == null) return <String>{};
    return ProductDraft.fromProduct(product).chargeIds.toSet();
  }

  String? _defaultBranchId() {
    if (widget.branches.isEmpty) return null;
    return widget.branches
            .where((branch) => branch.code.toUpperCase() == 'MAIN')
            .firstOrNull
            ?.publicId ??
        widget.branches.first.publicId;
  }

  @override
  void initState() {
    super.initState();
    for (final controller in [_name, _sku, _price, _description]) {
      controller.addListener(_onChanged);
    }
  }

  @override
  void didUpdateWidget(_ProductRow oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Adopt refreshed server values, but never clobber in-progress edits.
    if (!_isDraft && !_dirty) _resync();
  }

  @override
  void dispose() {
    _name.dispose();
    _sku.dispose();
    _price.dispose();
    _description.dispose();
    super.dispose();
  }

  void _onChanged() {
    if (mounted) setState(() {});
  }

  void _resync() {
    final product = _product;
    if (product == null) return;
    _name.text = product.name;
    _sku.text = product.sku;
    _price.text = _format(product.price);
    _description.text = product.description ?? '';
    _unit = product.unitOfMeasure;
    _categoryId = product.category.publicId;
    _selectedBranchIds = _initialBranchIds();
    _selectedChargeIds = _initialChargeIds();
  }

  static String _format(double value) => value == value.roundToDouble()
      ? value.round().toString()
      : value.toStringAsFixed(2);

  double? get _priceValue {
    final parsed = double.tryParse(_price.text.trim());
    if (parsed == null || parsed <= 0) return null;
    return parsed;
  }

  bool _sameSet(Set<String> a, Set<String> b) =>
      a.length == b.length && a.containsAll(b);

  bool get _dirty {
    final product = _product;
    final baseBranchIds = product == null
        ? _defaultBranchIdsForCompare()
        : ProductDraft.fromProduct(product).branchIds.toSet();
    final baseChargeIds = product == null
        ? <String>{}
        : ProductDraft.fromProduct(product).chargeIds.toSet();
    if (product == null) {
      return _name.text.trim().isNotEmpty ||
          _sku.text.trim().isNotEmpty ||
          _price.text.trim().isNotEmpty ||
          _description.text.trim().isNotEmpty ||
          _unit != 'litre' ||
          !_sameSet(_selectedBranchIds, baseBranchIds) ||
          _selectedChargeIds.isNotEmpty;
    }
    return _name.text.trim() != product.name ||
        _sku.text.trim() != product.sku ||
        _price.text.trim() != _format(product.price) ||
        _description.text.trim() != (product.description?.trim() ?? '') ||
        _unit != product.unitOfMeasure ||
        _categoryId != product.category.publicId ||
        !_sameSet(_selectedBranchIds, baseBranchIds) ||
        !_sameSet(_selectedChargeIds, baseChargeIds);
  }

  Set<String> _defaultBranchIdsForCompare() {
    final fallback = _defaultBranchId();
    return fallback == null ? <String>{} : {fallback};
  }

  bool get _valid =>
      _name.text.trim().isNotEmpty &&
      _sku.text.trim().isNotEmpty &&
      _unit.trim().isNotEmpty &&
      _description.text.trim().length <= 500 &&
      _priceValue != null &&
      _categoryId != null &&
      _selectedBranchIds.isNotEmpty;

  String? _optional(String value) {
    final normalized = value.trim();
    return normalized.isEmpty ? null : normalized;
  }

  Future<void> _save() async {
    if (!_valid || _saving) return;
    setState(() => _saving = true);
    try {
      if (_isDraft) {
        await widget.onCreate!(ProductDraft(
          sku: _sku.text,
          name: _name.text,
          description: _optional(_description.text),
          categoryId: _categoryId!,
          unitOfMeasure: _unit,
          price: _priceValue!,
          branchIds: _selectedBranchIds.toList(growable: false),
          chargeIds: _selectedChargeIds.toList(growable: false),
        ));
        // On success the screen closes the draft; on failure the list error
        // banner carries the server message.
      } else {
        await widget.onSave!(
          _product!.publicId,
          ProductDraft(
            sku: _sku.text,
            name: _name.text,
            description: _optional(_description.text),
            categoryId: _categoryId!,
            unitOfMeasure: _unit,
            price: _priceValue!,
            branchIds: _selectedBranchIds.toList(growable: false),
            chargeIds: _selectedChargeIds.toList(growable: false),
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _cancel() {
    if (_isDraft) {
      widget.onCancelDraft!();
      return;
    }
    setState(_resync);
  }

  @override
  Widget build(BuildContext context) {
    final product = _product;
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              Expanded(
                flex: 3,
                child: _padded(
                  Row(
                    children: [
                      if (product != null) ...[
                        _CatalogueRowThumb(product: product),
                        const SizedBox(width: 8),
                      ],
                      Expanded(
                        child: _cellField(
                          controller: _name,
                          hint: 'Product name',
                          bold: true,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              Expanded(
                flex: 2,
                child: _padded(_cellField(controller: _sku, hint: 'SKU')),
              ),
              Expanded(flex: 2, child: _padded(_categoryField())),
              Expanded(flex: 1, child: _padded(_unitField())),
              Expanded(
                flex: 1,
                child: _padded(
                  _cellField(
                    controller: _price,
                    hint: 'Price',
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                  ),
                ),
              ),
              Expanded(
                flex: 2,
                child: _padded(
                  _cellField(controller: _description, hint: 'Description'),
                ),
              ),
              Expanded(flex: 2, child: _padded(_branchMultiSelect())),
              Expanded(flex: 2, child: _padded(_taxMultiSelect())),
              Expanded(flex: 1, child: _padded(_statusCell(product))),
              SizedBox(width: 150, child: _actionsCell(product)),
            ],
          ),
        ),
        const Divider(height: 1),
      ],
    );
  }

  Widget _padded(Widget child) => Padding(
    padding: const EdgeInsets.symmetric(horizontal: 4),
    child: child,
  );

  Widget _cellField({
    required TextEditingController controller,
    required String hint,
    bool bold = false,
    TextInputType? keyboardType,
  }) => TextField(
    controller: controller,
    style: TextStyle(
      fontSize: 13,
      fontWeight: bold ? FontWeight.w700 : FontWeight.w400,
    ),
    keyboardType: keyboardType,
    decoration: InputDecoration(
      hintText: hint,
      isDense: true,
      contentPadding: const EdgeInsets.symmetric(
        horizontal: 8,
        vertical: 8,
      ),
    ),
  );

  Widget _categoryField() {
    final categories = widget.categories;
    final current = categories.any(
      (category) => category.publicId == _categoryId,
    );
    return DropdownButtonFormField<String>(
      isExpanded: true,
      initialValue: current ? _categoryId : null,
      hint: const Text('Category', overflow: TextOverflow.ellipsis),
      style: const TextStyle(fontSize: 13, color: DoodhColors.ink),
      decoration: const InputDecoration(
        isDense: true,
        contentPadding: EdgeInsets.symmetric(horizontal: 8, vertical: 8),
      ),
      items: [
        for (final category in categories)
          DropdownMenuItem<String>(
            value: category.publicId,
            child: Text(
              category.name,
              overflow: TextOverflow.ellipsis,
            ),
          ),
      ],
      onChanged: (value) => setState(() => _categoryId = value),
    );
  }

  Widget _unitField() => DropdownButtonFormField<String>(
    isExpanded: true,
    initialValue: _productUnits.contains(_unit) ? _unit : null,
    hint: const Text(
      'Unit',
      overflow: TextOverflow.ellipsis,
      style: TextStyle(fontSize: 13),
    ),
    style: const TextStyle(fontSize: 13, color: DoodhColors.ink),
    decoration: const InputDecoration(
      isDense: true,
      contentPadding: EdgeInsets.symmetric(horizontal: 8, vertical: 8),
    ),
    items: [
      for (final unit in _productUnits)
        DropdownMenuItem<String>(
          value: unit,
          child: Text(unit, overflow: TextOverflow.ellipsis),
        ),
    ],
    onChanged: (value) {
      if (value != null) setState(() => _unit = value);
    },
  );

  /// Branches live in their own column as a multi-select dropdown (the
  /// server requires at least one branch, so validity tracks non-empty).
  Widget _branchMultiSelect() => _MultiSelectField(
    key: ValueKey('product-branches-${_product?.publicId ?? 'draft'}'),
    label: 'Branches',
    hint: 'Branches',
    optionKeyPrefix: 'product-row-branch-',
    selected: _selectedBranchIds,
    options: [
      for (final branch in widget.branches)
        _MultiOption(branch.publicId, '${branch.code} · ${branch.name}'),
    ],
    selectedLabels: {
      for (final branch in widget.branches) branch.publicId: branch.code,
      if (_product != null)
        for (final link in _product!.branchAvailability)
          link.branchId: link.branchName,
    },
    onChanged: (next) => setState(() => _selectedBranchIds = next),
  );

  /// Applicable taxes column: active item-level charges only — global and
  /// inactive charges stay in the details dialog as reference chips.
  Widget _taxMultiSelect() {
    final options = widget.charges
        .where((charge) => charge.isActive && !charge.applicableOnAll)
        .toList(growable: false);
    return _MultiSelectField(
      key: ValueKey('product-taxes-${_product?.publicId ?? 'draft'}'),
      label: 'Applicable taxes',
      hint: 'Taxes',
      optionKeyPrefix: 'product-row-charge-',
      selected: _selectedChargeIds,
      options: [
        for (final charge in options)
          _MultiOption(
            charge.publicId,
            '${charge.chargeCode} · ${_chargePct(charge.percentage)}%',
          ),
      ],
      selectedLabels: {
        for (final charge in widget.charges)
          charge.publicId: charge.chargeCode,
        if (_product != null)
          for (final mapping in _product!.applicableCharges)
            mapping.chargeId: mapping.chargeCode,
      },
      onChanged: (next) => setState(() => _selectedChargeIds = next),
    );
  }

  String _chargePct(double value) => value == value.roundToDouble()
      ? value.round().toString()
      : value.toStringAsFixed(2);

  Widget _statusCell(CatalogueProduct? product) {
    if (product == null) {
      return const Tooltip(
        message: 'New products start active',
        child: Switch(value: true, onChanged: null),
      );
    }
    return Tooltip(
      message: product.isActive
          ? 'Deactivate ${product.sku}'
          : 'Activate ${product.sku}',
      child: Switch(
        value: product.isActive,
        onChanged: (value) => widget.onToggleActive!(value),
      ),
    );
  }

  Widget _actionsCell(CatalogueProduct? product) => Wrap(
    spacing: 0,
    runSpacing: 0,
    crossAxisAlignment: WrapCrossAlignment.center,
    children: [
      // The draft always offers save/discard (disabled until it validates).
      if (_isDraft || _dirty) ...[
        _toolButton(
          tooltip: 'Save ${product?.sku ?? 'new product'}',
          icon: Icons.check_rounded,
          onPressed: !_valid ? null : _save,
        ),
        _toolButton(
          tooltip: 'Discard changes',
          icon: Icons.close_rounded,
          onPressed: _cancel,
        ),
      ],
      // Details opens the full form (image plus the same branch/tax
      // assignment) — for the draft it is the create dialog.
      _toolButton(
        key: ValueKey('admin-product-details-${product?.publicId ?? 'draft'}'),
        tooltip: 'Product details',
        icon: Icons.image_outlined,
        onPressed: widget.onOpenDetails,
      ),
      if (product != null)
        _toolButton(
          tooltip: 'Branch availability',
          icon: Icons.store_outlined,
          onPressed: widget.onOpenAvailability,
        ),
    ],
  );

  Widget _toolButton({
    Key? key,
    required String tooltip,
    required IconData icon,
    required VoidCallback? onPressed,
  }) => IconButton(
    key: key,
    tooltip: tooltip,
    onPressed: _saving ? null : onPressed,
    style: IconButton.styleFrom(
      minimumSize: const Size(36, 36),
      padding: const EdgeInsets.all(6),
      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
    ),
    icon: Icon(icon, size: 20),
  );
}

/// Single option inside [_MultiSelectField].
class _MultiOption {
  const _MultiOption(this.id, this.label);
  final String id;
  final String label;
}

/// Compact multi-select dropdown used by the product grid columns and the
/// product dialog: a dense input-style button showing the selection count
/// that opens a checkbox dialog. Locked ids (selected but no longer
/// offered) are preserved in the count and passed back through on save.
class _MultiSelectField extends StatelessWidget {
  const _MultiSelectField({
    super.key,
    required this.label,
    required this.hint,
    required this.options,
    required this.selected,
    this.selectedLabels = const {},
    required this.onChanged,
    this.optionKeyPrefix,
  });

  final String label;
  final String hint;
  final List<_MultiOption> options;
  final Set<String> selected;
  final Map<String, String> selectedLabels;
  final ValueChanged<Set<String>> onChanged;

  /// When set, each option chip carries `ValueKey('$optionKeyPrefix$id')`
  /// so tests can target individual options inside the selection dialog.
  final String? optionKeyPrefix;

  String get _display {
    if (selected.isEmpty) return '';
    if (selected.length == 1) {
      final id = selected.single;
      final match = options.where((o) => o.id == id).firstOrNull;
      return match?.label ?? selectedLabels[id] ?? '1 selected';
    }
    return '${selected.length} selected';
  }

  String get _tooltip {
    if (selected.isEmpty) return hint;
    final names = [
      for (final id in selected)
        options.where((o) => o.id == id).firstOrNull?.label ??
            selectedLabels[id] ??
            id,
    ];
    return names.join(', ');
  }

  Future<void> _open(BuildContext context) async {
    var temp = Set<String>.of(selected);
    final result = await showDialog<Set<String>>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) => AlertDialog(
          title: Text(label),
          content: SizedBox(
            width: 340,
            child: SingleChildScrollView(
              child: options.isEmpty
                  ? const Text('No options available yet.')
                  : Wrap(
                      spacing: 8,
                      runSpacing: 4,
                      children: [
                        for (final option in options)
                          FilterChip(
                            key: optionKeyPrefix == null
                                ? null
                                : ValueKey('$optionKeyPrefix${option.id}'),
                            label: Text(option.label),
                            selected: temp.contains(option.id),
                            onSelected: (value) => setDialogState(() {
                              if (value) {
                                temp.add(option.id);
                              } else {
                                temp.remove(option.id);
                              }
                            }),
                          ),
                      ],
                    ),
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => setDialogState(() => temp.clear()),
              child: const Text('Clear'),
            ),
            TextButton(
              onPressed: () => Navigator.pop(dialogContext),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(dialogContext, temp),
              child: const Text('Done'),
            ),
          ],
        ),
      ),
    );
    if (result != null) onChanged(result);
  }

  @override
  Widget build(BuildContext context) => Tooltip(
    message: _tooltip,
    child: InkWell(
      borderRadius: BorderRadius.circular(6),
      onTap: () => _open(context),
      child: InputDecorator(
        decoration: InputDecoration(
          hintText: hint,
          isDense: true,
          contentPadding: const EdgeInsets.symmetric(
            horizontal: 8,
            vertical: 8,
          ),
          suffixIcon: const Icon(Icons.arrow_drop_down_rounded, size: 20),
          suffixIconConstraints: const BoxConstraints(
            minWidth: 24,
            minHeight: 24,
          ),
        ),
        isEmpty: selected.isEmpty,
        child: Text(
          _display,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(fontSize: 13),
        ),
      ),
    ),
  );
}

/// Product thumbnail shared by grid rows (extracted so the editable row
/// widget can live outside the screen state).
class _CatalogueRowThumb extends StatelessWidget {
  const _CatalogueRowThumb({required this.product});

  final CatalogueProduct product;

  @override
  Widget build(BuildContext context) {
    final url = product.usableImageUrl;
    return ClipRRect(
      borderRadius: BorderRadius.circular(8),
      child: SizedBox(
        width: 44,
        height: 44,
        child: url == null
            ? Container(
                color: DoodhColors.mint,
                child: Icon(
                  doodhProductUnitIcon(product.unitOfMeasure),
                  color: DoodhColors.tealDark,
                ),
              )
            : Image.network(
                resolveMediaUrl(url)!,
                fit: BoxFit.cover,
                errorBuilder: (_, _, _) => Container(
                  color: DoodhColors.mint,
                  child: const Icon(
                    Icons.water_drop_rounded,
                    color: DoodhColors.tealDark,
                  ),
                ),
              ),
      ),
    );
  }
}

/// One editable category grid row. Name, code and description edit inline
/// with save/discard actions; the status switch is the only other control —
/// there is no Edit button and no delete (the API offers neither for
/// categories: deactivation is the lifecycle).
class _CategoryRow extends StatefulWidget {
  const _CategoryRow.draft({
    super.key,
    required this.productCount,
    required this.onCreate,
    required this.onCancelDraft,
  }) : category = null,
       onSave = null,
       onToggleActive = null;

  const _CategoryRow.existing({
    super.key,
    required ProductCategory this.category,
    required this.productCount,
    required this.onSave,
    required this.onToggleActive,
  }) : onCreate = null,
       onCancelDraft = null;

  final ProductCategory? category;
  final int productCount;
  final Future<bool> Function(CategoryDraft draft)? onCreate;
  final VoidCallback? onCancelDraft;
  final Future<bool> Function(String categoryId, CategoryDraft draft)? onSave;
  final ValueChanged<bool>? onToggleActive;

  @override
  State<_CategoryRow> createState() => _CategoryRowState();
}

class _CategoryRowState extends State<_CategoryRow> {
  late final TextEditingController _name = TextEditingController(
    text: widget.category?.name ?? '',
  );
  late final TextEditingController _code = TextEditingController(
    text: widget.category?.code ?? '',
  );
  late final TextEditingController _description = TextEditingController(
    text: widget.category?.description ?? '',
  );
  bool _saving = false;

  ProductCategory? get _category => widget.category;

  bool get _isDraft => _category == null;

  @override
  void initState() {
    super.initState();
    for (final controller in [_name, _code, _description]) {
      controller.addListener(_onChanged);
    }
  }

  @override
  void didUpdateWidget(_CategoryRow oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!_isDraft && !_dirty) _resync();
  }

  @override
  void dispose() {
    _name.dispose();
    _code.dispose();
    _description.dispose();
    super.dispose();
  }

  void _onChanged() {
    if (mounted) setState(() {});
  }

  void _resync() {
    final category = _category;
    if (category == null) return;
    _name.text = category.name;
    _code.text = category.code;
    _description.text = category.description ?? '';
  }

  bool get _dirty {
    final category = _category;
    if (category == null) {
      return _name.text.trim().isNotEmpty ||
          _code.text.trim().isNotEmpty ||
          _description.text.trim().isNotEmpty;
    }
    return _name.text.trim() != category.name ||
        _code.text.trim() != category.code ||
        _description.text.trim() != (category.description?.trim() ?? '');
  }

  bool get _valid =>
      _name.text.trim().isNotEmpty &&
      _code.text.trim().isNotEmpty &&
      _description.text.trim().length <= 500;

  Future<void> _save() async {
    if (!_valid || _saving) return;
    setState(() => _saving = true);
    try {
      if (_isDraft) {
        await widget.onCreate!(CategoryDraft(
          code: _code.text,
          name: _name.text,
          description: _description.text,
        ));
      } else {
        await widget.onSave!(
          _category!.publicId,
          CategoryDraft(
            code: _code.text,
            name: _name.text,
            description: _description.text,
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _cancel() {
    if (_isDraft) {
      widget.onCancelDraft!();
      return;
    }
    setState(_resync);
  }

  @override
  Widget build(BuildContext context) {
    final category = _category;
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              Expanded(
                flex: 2,
                child: _padded(
                  _cellField(
                    controller: _name,
                    hint: 'Category name',
                    bold: true,
                  ),
                ),
              ),
              Expanded(
                flex: 1,
                child: _padded(
                  _cellField(controller: _code, hint: 'Code'),
                ),
              ),
              Expanded(
                flex: 3,
                child: _padded(
                  _cellField(
                    controller: _description,
                    hint: 'Description',
                  ),
                ),
              ),
              Expanded(
                flex: 1,
                child: _padded(
                  category == null
                      ? const Tooltip(
                          message: 'New categories start active',
                          child: Switch(value: true, onChanged: null),
                        )
                      : Tooltip(
                          message: category.isActive
                              ? 'Deactivate ${category.code}'
                              : 'Activate ${category.code}',
                          child: Switch(
                            value: category.isActive,
                            onChanged: (value) =>
                                widget.onToggleActive!(value),
                          ),
                        ),
                ),
              ),
              // No separate Actions column: the count stays left-aligned
              // with its header, and save/discard appear inline only
              // while the row is dirty (or always for the draft).
              Expanded(flex: 1, child: _padded(_countCell(category))),
            ],
          ),
        ),
        const Divider(height: 1),
      ],
    );
  }

  Widget _padded(Widget child) => Padding(
    padding: const EdgeInsets.symmetric(horizontal: 4),
    child: child,
  );

  Widget _countCell(ProductCategory? category) {
    final showActions = _isDraft || _dirty;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Flexible(
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
            child: Text(
              '${widget.productCount}',
              style: const TextStyle(fontSize: 13),
            ),
          ),
        ),
        if (showActions) ...[
          IconButton(
            tooltip: 'Save ${category?.code ?? 'new category'}',
            style: IconButton.styleFrom(
              minimumSize: const Size(36, 36),
              padding: const EdgeInsets.all(6),
              tapTargetSize: MaterialTapTargetSize.shrinkWrap,
            ),
            onPressed: !_valid || _saving ? null : _save,
            icon: const Icon(Icons.check_rounded, size: 20),
          ),
          IconButton(
            tooltip: 'Discard changes',
            style: IconButton.styleFrom(
              minimumSize: const Size(36, 36),
              padding: const EdgeInsets.all(6),
              tapTargetSize: MaterialTapTargetSize.shrinkWrap,
            ),
            onPressed: _saving ? null : _cancel,
            icon: const Icon(Icons.close_rounded, size: 20),
          ),
        ],
      ],
    );
  }

  Widget _cellField({
    required TextEditingController controller,
    required String hint,
    bool bold = false,
  }) => TextField(
    controller: controller,
    style: TextStyle(
      fontSize: 13,
      fontWeight: bold ? FontWeight.w700 : FontWeight.w400,
    ),
    decoration: InputDecoration(
      hintText: hint,
      isDense: true,
      contentPadding: const EdgeInsets.symmetric(
        horizontal: 8,
        vertical: 8,
      ),
    ),
  );

}

class _HeaderCell extends StatelessWidget {
  const _HeaderCell(this.label);
  final String label;
  @override
  Widget build(BuildContext context) => Padding(
    // Horizontal 4 matches the row cells' outer gutter so header labels
    // align exactly with the in-grid field content below.
    padding: const EdgeInsets.symmetric(horizontal: 4),
    child: Text(
      label,
      style: const TextStyle(
        color: DoodhColors.muted,
        fontSize: 12,
        fontWeight: FontWeight.w700,
      ),
    ),
  );
}

/// Injectable image-pick function so tests can stub the platform picker.
typedef CatalogueImagePicker = Future<XFile?> Function({
  required ImageSource source,
});

/// Dialog result: the product fields, plus any pending image operation the
/// caller must perform through the admin controller (upload/replace, or
/// removal of the current image).
class ProductImageChange {
  const ProductImageChange({
    this.bytes,
    this.fileName,
    this.contentType,
    this.remove = false,
  });

  final Uint8List? bytes;
  final String? fileName;
  final String? contentType;
  final bool remove;
}

class _ProductDialog extends ConsumerStatefulWidget {
  const _ProductDialog({
    required this.categories,
    required this.branches,
    this.product,
    this.pickImage,
  });
  final List<ProductCategory> categories;

  /// Active, selectable branches. Archived/inactive branches are not offered
  /// for (re-)assignment and appear only as reference chips when a product
  /// already holds a historical link to them.
  final List<CatalogueBranch> branches;
  final CatalogueProduct? product;

  /// Test seam overriding the platform image picker (milk-test convention).
  final CatalogueImagePicker? pickImage;
  @override
  ConsumerState<_ProductDialog> createState() => _ProductDialogState();
}

class _ProductDialogState extends ConsumerState<_ProductDialog> {
  late final _sku = TextEditingController(text: widget.product?.sku);
  late final _name = TextEditingController(text: widget.product?.name);
  late final _description = TextEditingController(
    text: widget.product?.description,
  );
  late final _price = TextEditingController(
    text: widget.product?.price.toString(),
  );
  late String? _categoryId =
      widget.product?.category.publicId ??
      widget.categories.firstOrNull?.publicId;
  late String _unit = widget.product?.unitOfMeasure ?? 'litre';
  late Set<String> _selectedBranchIds = _initialBranchSelection();
  String? _branchError;

  /// Product-tax assignment selection. Seeded from the product's existing
  /// ProductCharge mappings — INCLUDING inactive ones — because the server
  /// applies replace semantics: anything not sent back is removed.
  late Set<String> _selectedChargeIds = _initialChargeSelection();

  @override
  void initState() {
    super.initState();
    // Load the Tax & Charges master for the multi-select options (no-op when
    // the controller already holds the list). Failures degrade gracefully:
    // existing assignments stay visible as kept chips.
    if (ref.read(chargeControllerProvider).charges.isEmpty) {
      Future.microtask(
        () => ref.read(chargeControllerProvider.notifier).load(),
      );
    }
  }

  CatalogueImagePicker? get _pickImage => widget.pickImage;

  // Pending product-image operation captured by the dialog and executed by
  // the screen after the product itself is saved.
  ProductImageChange? _imageChange;
  bool _imageBusy = false;
  String? _imageError;

  @override
  void dispose() {
    _sku.dispose();
    _name.dispose();
    _description.dispose();
    _price.dispose();
    super.dispose();
  }

  Set<String> _initialBranchSelection() {
    final product = widget.product;
    if (product == null) return {};
    final selectable = widget.branches.map((branch) => branch.publicId).toSet();
    return product.branchAvailability
        .where((branch) => selectable.contains(branch.branchId))
        .map((branch) => branch.branchId)
        .toSet();
  }

  /// Historical assignments to branches that are no longer selectable
  /// (archived/inactive). They stay visible for reference and are not sent
  /// in `branchIds` — the server preserves archived links automatically.
  List<BranchAvailability> get _lockedBranches {
    final product = widget.product;
    if (product == null) return const [];
    final selectable = widget.branches.map((branch) => branch.publicId).toSet();
    return product.branchAvailability
        .where((branch) => !selectable.contains(branch.branchId))
        .toList(growable: false);
  }

  /// Branches as a multi-select dropdown (replaces the full chip list so
  /// long branch rosters never stretch the dialog).
  Widget _buildBranchDropdown() => _MultiSelectField(
    key: const ValueKey('product-dialog-branches'),
    label: 'Branches',
    hint: 'Select branches',
    optionKeyPrefix: 'product-branch-option-',
    selected: _selectedBranchIds,
    options: [
      for (final branch in widget.branches)
        _MultiOption(branch.publicId, '${branch.code} · ${branch.name}'),
    ],
    selectedLabels: {
      for (final branch in widget.branches) branch.publicId: branch.code,
    },
    onChanged: (next) => setState(() {
      _selectedBranchIds = next;
      _branchError = null;
    }),
  );

  Set<String> _initialChargeSelection() =>
      widget.product?.applicableCharges
          .map((charge) => charge.chargeId)
          .toSet() ??
      <String>{};

  /// Charges offered for NEW assignment: active item-level charges only —
  /// inactive charges are not assignable and global (Applicable on All)
  /// charges never take product mappings.
  List<Charge> get _chargeOptions {
    final charges = ref.watch(chargeControllerProvider).charges;
    return charges
        .where((charge) => charge.isActive && !charge.applicableOnAll)
        .toList(growable: false);
  }

  /// Existing assignments that cannot be picked from the options list
  /// (typically mapped-but-inactive charges, or any assignment while the
  /// charge master failed to load). They stay visible for reference and are
  /// sent back unchanged unless explicitly removed.
  List<ProductApplicableCharge> get _lockedCharges {
    final product = widget.product;
    if (product == null) return const [];
    final optionIds = _chargeOptions.map((charge) => charge.publicId).toSet();
    return product.applicableCharges
        .where((mapping) => !optionIds.contains(mapping.chargeId))
        .toList(growable: false);
  }

  /// Applicable taxes as a multi-select dropdown over the active
  /// item-level charges (`isActive && !applicableOnAll`).
  Widget _buildChargeDropdown() {
    final options = _chargeOptions;
    if (options.isEmpty && _lockedCharges.isEmpty) {
      return Text(
        ref.watch(chargeControllerProvider).isLoading
            ? 'Loading taxes…'
            : 'No product-level taxes configured yet.',
      );
    }
    return _MultiSelectField(
      key: const ValueKey('product-dialog-taxes'),
      label: 'Applicable taxes',
      hint: 'Select taxes',
      optionKeyPrefix: 'product-charge-option-',
      selected: _selectedChargeIds,
      options: [
        for (final charge in options)
          _MultiOption(
            charge.publicId,
            '${charge.chargeCode} · ${charge.chargeType} · '
            '${_chargePercentage(charge.percentage)}%',
          ),
      ],
      selectedLabels: {
        for (final charge in options) charge.publicId: charge.chargeCode,
      },
      onChanged: (next) => setState(() => _selectedChargeIds = next),
    );
  }

  Future<void> _pickNewImage() async {
    setState(() => _imageError = null);
    final picker = _pickImage ?? ImagePicker().pickImage;
    try {
      final picked = await picker(source: ImageSource.gallery);
      if (picked == null || !mounted) return;
      final bytes = await picked.readAsBytes();
      // The server validates true content type from magic bytes; the declared
      // type is derived from the picked file (MIME first, then extension) and
      // must match it.
      final contentType =
          _declaredImageType(picked.mimeType) ??
          _declaredImageType(picked.name);
      if (contentType == null) {
        setState(
          () => _imageError =
              'Only JPEG, PNG, or WebP images can be used as a product image.',
        );
        return;
      }
      final defaultName =
          'product-image.${contentType.split('/').last.replaceAll('jpeg', 'jpg')}';
      final name = picked.name.trim().isEmpty ? defaultName : picked.name;
      setState(() {
        _imageBusy = false;
        _imageChange = ProductImageChange(
          bytes: bytes,
          fileName: name,
          contentType: contentType,
        );
      });
    } on Object {
      if (mounted) {
        setState(
          () => _imageError = 'The image could not be read. Try another file.',
        );
      }
    }
  }

  Future<void> _removeImage() async {
    final product = widget.product;
    if (product == null) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Remove product image?'),
        content: Text(
          'The image for ${product.name} will be removed. '
          'Customers will see the standard DoodhDirect placeholder again.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Remove'),
          ),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      setState(() {
        _imageChange = const ProductImageChange(remove: true);
        _imageError = null;
      });
    }
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Row(
      children: [
        Expanded(
          child: Text(
            widget.product == null ? 'Add product' : 'Edit product',
          ),
        ),
        IconButton(
          tooltip: 'Close',
          onPressed: () => Navigator.pop(context),
          icon: const Icon(Icons.close_rounded),
        ),
      ],
    ),
    content: SingleChildScrollView(
      child: SizedBox(
        width: 560,
        child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _buildImageSection(context),
          LayoutBuilder(
            builder: (context, constraints) {
              final twoCol = constraints.maxWidth > 460;
              Widget cell(Widget child) => twoCol
                  ? Expanded(child: child)
                  : SizedBox(width: double.infinity, child: child);
              Widget gap() => SizedBox(
                width: twoCol ? 12 : 0,
                height: twoCol ? 0 : 12,
              );
              final skuField = TextField(
                controller: _sku,
                decoration: const InputDecoration(labelText: 'SKU'),
              );
              final categoryField = DropdownButtonFormField<String>(
                isExpanded: true,
                initialValue: _categoryId,
                decoration: const InputDecoration(labelText: 'Category'),
                items: widget.categories
                    .map(
                      (c) => DropdownMenuItem(
                        value: c.publicId,
                        child: Text(c.name),
                      ),
                    )
                    .toList(),
                onChanged: (value) => setState(() => _categoryId = value),
              );
              final nameField = TextField(
                controller: _name,
                decoration: const InputDecoration(labelText: 'Name'),
              );
              final unitField = DropdownButtonFormField<String>(
                isExpanded: true,
                initialValue: _unit,
                decoration: const InputDecoration(labelText: 'Unit'),
                items: const ['litre', 'kilogram', 'gram', 'piece']
                    .map((u) => DropdownMenuItem(value: u, child: Text(u)))
                    .toList(),
                onChanged: (value) => setState(() => _unit = value!),
              );
              final descField = TextField(
                controller: _description,
                decoration: const InputDecoration(labelText: 'Description'),
              );
              final priceField = TextField(
                controller: _price,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(labelText: 'Price'),
              );
              if (!twoCol) {
                return Column(
                  children: [
                    skuField,
                    const SizedBox(height: 12),
                    categoryField,
                    const SizedBox(height: 12),
                    nameField,
                    const SizedBox(height: 12),
                    unitField,
                    const SizedBox(height: 12),
                    descField,
                    const SizedBox(height: 12),
                    priceField,
                  ],
                );
              }
              return Column(
                children: [
                  Row(children: [cell(skuField), gap(), cell(categoryField)]),
                  const SizedBox(height: 12),
                  Row(children: [cell(nameField), gap(), cell(unitField)]),
                  const SizedBox(height: 12),
                  Row(children: [cell(descField), gap(), cell(priceField)]),
                ],
              );
            },
          ),
          const SizedBox(height: 16),
          const Text('Branches'),
          const SizedBox(height: 8),
          _buildBranchDropdown(),
          if (_lockedBranches.isNotEmpty) ...[
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 4,
              children: [
                for (final locked in _lockedBranches)
                  Tooltip(
                    message: 'No longer assignable; kept for history',
                    child: Chip(
                      label: Text('${locked.branchCode} (reference)'),
                      backgroundColor: Theme.of(context)
                          .colorScheme
                          .surfaceContainerHighest,
                    ),
                  ),
              ],
            ),
          ],
          if (_branchError != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                _branchError!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ),
          const SizedBox(height: 16),
          const Text('Applicable taxes'),
          const SizedBox(height: 8),
          _buildChargeDropdown(),
          if (_lockedCharges.isNotEmpty) ...[
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 4,
              children: [
                for (final locked in _lockedCharges)
                  Tooltip(
                    message: locked.isActive
                        ? 'Kept assignment — the Tax & Charges master is unavailable.'
                        : 'Inactive charge — kept as configuration; it does not apply '
                              'to checkout while inactive.',
                    child: Chip(
                      key: ValueKey('product-charge-locked-${locked.chargeId}'),
                      label: Text(
                        locked.isActive
                            ? '${locked.chargeCode} (reference)'
                            : '${locked.chargeCode} (inactive)',
                      ),
                      backgroundColor: Theme.of(context)
                          .colorScheme
                          .surfaceContainerHighest,
                    ),
                  ),
              ],
            ),
          ],
        ],
        ),
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Cancel'),
      ),
      FilledButton(
        style: FilledButton.styleFrom(
          backgroundColor: const Color(0xFF198754),
        ),
        onPressed: () {
          final price = double.tryParse(_price.text);
          if (_categoryId != null && price != null) {
            if (_selectedBranchIds.isEmpty) {
              setState(() => _branchError = 'Select at least one branch.');
              return;
            }
            Navigator.pop(context, (
              draft: ProductDraft(
                sku: _sku.text,
                name: _name.text,
                description: _description.text,
                categoryId: _categoryId!,
                unitOfMeasure: _unit,
                price: price,
                branchIds: _selectedBranchIds.toList(growable: false),
                // The COMPLETE charge set is sent (kept inactive assignments
                // included) — the server applies replace semantics and
                // removes anything missing from this list.
                chargeIds: _selectedChargeIds.toList(growable: false),
              ),
              imageChange: _imageChange,
            ));
          }
        },
        child: const Text('Save'),
      ),
    ],
  );

  String _chargePercentage(double value) => value == value.roundToDouble()
      ? value.round().toString()
      : value.toStringAsFixed(2);

  /// Product image management section: placeholder + Add Image when none
  /// exists; preview + Replace/Remove once an image is configured.
  Widget _buildImageSection(BuildContext context) {
    final product = widget.product;
    final pendingRemoval = _imageChange?.remove == true;
    final pendingBytes = _imageChange?.bytes;
    final existingUrl = product?.usableImageUrl;
    final hasImage =
        !pendingRemoval && (pendingBytes != null || existingUrl != null);

    Widget previewChild() {
      if (pendingBytes != null && !pendingRemoval) {
        return Image.memory(
          pendingBytes,
          fit: BoxFit.cover,
          errorBuilder: (_, _, _) => const _AdminProductImagePlaceholder(),
        );
      }
      if (existingUrl != null && !pendingRemoval) {
        return Image.network(
          resolveMediaUrl(existingUrl)!,
          fit: BoxFit.cover,
          errorBuilder: (_, _, _) => const _AdminProductImagePlaceholder(),
        );
      }
      return const _AdminProductImagePlaceholder();
    }

    Widget uploadBox() {
      return Expanded(
        child: InkWell(
        borderRadius: BorderRadius.circular(12),
        onTap: _imageBusy ? null : _pickNewImage,
        child: Container(
          height: 132,
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: DoodhColors.line,
              style: BorderStyle.solid,
            ),
            color: const Color(0xFFFBFBF9),
          ),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(
                Icons.cloud_upload_outlined,
                color: DoodhColors.muted,
                size: 22,
              ),
              const Text(
                'Drag and drop an image here',
                textAlign: TextAlign.center,
                style: TextStyle(fontSize: 11),
              ),
              const Text('or', style: TextStyle(fontSize: 11)),
              TextButton.icon(
                key: const ValueKey('admin-product-image-add'),
                style: TextButton.styleFrom(
                  minimumSize: const Size(0, 28),
                  padding: const EdgeInsets.symmetric(
                    horizontal: 8,
                    vertical: 2,
                  ),
                  tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  textStyle: const TextStyle(fontSize: 12),
                ),
                onPressed: _imageBusy ? null : _pickNewImage,
                icon: const Icon(Icons.add_photo_alternate_outlined, size: 16),
                label: Text(
                  existingUrl == null ? 'Add Image' : 'Replace Image',
                ),
              ),
            ],
          ),
        ),
        ),
      );
    }

    Widget previewBox() {
      return Expanded(
        child: Semantics(
        label: hasImage
            ? 'Current product image preview'
            : 'No product image configured',
          // A fixed-height branded box (not DoodhProductImage) keeps the
          // AlertDialog's intrinsic-dimensions pass layoutable.
          child: Container(
            key: const ValueKey('admin-product-image-preview'),
            height: 132,
            width: double.infinity,
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(12),
              gradient: const LinearGradient(
                colors: [Color(0xFFEAF0DC), Color(0xFFCDE7DF)],
                begin: Alignment.topLeft,
                end: Alignment.bottomRight,
              ),
            ),
            clipBehavior: Clip.antiAlias,
            // Preview content shares the same branded fallback as before;
            // undecodable bytes degrade to the placeholder.
            child: previewChild(),
          ),
        ),
      );
    }

    return Column(
      key: const ValueKey('admin-product-image-section'),
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text('Product image'),
        const SizedBox(height: 8),
        LayoutBuilder(
          builder: (context, constraints) {
            if (constraints.maxWidth < 420) {
              return Column(
                children: [
                  Row(children: [uploadBox()]),
                  const SizedBox(height: 8),
                  Row(children: [previewBox()]),
                ],
              );
            }
            return Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [uploadBox(), const SizedBox(width: 12), previewBox()],
            );
          },
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 4,
          children: [
            if (_imageBusy)
              const SizedBox(
                width: 22,
                height: 22,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
            if (hasImage && product != null)
              TextButton.icon(
                key: const ValueKey('admin-product-image-remove'),
                onPressed: _removeImage,
                icon: const Icon(Icons.delete_outline),
                label: const Text('Remove Image'),
              ),
          ],
        ),
        if (pendingRemoval)
          const Text(
            'Image will be removed when you save.',
            style: TextStyle(fontStyle: FontStyle.italic),
          ),
        if (_imageError != null)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(
              _imageError!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
        const SizedBox(height: 12),
      ],
    );
  }

  /// Resolves the declared image content type from a MIME string or a file
  /// name extension. Returns null for anything that is not a supported image.
  String? _declaredImageType(String? fileNameOrMime) {
    final normalized = fileNameOrMime?.trim().toLowerCase();
    if (normalized == null || normalized.isEmpty) return null;
    if (normalized == 'image/jpeg' ||
        normalized == 'image/png' ||
        normalized == 'image/webp') {
      return normalized;
    }
    final extension = normalized.split('.').lastOrNull;
    return switch (extension) {
      'jpg' || 'jpeg' => 'image/jpeg',
      'png' => 'image/png',
      'webp' => 'image/webp',
      _ => null,
    };
  }
}

/// Branded placeholder shown in the admin dialog when no product image is
/// configured (or the configured one failed to load).
class _AdminProductImagePlaceholder extends StatelessWidget {
  const _AdminProductImagePlaceholder();

  @override
  Widget build(BuildContext context) => Center(
    child: Column(
      mainAxisAlignment: MainAxisAlignment.center,
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(
          Icons.water_drop_rounded,
          size: 34,
          color: DoodhColors.tealDark.withValues(alpha: .85),
        ),
        const SizedBox(height: 6),
        Text(
          'DoodhDirect',
          style: Theme.of(context).textTheme.labelSmall?.copyWith(
            color: DoodhColors.tealDark,
            fontWeight: FontWeight.w700,
            letterSpacing: .4,
          ),
        ),
      ],
    ),
  );
}

class _AvailabilityDialog extends StatefulWidget {
  const _AvailabilityDialog({required this.branches, this.current});
  final List<CatalogueBranch> branches;
  final BranchAvailability? current;
  @override
  State<_AvailabilityDialog> createState() => _AvailabilityDialogState();
}

class _AvailabilityDialogState extends State<_AvailabilityDialog> {
  late String _branchId =
      widget.current?.branchId ??
      widget.branches
          .where((branch) => branch.code.toUpperCase() == 'MAIN')
          .firstOrNull
          ?.publicId ??
      widget.branches.first.publicId;
  late bool _available = widget.current?.isAvailable ?? false;
  late final _limit = TextEditingController(
    text: widget.current?.maxDailyQuantity?.toString(),
  );
  @override
  void dispose() {
    _limit.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: const Text('Configure branch availability'),
    content: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        DropdownButtonFormField<String>(
          initialValue: _branchId,
          decoration: const InputDecoration(labelText: 'Branch'),
          items: widget.branches
              .map(
                (b) => DropdownMenuItem(
                  value: b.publicId,
                  child: Text('${b.code} · ${b.name}'),
                ),
              )
              .toList(),
          onChanged: (value) => setState(() => _branchId = value!),
        ),
        SwitchListTile(
          title: const Text('Available for customer orders'),
          subtitle: Text(
            _available
                ? 'This branch can fulfil the product.'
                : 'Enable explicitly after assigning the branch.',
          ),
          value: _available,
          onChanged: (value) => setState(() => _available = value),
        ),
        TextField(
          controller: _limit,
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: const InputDecoration(
            labelText: 'Maximum daily quantity',
          ),
        ),
      ],
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Cancel'),
      ),
      FilledButton(
        onPressed: () => Navigator.pop(
          context,
          BranchAvailabilityDraft(
            branchId: _branchId,
            isAvailable: _available,
            maxDailyQuantity: double.tryParse(_limit.text),
          ),
        ),
        child: const Text('Save'),
      ),
    ],
  );
}
