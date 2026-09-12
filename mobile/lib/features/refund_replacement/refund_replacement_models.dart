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
  approved,
  rejected,
  completed,
  unknown;

  static RefundReplacementStatus parse(Object? value) => switch (value) {
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

  bool get isTerminal =>
      this == RefundReplacementStatus.rejected ||
      this == RefundReplacementStatus.completed;
}

enum RefundReplacementSource {
  customerPostDelivery,
  milkTestRejected,
  unknown;

  static RefundReplacementSource parse(Object? value) => switch (value) {
    'CustomerPostDelivery' || 'customerPostDelivery' =>
      customerPostDelivery,
    'MilkTestRejected' || 'milkTestRejected' => milkTestRejected,
    _ => unknown,
  };

  String get label => switch (this) {
    customerPostDelivery => 'Requested after delivery',
    milkTestRejected => 'Milk test rejected',
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
        isMilkTestRejectedFlow: json['isMilkTestRejectedFlow'] as bool? ?? false,
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
  });

  factory RefundReplacementRequest.fromJson(Map<String, dynamic> json) =>
      RefundReplacementRequest(
        requestId: json['requestId'] as String,
        requestNumber: json['requestNumber'] as String,
        orderId: (json['orderId'] as num).toInt(),
        orderPublicId: json['orderPublicId'] as String?,
        orderNumber: json['orderNumber'] as String,
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
      );

  final String requestId;
  final String requestNumber;
  final int orderId;
  final String? orderPublicId;
  final String orderNumber;
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
  });

  factory RefundReplacementListItem.fromJson(Map<String, dynamic> json) =>
      RefundReplacementListItem(
        requestId: json['requestId'] as String,
        requestNumber: json['requestNumber'] as String,
        orderId: (json['orderId'] as num).toInt(),
        orderPublicId: json['orderPublicId'] as String?,
        orderNumber: json['orderNumber'] as String,
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
      );

  final String requestId;
  final String requestNumber;
  final int orderId;
  final String? orderPublicId;
  final String orderNumber;
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
