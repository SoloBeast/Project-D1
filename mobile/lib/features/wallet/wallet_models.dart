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

enum WithdrawalStatus { pending, completed, rejected }

WithdrawalStatus withdrawalStatusFromApi(String value) => switch (value) {
  'Pending' => WithdrawalStatus.pending,
  'Completed' => WithdrawalStatus.completed,
  'Rejected' => WithdrawalStatus.rejected,
  _ => throw FormatException('Unknown withdrawal status: $value'),
};

class WithdrawalRequest {
  const WithdrawalRequest({
    required this.publicId,
    required this.requestNumber,
    required this.amount,
    required this.currency,
    required this.status,
    required this.maskedDestination,
    required this.bankName,
    this.decisionRemarks,
    this.transferReference,
    required this.createdAt,
    this.decidedAt,
  });

  factory WithdrawalRequest.fromJson(Map<String, dynamic> json) =>
      WithdrawalRequest(
        publicId: json['publicId'] as String,
        requestNumber: json['requestNumber'] as String,
        amount: (json['amount'] as num).toDouble(),
        currency: json['currency'] as String,
        status: withdrawalStatusFromApi(json['status'] as String),
        maskedDestination: json['maskedDestination'] as String,
        bankName: json['bankNameSnapshot'] as String,
        decisionRemarks: json['decisionRemarks'] as String?,
        transferReference: json['transferReference'] as String?,
        createdAt: DateTime.parse(json['createdAt'] as String),
        decidedAt: json['decidedAt'] == null
            ? null
            : DateTime.parse(json['decidedAt'] as String),
      );

  final String publicId;
  final String requestNumber;
  final double amount;
  final String currency;
  final WithdrawalStatus status;
  final String maskedDestination;
  final String bankName;
  final String? decisionRemarks;
  final String? transferReference;
  final DateTime createdAt;
  final DateTime? decidedAt;

  bool get isPending => status == WithdrawalStatus.pending;
  bool get isCompleted => status == WithdrawalStatus.completed;
  String get formattedAmount => '₹${amount.toStringAsFixed(2)}';
  String get statusLabel => switch (status) {
    WithdrawalStatus.pending => 'Pending review',
    WithdrawalStatus.completed => 'Paid',
    WithdrawalStatus.rejected => 'Rejected',
  };
}

String? validateWithdrawalAmount(String? value, double availableBalance) {
  final normalized = value?.trim() ?? '';
  if (normalized.isEmpty) return 'Enter an amount.';
  final parsed = double.tryParse(normalized);
  if (parsed == null) return 'Enter a valid amount.';
  if (parsed <= 0) return 'Amount must be greater than zero.';
  final rounded = double.parse(parsed.toStringAsFixed(2));
  if ((rounded - parsed).abs() > 1e-9) {
    return 'Amount supports at most two decimal places.';
  }
  if (parsed > availableBalance) {
    return 'Amount cannot exceed your available balance '
        'of ₹${availableBalance.toStringAsFixed(2)}.';
  }
  return null;
}

String formatWalletDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/'
    '${value.month.toString().padLeft(2, '0')}/${value.year} '
    '${value.hour.toString().padLeft(2, '0')}:'
    '${value.minute.toString().padLeft(2, '0')}';
