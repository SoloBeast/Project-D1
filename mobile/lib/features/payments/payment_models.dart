enum PaymentMethod {
  razorpay('Razorpay', 'Razorpay'),
  wallet('Wallet', 'DoodhDirect Wallet'),
  development('Development', 'Development payment');

  const PaymentMethod(this.apiValue, this.label);

  final String apiValue;
  final String label;

  String get selectionLabel => label;
}

enum PaymentStatus {
  initiated,
  pending,
  success,
  failed,
  expired,
  refundPending,
  partiallyRefunded,
  refunded,
  unknown;

  factory PaymentStatus.fromApi(String value) => switch (value.toLowerCase()) {
    'initiated' => PaymentStatus.initiated,
    'pending' => PaymentStatus.pending,
    'success' => PaymentStatus.success,
    'failed' => PaymentStatus.failed,
    'expired' => PaymentStatus.expired,
    'refundpending' => PaymentStatus.refundPending,
    'partiallyrefunded' => PaymentStatus.partiallyRefunded,
    'refunded' => PaymentStatus.refunded,
    _ => PaymentStatus.unknown,
  };

  bool get isSuccessful =>
      this == PaymentStatus.success ||
      this == PaymentStatus.partiallyRefunded ||
      this == PaymentStatus.refunded;

  bool get isPending =>
      this == PaymentStatus.initiated ||
      this == PaymentStatus.pending ||
      this == PaymentStatus.refundPending;

  bool get isTerminalFailure =>
      this == PaymentStatus.failed || this == PaymentStatus.expired;
}

class PaymentCapability {
  const PaymentCapability({
    required this.method,
    required this.provider,
    required this.label,
    required this.isAvailable,
    required this.unavailableReason,
  });

  factory PaymentCapability.fromJson(Map<String, dynamic> json) =>
      PaymentCapability(
        method: _paymentMethod(json['method'] as String),
        provider: json['provider'] as String,
        label: json['label'] as String,
        isAvailable: json['isAvailable'] as bool,
        unavailableReason: json['unavailableReason'] as String?,
      );

  final PaymentMethod method;
  final String provider;
  final String label;
  final bool isAvailable;
  final String? unavailableReason;
}

class PaymentDetails {
  const PaymentDetails({
    required this.publicId,
    required this.orderId,
    required this.orderNumber,
    required this.subscriptionId,
    required this.method,
    required this.provider,
    required this.status,
    required this.amount,
    required this.refundedAmount,
    required this.currency,
    required this.gatewayOrderId,
    required this.gatewayPaymentId,
    required this.gatewayKeyId,
    required this.failureCode,
    required this.failureMessage,
    required this.expiresAtUtc,
    required this.verifiedAtUtc,
    required this.createdAtUtc,
  });

  factory PaymentDetails.fromJson(Map<String, dynamic> json) => PaymentDetails(
    publicId: json['publicId'] as String,
    orderId: json['orderId'] as String?,
    orderNumber: json['orderNumber'] as String?,
    subscriptionId: json['subscriptionId'] as String?,
    method: _paymentMethod(json['method'] as String),
    provider: json['provider'] as String,
    status: PaymentStatus.fromApi(json['status'] as String),
    amount: (json['amount'] as num).toDouble(),
    refundedAmount: (json['refundedAmount'] as num).toDouble(),
    currency: json['currency'] as String,
    gatewayOrderId: json['gatewayOrderId'] as String?,
    gatewayPaymentId: json['gatewayPaymentId'] as String?,
    gatewayKeyId: json['gatewayKeyId'] as String?,
    failureCode: json['failureCode'] as String?,
    failureMessage: json['failureMessage'] as String?,
    expiresAtUtc: _parseAbsoluteTimestamp(json, 'expiresAtUtc', 'expiresAt'),
    verifiedAtUtc: _parseOptionalAbsoluteTimestamp(
      json,
      'verifiedAtUtc',
      'verifiedAt',
    ),
    createdAtUtc: _parseAbsoluteTimestamp(json, 'createdAtUtc', 'createdAt'),
  );

  final String publicId;
  final String? orderId;
  final String? orderNumber;
  final String? subscriptionId;
  final PaymentMethod method;
  final String provider;
  final PaymentStatus status;
  final double amount;
  final double refundedAmount;
  final String currency;
  final String? gatewayOrderId;
  final String? gatewayPaymentId;
  final String? gatewayKeyId;
  final String? failureCode;
  final String? failureMessage;
  final DateTime expiresAtUtc;
  final DateTime? verifiedAtUtc;
  final DateTime createdAtUtc;

  bool get isOrderPayment => orderId != null && subscriptionId == null;
  bool get isSubscriptionPayment => subscriptionId != null && orderId == null;
  bool get hasValidTarget => isOrderPayment || isSubscriptionPayment;
  bool get usesRazorpay =>
      method == PaymentMethod.razorpay && provider.toLowerCase() == 'razorpay';
  bool get usesDevelopmentMock =>
      method == PaymentMethod.development && provider.toLowerCase() == 'mock';
  bool get isExpiredByTime => DateTime.now().toUtc().isAfter(expiresAtUtc);
  String get formattedAmount => '₹${amount.toStringAsFixed(2)}';
  String get targetLabel => isSubscriptionPayment
      ? 'subscription'
      : orderNumber == null
      ? 'order'
      : 'order $orderNumber';
}

DateTime _parseAbsoluteTimestamp(
  Map<String, dynamic> json,
  String preferredKey,
  String compatibilityKey,
) {
  final value = json[preferredKey] ?? json[compatibilityKey];
  if (value is! String) {
    throw FormatException(
      'Missing or invalid absolute timestamp: $preferredKey',
    );
  }
  return DateTime.parse(value).toUtc();
}

DateTime? _parseOptionalAbsoluteTimestamp(
  Map<String, dynamic> json,
  String preferredKey,
  String compatibilityKey,
) {
  final value = json.containsKey(preferredKey)
      ? json[preferredKey]
      : json[compatibilityKey];
  if (value == null) return null;
  if (value is! String) {
    throw FormatException(
      'Invalid absolute timestamp: $preferredKey',
    );
  }
  return DateTime.parse(value).toUtc();
}

PaymentMethod _paymentMethod(String value) => switch (value.toLowerCase()) {
  'wallet' => PaymentMethod.wallet,
  'development' => PaymentMethod.development,
  'razorpay' => PaymentMethod.razorpay,
  _ => throw FormatException('Unknown payment method: $value'),
};
