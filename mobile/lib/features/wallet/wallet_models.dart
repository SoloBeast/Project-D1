class WalletDetails {
  const WalletDetails({
    required this.publicId,
    required this.balance,
    required this.currency,
    required this.createdAt,
    required this.updatedAt,
  });

  factory WalletDetails.fromJson(Map<String, dynamic> json) => WalletDetails(
    publicId: json['publicId'] as String,
    balance: (json['balance'] as num).toDouble(),
    currency: json['currency'] as String,
    createdAt: DateTime.parse(json['createdAt'] as String),
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );

  final String publicId;
  final double balance;
  final String currency;
  final DateTime createdAt;
  final DateTime updatedAt;

  String get formattedBalance => '₹${balance.toStringAsFixed(2)}';
}

class WalletTransaction {
  const WalletTransaction({
    required this.publicId,
    required this.type,
    required this.balanceBefore,
    required this.amount,
    required this.balanceAfter,
    required this.currency,
    required this.description,
    required this.occurredAt,
    required this.paymentId,
    required this.orderId,
  });

  factory WalletTransaction.fromJson(Map<String, dynamic> json) =>
      WalletTransaction(
        publicId: json['publicId'] as String,
        type: json['type'] as String,
        balanceBefore: (json['balanceBefore'] as num).toDouble(),
        amount: (json['amount'] as num).toDouble(),
        balanceAfter: (json['balanceAfter'] as num).toDouble(),
        currency: json['currency'] as String,
        description: json['description'] as String,
        occurredAt: DateTime.parse(json['occurredAt'] as String),
        paymentId: json['paymentId'] as String?,
        orderId: json['orderId'] as String?,
      );

  final String publicId;
  final String type;
  final double balanceBefore;
  final double amount;
  final double balanceAfter;
  final String currency;
  final String description;
  final DateTime occurredAt;
  final String? paymentId;
  final String? orderId;

  bool get isCredit => amount >= 0;
  bool get isReconciled =>
      ((balanceBefore + amount) - balanceAfter).abs() < 0.005;
  String get formattedAmount =>
      '${isCredit ? '+' : '-'}₹${amount.abs().toStringAsFixed(2)}';
}

String formatWalletDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/'
    '${value.month.toString().padLeft(2, '0')}/${value.year} '
    '${value.hour.toString().padLeft(2, '0')}:'
    '${value.minute.toString().padLeft(2, '0')}';
