enum RefundReplacementType {
  refund,
  replacement,
  unknown;

  static RefundReplacementType parse(Object? value) => switch (value) {
    'Refund' || 'refund' => refund,
    'Replacement' || 'replacement' => replacement,
    _ => unknown,
  };

  String get label => switch (this) {
    refund => 'Refund',
    replacement => 'Replacement',
    unknown => 'Unknown',
  };
}

enum RefundReplacementStatus {
  pending,
  awaitingCustomerChoice,
  approved,
  rejected,
  completed,
  failed,
  // D1-D slice 7.1: subscription cancelled while an approved re-delivery
  // replacement was still pending — voided and refunded without delivery.
  // Never a successful fulfillment (completed) nor a replacement failure
  // (failed); the two cancellation/failure refunds stay distinguishable.
  cancelled,
  unknown;

  static RefundReplacementStatus parse(Object? value) => switch (value) {
    'Pending' || 'pending' => pending,
    'AwaitingCustomerChoice' ||
    'awaitingCustomerChoice' => awaitingCustomerChoice,
    'Approved' || 'approved' => approved,
    'Rejected' || 'rejected' => rejected,
    'Completed' || 'completed' => completed,
    'Failed' || 'failed' => failed,
    'Cancelled' || 'cancelled' => cancelled,
    _ => unknown,
  };

  String get label => switch (this) {
    pending => 'Pending',
    awaitingCustomerChoice => 'Awaiting choice',
    approved => 'Approved',
    rejected => 'Rejected',
    completed => 'Completed',
    failed => 'Closed without delivery',
    cancelled => 'Cancelled (refunded)',
    unknown => 'Unknown',
  };

  bool get isTerminal =>
      this == RefundReplacementStatus.rejected ||
      this == RefundReplacementStatus.completed ||
      this == RefundReplacementStatus.failed ||
      this == RefundReplacementStatus.cancelled;

  bool get isAwaitingChoice => this == awaitingCustomerChoice;
}

/// D1-B: the customer's explicit remedy selection on a delivery-fail ticket.
/// Null until the customer chooses; immutable once recorded. The UI never
/// computes money, deductions, or replacement deliveries from this value.
enum CustomerChoice {
  refund,
  reDelivery,
  unknown;

  static CustomerChoice? parse(Object? value) => switch (value) {
    null => null,
    'Refund' || 'refund' => refund,
    'ReDelivery' || 'reDelivery' || 'redelivery' => reDelivery,
    _ => unknown,
  };

  String get label => switch (this) {
    refund => 'Refund',
    reDelivery => 'Re-delivery',
    unknown => 'Unknown',
  };

  String get wireValue => switch (this) {
    refund => 'Refund',
    reDelivery => 'ReDelivery',
    unknown => 'Refund',
  };
}

enum RefundReplacementSource {
  customerPostDelivery,
  milkTestRejected,
  deliveryFailed,
  unknown;

  static RefundReplacementSource parse(Object? value) => switch (value) {
    'CustomerPostDelivery' || 'customerPostDelivery' => customerPostDelivery,
    'MilkTestRejected' || 'milkTestRejected' => milkTestRejected,
    'DeliveryFailed' || 'deliveryFailed' => deliveryFailed,
    _ => unknown,
  };

  String get label => switch (this) {
    customerPostDelivery => 'Requested after delivery',
    milkTestRejected => 'Milk test rejected',
    deliveryFailed => 'Delivery failed',
    unknown => 'Unknown',
  };
}

/// Server-authoritative eligibility for the customer UI. The UI never computes
/// the window itself; it renders this result and disables/hides entry when
/// [isEligible] is false.
class RefundReplacementEligibility {
  const RefundReplacementEligibility({
    required this.isEligible,
    required this.ineligibleReason,
    required this.windowHours,
    required this.deadlineUtc,
    required this.hasActiveRequest,
    required this.proofImageRequired,
    required this.isMilkTestRejectedFlow,
  });

  factory RefundReplacementEligibility.fromJson(Map<String, dynamic> json) =>
      RefundReplacementEligibility(
        isEligible: json['isEligible'] as bool? ?? false,
        ineligibleReason: json['ineligibleReason'] as String?,
        windowHours: (json['windowHours'] as num?)?.toInt() ?? 0,
        deadlineUtc: _optionalProtocolDate(json, 'deadline', 'deadlineUtc'),
        hasActiveRequest: json['hasActiveRequest'] as bool? ?? false,
        proofImageRequired: json['proofImageRequired'] as bool? ?? false,
        isMilkTestRejectedFlow:
            json['isMilkTestRejectedFlow'] as bool? ?? false,
      );

  final bool isEligible;
  final String? ineligibleReason;
  final int windowHours;
  final DateTime? deadlineUtc;
  final bool hasActiveRequest;
  final bool proofImageRequired;
  final bool isMilkTestRejectedFlow;
}

class RefundReplacementImage {
  const RefundReplacementImage({
    required this.imageId,
    required this.fileName,
    required this.contentType,
    required this.fileSize,
    required this.uploadedAtUtc,
  });

  factory RefundReplacementImage.fromJson(Map<String, dynamic> json) =>
      RefundReplacementImage(
        imageId: json['imageId'] as String,
        fileName: json['fileName'] as String,
        contentType: json['contentType'] as String,
        fileSize: (json['fileSize'] as num).toInt(),
        uploadedAtUtc: _requiredProtocolDate(
          json,
          'uploadedAt',
          'uploadedAtUtc',
        ),
      );

  final String imageId;
  final String fileName;
  final String contentType;
  final int fileSize;
  final DateTime uploadedAtUtc;
}

class RefundReplacementRequest {
  const RefundReplacementRequest({
    required this.requestId,
    required this.requestNumber,
    required this.orderId,
    required this.orderPublicId,
    required this.orderNumber,
    required this.deliveryId,
    required this.deliveryNumber,
    required this.customerId,
    required this.customerName,
    required this.customerMobile,
    required this.branchId,
    required this.branchCode,
    required this.branchName,
    required this.milkTestId,
    required this.milkTestPublicId,
    required this.type,
    required this.source,
    required this.status,
    required this.reason,
    required this.remarks,
    required this.submittedAtUtc,
    required this.deadlineUtc,
    required this.decidedByUserId,
    required this.decidedByName,
    required this.decidedAtUtc,
    required this.decisionRemarks,
    required this.completedByUserId,
    required this.completedByName,
    required this.completedAtUtc,
    required this.completionRemarks,
    required this.proofImageRequired,
    required this.images,
    this.subscriptionDeliveryPublicId,
    this.failedAtUtc,
    this.failureReasonSnapshot,
    this.customerChoice,
    this.customerChoiceAtUtc,
    this.replacementDate,
    this.replacementDeliveryNumber,
    // Audit remediation B: true when the failed occurrence is itself a
    // re-delivery replacement, so Refund is the only remedy and rejection
    // is refused. Defaults to false for older payloads.
    this.isRefundOnly = false,
  });

  factory RefundReplacementRequest.fromJson(Map<String, dynamic> json) =>
      RefundReplacementRequest(
        requestId: json['requestId'] as String,
        requestNumber: json['requestNumber'] as String,
        orderId: (json['orderId'] as num?)?.toInt(),
        orderPublicId: json['orderPublicId'] as String?,
        orderNumber: json['orderNumber'] as String?,
        deliveryId: json['deliveryId'] as String,
        deliveryNumber: json['deliveryNumber'] as String?,
        customerId: (json['customerId'] as num).toInt(),
        customerName: json['customerName'] as String,
        customerMobile: json['customerMobile'] as String,
        branchId: (json['branchId'] as num).toInt(),
        branchCode: json['branchCode'] as String,
        branchName: json['branchName'] as String,
        milkTestId: (json['milkTestId'] as num?)?.toInt(),
        milkTestPublicId: json['milkTestPublicId'] as String?,
        type: RefundReplacementType.parse(json['type']),
        source: RefundReplacementSource.parse(json['source']),
        status: RefundReplacementStatus.parse(json['status']),
        reason: json['reason'] as String,
        remarks: json['remarks'] as String?,
        submittedAtUtc: _requiredProtocolDate(
          json,
          'submittedAt',
          'submittedAtUtc',
        ),
        deadlineUtc: _optionalProtocolDate(json, 'deadline', 'deadlineUtc'),
        decidedByUserId: (json['decidedByUserId'] as num?)?.toInt(),
        decidedByName: json['decidedByName'] as String?,
        decidedAtUtc: _optionalProtocolDate(json, 'decidedAt', 'decidedAtUtc'),
        decisionRemarks: json['decisionRemarks'] as String?,
        completedByUserId: (json['completedByUserId'] as num?)?.toInt(),
        completedByName: json['completedByName'] as String?,
        completedAtUtc: _optionalProtocolDate(
          json,
          'completedAt',
          'completedAtUtc',
        ),
        completionRemarks: json['completionRemarks'] as String?,
        proofImageRequired: json['proofImageRequired'] as bool? ?? false,
        images: _images(json['images']),
        subscriptionDeliveryPublicId:
            json['subscriptionDeliveryPublicId'] as String?,
        failedAtUtc: _optionalProtocolDate(json, 'failedAt', 'failedAtUtc'),
        failureReasonSnapshot:
            json['failureReasonSnapshot'] as String? ??
            json['failureReason'] as String?,
        customerChoice: CustomerChoice.parse(json['customerChoice']),
        customerChoiceAtUtc: _optionalProtocolDate(
          json,
          'customerChoiceAt',
          'customerChoiceAtUtc',
        ),
        replacementDate: _optionalDateOnly(json['replacementDate']),
        replacementDeliveryNumber:
            json['replacementDeliveryNumber'] as String?,
        isRefundOnly: (json['isRefundOnly'] as bool?) ?? false,
      );

  final String requestId;
  final String requestNumber;
  final int? orderId;
  final String? orderPublicId;
  final String? orderNumber;
  final String deliveryId;
  final String? deliveryNumber;
  final int customerId;
  final String customerName;
  final String customerMobile;
  final int branchId;
  final String branchCode;
  final String branchName;
  final int? milkTestId;
  final String? milkTestPublicId;
  final RefundReplacementType type;
  final RefundReplacementSource source;
  final RefundReplacementStatus status;
  final String reason;
  final String? remarks;
  final DateTime submittedAtUtc;
  final DateTime? deadlineUtc;
  final int? decidedByUserId;
  final String? decidedByName;
  final DateTime? decidedAtUtc;
  final String? decisionRemarks;
  final int? completedByUserId;
  final String? completedByName;
  final DateTime? completedAtUtc;
  final String? completionRemarks;
  final bool proofImageRequired;
  final List<RefundReplacementImage> images;
  final String? subscriptionDeliveryPublicId;
  final DateTime? failedAtUtc;
  final String? failureReasonSnapshot;
  final CustomerChoice? customerChoice;
  final DateTime? customerChoiceAtUtc;
  /// D1-D slice 6.1: the server-approved replacement date (date-only, no
  /// time) for a re-delivery ticket. The backend persists it on the
  /// replacement occurrence and echoes it on every read (list and detail),
  /// so it survives reloads without depending on the approval response.
  /// [replacementDeliveryNumber] is the replacement operational delivery's
  /// public DELIVERY-series number, present only once the replacement has
  /// materialized through the normal pipeline; it is never invented. The
  /// replacement delivery's public identifier is an opaque GUID, so it is
  /// deliberately not modeled here and never shown as a business reference.
  final DateTime? replacementDate;
  final String? replacementDeliveryNumber;
  final bool isRefundOnly;

  /// True while the ticket awaits the customer's Refund / Re-Delivery choice.
  bool get isAwaitingChoice =>
      status.isAwaitingChoice && customerChoice == null;

  /// True when staff may approve this ticket: an undecided normal request, or
  /// a delivery-fail ticket whose customer choice is already recorded and is
  /// waiting on the staff decision. Mirrors the backend's
  /// Pending-or-AwaitingCustomerChoice approval precondition without
  /// reproducing any date, cutoff, or eligibility business rules.
  bool get isStaffDecidable =>
      status == RefundReplacementStatus.pending ||
      (status.isAwaitingChoice && customerChoice != null);

  /// True when approving this ticket goes through the re-delivery date flow:
  /// a subscription delivery-fail ticket with a recorded Re-Delivery choice.
  /// The backend remains authoritative for every date rule; the UI only
  /// surfaces the proposal and forwards the confirmed date.
  bool get isRedeliveryApproval =>
      isStaffDecidable &&
      source == RefundReplacementSource.deliveryFailed &&
      customerChoice == CustomerChoice.reDelivery &&
      subscriptionDeliveryPublicId != null;

  /// True for one-time order-backed tickets (no subscription occurrence).
  /// Completing these records the out-of-system settlement only — no
  /// in-system wallet credit or refund row is created (backend locked).
  bool get isOneTimeOrderTicket =>
      subscriptionDeliveryPublicId == null && orderId != null;

  /// True when the persisted choice window has passed without a choice.
  /// Advisory only; the backend deadline is authoritative and D1-C owns
  /// timeout processing, so the UI never initiates a refund itself.
  bool get isChoiceExpired {
    if (customerChoice != null || !status.isAwaitingChoice) return false;
    final deadline = deadlineUtc;
    if (deadline == null) return false;
    return !DateTime.now().toUtc().isBefore(deadline);
  }
}

/// Lightweight row for the branch-scoped staff list.
class RefundReplacementListItem {
  const RefundReplacementListItem({
    required this.requestId,
    required this.requestNumber,
    required this.orderId,
    required this.orderPublicId,
    required this.orderNumber,
    required this.deliveryId,
    required this.deliveryNumber,
    required this.customerId,
    required this.customerName,
    required this.customerMobile,
    required this.branchId,
    required this.branchCode,
    required this.branchName,
    required this.milkTestId,
    required this.milkTestPublicId,
    required this.type,
    required this.source,
    required this.status,
    required this.reason,
    required this.remarks,
    required this.submittedAtUtc,
    required this.deadlineUtc,
    required this.proofImageRequired,
    required this.imageCount,
    this.customerChoice,
    this.customerChoiceAtUtc,
  });

  factory RefundReplacementListItem.fromJson(Map<String, dynamic> json) =>
      RefundReplacementListItem(
        requestId: json['requestId'] as String,
        requestNumber: json['requestNumber'] as String,
        orderId: (json['orderId'] as num?)?.toInt(),
        orderPublicId: json['orderPublicId'] as String?,
        orderNumber: json['orderNumber'] as String?,
        deliveryId: json['deliveryId'] as String,
        deliveryNumber: json['deliveryNumber'] as String?,
        customerId: (json['customerId'] as num).toInt(),
        customerName: json['customerName'] as String,
        customerMobile: json['customerMobile'] as String,
        branchId: (json['branchId'] as num).toInt(),
        branchCode: json['branchCode'] as String,
        branchName: json['branchName'] as String,
        milkTestId: (json['milkTestId'] as num?)?.toInt(),
        milkTestPublicId: json['milkTestPublicId'] as String?,
        type: RefundReplacementType.parse(json['type']),
        source: RefundReplacementSource.parse(json['source']),
        status: RefundReplacementStatus.parse(json['status']),
        reason: json['reason'] as String,
        remarks: json['remarks'] as String?,
        submittedAtUtc: _requiredProtocolDate(
          json,
          'submittedAt',
          'submittedAtUtc',
        ),
        deadlineUtc: _optionalProtocolDate(json, 'deadline', 'deadlineUtc'),
        proofImageRequired: json['proofImageRequired'] as bool? ?? false,
        imageCount: (json['imageCount'] as num?)?.toInt() ?? 0,
        customerChoice: CustomerChoice.parse(json['customerChoice']),
        customerChoiceAtUtc: _optionalProtocolDate(
          json,
          'customerChoiceAt',
          'customerChoiceAtUtc',
        ),
      );

  final String requestId;
  final String requestNumber;
  final int? orderId;
  final String? orderPublicId;
  final String? orderNumber;
  final String deliveryId;
  final String? deliveryNumber;
  final int customerId;
  final String customerName;
  final String customerMobile;
  final int branchId;
  final String branchCode;
  final String branchName;
  final int? milkTestId;
  final String? milkTestPublicId;
  final RefundReplacementType type;
  final RefundReplacementSource source;
  final RefundReplacementStatus status;
  final String reason;
  final String? remarks;
  final DateTime submittedAtUtc;
  final DateTime? deadlineUtc;
  final bool proofImageRequired;
  final int imageCount;
  final CustomerChoice? customerChoice;
  final DateTime? customerChoiceAtUtc;
}

class RefundReplacementPage {
  const RefundReplacementPage({
    required this.items,
    required this.page,
    required this.pageSize,
    required this.totalCount,
  });

  factory RefundReplacementPage.fromJson(Map<String, dynamic> json) =>
      RefundReplacementPage(
        items: (json['items'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(RefundReplacementListItem.fromJson)
            .toList(growable: false),
        page: (json['page'] as num).toInt(),
        pageSize: (json['pageSize'] as num).toInt(),
        totalCount: (json['totalCount'] as num).toInt(),
      );

  final List<RefundReplacementListItem> items;
  final int page;
  final int pageSize;
  final int totalCount;
}

DateTime _requiredProtocolDate(
  Map<String, dynamic> json,
  String key,
  String fallbackKey,
) {
  final value = json[key] ?? json[fallbackKey];
  if (value is! String || value.isEmpty) {
    throw FormatException('Missing refund/replacement timestamp: $key.');
  }
  return DateTime.parse(value).toUtc();
}

DateTime? _optionalProtocolDate(
  Map<String, dynamic> json,
  String key,
  String fallbackKey,
) {
  final value = json[key] ?? json[fallbackKey];
  return value is String ? DateTime.parse(value).toUtc() : null;
}

List<RefundReplacementImage> _images(Object? value) =>
    (value as List<dynamic>? ?? const [])
        .cast<Map<String, dynamic>>()
        .map(RefundReplacementImage.fromJson)
        .toList(growable: false);

/// D1-D: the server-proposed next eligible replacement date for a re-delivery
/// ticket awaiting staff approval. Advisory only; approval re-validates inside
/// its own transaction, so the UI never enforces date rules itself.
class ReplacementDateProposal {
  const ReplacementDateProposal({
    required this.requestId,
    required this.proposedDate,
  });

  factory ReplacementDateProposal.fromJson(Map<String, dynamic> json) =>
      ReplacementDateProposal(
        requestId: json['requestId'] as String,
        proposedDate: _requiredDateOnly(json['proposedDate']),
      );

  final String requestId;

  /// Date-only (midnight UTC); formatted without timezone conversion so the
  /// approved calendar day is shown exactly as the server sent it.
  final DateTime proposedDate;
}

/// Parses a backend `DateOnly` (`yyyy-MM-dd`, no time component) into a
/// date-only [DateTime]. Returns null for missing values.
DateTime? _optionalDateOnly(Object? value) {
  if (value == null) return null;
  if (value is! String || value.isEmpty) {
    throw FormatException('Invalid refund/replacement date value.');
  }
  return _parseDateOnly(value);
}

DateTime _requiredDateOnly(Object? value) {
  if (value is! String || value.isEmpty) {
    throw FormatException('Missing refund/replacement date value.');
  }
  return _parseDateOnly(value);
}

DateTime _parseDateOnly(String value) {
  final parsed = DateTime.parse(value);
  return DateTime.utc(parsed.year, parsed.month, parsed.day);
}
