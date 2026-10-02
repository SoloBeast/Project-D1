import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
import 'package:flutter/material.dart';

import 'catalogue_models.dart';

/// Maps the catalogue feature model onto the shared, reusable
/// [DoodhProductCard].
///
/// This is a presentation-only adapter: pricing, availability, discount and
/// cart state all come from the server-authoritative model, so it never
/// computes or invents those values. It is shared by the catalogue grid and the
/// customer home discovery sections so both surfaces always render an
/// identical product card.
class CatalogueProductCard extends StatelessWidget {
  const CatalogueProductCard({
    super.key,
    required this.product,
    required this.onOpen,
    required this.onAddToCart,
    required this.onSubscribe,
    this.cartQuantity,
    this.imageHeight = 132,
  });

  final CatalogueProduct product;
  final VoidCallback onOpen;
  final VoidCallback onAddToCart;
  final VoidCallback onSubscribe;

  /// Quantity already in the cart for this product, if any.
  final double? cartQuantity;

  final double imageHeight;

  @override
  Widget build(BuildContext context) => DoodhProductCard(
    name: product.name,
    categoryName: product.category.name,
    price: product.price,
    unitLabel: product.unitLabel,
    imageUrl: product.usableImageUrl,
    isAvailable: product.isAvailable,
    unitIcon: doodhProductUnitIcon(product.unitOfMeasure),
    imageHeight: imageHeight,
    cartQuantityLabel: cartQuantity == null
        ? null
        : '${formatQuantity(cartQuantity!)} ${product.unitLabel}',
    onTap: onOpen,
    onAddToCart: onAddToCart,
    onSubscribe: onSubscribe,
  );
}
