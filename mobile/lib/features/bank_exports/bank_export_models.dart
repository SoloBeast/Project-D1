/// Stage 3: manual-bank refund export batches (finance only).
///
/// All amounts and account details shown here come from frozen batch/item
/// snapshots. Masked account numbers are display-only; full numbers exist
/// solely inside the downloaded CSV bytes.
class BankExportCandidate {
  const BankExportCandidate({
    required this.ticketId,
    required this.requestNumber,
    this.orderNumber,
    required this.customerDisplayName,
    required this.reservedAmount,
    required this.reservedCurrency,
    required this.reservationCount,
    required this.companyBankConfigured,
  });

  factory BankExportCandidate.fromJson(Map<String, dynamic> json) =>
      BankExportCandidate(
        ticketId: json['ticketId'] as String,
        requestNumber: json['requestNumber'] as String,
        orderNumber: json['orderNumber'] as String?,
        customerDisplayName: json['customerDisplayName'] as String,
        reservedAmount: (json['reservedAmount'] as num).toDouble(),
        reservedCurrency: json['reservedCurrency'] as String,
        reservationCount: json['reservationCount'] as int,
        companyBankConfigured: json['companyBankConfigured'] as bool? ?? true,
      );

  final String ticketId;
  final String requestNumber;
  final String? orderNumber;
  final String customerDisplayName;
  final double reservedAmount;
  final String reservedCurrency;
  final int reservationCount;
  final bool companyBankConfigured;
}

class BankExportItem {
  const BankExportItem({
    required this.itemId,
    required this.ticketId,
    required this.requestNumber,
    this.orderNumber,
    required this.customerDisplayName,
    required this.amount,
    required this.currency,
    required this.status,
    required this.beneficiaryAccountHolderName,
    required this.beneficiaryBankName,
    required this.beneficiaryMaskedAccountNumber,
    required this.beneficiaryIfsc,
    required this.claimedAt,
    this.generatedAt,
    this.submittedAt,
    this.paidAt,
    this.transferReference,
    this.failureReason,
    this.voidReason,
    this.releaseReason,
  });

  factory BankExportItem.fromJson(Map<String, dynamic> json) =>
      BankExportItem(
        itemId: json['itemId'] as String,
        ticketId: json['ticketId'] as String,
        requestNumber: json['requestNumber'] as String,
        orderNumber: json['orderNumber'] as String?,
        customerDisplayName: json['customerDisplayName'] as String,
        amount: (json['amount'] as num).toDouble(),
        currency: json['currency'] as String,
        status: json['status'] as String,
        beneficiaryAccountHolderName:
            json['beneficiaryAccountHolderName'] as String,
        beneficiaryBankName: json['beneficiaryBankName'] as String,
        beneficiaryMaskedAccountNumber:
            json['beneficiaryMaskedAccountNumber'] as String,
        beneficiaryIfsc: json['beneficiaryIfsc'] as String,
        claimedAt: json['claimedAt'] as String,
        generatedAt: json['generatedAt'] as String?,
        submittedAt: json['submittedAt'] as String?,
        paidAt: json['paidAt'] as String?,
        transferReference: json['transferReference'] as String?,
        failureReason: json['failureReason'] as String?,
        voidReason: json['voidReason'] as String?,
        releaseReason: json['releaseReason'] as String?,
      );

  final String itemId;
  final String ticketId;
  final String requestNumber;
  final String? orderNumber;
  final String customerDisplayName;
  final double amount;
  final String currency;
  final String status;
  final String beneficiaryAccountHolderName;
  final String beneficiaryBankName;
  final String beneficiaryMaskedAccountNumber;
  final String beneficiaryIfsc;
  final String claimedAt;
  final String? generatedAt;
  final String? submittedAt;
  final String? paidAt;
  final String? transferReference;
  final String? failureReason;
  final String? voidReason;
  final String? releaseReason;

  bool get isTerminal =>
      status == 'Paid' ||
      status == 'Failed' ||
      status == 'Voided' ||
      status == 'Released';
  bool get canConfirm => status == 'Generated' || status == 'Submitted';
  bool get canRelease => status == 'Claimed' || status == 'Generated';
}

class BankExportBatch {
  const BankExportBatch({
    required this.batchId,
    required this.batchNumber,
    required this.status,
    required this.createdAt,
    this.generatedAt,
    this.submittedAt,
    this.closedAt,
    this.voidReason,
    required this.sourceAccountHolderName,
    required this.sourceBankName,
    required this.sourceMaskedAccountNumber,
    required this.sourceIfsc,
    required this.itemCount,
    required this.totalAmount,
    this.currency,
    required this.items,
  });

  factory BankExportBatch.fromJson(Map<String, dynamic> json) =>
      BankExportBatch(
        batchId: json['batchId'] as String,
        batchNumber: json['batchNumber'] as String,
        status: json['status'] as String,
        createdAt: json['createdAt'] as String,
        generatedAt: json['generatedAt'] as String?,
        submittedAt: json['submittedAt'] as String?,
        closedAt: json['closedAt'] as String?,
        voidReason: json['voidReason'] as String?,
        sourceAccountHolderName: json['sourceAccountHolderName'] as String,
        sourceBankName: json['sourceBankName'] as String,
        sourceMaskedAccountNumber:
            json['sourceMaskedAccountNumber'] as String,
        sourceIfsc: json['sourceIfsc'] as String,
        itemCount: json['itemCount'] as int,
        totalAmount: (json['totalAmount'] as num).toDouble(),
        currency: json['currency'] as String?,
        items:
            ((json['items'] as List<dynamic>?) ?? const [])
                .map((e) => BankExportItem.fromJson(e as Map<String, dynamic>))
                .toList(),
      );

  final String batchId;
  final String batchNumber;
  final String status;
  final String createdAt;
  final String? generatedAt;
  final String? submittedAt;
  final String? closedAt;
  final String? voidReason;
  final String sourceAccountHolderName;
  final String sourceBankName;
  final String sourceMaskedAccountNumber;
  final String sourceIfsc;
  final int itemCount;
  final double totalAmount;
  final String? currency;
  final List<BankExportItem> items;

  bool get isTerminal => status == 'Closed' || status == 'Voided';
}
