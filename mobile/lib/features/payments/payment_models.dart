enum PaymentMethod {
  razorpay('Razorpay', 'Razorpay'),
  wallet('Wallet', 'DoodhDirect Wallet');

  const PaymentMethod(this.apiValue, this.label);

  final String apiValue;
  final String label;
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

class PaymentDetails {
  const PaymentDetails({
    required this.publicId,
    required this.orderId,
    required this.orderNumber,
    required this.method,
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
    orderId: json['orderId'] as String,
    orderNumber: json['orderNumber'] as String,
    method: _paymentMethod(json['method'] as String),
    status: PaymentStatus.fromApi(json['status'] as String),
    amount: (json['amount'] as num).toDouble(),
    refundedAmount: (json['refundedAmount'] as num).toDouble(),
    currency: json['currency'] as String,
    gatewayOrderId: json['gatewayOrderId'] as String?,
    gatewayPaymentId: json['gatewayPaymentId'] as String?,
    gatewayKeyId: json['gatewayKeyId'] as String?,
    failureCode: json['failureCode'] as String?,
    failureMessage: json['failureMessage'] as String?,
    expiresAtUtc: DateTime.parse(json['expiresAtUtc'] as String),
    verifiedAtUtc: json['verifiedAtUtc'] == null
        ? null
        : DateTime.parse(json['verifiedAtUtc'] as String),
    createdAtUtc: DateTime.parse(json['createdAtUtc'] as String),
  );

  final String publicId;
  final String orderId;
  final String orderNumber;
  final PaymentMethod method;
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

  bool get usesMockGateway =>
      gatewayOrderId?.startsWith('order_mock_') ?? false;
  bool get isExpiredByTime => DateTime.now().toUtc().isAfter(expiresAtUtc);
  String get formattedAmount => '₹${amount.toStringAsFixed(2)}';
}

PaymentMethod _paymentMethod(String value) => switch (value.toLowerCase()) {
  'wallet' => PaymentMethod.wallet,
  _ => PaymentMethod.razorpay,
};
