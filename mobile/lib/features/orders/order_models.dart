import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';

class OrderItemInput {
  const OrderItemInput({required this.productId, required this.quantity});

  final String productId;
  final double quantity;

  Map<String, dynamic> toJson() => {
    'productId': productId,
    'quantity': quantity,
  };
}

class CheckoutAddressDraft {
  const CheckoutAddressDraft({
    this.label,
    required this.addressLine1,
    this.addressLine2,
    required this.locality,
    required this.city,
    required this.state,
    required this.pinCode,
    this.landmark,
    this.deliveryInstructions,
    required this.contactName,
    required this.contactMobile,
    required this.latitude,
    required this.longitude,
  });

  final String? label;
  final String addressLine1;
  final String? addressLine2;
  final String locality;
  final String city;
  final String state;
  final String pinCode;
  final String? landmark;
  final String? deliveryInstructions;
  final String contactName;
  final String contactMobile;
  final double latitude;
  final double longitude;

  factory CheckoutAddressDraft.fromCustomerDraft(AddressDraft draft) =>
      CheckoutAddressDraft(
        label: draft.label,
        addressLine1: draft.addressLine1,
        addressLine2: draft.addressLine2,
        locality: draft.locality,
        city: draft.city,
        state: draft.state,
        pinCode: draft.pinCode,
        landmark: draft.landmark,
        deliveryInstructions: draft.deliveryInstructions,
        contactName: draft.contactName,
        contactMobile: draft.contactMobile,
        latitude: draft.latitude,
        longitude: draft.longitude,
      );

  Map<String, dynamic> toJson() => {
    'label': _optional(label),
    'addressLine1': addressLine1.trim(),
    'addressLine2': _optional(addressLine2),
    'locality': locality.trim(),
    'city': city.trim(),
    'state': state.trim(),
    'pinCode': pinCode.trim(),
    'landmark': _optional(landmark),
    'deliveryInstructions': _optional(deliveryInstructions),
    'contactName': contactName.trim(),
    'contactMobile': contactMobile.trim(),
    'latitude': latitude,
    'longitude': longitude,
  };
}

class CheckoutAddressSelection {
  const CheckoutAddressSelection.saved(this.addressId) : manualAddress = null;
  const CheckoutAddressSelection.manual(this.manualAddress) : addressId = null;

  final String? addressId;
  final CheckoutAddressDraft? manualAddress;

  bool get isValid => (addressId != null) != (manualAddress != null);
}

class CheckoutRequest {
  const CheckoutRequest({
    this.addressId,
    this.manualAddress,
    required this.items,
  }) : assert((addressId != null) != (manualAddress != null));

  final String? addressId;
  final CheckoutAddressDraft? manualAddress;
  final List<OrderItemInput> items;

  Map<String, dynamic> toJson() => {
    if (addressId != null) 'addressId': addressId,
    if (manualAddress != null) 'manualAddress': manualAddress!.toJson(),
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
    this.charges = const <OrderChargeLine>[],
    this.chargesTotal = 0,
    required this.payableAmount,
  });

  factory CheckoutPreview.fromJson(Map<String, dynamic> json) =>
      CheckoutPreview(
        addressId: json['addressId'] as String?,
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
        charges: (json['charges'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(OrderChargeLine.fromJson)
            .toList(growable: false),
        chargesTotal: (json['chargesTotal'] as num? ?? 0).toDouble(),
        payableAmount: (json['payableAmount'] as num).toDouble(),
      );

  final String? addressId;
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
  final List<OrderChargeLine> charges;
  final double chargesTotal;
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

/// One charge line exactly as the server applied it at checkout (or previewed
/// it). The client renders these values verbatim — it never recomputes amounts
/// from percentages, and order snapshots are historical (master edits later do
/// not change them).
class OrderChargeLine {
  const OrderChargeLine({
    required this.chargeType,
    required this.chargeCode,
    required this.description,
    required this.percentage,
    required this.baseAmount,
    required this.amount,
  });

  factory OrderChargeLine.fromJson(Map<String, dynamic> json) =>
      OrderChargeLine(
        chargeType: json['chargeType'] as String,
        chargeCode: json['chargeCode'] as String,
        description: json['description'] == null
            ? null
            : json['description'] as String,
        percentage: (json['percentage'] as num).toDouble(),
        baseAmount: (json['baseAmount'] as num).toDouble(),
        amount: (json['amount'] as num).toDouble(),
      );

  final String chargeType;
  final String chargeCode;
  final String? description;
  final double percentage;
  final double baseAmount;
  final double amount;

  /// Consumer-facing label: the configured description when present, otherwise
  /// a safe fallback built from the master fields (never internal IDs).
  String get displayLabel {
    final normalized = description?.trim();
    if (normalized != null && normalized.isNotEmpty) return normalized;
    return chargeType.trim().isEmpty ? chargeCode : chargeType;
  }

  String get formattedAmount => '₹${amount.toStringAsFixed(2)}';

  String get formattedPercentage => percentage == percentage.roundToDouble()
      ? '${percentage.round()}%'
      : '${percentage.toStringAsFixed(2)}%';
}

class OrderSummary {
  const OrderSummary({
    required this.publicId,
    required this.orderNumber,
    required this.type,
    required this.status,
    required this.createdAt,
    required this.addressLabel,
    required this.city,
    required this.branchName,
    required this.items,
    required this.subtotal,
    required this.discountAmount,
    this.charges = const <OrderChargeLine>[],
    this.chargesTotal = 0,
    required this.payableAmount,
    required this.cancelledAt,
    required this.paymentPublicId,
    required this.paymentStatus,
    required this.gatewayPaymentId,
    required this.deliveryPublicId,
    required this.deliveryReferenceNumber,
    required this.deliveryStatus,
  });

  factory OrderSummary.fromJson(Map<String, dynamic> json) => OrderSummary(
    publicId: json['publicId'] as String,
    orderNumber: json['orderNumber'] as String,
    type: json['type'] as String,
    status: json['status'] as String,
    createdAt: DateTime.parse(json['createdAt'] as String),
    addressLabel: json['addressLabel'] as String,
    city: json['city'] as String,
    branchName: json['branchName'] as String,
    items: (json['items'] as List<dynamic>)
        .cast<Map<String, dynamic>>()
        .map(OrderItem.fromJson)
        .toList(growable: false),
    subtotal: (json['subtotal'] as num).toDouble(),
    discountAmount: (json['discountAmount'] as num).toDouble(),
    // Historical snapshot: the frozen charge lines from checkout time. Never
    // re-derived from the current master.
    charges: (json['charges'] as List<dynamic>? ?? const [])
        .cast<Map<String, dynamic>>()
        .map(OrderChargeLine.fromJson)
        .toList(growable: false),
    chargesTotal: (json['chargesTotal'] as num? ?? 0).toDouble(),
    payableAmount: (json['payableAmount'] as num).toDouble(),
    cancelledAt: json['cancelledAt'] == null
        ? null
        : DateTime.parse(json['cancelledAt'] as String),
    paymentPublicId: json['paymentPublicId'] as String?,
    paymentStatus: json['paymentStatus'] as String?,
    gatewayPaymentId: json['gatewayPaymentId'] as String?,
    deliveryPublicId: json['deliveryPublicId'] as String?,
    deliveryReferenceNumber: json['deliveryReferenceNumber'] as String?,
    deliveryStatus: json['deliveryStatus'] as String?,
  );

  final String publicId;
  final String orderNumber;
  final String type;
  final String status;
  final DateTime createdAt;
  final String addressLabel;
  final String city;
  final String branchName;
  final List<OrderItem> items;
  final double subtotal;
  final double discountAmount;
  final List<OrderChargeLine> charges;
  final double chargesTotal;
  final double payableAmount;
  final DateTime? cancelledAt;
  final String? paymentPublicId;
  final String? paymentStatus;
  final String? gatewayPaymentId;
  final String? deliveryPublicId;
  final String? deliveryReferenceNumber;
  final String? deliveryStatus;

  bool get canCancel => status.toLowerCase() == 'confirmed';
  String get formattedTotal => '₹${payableAmount.toStringAsFixed(2)}';
  String get itemSummary => items
      .map((item) => '${item.productName} × ${formatQuantity(item.quantity)}')
      .join(', ');
}

class OrderCartItem {
  const OrderCartItem({required this.product, required this.quantity});

  factory OrderCartItem.fromJson(Map<String, dynamic> json) => OrderCartItem(
    product: CatalogueProduct.fromJson(json['product'] as Map<String, dynamic>),
    quantity: (json['quantity'] as num).toDouble(),
  );

  final CatalogueProduct product;
  final double quantity;

  Map<String, dynamic> toJson() => {
    'product': product.toJson(),
    'quantity': quantity,
  };

  OrderCartItem copyWith({double? quantity}) =>
      OrderCartItem(product: product, quantity: quantity ?? this.quantity);
}

String? _optional(String? value) {
  final normalized = value?.trim();
  return normalized == null || normalized.isEmpty ? null : normalized;
}

String formatOrderDate(DateTime value) {
  final local = value;
  final hour = local.hour % 12 == 0 ? 12 : local.hour % 12;
  final period = local.hour < 12 ? 'AM' : 'PM';
  const months = <String>[
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ];
  return '${local.day.toString().padLeft(2, '0')}-'
      '${months[local.month - 1]}-${local.year} '
      '${hour.toString().padLeft(2, '0')}:'
      '${local.minute.toString().padLeft(2, '0')} $period';
}
