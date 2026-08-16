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
    DeliverySourceType.oneTimeOrder => 'Order',
    DeliverySourceType.subscriptionOccurrence => 'Subscription',
    DeliverySourceType.unknown => 'Delivery',
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
    required this.recordedAtUtc,
  });
  factory DeliveryLocation.fromJson(Map<String, dynamic> json) =>
      DeliveryLocation(
        latitude: (json['latitude'] as num).toDouble(),
        longitude: (json['longitude'] as num).toDouble(),
        accuracyMetres: (json['accuracyMetres'] as num?)?.toDouble(),
        recordedAtUtc: DateTime.parse(json['recordedAtUtc'] as String),
      );
  final double latitude;
  final double longitude;
  final double? accuracyMetres;
  final DateTime recordedAtUtc;
}

class DeliveryAssignment {
  const DeliveryAssignment({
    required this.employeeId,
    required this.employeeName,
    required this.assignedByUserId,
    required this.assignedAtUtc,
    required this.reason,
  });
  factory DeliveryAssignment.fromJson(Map<String, dynamic> json) =>
      DeliveryAssignment(
        employeeId: json['employeeId'] as String,
        employeeName: json['employeeName'] as String?,
        assignedByUserId: json['assignedByUserId'] as String,
        assignedAtUtc: DateTime.parse(json['assignedAtUtc'] as String),
        reason: json['reason'] as String?,
      );
  final String employeeId;
  final String? employeeName;
  final String assignedByUserId;
  final DateTime assignedAtUtc;
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
    required this.completedAtUtc,
    required this.failedAtUtc,
    required this.failureReason,
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
        completedAtUtc: _date(json['completedAtUtc']),
        failedAtUtc: _date(json['failedAtUtc']),
        failureReason: json['failureReason'] as String?,
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
  final DateTime? completedAtUtc;
  final DateTime? failedAtUtc;
  final String? failureReason;
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
    required this.assignedAtUtc,
    required this.pickedUpAtUtc,
    required this.outForDeliveryAtUtc,
    required this.arrivedAtUtc,
    required this.otpVerifiedAtUtc,
    required this.completedAtUtc,
    required this.failedAtUtc,
    required this.failureReason,
    required this.remarks,
    required this.operationalNotes,
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
        assignedAtUtc: _date(json['assignedAtUtc']),
        pickedUpAtUtc: _date(json['pickedUpAtUtc']),
        outForDeliveryAtUtc: _date(json['outForDeliveryAtUtc']),
        arrivedAtUtc: _date(json['arrivedAtUtc']),
        otpVerifiedAtUtc: _date(json['otpVerifiedAtUtc']),
        completedAtUtc: _date(json['completedAtUtc']),
        failedAtUtc: _date(json['failedAtUtc']),
        failureReason: json['failureReason'] as String?,
        remarks: json['remarks'] as String?,
        operationalNotes: json['operationalNotes'] as String?,
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
  final DateTime? assignedAtUtc;
  final DateTime? pickedUpAtUtc;
  final DateTime? outForDeliveryAtUtc;
  final DateTime? arrivedAtUtc;
  final DateTime? otpVerifiedAtUtc;
  final DateTime? completedAtUtc;
  final DateTime? failedAtUtc;
  final String? failureReason;
  final String? remarks;
  final String? operationalNotes;
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
