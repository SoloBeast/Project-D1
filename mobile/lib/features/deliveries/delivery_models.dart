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
  unknown;

  factory DeliveryStatus.fromApi(String value) => switch (value.toLowerCase()) {
    'readyforassignment' => DeliveryStatus.readyForAssignment,
    'assigned' => DeliveryStatus.assigned,
    'pickedup' => DeliveryStatus.pickedUp,
    'outfordelivery' => DeliveryStatus.outForDelivery,
    'arrived' => DeliveryStatus.arrived,
    'delivered' => DeliveryStatus.delivered,
    'failed' => DeliveryStatus.failed,
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
    required this.isTrackingActive,
    required this.latestLocation,
    required this.assignments,
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
        isTrackingActive: json['isTrackingActive'] as bool,
        latestLocation: _location(json['latestLocation']),
        assignments: (json['assignments'] as List<dynamic>? ?? const [])
            .cast<Map<String, dynamic>>()
            .map(DeliveryAssignment.fromJson)
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
  final bool isTrackingActive;
  final DeliveryLocation? latestLocation;
  final List<DeliveryAssignment> assignments;
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

String formatApiDeliveryDate(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';
