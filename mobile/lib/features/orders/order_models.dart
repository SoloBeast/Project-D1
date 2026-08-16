import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';

class OrderItemInput {
  const OrderItemInput({required this.productId, required this.quantity});

  final String productId;
  final double quantity;

  Map<String, dynamic> toJson() => {
    'productId': productId,
    'quantity': quantity,
  };
}

class CheckoutRequest {
  const CheckoutRequest({required this.addressId, required this.items});

  final String addressId;
  final List<OrderItemInput> items;

  Map<String, dynamic> toJson() => {
    'addressId': addressId,
    'items': items.map((item) => item.toJson()).toList(growable: false),
  };
}

class CheckoutLine {
  const CheckoutLine({
    required this.productId,
    required this.productName,
    required this.sku,
    required this.unitOfMeasure,
    required this.quantity,
    required this.unitPrice,
    required this.lineTotal,
  });

  factory CheckoutLine.fromJson(Map<String, dynamic> json) => CheckoutLine(
    productId: json['productId'] as String,
    productName: json['productName'] as String,
    sku: json['sku'] as String,
    unitOfMeasure: json['unitOfMeasure'] as String,
    quantity: (json['quantity'] as num).toDouble(),
    unitPrice: (json['unitPrice'] as num).toDouble(),
    lineTotal: (json['lineTotal'] as num).toDouble(),
  );

  final String productId;
  final String productName;
  final String sku;
  final String unitOfMeasure;
  final double quantity;
  final double unitPrice;
  final double lineTotal;
}

class CheckoutPreview {
  const CheckoutPreview({
    required this.addressId,
    required this.addressLabel,
    required this.addressLine1,
    required this.addressLine2,
    required this.locality,
    required this.city,
    required this.state,
    required this.pinCode,
    required this.contactName,
    required this.contactMobile,
    required this.branchId,
    required this.branchCode,
    required this.branchName,
    required this.distanceKm,
    required this.items,
    required this.subtotal,
    required this.discountAmount,
    required this.payableAmount,
  });

  factory CheckoutPreview.fromJson(Map<String, dynamic> json) =>
      CheckoutPreview(
        addressId: json['addressId'] as String,
        addressLabel: json['addressLabel'] as String,
        addressLine1: json['addressLine1'] as String,
        addressLine2: json['addressLine2'] as String?,
        locality: json['locality'] as String,
        city: json['city'] as String,
        state: json['state'] as String,
        pinCode: json['pinCode'] as String,
        contactName: json['contactName'] as String,
        contactMobile: json['contactMobile'] as String,
        branchId: json['branchId'] as String,
        branchCode: json['branchCode'] as String,
        branchName: json['branchName'] as String,
        distanceKm: (json['distanceKm'] as num).toDouble(),
        items: (json['items'] as List<dynamic>)
            .cast<Map<String, dynamic>>()
            .map(CheckoutLine.fromJson)
            .toList(growable: false),
        subtotal: (json['subtotal'] as num).toDouble(),
        discountAmount: (json['discountAmount'] as num).toDouble(),
        payableAmount: (json['payableAmount'] as num).toDouble(),
      );

  final String addressId;
  final String addressLabel;
  final String addressLine1;
  final String? addressLine2;
  final String locality;
  final String city;
  final String state;
  final String pinCode;
  final String contactName;
  final String contactMobile;
  final String branchId;
  final String branchCode;
  final String branchName;
  final double distanceKm;
  final List<CheckoutLine> items;
  final double subtotal;
  final double discountAmount;
  final double payableAmount;
}

class OrderItem {
  const OrderItem({
    required this.productId,
    required this.productName,
    required this.sku,
    required this.unitOfMeasure,
    required this.quantity,
    required this.unitPrice,
    required this.lineTotal,
  });

  factory OrderItem.fromJson(Map<String, dynamic> json) => OrderItem(
    productId: json['productId'] as String,
    productName: json['productName'] as String,
    sku: json['sku'] as String,
    unitOfMeasure: json['unitOfMeasure'] as String,
    quantity: (json['quantity'] as num).toDouble(),
    unitPrice: (json['unitPrice'] as num).toDouble(),
    lineTotal: (json['lineTotal'] as num).toDouble(),
  );

  final String productId;
  final String productName;
  final String sku;
  final String unitOfMeasure;
  final double quantity;
  final double unitPrice;
  final double lineTotal;
}

class OrderSummary {
  const OrderSummary({
    required this.publicId,
    required this.orderNumber,
    required this.type,
    required this.status,
    required this.createdAtUtc,
    required this.addressLabel,
    required this.city,
    required this.branchName,
    required this.items,
    required this.subtotal,
    required this.discountAmount,
    required this.payableAmount,
    required this.cancelledAtUtc,
  });

  factory OrderSummary.fromJson(Map<String, dynamic> json) => OrderSummary(
    publicId: json['publicId'] as String,
    orderNumber: json['orderNumber'] as String,
    type: json['type'] as String,
    status: json['status'] as String,
    createdAtUtc: DateTime.parse(json['createdAtUtc'] as String),
    addressLabel: json['addressLabel'] as String,
    city: json['city'] as String,
    branchName: json['branchName'] as String,
    items: (json['items'] as List<dynamic>)
        .cast<Map<String, dynamic>>()
        .map(OrderItem.fromJson)
        .toList(growable: false),
    subtotal: (json['subtotal'] as num).toDouble(),
    discountAmount: (json['discountAmount'] as num).toDouble(),
    payableAmount: (json['payableAmount'] as num).toDouble(),
    cancelledAtUtc: json['cancelledAtUtc'] == null
        ? null
        : DateTime.parse(json['cancelledAtUtc'] as String),
  );

  final String publicId;
  final String orderNumber;
  final String type;
  final String status;
  final DateTime createdAtUtc;
  final String addressLabel;
  final String city;
  final String branchName;
  final List<OrderItem> items;
  final double subtotal;
  final double discountAmount;
  final double payableAmount;
  final DateTime? cancelledAtUtc;

  bool get canCancel => status.toLowerCase() == 'confirmed';
  String get formattedTotal => '₹${payableAmount.toStringAsFixed(2)}';
  String get itemSummary => items
      .map((item) => '${item.productName} × ${formatQuantity(item.quantity)}')
      .join(', ');
}

class OrderCartItem {
  const OrderCartItem({required this.product, required this.quantity});

  final CatalogueProduct product;
  final double quantity;

  OrderCartItem copyWith({double? quantity}) =>
      OrderCartItem(product: product, quantity: quantity ?? this.quantity);
}

String formatOrderDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/${value.month.toString().padLeft(2, '0')}/${value.year}';
