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
  // Legacy remediation: an approved, order-backed Replacement ticket for a
  // one-time order was replaced by a supported Refund ticket. Terminal, but
  // never a fulfillment (completed), a delivery failure (failed), a refund
  // settlement, or a rejection — the linked Refund ticket carries the remedy.
  superseded,
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
    'Superseded' || 'superseded' => superseded,
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
    superseded => 'Superseded by Refund',
    unknown => 'Unknown',
  };

  bool get isTerminal =>
      this == RefundReplacementStatus.rejected ||
      this == RefundReplacementStatus.completed ||
      this == RefundReplacementStatus.failed ||
      this == RefundReplacementStatus.cancelled ||
      this == RefundReplacementStatus.superseded;

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

/// Stage 2: explicit persisted settlement method for a Refund ticket.
/// [unspecified] covers legacy tickets and non-refund shapes (established
/// wallet behavior). New Refund-shaped approvals designate [wallet] or
/// [manualBankTransfer] explicitly; the backend rejects a missing choice.
enum RefundSettlementMethod {
  unspecified,
  wallet,
  manualBankTransfer,
  unknown;

  static RefundSettlementMethod parse(Object? value) => switch (value) {
    'Wallet' || 'wallet' => wallet,
    'ManualBankTransfer' || 'manualBankTransfer' => manualBankTransfer,
    null || 'Unspecified' || 'unspecified' => unspecified,
    _ => unknown,
  };

  String get label => switch (this) {
    wallet => 'Wallet refund',
    manualBankTransfer => 'Manual bank transfer',
    unspecified => 'Not specified',
    unknown => 'Unknown',
  };

  String get wireValue => switch (this) {
    wallet => 'Wallet',
    manualBankTransfer => 'ManualBankTransfer',
    unspecified => 'Unspecified',
    unknown => 'Unspecified',
  };
}

/// The linked delivery's current status, projected from the authoritative
/// persisted Delivery record by the ticket read APIs. Staff UI gating only:
/// remediation eligibility requires [failed], and any other value —
/// including [unknown] for missing/unrecognized payloads — fails closed.
/// The backend revalidates inside its own transaction at execution time.
enum DeliveryStatus {
  readyForAssignment,
  assigned,
  pickedUp,
  outForDelivery,
  arrived,
  delivered,
  failed,
  cancelled,
  unknown;

  static DeliveryStatus parse(Object? value) => switch (value) {
    'ReadyForAssignment' || 'readyForAssignment' => readyForAssignment,
    'Assigned' || 'assigned' => assigned,
    'PickedUp' || 'pickedUp' => pickedUp,
    'OutForDelivery' || 'outForDelivery' => outForDelivery,
    'Arrived' || 'arrived' => arrived,
    'Delivered' || 'delivered' => delivered,
    'Failed' || 'failed' => failed,
    'Cancelled' || 'cancelled' => cancelled,
    _ => unknown,
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
    this.isFailedDeliveryWithoutProduct = false,
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
        isFailedDeliveryWithoutProduct:
            json['isFailedDeliveryWithoutProduct'] as bool? ?? false,
      );

  final bool isEligible;
  final String? ineligibleReason;
  final int windowHours;
  final DateTime? deadlineUtc;
  final bool hasActiveRequest;
  final bool proofImageRequired;
  final bool isMilkTestRejectedFlow;
  /// Stage 2: server-authoritative flag — the linked delivery is confirmed
  /// failed with no product received, so no photo is required. The UI only
  /// renders this answer; it never computes it.
  final bool isFailedDeliveryWithoutProduct;
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
    // Stage 1/2: support's frozen applied deduction percentage for an
    // order-backed Refund ticket (shared Admin Centre value, or zero when
    // waived). Null until decided and for non-order tickets.
    this.appliedDeductionPercentage,
    // Stage 1/2: true when support waived the customer-fault deduction.
    this.deductionWaived = false,
    // Stage 1/2: required reason when the deduction was waived.
    this.deductionWaiverReason,
    // Legacy remediation: on a replacement Refund ticket, the request
    // number of the superseded legacy ticket. Null on every other ticket.
    this.supersedesRequestNumber,
    // Legacy remediation: public id of the superseded legacy ticket, used
    // for authorized navigation to the legacy detail. Null otherwise.
    this.supersedesRequestPublicId,
    // Legacy remediation: on a superseded legacy ticket, the request
    // number of the replacement Refund ticket raised for the same
    // delivery. The detail API exposes the number only (no public id),
    // so it is displayed as text, never as a deep link. Null otherwise.
    this.supersededByRequestNumber,
    // Stage 2.1: the linked delivery's authoritative status. Null when
    // the payload predates the contract; eligibility fails closed.
    this.deliveryStatus,
    // Stage 3: the deduction percentage frozen when the delivery failure
    // was recorded (Admin Centre value at that time). Drives the staff
    // apply/waive prompt and the customer refund disclosure. Null for
    // non-delivery-fail tickets and older payloads.
    this.deliveryFailDeductionPercentageSnapshot,
    // Stage 2: explicit settlement method. Defaults to unspecified for
    // legacy payloads (established wallet behavior).
    this.settlementMethod = RefundSettlementMethod.unspecified,
    // Stage 2: frozen manual-bank transfer amount/currency. Null except
    // on ManualBankTransfer tickets.
    this.reservedAmount,
    this.reservedCurrency,
    // Stage 2: masked beneficiary snapshot (never the full account
    // number). Null except on ManualBankTransfer tickets.
    this.beneficiaryHolderName,
    this.beneficiaryBankName,
    this.beneficiaryIfsc,
    this.beneficiaryAccountLast4,
    // Employee-origin policy: true when the authoritative owning account
    // is a non-customer (employee/owner/administrator) account.
    // Server-derived, never sent by the client; staff UI gating only —
    // execution revalidates inside the backend service guard. Defaults to
    // false for older payloads.
    this.isEmployeeOriginated = false,
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
        appliedDeductionPercentage:
            (json['appliedDeductionPercentage'] as num?)?.toDouble(),
        deductionWaived: (json['deductionWaived'] as bool?) ?? false,
        deductionWaiverReason: json['deductionWaiverReason'] as String?,
        supersedesRequestNumber:
            json['supersedesRequestNumber'] as String?,
        supersedesRequestPublicId:
            json['supersedesRequestPublicId'] as String?,
        supersededByRequestNumber:
            json['supersededByRequestNumber'] as String?,
        deliveryFailDeductionPercentageSnapshot:
            (json['deliveryFailDeductionPercentageSnapshot'] as num?)
                ?.toDouble(),
        settlementMethod: RefundSettlementMethod.parse(
          json['settlementMethod'],
        ),
        reservedAmount: (json['reservedAmount'] as num?)?.toDouble(),
        reservedCurrency: json['reservedCurrency'] as String?,
        beneficiaryHolderName: json['beneficiaryHolderName'] as String?,
        beneficiaryBankName: json['beneficiaryBankName'] as String?,
        beneficiaryIfsc: json['beneficiaryIfsc'] as String?,
        beneficiaryAccountLast4:
            json['beneficiaryAccountLast4'] as String?,
        isEmployeeOriginated:
            (json['isEmployeeOriginated'] as bool?) ?? false,
        deliveryStatus: json['deliveryStatus'] == null
            ? null
            : DeliveryStatus.parse(json['deliveryStatus']),
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
  final double? appliedDeductionPercentage;
  final bool deductionWaived;
  final String? deductionWaiverReason;
  final String? supersedesRequestNumber;
  final String? supersedesRequestPublicId;
  final String? supersededByRequestNumber;
  final DeliveryStatus? deliveryStatus;
  final double? deliveryFailDeductionPercentageSnapshot;
  final RefundSettlementMethod settlementMethod;
  final double? reservedAmount;
  final String? reservedCurrency;
  final String? beneficiaryHolderName;
  final String? beneficiaryBankName;
  final String? beneficiaryIfsc;
  final String? beneficiaryAccountLast4;
  final bool isEmployeeOriginated;

  /// True when this Refund ticket is designated for manual bank transfer:
  /// completion is disabled — payment happens through the bank-transfer
  /// confirmation, never through wallet settlement.
  bool get isManualBankTransfer =>
      settlementMethod == RefundSettlementMethod.manualBankTransfer;

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
  /// Stage 2: completing an approved order Refund settles the frozen refund
  /// to the customer's wallet through the backend's authoritative
  /// settlement — approval alone moves no money.
  bool get isOneTimeOrderTicket =>
      subscriptionDeliveryPublicId == null && orderId != null;

  /// True for order-backed Refund tickets: the only remedy the backend
  /// supports for one-time orders. Replacement/re-delivery is never offered
  /// here — the server rejects it.
  bool get isOrderRefundTicket =>
      isOneTimeOrderTicket && type == RefundReplacementType.refund;

  /// True for subscription failed-delivery Refund tickets (any choice
  /// state). Used for the pre-choice customer disclosure of the frozen
  /// deduction percentage. The backend revalidates every precondition —
  /// the UI only surfaces the frozen snapshot, never computes money.
  bool get isSubscriptionDeliveryFailTicket =>
      !isOneTimeOrderTicket &&
      subscriptionDeliveryPublicId != null &&
      source == RefundReplacementSource.deliveryFailed &&
      type == RefundReplacementType.refund;

  /// True for subscription failed-delivery tickets with the customer's
  /// Refund choice recorded. Stage 3: approving these requires support's
  /// explicit apply/waive customer-fault deduction decision (mirroring the
  /// order flow). The backend revalidates every precondition.
  bool get isSubscriptionDeliveryFailRefund =>
      isSubscriptionDeliveryFailTicket &&
      customerChoice == CustomerChoice.refund;

  /// True when this ticket was closed by legacy remediation: the
  /// unsupported one-time Replacement request was replaced by a supported
  /// Refund request. Terminal — but never a fulfillment, a refund
  /// settlement, or a rejection. The linked Refund ticket (named by
  /// [supersededByRequestNumber]) carries the remedy.
  bool get isSuperseded =>
      status == RefundReplacementStatus.superseded;

  /// True when this ticket is the Refund half of a remediation pair: it
  /// names the superseded legacy ticket via [supersedesRequestNumber].
  bool get isRemediationRefund => supersedesRequestNumber != null;

  /// True when the staff remediation action may be offered for this ticket,
  /// verified only from authoritative API fields: order-backed, an approved
  /// Replacement, not already superseded, and the linked delivery's
  /// authoritative status is Failed. A missing or unrecognized delivery
  /// status fails closed (no action). The backend still revalidates every
  /// precondition inside its own transaction — it returns 409 when the
  /// ticket no longer qualifies, and the UI reloads state instead of
  /// retrying blindly.
  bool get isLegacyRemediationEligible =>
      !isSuperseded &&
      status == RefundReplacementStatus.approved &&
      type == RefundReplacementType.replacement &&
      orderPublicId != null &&
      deliveryStatus == DeliveryStatus.failed;

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
    // Legacy remediation: on a replacement Refund row, the request number
    // of the superseded legacy ticket. Null on every other row.
    this.supersedesRequestNumber,
    // Stage 2.1: the linked delivery's authoritative status. Null when
    // the payload predates the contract; eligibility fails closed.
    this.deliveryStatus,
    // Employee-origin policy indicator for list rows. Server-derived;
    // staff UI gating only. Defaults to false for older payloads.
    this.isEmployeeOriginated = false,
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
        supersedesRequestNumber:
            json['supersedesRequestNumber'] as String?,
        deliveryStatus: json['deliveryStatus'] == null
            ? null
            : DeliveryStatus.parse(json['deliveryStatus']),
        isEmployeeOriginated:
            (json['isEmployeeOriginated'] as bool?) ?? false,
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
  final String? supersedesRequestNumber;
  final DeliveryStatus? deliveryStatus;
  final bool isEmployeeOriginated;

  /// True when this row was closed by legacy remediation.
  bool get isSuperseded =>
      status == RefundReplacementStatus.superseded;

  /// True when this row is the Refund half of a remediation pair.
  bool get isRemediationRefund => supersedesRequestNumber != null;
}

/// Legacy remediation result: the superseded legacy ticket paired with the
/// newly created linked Pending Refund ticket. The Refund ticket carries no
/// settlement — it follows the standard Apply/Waive approval and
/// wallet-settlement workflow. Idempotent retries return the existing pair.
class RemediationPair {
  const RemediationPair({
    required this.legacyTicket,
    required this.refundTicket,
  });

  factory RemediationPair.fromJson(Map<String, dynamic> json) =>
      RemediationPair(
        legacyTicket: RefundReplacementRequest.fromJson(
          json['legacyTicket'] as Map<String, dynamic>,
        ),
        refundTicket: RefundReplacementRequest.fromJson(
          json['refundTicket'] as Map<String, dynamic>,
        ),
      );

  final RefundReplacementRequest legacyTicket;
  final RefundReplacementRequest refundTicket;
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

/// Stage 2: read-only preview of an order-backed Refund ticket's proposed
/// wallet settlement. Every value is server-authoritative; the UI renders
/// them verbatim and never computes money itself. Advisory only — approval
/// snapshots the applied percentage and settlement revalidates inside its
/// own transaction.
class OrderRefundPreview {
  const OrderRefundPreview({
    required this.requestId,
    required this.requestNumber,
    required this.orderPublicId,
    required this.orderNumber,
    required this.grossBasis,
    required this.appliedPercentage,
    required this.deductionAmount,
    required this.eligiblePaid,
    required this.proposedRefund,
    required this.currency,
  });

  factory OrderRefundPreview.fromJson(Map<String, dynamic> json) =>
      OrderRefundPreview(
        requestId: json['requestId'] as String,
        requestNumber: json['requestNumber'] as String,
        orderPublicId: json['orderPublicId'] as String,
        orderNumber: json['orderNumber'] as String,
        grossBasis: (json['grossBasis'] as num).toDouble(),
        appliedPercentage: (json['appliedPercentage'] as num).toDouble(),
        deductionAmount: (json['deductionAmount'] as num).toDouble(),
        eligiblePaid: (json['eligiblePaid'] as num).toDouble(),
        proposedRefund: (json['proposedRefund'] as num).toDouble(),
        currency: json['currency'] as String,
      );

  final String requestId;
  final String requestNumber;
  final String orderPublicId;
  final String orderNumber;
  final double grossBasis;
  final double appliedPercentage;
  final double deductionAmount;
  final double eligiblePaid;
  final double proposedRefund;
  final String currency;

  /// True when no wallet refund can succeed: nothing eligible was paid
  /// (unpaid/COD or fully refunded) or the deduction covers the basis.
  /// Advisory — the backend revalidates at settlement.
  bool get hasNoRefund => proposedRefund <= 0 || eligiblePaid <= 0;
}

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
