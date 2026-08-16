class WalletDetails {
  const WalletDetails({
    required this.publicId,
    required this.balance,
    required this.currency,
    required this.createdAtUtc,
    required this.updatedAtUtc,
  });

  factory WalletDetails.fromJson(Map<String, dynamic> json) => WalletDetails(
    publicId: json['publicId'] as String,
    balance: (json['balance'] as num).toDouble(),
    currency: json['currency'] as String,
    createdAtUtc: DateTime.parse(json['createdAtUtc'] as String),
    updatedAtUtc: DateTime.parse(json['updatedAtUtc'] as String),
  );

  final String publicId;
  final double balance;
  final String currency;
  final DateTime createdAtUtc;
  final DateTime updatedAtUtc;

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
    required this.occurredAtUtc,
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
        occurredAtUtc: DateTime.parse(json['occurredAtUtc'] as String),
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
  final DateTime occurredAtUtc;
  final String? paymentId;
  final String? orderId;

  bool get isCredit => amount >= 0;
  bool get isReconciled =>
      ((balanceBefore + amount) - balanceAfter).abs() < 0.005;
  String get formattedAmount =>
      '${isCredit ? '+' : '-'}₹${amount.abs().toStringAsFixed(2)}';
}

String formatWalletDate(DateTime value) {
  final local = value.toLocal();
  return '${local.day.toString().padLeft(2, '0')}/'
      '${local.month.toString().padLeft(2, '0')}/${local.year} '
      '${local.hour.toString().padLeft(2, '0')}:'
      '${local.minute.toString().padLeft(2, '0')}';
}
