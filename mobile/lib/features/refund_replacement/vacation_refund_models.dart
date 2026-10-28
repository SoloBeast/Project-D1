/// Consolidated end-of-subscription vacation-refund request models.
///
/// A vacation refund is one request per completed subscription containing
/// every eligible vacation-skipped occurrence. The UI renders the frozen
/// server-computed money exactly as received and never reprices it.
enum VacationRefundStatus {
  pending,
  approved,
  rejected,
  completed,
  unknown;

  static VacationRefundStatus parse(Object? value) => switch (value) {
    'Pending' || 'pending' => pending,
    'Approved' || 'approved' => approved,
    'Rejected' || 'rejected' => rejected,
    'Completed' || 'completed' => completed,
    _ => unknown,
  };

  String get label => switch (this) {
    pending => 'Pending',
    approved => 'Approved',
    rejected => 'Rejected',
    completed => 'Completed',
    unknown => 'Unknown',
  };

  bool get isTerminal => this == rejected || this == completed;

  /// Only a completed request carries a wallet credit. Pending and approved
  /// requests must never be presented as issued refunds.
  bool get refundIssued => this == completed;
}

class VacationRefundOccurrence {
  const VacationRefundOccurrence({
    required this.occurrencePublicId,
    required this.scheduledDate,
    required this.quantity,
    required this.allocationAmount,
  });

  factory VacationRefundOccurrence.fromJson(Map<String, dynamic> json) =>
      VacationRefundOccurrence(
        occurrencePublicId: json['occurrencePublicId'] as String,
        scheduledDate: DateTime.parse(json['scheduledDate'] as String),
        quantity: (json['quantity'] as num).toDouble(),
        allocationAmount: (json['allocationAmount'] as num).toDouble(),
      );

  final String occurrencePublicId;
  final DateTime scheduledDate;
  final double quantity;
  final double allocationAmount;
}

class VacationRefundRequest {
  const VacationRefundRequest({
    required this.requestId,
    required this.requestNumber,
    required this.subscriptionPublicId,
    required this.customerId,
    required this.customerName,
    required this.customerMobile,
    required this.branchId,
    required this.branchCode,
    required this.branchName,
    required this.reason,
    required this.status,
    required this.occurrenceCount,
    required this.grossPerOccurrence,
    required this.totalAmount,
    required this.deductionPercentage,
    required this.payableAmountSnapshot,
    required this.totalEntitlementSnapshot,
    required this.currency,
    required this.submittedAt,
    required this.occurrences,
    this.decidedByUserId,
    this.decidedByName,
    this.decidedAt,
    this.decisionRemarks,
    this.completedByUserId,
    this.completedByName,
    this.completedAt,
    this.completionRemarks,
    this.isEmployeeOriginated = false,
  });

  factory VacationRefundRequest.fromJson(Map<String, dynamic> json) =>
      VacationRefundRequest(
        requestId: json['requestId'] as String,
        requestNumber: json['requestNumber'] as String,
        subscriptionPublicId: json['subscriptionPublicId'] as String,
        customerId: (json['customerId'] as num).toInt(),
        customerName: json['customerName'] as String,
        customerMobile: json['customerMobile'] as String,
        branchId: (json['branchId'] as num).toInt(),
        branchCode: json['branchCode'] as String,
        branchName: json['branchName'] as String,
        reason: json['reason'] as String,
        status: VacationRefundStatus.parse(json['status']),
        occurrenceCount: (json['occurrenceCount'] as num).toInt(),
        grossPerOccurrence: (json['grossPerOccurrence'] as num).toDouble(),
        totalAmount: (json['totalAmount'] as num).toDouble(),
        deductionPercentage: (json['deductionPercentage'] as num).toDouble(),
        payableAmountSnapshot:
            (json['payableAmountSnapshot'] as num).toDouble(),
        totalEntitlementSnapshot:
            (json['totalEntitlementSnapshot'] as num).toInt(),
        currency: json['currency'] as String,
        submittedAt: DateTime.parse(json['submittedAt'] as String),
        occurrences:
            ((json['occurrences'] as List<dynamic>?) ?? const [])
                .cast<Map<String, dynamic>>()
                .map(VacationRefundOccurrence.fromJson)
                .toList(growable: false),
        decidedByUserId: (json['decidedByUserId'] as num?)?.toInt(),
        decidedByName: json['decidedByName'] as String?,
        decidedAt: json['decidedAt'] == null
            ? null
            : DateTime.parse(json['decidedAt'] as String),
        decisionRemarks: json['decisionRemarks'] as String?,
        completedByUserId: (json['completedByUserId'] as num?)?.toInt(),
        completedByName: json['completedByName'] as String?,
        completedAt: json['completedAt'] == null
            ? null
            : DateTime.parse(json['completedAt'] as String),
        completionRemarks: json['completionRemarks'] as String?,
        isEmployeeOriginated:
            json['isEmployeeOriginated'] as bool? ?? false,
      );

  final String requestId;
  final String requestNumber;
  final String subscriptionPublicId;
  final int customerId;
  final String customerName;
  final String customerMobile;
  final int branchId;
  final String branchCode;
  final String branchName;
  final String reason;
  final VacationRefundStatus status;
  final int occurrenceCount;
  final double grossPerOccurrence;
  final double totalAmount;
  final double deductionPercentage;
  final double payableAmountSnapshot;
  final int totalEntitlementSnapshot;
  final String currency;
  final DateTime submittedAt;
  final List<VacationRefundOccurrence> occurrences;
  final int? decidedByUserId;
  final String? decidedByName;
  final DateTime? decidedAt;
  final String? decisionRemarks;
  final int? completedByUserId;
  final String? completedByName;
  final DateTime? completedAt;
  final String? completionRemarks;

  /// Server-derived origin indicator: true when the owning account is a
  /// staff account. Staff UI gating only — execution revalidates inside
  /// the backend service guard.
  final bool isEmployeeOriginated;
}

/// Customer-facing vacation-refund state for one subscription, served by
/// `GET /api/v1/subscriptions/{id}/vacation-refund-status`.
///
/// When [request] is present it is the authoritative review state (rendered
/// with [VacationRefundCard]). When it is absent after the term has ended,
/// the counts and [blockedReason] explain whether evaluation is still
/// pending or is held up — instead of an empty history with no context.
class VacationRefundSubscriptionStatus {
  const VacationRefundSubscriptionStatus({
    required this.subscriptionPublicId,
    required this.termEnded,
    required this.processed,
    required this.request,
    required this.vacationSkippedCount,
    required this.unresolvedInTermCount,
    required this.hasBlockingTicket,
    required this.hasBackingPayment,
    required this.blockedReason,
  });

  factory VacationRefundSubscriptionStatus.fromJson(
    Map<String, dynamic> json,
  ) =>
      VacationRefundSubscriptionStatus(
        subscriptionPublicId: json['subscriptionPublicId'] as String,
        termEnded: json['termEnded'] as bool,
        processed: json['processed'] as bool,
        request: json['request'] is Map<String, dynamic>
            ? VacationRefundRequest.fromJson(
                json['request'] as Map<String, dynamic>,
              )
            : null,
        vacationSkippedCount: (json['vacationSkippedCount'] as num).toInt(),
        unresolvedInTermCount: (json['unresolvedInTermCount'] as num).toInt(),
        hasBlockingTicket: json['hasBlockingTicket'] as bool,
        hasBackingPayment: json['hasBackingPayment'] as bool,
        blockedReason: json['blockedReason'] as String?,
      );

  final String subscriptionPublicId;
  final bool termEnded;
  final bool processed;
  final VacationRefundRequest? request;
  final int vacationSkippedCount;
  final int unresolvedInTermCount;
  final bool hasBlockingTicket;
  final bool hasBackingPayment;
  final String? blockedReason;
}
