import 'package:doodh_direct_mobile/core/time/india_time.dart';

class DeliveryFailureReasons {
  static const customerNotAvailable = 'Customer not available';
  static const addressNotFound = 'Address not found';
  static const vehicleIssue = 'Vehicle issue';
  static const productDamaged = 'Product damaged';
  static const other = 'Other';

  static const all = <String>[
    customerNotAvailable,
    addressNotFound,
    vehicleIssue,
    productDamaged,
    other,
  ];
}

enum DeliverySourceType {
  oneTimeOrder,
  subscriptionOccurrence,
  unknown;

  factory DeliverySourceType.fromApi(String value) =>
      switch (value.toLowerCase()) {
        'onetimeorder' => DeliverySourceType.oneTimeOrder,
        'subscriptionoccurrence' => DeliverySourceType.subscriptionOccurrence,
        _ => DeliverySourceType.unknown,
      };

  String get label => switch (this) {
    DeliverySourceType.oneTimeOrder => 'One-time',
    DeliverySourceType.subscriptionOccurrence => 'Subscription',
    DeliverySourceType.unknown => 'Delivery',
  };
}

enum SubscriptionDeliverySlot {
  morning,
  evening,
  unknown;

  factory SubscriptionDeliverySlot.fromApi(String? value) =>
      switch (value?.toLowerCase()) {
        'morning' => SubscriptionDeliverySlot.morning,
        'evening' => SubscriptionDeliverySlot.evening,
        _ => SubscriptionDeliverySlot.unknown,
      };

  String get label => switch (this) {
    SubscriptionDeliverySlot.morning => 'Morning',
    SubscriptionDeliverySlot.evening => 'Evening',
    SubscriptionDeliverySlot.unknown => 'Unknown slot',
  };

  String get apiValue => switch (this) {
    SubscriptionDeliverySlot.morning => 'Morning',
    SubscriptionDeliverySlot.evening => 'Evening',
    SubscriptionDeliverySlot.unknown => 'Unknown',
  };
}

enum DeliveryStatus {
  readyForAssignment,
  assigned,
  pickedUp,
  outForDelivery,
  arrived,
  delivered,
  failed,
  // D1-D slice 5/7: terminal void for a pre-pickup operational delivery
  // whose approved re-delivery replacement was cancelled with its
  // subscription. Never a failure (no failure ticket is raised).
  cancelled,
  unknown;

  factory DeliveryStatus.fromApi(String value) => switch (value.toLowerCase()) {
    'readyforassignment' => DeliveryStatus.readyForAssignment,
    'assigned' => DeliveryStatus.assigned,
    'pickedup' => DeliveryStatus.pickedUp,
    'outfordelivery' => DeliveryStatus.outForDelivery,
    'arrived' => DeliveryStatus.arrived,
    'delivered' => DeliveryStatus.delivered,
    'failed' => DeliveryStatus.failed,
    'cancelled' => DeliveryStatus.cancelled,
    _ => DeliveryStatus.unknown,
  };

  String get label => switch (this) {
    DeliveryStatus.readyForAssignment => 'Ready for assignment',
    DeliveryStatus.assigned => 'Assigned',
    DeliveryStatus.pickedUp => 'Picked up',
    DeliveryStatus.outForDelivery => 'Out for delivery',
    DeliveryStatus.arrived => 'Arrived',
    DeliveryStatus.delivered => 'Delivered',
    DeliveryStatus.failed => 'Failed',
    DeliveryStatus.cancelled => 'Cancelled',
    DeliveryStatus.unknown => 'Unknown',
  };
}

class DeliveryLocation {
  const DeliveryLocation({
    required this.latitude,
    required this.longitude,
    required this.accuracyMetres,
    required this.recordedAt,
  });
  factory DeliveryLocation.fromJson(Map<String, dynamic> json) =>
      DeliveryLocation(
        latitude: (json['latitude'] as num).toDouble(),
        longitude: (json['longitude'] as num).toDouble(),
        accuracyMetres: (json['accuracyMetres'] as num?)?.toDouble(),
        recordedAt: DateTime.parse(json['recordedAt'] as String),
      );
  final double latitude;
  final double longitude;
  final double? accuracyMetres;
  final DateTime recordedAt;
}

class DeliveryAssignment {
  const DeliveryAssignment({
    required this.employeeId,
    required this.employeeName,
    required this.assignedByUserId,
    required this.assignedAt,
    required this.reason,
  });
  factory DeliveryAssignment.fromJson(Map<String, dynamic> json) =>
      DeliveryAssignment(
        employeeId: json['employeeId'] as String,
        employeeName: json['employeeName'] as String?,
        assignedByUserId: json['assignedByUserId'] as String,
        assignedAt: DateTime.parse(json['assignedAt'] as String),
        reason: json['reason'] as String?,
      );
  final String employeeId;
  final String? employeeName;
  final String assignedByUserId;
  final DateTime assignedAt;
  final String? reason;
}

class CustomerDelivery {
  const CustomerDelivery({
    required this.deliveryId,
    required this.sourceType,
    required this.referenceNumber,
    required this.status,
    required this.scheduledDate,
    required this.destinationAddress,
    // Explicit subscription linkage (backend CustomerDeliveryResult):
    // clients must use these instead of parsing the SUB-{guid}-{date}
    // reference text. Null for one-time order deliveries and for
    // synthetic pre-materialization rows built client-side.
    this.subscriptionDeliveryId,
    this.subscriptionId,
    this.subscriptionNumber,
    this.subscriptionSlot,
    this.quantity,
    this.orderSummary,
    this.subscriptionProductName,
    this.subscriptionUnitOfMeasure,
    required this.assignedEmployeeId,
    required this.assignedEmployeeName,
    required this.isTrackingActive,
    required this.latestLocation,
    required this.completedAt,
    required this.failedAt,
    required this.failureReason,
    required this.activeOtp,
  });
  factory CustomerDelivery.fromJson(Map<String, dynamic> json) =>
      CustomerDelivery(
        deliveryId: json['deliveryId'] as String,
        sourceType: DeliverySourceType.fromApi(json['sourceType'] as String),
        referenceNumber: json['referenceNumber'] as String,
        status: DeliveryStatus.fromApi(json['status'] as String),
        scheduledDate: DateTime.parse(json['scheduledDate'] as String),
        destinationAddress: json['destinationAddress'] as String,
        assignedEmployeeId: json['assignedEmployeeId'] as String?,
        assignedEmployeeName: json['assignedEmployeeName'] as String?,
        isTrackingActive: json['isTrackingActive'] as bool,
        latestLocation: _location(json['latestLocation']),
        completedAt: _date(json['completedAt']),
        failedAt: _date(json['failedAt']),
        failureReason: json['failureReason'] as String?,
        activeOtp: json['activeOtp'] as String?,
        subscriptionDeliveryId: json['subscriptionDeliveryId'] as String?,
        subscriptionId: json['subscriptionId'] as String?,
        subscriptionNumber: json['subscriptionNumber'] as String?,
        subscriptionSlot: json['subscriptionSlot'] == null
            ? null
            : SubscriptionDeliverySlot.fromApi(
                json['subscriptionSlot'] as String,
              ),
        quantity: (json['quantity'] as num?)?.toDouble(),
        orderSummary: json['orderSummary'] == null
            ? null
            : DeliveryOrderSummary.fromJson(
                json['orderSummary'] as Map<String, dynamic>,
              ),
        subscriptionProductName:
            json['subscriptionProductName'] as String?,
        subscriptionUnitOfMeasure:
            json['subscriptionUnitOfMeasure'] as String?,
      );
  final String deliveryId;
  final DeliverySourceType sourceType;
  final String referenceNumber;
  final DeliveryStatus status;
  final DateTime scheduledDate;
  final String destinationAddress;
  final String? assignedEmployeeId;
  final String? assignedEmployeeName;
  final bool isTrackingActive;
  final DeliveryLocation? latestLocation;
  final DateTime? completedAt;
  final DateTime? failedAt;
  final String? failureReason;
  final String? activeOtp;
  final String? subscriptionDeliveryId;
  final String? subscriptionId;
  final String? subscriptionNumber;
  final SubscriptionDeliverySlot? subscriptionSlot;
  final double? quantity;
  final DeliveryOrderSummary? orderSummary;
  final String? subscriptionProductName;
  final String? subscriptionUnitOfMeasure;

  /// An unresolved subscription delivery whose India-local scheduled date has
  /// passed. This is a presentation state only; the backend's operational
  /// status remains authoritative until staff records an outcome.
  bool get isPastDue {
    if (sourceType != DeliverySourceType.subscriptionOccurrence ||
        status == DeliveryStatus.delivered ||
        status == DeliveryStatus.failed ||
        status == DeliveryStatus.cancelled) {
      return false;
    }
    final date = scheduledDate.isUtc
        ? toIndiaTime(scheduledDate)
        : scheduledDate;
    final today = indiaNow();
    return DateTime(date.year, date.month, date.day).isBefore(
      DateTime(today.year, today.month, today.day),
    );
  }

  /// Customer-visible tracking reference: the persisted subscription
  /// document number plus the occurrence date for subscription
  /// occurrences. Falls back to the operational reference number when no
  /// document number is known (one-time orders, historical rows, synthetic
  /// pre-materialization rows). The operational reference stays the
  /// internal correlation key and is never parsed for identity.
  String get trackingReference {
    final number = subscriptionNumber;
    if (sourceType == DeliverySourceType.subscriptionOccurrence &&
        number != null &&
        number.isNotEmpty) {
      return '$number · ${formatDeliveryDate(scheduledDate)}';
    }
    return referenceNumber;
  }
}

class DeliveryOrderSummary {
  const DeliveryOrderSummary({
    required this.orderNumber,
    required this.totalQuantity,
    required this.totalAmount,
    required this.items,
  });

  factory DeliveryOrderSummary.fromJson(Map<String, dynamic> json) =>
      DeliveryOrderSummary(
        orderNumber: json['orderNumber'] as String,
        totalQuantity: (json['totalQuantity'] as num).toDouble(),
        totalAmount: (json['totalAmount'] as num).toDouble(),
        items: (json['items'] as List<dynamic>? ?? const []).cast<String>(),
      );

  final String orderNumber;
  final double totalQuantity;
  final double totalAmount;
  final List<String> items;
}

class DeliveryBatchAllocation {
  const DeliveryBatchAllocation({
    required this.batchId,
    required this.batchNumber,
    required this.quantityAllocated,
    required this.allocatedAt,
    required this.updatedAt,
  });

  factory DeliveryBatchAllocation.fromJson(Map<String, dynamic> json) =>
      DeliveryBatchAllocation(
        batchId: (json['batchId'] as num).toInt(),
        batchNumber: json['batchNumber'] as String,
        quantityAllocated: (json['quantityAllocated'] as num).toDouble(),
        allocatedAt: DateTime.parse(json['allocatedAt'] as String),
        updatedAt: DateTime.parse(json['updatedAt'] as String),
      );

  final int batchId;
  final String batchNumber;
  final double quantityAllocated;
  final DateTime allocatedAt;
  final DateTime updatedAt;

  Map<String, dynamic> toSaveJson() => {
    'batchId': batchId,
    'quantityAllocated': quantityAllocated,
  };
}

class EligibleDeliveryBatch {
  const EligibleDeliveryBatch({
    required this.batchId,
    required this.batchNumber,
    required this.productionAt,
    required this.quantityProduced,
    required this.quantityAvailable,
    required this.unit,
    required this.status,
  });

  factory EligibleDeliveryBatch.fromJson(Map<String, dynamic> json) =>
      EligibleDeliveryBatch(
        batchId: (json['batchId'] as num).toInt(),
        batchNumber: json['batchNumber'] as String,
        productionAt: DateTime.parse(json['productionAt'] as String),
        quantityProduced: (json['quantityProduced'] as num).toDouble(),
        quantityAvailable: (json['quantityAvailable'] as num).toDouble(),
        unit: json['unit'] as String? ?? 'L',
        status: json['status'] as String? ?? 'available',
      );

  final int batchId;
  final String batchNumber;
  final DateTime productionAt;
  final double quantityProduced;
  final double quantityAvailable;
  final String unit;
  final String status;
}

class DeliveryBatchAllocationsInfo {
  const DeliveryBatchAllocationsInfo({
    required this.eligibleBatches,
    required this.allocations,
    required this.totalRequiredQuantity,
  });

  factory DeliveryBatchAllocationsInfo.fromJson(Map<String, dynamic> json) =>
      DeliveryBatchAllocationsInfo(
        eligibleBatches: (json['eligibleBatches'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(EligibleDeliveryBatch.fromJson)
            .toList(growable: false),
        allocations: (json['allocations'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(DeliveryBatchAllocation.fromJson)
            .toList(growable: false),
        totalRequiredQuantity: (json['totalRequiredQuantity'] as num)
            .toDouble(),
      );

  final List<EligibleDeliveryBatch> eligibleBatches;
  final List<DeliveryBatchAllocation> allocations;
  final double totalRequiredQuantity;
}

class DeliveryDetails {
  const DeliveryDetails({
    required this.deliveryId,
    required this.sourceType,
    required this.referenceNumber,
    required this.status,
    required this.scheduledDate,
    required this.branchId,
    required this.customerId,
    required this.customerName,
    required this.customerMobile,
    required this.destinationAddress,
    required this.deliveryInstructions,
    required this.destinationLatitude,
    required this.destinationLongitude,
    required this.assignedEmployeeId,
    required this.assignedEmployeeName,
    required this.assignedAt,
    required this.pickedUpAt,
    required this.outForDeliveryAt,
    required this.arrivedAt,
    required this.otpVerifiedAt,
    required this.completedAt,
    required this.failedAt,
    required this.failureReason,
    required this.remarks,
    required this.operationalNotes,
    required this.subscriptionSlot,
    required this.quantity,
    required this.orderSummary,
    required this.subscriptionId,
    required this.subscriptionNumber,
    required this.subscriptionDeliveryId,
    required this.subscriptionProductName,
    required this.subscriptionUnitOfMeasure,
    required this.isTrackingActive,
    required this.latestLocation,
    required this.assignments,
    required this.batchAllocations,
  });
  factory DeliveryDetails.fromJson(Map<String, dynamic> json) =>
      DeliveryDetails(
        deliveryId: json['deliveryId'] as String,
        sourceType: DeliverySourceType.fromApi(json['sourceType'] as String),
        referenceNumber: json['referenceNumber'] as String,
        status: DeliveryStatus.fromApi(json['status'] as String),
        scheduledDate: DateTime.parse(json['scheduledDate'] as String),
        branchId: (json['branchId'] as num).toInt(),
        customerId: json['customerId'] as String,
        customerName: json['customerName'] as String,
        customerMobile: json['customerMobile'] as String,
        destinationAddress: json['destinationAddress'] as String,
        deliveryInstructions: json['deliveryInstructions'] as String?,
        destinationLatitude: (json['destinationLatitude'] as num).toDouble(),
        destinationLongitude: (json['destinationLongitude'] as num).toDouble(),
        assignedEmployeeId: json['assignedEmployeeId'] as String?,
        assignedEmployeeName: json['assignedEmployeeName'] as String?,
        assignedAt: _date(json['assignedAt']),
        pickedUpAt: _date(json['pickedUpAt']),
        outForDeliveryAt: _date(json['outForDeliveryAt']),
        arrivedAt: _date(json['arrivedAt']),
        otpVerifiedAt: _date(json['otpVerifiedAt']),
        completedAt: _date(json['completedAt']),
        failedAt: _date(json['failedAt']),
        failureReason: json['failureReason'] as String?,
        remarks: json['remarks'] as String?,
        operationalNotes: json['operationalNotes'] as String?,
        subscriptionSlot: json['subscriptionSlot'] == null
            ? null
            : SubscriptionDeliverySlot.fromApi(
                json['subscriptionSlot'] as String,
              ),
        quantity: (json['quantity'] as num?)?.toDouble(),
        orderSummary: json['orderSummary'] == null
            ? null
            : DeliveryOrderSummary.fromJson(
                json['orderSummary'] as Map<String, dynamic>,
              ),
        subscriptionId: json['subscriptionId'] as String?,
        subscriptionNumber: json['subscriptionNumber'] as String?,
        subscriptionDeliveryId: json['subscriptionDeliveryId'] as String?,
        subscriptionProductName: json['subscriptionProductName'] as String?,
        subscriptionUnitOfMeasure:
            json['subscriptionUnitOfMeasure'] as String?,
        isTrackingActive: json['isTrackingActive'] as bool,
        latestLocation: _location(json['latestLocation']),
        assignments: (json['assignments'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(DeliveryAssignment.fromJson)
            .toList(growable: false),
        batchAllocations:
            (json['batchAllocations'] as List<dynamic>? ?? const [])
                .cast<Map<String, dynamic>>()
                .map(DeliveryBatchAllocation.fromJson)
                .toList(growable: false),
      );
  final String deliveryId;
  final DeliverySourceType sourceType;
  final String referenceNumber;
  final DeliveryStatus status;
  final DateTime scheduledDate;
  final int branchId;
  final String customerId;
  final String customerName;
  final String customerMobile;
  final String destinationAddress;
  final String? deliveryInstructions;
  final double destinationLatitude;
  final double destinationLongitude;
  final String? assignedEmployeeId;
  final String? assignedEmployeeName;
  final DateTime? assignedAt;
  final DateTime? pickedUpAt;
  final DateTime? outForDeliveryAt;
  final DateTime? arrivedAt;
  final DateTime? otpVerifiedAt;
  final DateTime? completedAt;
  final DateTime? failedAt;
  final String? failureReason;
  final String? remarks;
  final String? operationalNotes;
  final SubscriptionDeliverySlot? subscriptionSlot;
  final double? quantity;
  final DeliveryOrderSummary? orderSummary;
  final String? subscriptionId;
  final String? subscriptionNumber;
  final String? subscriptionDeliveryId;
  final String? subscriptionProductName;
  final String? subscriptionUnitOfMeasure;
  final bool isTrackingActive;
  final DeliveryLocation? latestLocation;
  final List<DeliveryAssignment> assignments;
  final List<DeliveryBatchAllocation> batchAllocations;
}

/// A past-due scheduled subscription occurrence that was never
/// operationalized: no delivery row exists, so the visit was never
/// attempted. Returned by the branch missed-occurrences endpoint for
/// manager confirmation. [estimatedRefundAmount] is the live per-delivery
/// gross share (frozen payable share including incorporated charges); the
/// ticket freezes it at confirmation time subject to payment limits.
class MissedOccurrence {
  const MissedOccurrence({
    required this.occurrenceId,
    required this.subscriptionId,
    required this.subscriptionNumber,
    required this.scheduledDate,
    required this.daysOverdue,
    required this.branchId,
    required this.branchName,
    required this.customerId,
    required this.customerName,
    required this.estimatedRefundAmount,
  });
  factory MissedOccurrence.fromJson(Map<String, dynamic> json) =>
      MissedOccurrence(
        occurrenceId: json['occurrenceId'] as String,
        subscriptionId: json['subscriptionId'] as String,
        subscriptionNumber: json['subscriptionNumber'] as String? ?? '',
        scheduledDate: DateTime.parse(json['scheduledDate'] as String),
        daysOverdue: (json['daysOverdue'] as num).toInt(),
        branchId: (json['branchId'] as num).toInt(),
        branchName: json['branchName'] as String? ?? '',
        customerId: json['customerId'] as String,
        customerName: json['customerName'] as String? ?? '',
        estimatedRefundAmount:
            (json['estimatedRefundAmount'] as num).toDouble(),
      );
  final String occurrenceId;
  final String subscriptionId;
  final String subscriptionNumber;
  final DateTime scheduledDate;
  final int daysOverdue;
  final int branchId;
  final String branchName;
  final String customerId;
  final String customerName;
  final double estimatedRefundAmount;
}

class DeliveryEmployee {
  const DeliveryEmployee({
    required this.employeeId,
    required this.displayName,
    required this.branchId,
  });
  factory DeliveryEmployee.fromJson(Map<String, dynamic> json) =>
      DeliveryEmployee(
        employeeId: json['employeeId'] as String,
        displayName: json['displayName'] as String,
        branchId: (json['branchId'] as num).toInt(),
      );
  final String employeeId;
  final String displayName;
  final int branchId;
}

class DeliveryMaterialization {
  const DeliveryMaterialization({
    required this.ordersCreated,
    required this.subscriptionOccurrencesCreated,
  });
  factory DeliveryMaterialization.fromJson(Map<String, dynamic> json) =>
      DeliveryMaterialization(
        ordersCreated: json['ordersCreated'] as int,
        subscriptionOccurrencesCreated:
            json['subscriptionOccurrencesCreated'] as int,
      );
  final int ordersCreated;
  final int subscriptionOccurrencesCreated;
}

class BulkAssignmentResult {
  const BulkAssignmentResult({required this.deliveries});

  factory BulkAssignmentResult.fromJson(Map<String, dynamic> json) =>
      BulkAssignmentResult(
        deliveries: (json['deliveries'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(DeliveryDetails.fromJson)
            .toList(growable: false),
      );

  final List<DeliveryDetails> deliveries;
}

class DeliveryNotesRequest {
  const DeliveryNotesRequest({this.remarks});
  final String? remarks;
  Map<String, dynamic> toJson() => {'remarks': remarks};
}

DeliveryLocation? _location(Object? value) => value == null
    ? null
    : DeliveryLocation.fromJson(value as Map<String, dynamic>);

DateTime? _date(Object? value) =>
    value == null ? null : DateTime.parse(value as String);

String formatDeliveryDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/'
    '${value.month.toString().padLeft(2, '0')}/${value.year}';

/// Formats a delivery timestamp as DD/MM/YYYY HH:mm in India time, matching
/// the refund/replacement ticket convention. Used for every timeline step so
/// staff and customers see the date and time of each recorded event.
String formatDeliveryDateTime(DateTime value) {
  final local = toIndiaTime(value);
  String two(int number) => number.toString().padLeft(2, '0');
  return '${two(local.day)}/${two(local.month)}/${local.year} '
      '${two(local.hour)}:${two(local.minute)}';
}

String formatApiDeliveryDate(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';
