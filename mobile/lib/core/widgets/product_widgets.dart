import 'package:doodh_direct_mobile/core/network/media_url.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:flutter/material.dart';

/// Resolves the icon used for a product's unit of measure.
///
/// Presentation only: unknown units fall back to a neutral box icon.
IconData doodhProductUnitIcon(String unitOfMeasure) {
  switch (unitOfMeasure.toLowerCase()) {
    case 'litre':
    case 'liter':
    case 'l':
      return Icons.local_drink_outlined;
    case 'kilogram':
    case 'kg':
    case 'gram':
    case 'g':
      return Icons.scale_outlined;
    case 'piece':
    case 'pieces':
    case 'unit':
      return Icons.inventory_2_outlined;
    default:
      return Icons.inventory_2_outlined;
  }
}

/// Product imagery with a branded DoodhDirect fallback.
///
/// New contract note: the catalogue API does not send product images yet, so
/// this widget MUST look intentional when no image is available (never an empty
/// area or a generic avatar). When [imageUrl] (or [imageProvider]) resolves, the
/// image is shown with a subtle fade and any network failure degrades to the
/// fallback rather than a broken-image glyph.
class DoodhProductImage extends StatelessWidget {
  const DoodhProductImage({
    super.key,
    this.imageUrl,
    this.imageProvider,
    this.semanticLabel,
    this.height = 132,
    this.borderRadius,
    this.icon,
    this.fallbackLabel = 'DoodhDirect',
    this.fit = BoxFit.cover,
  });

  final String? imageUrl;
  final ImageProvider? imageProvider;

  /// Accessible description of the image. Defaults to a generic label because
  /// the surrounding card already announces the product name.
  final String? semanticLabel;
  final double height;
  final BorderRadius? borderRadius;
  final IconData? icon;
  final String fallbackLabel;
  final BoxFit fit;

  ImageProvider? _resolveProvider() {
    if (imageProvider != null) return imageProvider;
    // Media URLs from the API are relative (/api/v1/products/{id}/image); on
    // Flutter Web they would otherwise resolve against the web app's own
    // origin instead of the API origin. Resolve through the central helper —
    // null/blank keeps the branded fallback.
    final url = resolveMediaUrl(imageUrl);
    if (url == null) return null;
    return NetworkImage(url);
  }

  Widget _fallback(BuildContext context) => Container(
    height: height,
    width: double.infinity,
    decoration: const BoxDecoration(
      gradient: LinearGradient(
        colors: [DoodhColors.mint, Color(0xFFCDE7DF)],
        begin: Alignment.topLeft,
        end: Alignment.bottomRight,
      ),
    ),
    child: LayoutBuilder(
      builder: (context, constraints) {
        // Compact thumbnails cannot fit the full branded lock-up without
        // overflowing. Keep the unit icon visible and reserve the label for
        // larger presentation surfaces.
        final showLabel =
            fallbackLabel.isNotEmpty && constraints.maxHeight >= 76;
        final iconSize = constraints.maxHeight < 60 ? 24.0 : 34.0;
        return Column(
          mainAxisAlignment: MainAxisAlignment.center,
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              icon ?? Icons.water_drop_rounded,
              size: iconSize,
              color: DoodhColors.tealDark.withValues(alpha: .85),
            ),
            if (showLabel) ...[
              const SizedBox(height: 6),
              Text(
                fallbackLabel,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                  color: DoodhColors.tealDark,
                  fontWeight: FontWeight.w700,
                  letterSpacing: .4,
                ),
              ),
            ],
          ],
        );
      },
    ),
  );

  @override
  Widget build(BuildContext context) {
    final provider = _resolveProvider();
    Widget content = provider == null
        ? _fallback(context)
        : ColoredBox(
            color: DoodhColors.mint,
            child: Image(
              image: provider,
              height: height,
              width: double.infinity,
              fit: fit,
              errorBuilder: (_, _, _) => _fallback(context),
              frameBuilder: (context, child, frame, wasSynchronouslyLoaded) =>
                  wasSynchronouslyLoaded
                  ? child
                  : AnimatedOpacity(
                      opacity: frame == null ? 0 : 1,
                      duration: DoodhMotion.normal,
                      child: child,
                    ),
            ),
          );
    if (borderRadius != null) {
      content = ClipRRect(borderRadius: borderRadius!, child: content);
    }
    return Semantics(
      container: true,
      image: true,
      label: semanticLabel ?? 'Product image',
      child: ExcludeSemantics(child: content),
    );
  }
}

/// A reusable customer-facing product card.
///
/// Information hierarchy follows the modernisation plan: image, name, price,
/// availability, then the primary action. The component takes plain values
/// (not the feature model) so it stays in the shared layer; screens map their
/// product model onto it. Original-price/discount affordances only render when
/// real values are supplied — the app never invents pricing.
class DoodhProductCard extends StatelessWidget {
  const DoodhProductCard({
    super.key,
    required this.name,
    required this.categoryName,
    required this.price,
    required this.unitLabel,
    this.originalAmount,
    this.imageUrl,
    this.imageProvider,
    this.isAvailable = true,
    this.cartQuantityLabel,
    this.unitIcon,
    this.semanticLabel,
    this.imageHeight = 132,
    this.onTap,
    this.onAddToCart,
    this.onSubscribe,
  });

  final String name;
  final String categoryName;
  final num price;
  final String unitLabel;
  final num? originalAmount;
  final String? imageUrl;
  final ImageProvider? imageProvider;
  final bool isAvailable;

  /// Formatted quantity already in the cart (e.g. `1.5 litre`). When supplied,
  /// the card surfaces an "In cart" confirmation above its actions.
  final String? cartQuantityLabel;

  final IconData? unitIcon;
  final String? semanticLabel;
  final double imageHeight;
  final VoidCallback? onTap;
  final VoidCallback? onAddToCart;
  final VoidCallback? onSubscribe;

  int? get _discountPercent {
    final original = originalAmount;
    if (original == null || original <= price || original <= 0) return null;
    return (((original - price) / original) * 100).round();
  }

  String get _resolvedSemanticLabel {
    if (semanticLabel != null) return semanticLabel!;
    final buffer = StringBuffer('$name, $categoryName, ')
      ..write('${DoodhPriceTag.format(price)} rupees per $unitLabel');
    final discount = _discountPercent;
    if (discount != null) buffer.write(', $discount percent off');
    buffer.write(isAvailable ? ', available' : ', out of stock');
    final quantity = cartQuantityLabel?.trim();
    if (quantity != null && quantity.isNotEmpty) {
      buffer.write(', in cart: $quantity');
    }
    return buffer.toString();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final discount = _discountPercent;
    final quantity = cartQuantityLabel?.trim();
    final inCart = quantity != null && quantity.isNotEmpty;

    return Container(
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(DoodhRadii.lgValue),
        border: Border.all(color: DoodhColors.line.withValues(alpha: .85)),
        boxShadow: const [
          BoxShadow(
            color: Color(0x0D123D35),
            blurRadius: 20,
            offset: Offset(0, 8),
          ),
        ],
      ),
      clipBehavior: Clip.antiAlias,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            button: onTap != null,
            container: true,
            label: _resolvedSemanticLabel,
            child: ExcludeSemantics(
              child: InkWell(
                onTap: onTap,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    DoodhProductImage(
                      imageUrl: imageUrl,
                      imageProvider: imageProvider,
                      icon: unitIcon,
                      height: imageHeight,
                      semanticLabel: '$name product image',
                      borderRadius: const BorderRadius.vertical(
                        top: Radius.circular(DoodhRadii.lgValue),
                      ),
                    ),
                    Padding(
                      // Slimmer gutters so 2-up phone cards (~155px) keep a
                      // comfortable content width for name/price/actions.
                      padding: const EdgeInsets.fromLTRB(12, 10, 12, 8),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              Expanded(
                                child: Text(
                                  categoryName.toUpperCase(),
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: theme.textTheme.labelSmall?.copyWith(
                                    color: DoodhColors.muted,
                                    fontWeight: FontWeight.w700,
                                    letterSpacing: .6,
                                  ),
                                ),
                              ),
                              if (discount != null) ...[
                                const SizedBox(width: DoodhSpacing.xs),
                                DoodhChip(
                                  label: '$discount% off',
                                  tone: DoodhTone.warning,
                                ),
                              ],
                            ],
                          ),
                          const SizedBox(height: DoodhSpacing.xs),
                          Text(
                            name,
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                            style: theme.textTheme.titleMedium?.copyWith(
                              color: DoodhColors.ink,
                              fontWeight: FontWeight.w800,
                            ),
                          ),
                          const SizedBox(height: DoodhSpacing.sm),
                          Row(
                            crossAxisAlignment: CrossAxisAlignment.end,
                            children: [
                              Expanded(
                                child: DoodhPriceTag(
                                  amount: price,
                                  originalAmount: originalAmount,
                                  style: theme.textTheme.titleLarge?.copyWith(
                                    fontSize: 18,
                                    color: DoodhColors.tealDark,
                                    fontWeight: FontWeight.w900,
                                  ),
                                ),
                              ),
                              Text(
                                '/ $unitLabel',
                                style: theme.textTheme.bodySmall?.copyWith(
                                  color: DoodhColors.muted,
                                  fontWeight: FontWeight.w600,
                                ),
                              ),
                            ],
                          ),
                          const SizedBox(height: 10),
                          Align(
                            alignment: Alignment.centerLeft,
                            child: isAvailable
                                ? const DoodhChip(
                                    label: 'Available now',
                                    tone: DoodhTone.success,
                                    icon: Icons.check_circle_outline,
                                  )
                                : const DoodhChip(
                                    label: 'Out of stock',
                                    tone: DoodhTone.warning,
                                    icon: Icons.error_outline,
                                  ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 0, 12, 12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (inCart) ...[
                  Align(
                    alignment: Alignment.centerLeft,
                    child: DoodhChip(
                      label: 'In cart · $quantity',
                      tone: DoodhTone.success,
                      icon: Icons.shopping_cart_outlined,
                    ),
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                ],
                DoodhButton(
                  label: isAvailable ? 'Add to cart' : 'Unavailable',
                  icon: isAvailable
                      ? Icons.add_shopping_cart_outlined
                      : Icons.block_outlined,
                  compact: true,
                  onPressed: isAvailable ? onAddToCart : null,
                ),
                if (onSubscribe != null && isAvailable) ...[
                  const SizedBox(height: DoodhSpacing.xs),
                  TextButton.icon(
                    onPressed: onSubscribe,
                    icon: const Icon(Icons.event_repeat_outlined, size: 18),
                    label: const Text('Subscribe'),
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// A polished, token-aligned search field for product discovery.
///
/// Owns its controller only when none is supplied so callers can drive the
/// query themselves. The clear affordance keeps a 48px touch target and is
/// announced through its tooltip.
class DoodhSearchBar extends StatefulWidget {
  const DoodhSearchBar({
    super.key,
    this.controller,
    this.hint = 'Search products',
    this.onChanged,
    this.onSubmitted,
    this.onClear,
    this.autofocus = false,
  });

  final TextEditingController? controller;
  final String hint;
  final ValueChanged<String>? onChanged;
  final ValueChanged<String>? onSubmitted;
  final VoidCallback? onClear;
  final bool autofocus;

  @override
  State<DoodhSearchBar> createState() => _DoodhSearchBarState();
}

class _DoodhSearchBarState extends State<DoodhSearchBar> {
  TextEditingController? _ownedController;
  bool _hasText = false;

  TextEditingController get _controller =>
      widget.controller ?? (_ownedController ??= TextEditingController());

  @override
  void initState() {
    super.initState();
    // Listen to the controller (not just the field) so an external
    // `controller.clear()` — e.g. a "Clear filters" action elsewhere — also
    // updates the clear affordance.
    _controller.addListener(_syncHasText);
    _hasText = _controller.text.isNotEmpty;
  }

  @override
  void dispose() {
    _controller.removeListener(_syncHasText);
    _ownedController?.dispose();
    super.dispose();
  }

  void _syncHasText() {
    final hasText = _controller.text.isNotEmpty;
    if (hasText != _hasText && mounted) setState(() => _hasText = hasText);
  }

  void _handleChanged(String value) => widget.onChanged?.call(value);

  void _handleClear() {
    _controller.clear();
    widget.onChanged?.call('');
    widget.onClear?.call();
  }

  @override
  Widget build(BuildContext context) => Semantics(
    textField: true,
    label: 'Search catalogue products',
    child: TextField(
      controller: _controller,
      autofocus: widget.autofocus,
      textInputAction: TextInputAction.search,
      onChanged: _handleChanged,
      onSubmitted: widget.onSubmitted,
      decoration: InputDecoration(
        hintText: widget.hint,
        filled: true,
        fillColor: Colors.white,
        prefixIcon: const Icon(Icons.search_rounded),
        suffixIcon: _hasText
            ? IconButton(
                tooltip: 'Clear search',
                onPressed: _handleClear,
                icon: const Icon(Icons.close),
              )
            : null,
        border: OutlineInputBorder(
          borderRadius: DoodhRadii.pill,
          borderSide: const BorderSide(color: DoodhColors.line),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: DoodhRadii.pill,
          borderSide: const BorderSide(color: DoodhColors.line),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: DoodhRadii.pill,
          borderSide: const BorderSide(color: DoodhColors.teal, width: 2),
        ),
      ),
    ),
  );
}

/// A selectable category option for [DoodhCategoryRail].
class DoodhCategoryOption {
  const DoodhCategoryOption({required this.id, required this.label});

  /// `null` represents the "all categories" option.
  final String? id;
  final String label;
}

/// A horizontal, scrollable category rail with an always-present "All" option.
///
/// Keeps category discovery compact on mobile (never a full-width dropdown that
/// eats vertical space) and makes the selected category visually obvious.
class DoodhCategoryRail extends StatelessWidget {
  const DoodhCategoryRail({
    super.key,
    required this.options,
    required this.selectedId,
    required this.onSelected,
    this.allLabel = 'All categories',
  });

  final List<DoodhCategoryOption> options;
  final String? selectedId;
  final ValueChanged<String?> onSelected;
  final String allLabel;

  @override
  Widget build(BuildContext context) {
    if (options.isEmpty) return const SizedBox.shrink();
    final all = <DoodhCategoryOption>[
      DoodhCategoryOption(id: null, label: allLabel),
      ...options,
    ];
    return SizedBox(
      height: 48,
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        itemCount: all.length,
        separatorBuilder: (_, _) => const SizedBox(width: DoodhSpacing.sm),
        itemBuilder: (context, index) {
          final option = all[index];
          return _SelectableChip(
            label: option.label,
            selected: option.id == selectedId,
            onTap: () => onSelected(option.id),
          );
        },
      ),
    );
  }
}

class _SelectableChip extends StatelessWidget {
  const _SelectableChip({
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Semantics(
    container: true,
    button: true,
    selected: selected,
    label: label,
    child: ExcludeSemantics(
      child: InkWell(
        onTap: onTap,
        borderRadius: DoodhRadii.pill,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: DoodhSpacing.xs),
          child: Container(
            height: 40,
            padding: const EdgeInsets.symmetric(horizontal: DoodhSpacing.md),
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: selected ? DoodhColors.teal : Colors.white,
              borderRadius: DoodhRadii.pill,
              border: Border.all(
                color: selected ? DoodhColors.teal : DoodhColors.line,
              ),
            ),
            child: Text(
              label,
              style: Theme.of(context).textTheme.labelLarge?.copyWith(
                color: selected ? Colors.white : DoodhColors.ink,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

/// A single trust signal (icon + short label).
class DoodhTrustItem {
  const DoodhTrustItem({required this.icon, required this.label});

  final IconData icon;
  final String label;
}

/// A compact trust strip for the customer entry surface.
///
/// Content is supplied by the caller so the app never hard-codes invented
/// promotions, prices or availability claims here.
class DoodhTrustStrip extends StatelessWidget {
  const DoodhTrustStrip({super.key, required this.items});

  final List<DoodhTrustItem> items;

  @override
  Widget build(BuildContext context) {
    if (items.isEmpty) return const SizedBox.shrink();
    final theme = Theme.of(context);
    return DoodhCard(
      padding: const EdgeInsets.all(12),
      child: Wrap(
        spacing: DoodhSpacing.md,
        runSpacing: 12,
        children: [
          for (final item in items)
            Semantics(
              container: true,
              label: item.label,
              child: ExcludeSemantics(
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 32,
                      height: 32,
                      decoration: const BoxDecoration(
                        color: DoodhColors.mint,
                        borderRadius: DoodhRadii.sm,
                      ),
                      child: Icon(
                        item.icon,
                        size: 18,
                        color: DoodhColors.tealDark,
                      ),
                    ),
                    const SizedBox(width: DoodhSpacing.sm),
                    Text(
                      item.label,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }
}
