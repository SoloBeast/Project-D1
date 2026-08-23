import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'catalogue_controller.dart';
import 'catalogue_models.dart';
import 'catalogue_repository.dart';

class ProductCatalogueScreen extends ConsumerStatefulWidget {
  const ProductCatalogueScreen({super.key});

  @override
  ConsumerState<ProductCatalogueScreen> createState() =>
      _ProductCatalogueScreenState();
}

class _ProductCatalogueScreenState
    extends ConsumerState<ProductCatalogueScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(catalogueControllerProvider.notifier).load(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(catalogueControllerProvider);
    final cartCount = ref.watch(
      orderControllerProvider.select((orderState) => orderState.cart.length),
    );
    return CustomerShell(
      currentPath: '/catalogue',
      title: 'Catalogue',
      floatingActionButton: cartCount == 0
          ? null
          : FloatingActionButton.extended(
              onPressed: () {
                ScaffoldMessenger.of(context).hideCurrentSnackBar();
                context.push('/checkout');
              },
              icon: const Icon(Icons.shopping_cart_outlined),
              label: Text('Cart ($cartCount)'),
            ),
      child: state.isLoading && state.products.isEmpty
          ? const LoadingStatePanel(message: 'Loading products')
          : state.errorMessage != null && state.products.isEmpty
          ? ErrorStatePanel(
              message: state.errorMessage!,
              onRetry: () =>
                  ref.read(catalogueControllerProvider.notifier).load(),
            )
          : RefreshIndicator(
              onRefresh: () =>
                  ref.read(catalogueControllerProvider.notifier).load(),
              child: CustomScrollView(
                slivers: [
                  SliverToBoxAdapter(child: _CategoryFilter(state: state)),
                  if (state.products.isEmpty)
                    const SliverFillRemaining(
                      hasScrollBody: false,
                      child: EmptyStatePanel(
                        title: 'No products available',
                        message: 'There are no products in this category yet.',
                      ),
                    )
                  else
                    SliverPadding(
                      padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                      sliver: SliverList.separated(
                        itemCount: state.products.length,
                        separatorBuilder: (_, _) => const SizedBox(height: 8),
                        itemBuilder: (context, index) => _ProductTile(
                          product: state.products[index],
                          onTap: () => context.push(
                            '/catalogue/products/${state.products[index].publicId}',
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

class _CategoryFilter extends ConsumerWidget {
  const _CategoryFilter({required this.state});

  final CatalogueState state;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Padding(
    padding: const EdgeInsets.fromLTRB(16, 16, 16, 12),
    child: DropdownButtonFormField<String?>(
      initialValue: state.selectedCategoryId,
      decoration: const InputDecoration(
        labelText: 'Category',
        prefixIcon: Icon(Icons.category_outlined),
        border: OutlineInputBorder(),
      ),
      items: [
        const DropdownMenuItem<String?>(
          value: null,
          child: Text('All categories'),
        ),
        ...state.categories.map(
          (category) => DropdownMenuItem<String?>(
            value: category.publicId,
            child: Text(category.name),
          ),
        ),
      ],
      onChanged: (value) =>
          ref.read(catalogueControllerProvider.notifier).selectCategory(value),
    ),
  );
}

class _ProductTile extends StatelessWidget {
  const _ProductTile({required this.product, required this.onTap});

  final CatalogueProduct product;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Card(
    child: ListTile(
      contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      leading: CircleAvatar(
        child: Icon(
          product.unitOfMeasure == 'litre'
              ? Icons.local_drink_outlined
              : Icons.inventory_2_outlined,
        ),
      ),
      title: Text(product.name),
      subtitle: Text('${product.category.name} · ${product.formattedPrice}'),
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
    ),
  );
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
    Future.microtask(_load);
  }

  @override
  void dispose() {
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

  @override
  Widget build(BuildContext context) {
    final product = _product;
    return Scaffold(
      appBar: AppBar(title: Text(product?.name ?? 'Product details')),
      body: product == null
          ? (_error == null
                ? const LoadingStatePanel(message: 'Loading product')
                : ErrorStatePanel(message: _error!, onRetry: _load))
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                Icon(
                  product.unitOfMeasure == 'litre'
                      ? Icons.local_drink_outlined
                      : Icons.inventory_2_outlined,
                  size: 72,
                  color: Theme.of(context).colorScheme.primary,
                ),
                const SizedBox(height: 16),
                Text(
                  product.name,
                  style: Theme.of(context).textTheme.headlineSmall,
                ),
                const SizedBox(height: 8),
                Text(product.description ?? 'Fresh dairy product.'),
                const SizedBox(height: 8),
                Text(
                  product.formattedPrice,
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 24),
                TextFormField(
                  controller: _quantityController,
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                  decoration: InputDecoration(
                    labelText: 'Quantity (${product.unitLabel})',
                    border: const OutlineInputBorder(),
                    helperText: 'Up to three decimal places',
                  ),
                  validator: (value) => _validateQuantity(value),
                ),
                const SizedBox(height: 12),
                FilledButton.icon(
                  onPressed: () {
                    final quantity = double.tryParse(_quantityController.text);
                    final validation = _validateQuantity(
                      _quantityController.text,
                    );
                    if (quantity == null || validation != null) {
                      ScaffoldMessenger.of(context).showSnackBar(
                        SnackBar(
                          content: Text(validation ?? 'Enter a valid quantity'),
                        ),
                      );
                      return;
                    }
                    ref
                        .read(orderControllerProvider.notifier)
                        .setCartItem(product, quantity);
                    final messenger = ScaffoldMessenger.of(context);
                    final router = GoRouter.of(context);
                    messenger.showSnackBar(
                      SnackBar(
                        content: Text('${product.name} added to your cart'),
                        duration: const Duration(seconds: 3),
                        action: SnackBarAction(
                          label: 'View cart',
                          onPressed: () {
                            messenger.hideCurrentSnackBar();
                            router.go('/checkout');
                          },
                        ),
                      ),
                    );
                    Future<void>.delayed(const Duration(seconds: 3), () {
                      if (context.mounted) messenger.hideCurrentSnackBar();
                    });
                  },
                  icon: const Icon(Icons.add_shopping_cart_outlined),
                  label: const Text('Add to cart'),
                ),
                const SizedBox(height: 12),
                Text(
                  'Available at ${product.branchAvailability.length} branch${product.branchAvailability.length == 1 ? '' : 'es'}',
                ),
                const SizedBox(height: 16),
                ...product.branchAvailability.map(
                  (branch) => ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: const Icon(Icons.store_outlined),
                    title: Text(branch.branchName),
                    subtitle: branch.maxDailyQuantity == null
                        ? const Text('Available')
                        : Text(
                            'Daily limit: ${formatQuantity(branch.maxDailyQuantity!)} ${product.unitLabel}',
                          ),
                    trailing: const Icon(Icons.check_circle_outline),
                  ),
                ),
              ],
            ),
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
  const AdminCatalogueScreen({super.key});

  @override
  ConsumerState<AdminCatalogueScreen> createState() =>
      _AdminCatalogueScreenState();
}

class _AdminCatalogueScreenState extends ConsumerState<AdminCatalogueScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(adminCatalogueControllerProvider.notifier).load(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(adminCatalogueControllerProvider);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Catalogue management'),
        actions: [
          IconButton(
            tooltip: 'Add category',
            onPressed: () => _editCategory(context),
            icon: const Icon(Icons.create_new_folder_outlined),
          ),
          IconButton(
            tooltip: 'Add product',
            onPressed: () => _editProduct(context),
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
              child: ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  if (state.errorMessage != null)
                    Text(
                      state.errorMessage!,
                      style: TextStyle(
                        color: Theme.of(context).colorScheme.error,
                      ),
                    ),
                  Text(
                    'Products',
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                  const SizedBox(height: 8),
                  if (state.products.isEmpty)
                    const EmptyStatePanel(
                      title: 'No products',
                      message: 'Create the first catalogue product.',
                    )
                  else
                    ...state.products.map(
                      (product) => Card(
                        child: ListTile(
                          title: Text(product.name),
                          subtitle: Text(
                            '${product.sku} · ${product.formattedPrice}\n'
                            '${product.isActive ? 'Active' : 'Inactive'} · '
                            '${_branchSummary(product)}',
                          ),
                          isThreeLine: true,
                          leading: Icon(
                            product.isActive
                                ? Icons.check_circle
                                : Icons.pause_circle,
                          ),
                          trailing: PopupMenuButton<String>(
                            onSelected: (action) async {
                              if (action == 'toggle') {
                                await ref
                                    .read(
                                      adminCatalogueControllerProvider.notifier,
                                    )
                                    .setProductActive(
                                      product.publicId,
                                      !product.isActive,
                                    );
                              } else if (action == 'edit') {
                                _editProduct(context, product);
                              } else if (action == 'branch') {
                                _editAvailability(context, product);
                              }
                            },
                            itemBuilder: (_) => [
                              PopupMenuItem(
                                value: 'edit',
                                child: Text('Edit product'),
                              ),
                              PopupMenuItem(
                                value: 'branch',
                                child: Text('Branch availability'),
                              ),
                              PopupMenuItem(
                                value: 'toggle',
                                child: Text(
                                  product.isActive ? 'Deactivate' : 'Activate',
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  const SizedBox(height: 24),
                  Text(
                    'Categories',
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                  const SizedBox(height: 8),
                  ...state.categories.map(
                    (category) => ListTile(
                      title: Text(category.name),
                      subtitle: Text(
                        '${category.code} · '
                        '${category.isActive ? 'Active' : 'Inactive'}',
                      ),
                      leading: Icon(
                        category.isActive
                            ? Icons.check_circle
                            : Icons.pause_circle,
                      ),
                      trailing: IconButton(
                        tooltip: category.isActive
                            ? 'Deactivate category'
                            : 'Activate category',
                        icon: Icon(
                          category.isActive ? Icons.pause : Icons.play_arrow,
                        ),
                        onPressed: () => ref
                            .read(adminCatalogueControllerProvider.notifier)
                            .setCategoryActive(
                              category.publicId,
                              !category.isActive,
                            ),
                      ),
                      onTap: () => _editCategory(context, category),
                    ),
                  ),
                ],
              ),
            ),
    );
  }

  String _branchSummary(CatalogueProduct product) {
    if (product.branchAvailability.isEmpty) return 'No branch assigned';
    final main = product.branchAvailability
        .where((branch) => branch.branchCode.toUpperCase() == 'MAIN')
        .firstOrNull;
    if (main != null) {
      return 'MAIN: ${main.isAvailable ? 'Available' : 'Unavailable'}';
    }
    return '${product.branchAvailability.length} branch assignment'
        '${product.branchAvailability.length == 1 ? '' : 's'}';
  }

  Future<void> _editProduct(
    BuildContext context, [
    CatalogueProduct? product,
  ]) async {
    final state = ref.read(adminCatalogueControllerProvider);
    final result = await showDialog<ProductDraft>(
      context: context,
      builder: (_) =>
          _ProductDialog(categories: state.categories, product: product),
    );
    if (result != null && context.mounted) {
      await ref
          .read(adminCatalogueControllerProvider.notifier)
          .saveProduct(product?.publicId, result);
    }
  }

  Future<void> _editCategory(
    BuildContext context, [
    ProductCategory? category,
  ]) async {
    final result = await showDialog<CategoryDraft>(
      context: context,
      builder: (_) => _CategoryDialog(category: category),
    );
    if (result != null && context.mounted) {
      await ref
          .read(adminCatalogueControllerProvider.notifier)
          .saveCategory(category?.publicId, result);
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

class _CategoryDialog extends StatefulWidget {
  const _CategoryDialog({this.category});
  final ProductCategory? category;
  @override
  State<_CategoryDialog> createState() => _CategoryDialogState();
}

class _CategoryDialogState extends State<_CategoryDialog> {
  late final _code = TextEditingController(text: widget.category?.code);
  late final _name = TextEditingController(text: widget.category?.name);
  late final _description = TextEditingController(
    text: widget.category?.description,
  );
  @override
  void dispose() {
    _code.dispose();
    _name.dispose();
    _description.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(widget.category == null ? 'New category' : 'Edit category'),
    content: Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        TextField(
          controller: _code,
          decoration: const InputDecoration(labelText: 'Code'),
        ),
        TextField(
          controller: _name,
          decoration: const InputDecoration(labelText: 'Name'),
        ),
        TextField(
          controller: _description,
          decoration: const InputDecoration(labelText: 'Description'),
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
          CategoryDraft(
            code: _code.text,
            name: _name.text,
            description: _description.text,
          ),
        ),
        child: const Text('Save'),
      ),
    ],
  );
}

class _ProductDialog extends StatefulWidget {
  const _ProductDialog({required this.categories, this.product});
  final List<ProductCategory> categories;
  final CatalogueProduct? product;
  @override
  State<_ProductDialog> createState() => _ProductDialogState();
}

class _ProductDialogState extends State<_ProductDialog> {
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
  @override
  void dispose() {
    _sku.dispose();
    _name.dispose();
    _description.dispose();
    _price.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(widget.product == null ? 'New product' : 'Edit product'),
    content: SingleChildScrollView(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          TextField(
            controller: _sku,
            decoration: const InputDecoration(labelText: 'SKU'),
          ),
          TextField(
            controller: _name,
            decoration: const InputDecoration(labelText: 'Name'),
          ),
          TextField(
            controller: _description,
            decoration: const InputDecoration(labelText: 'Description'),
          ),
          DropdownButtonFormField<String>(
            initialValue: _categoryId,
            decoration: const InputDecoration(labelText: 'Category'),
            items: widget.categories
                .map(
                  (c) =>
                      DropdownMenuItem(value: c.publicId, child: Text(c.name)),
                )
                .toList(),
            onChanged: (value) => setState(() => _categoryId = value),
          ),
          DropdownButtonFormField<String>(
            initialValue: _unit,
            decoration: const InputDecoration(labelText: 'Unit'),
            items: const [
              'litre',
              'kilogram',
              'gram',
              'piece',
            ].map((u) => DropdownMenuItem(value: u, child: Text(u))).toList(),
            onChanged: (value) => setState(() => _unit = value!),
          ),
          TextField(
            controller: _price,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            decoration: const InputDecoration(labelText: 'Price'),
          ),
        ],
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Cancel'),
      ),
      FilledButton(
        onPressed: () {
          final price = double.tryParse(_price.text);
          if (_categoryId != null && price != null) {
            Navigator.pop(
              context,
              ProductDraft(
                sku: _sku.text,
                name: _name.text,
                description: _description.text,
                categoryId: _categoryId!,
                unitOfMeasure: _unit,
                price: price,
              ),
            );
          }
        },
        child: const Text('Save'),
      ),
    ],
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
