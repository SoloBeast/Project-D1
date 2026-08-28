enum MilkTestStatus {
  requested,
  completed,
  unknown;

  static MilkTestStatus parse(Object? value) => switch (value) {
    'Requested' || 'requested' => requested,
    'Completed' || 'completed' => completed,
    _ => unknown,
  };

  String get label => switch (this) {
    requested => 'Requested',
    completed => 'Completed',
    unknown => 'Unknown',
  };
}

enum MilkTestCustomerDecision {
  pending,
  confirmed,
  rejected,
  unknown;

  static MilkTestCustomerDecision parse(Object? value) => switch (value) {
    'Pending' || 'pending' => pending,
    'Confirmed' || 'confirmed' => confirmed,
    'Rejected' || 'rejected' => rejected,
    _ => unknown,
  };

  String get label => switch (this) {
    pending => 'Awaiting customer decision',
    confirmed => 'Confirmed',
    rejected => 'Rejected',
    unknown => 'Unknown',
  };

  bool get isTerminal =>
      this == MilkTestCustomerDecision.confirmed ||
      this == MilkTestCustomerDecision.rejected;
}

class MilkTestImage {
  const MilkTestImage({
    required this.imageId,
    required this.fileName,
    required this.contentType,
    required this.fileSize,
    required this.uploadedAtUtc,
    required this.contentPath,
  });

  factory MilkTestImage.fromJson(Map<String, dynamic> json) => MilkTestImage(
    imageId: json['imageId'] as String,
    fileName: json['fileName'] as String,
    contentType: json['contentType'] as String,
    fileSize: (json['fileSize'] as num).toInt(),
    uploadedAtUtc: _requiredProtocolDate(
      json,
      'uploadedAt',
      'uploadedAtUtc',
    ),
    contentPath: json['contentPath'] as String,
  );

  final String imageId;
  final String fileName;
  final String contentType;
  final int fileSize;
  final DateTime uploadedAtUtc;
  final String contentPath;
}

class MilkTestParameter {
  const MilkTestParameter({
    required this.code,
    required this.name,
    required this.value,
    required this.unit,
  });

  factory MilkTestParameter.fromJson(Map<String, dynamic> json) =>
      MilkTestParameter(
        code: json['code'] as String,
        name: json['name'] as String,
        value: (json['value'] as num).toDouble(),
        unit: json['unit'] as String,
      );

  final String code;
  final String name;
  final double value;
  final String unit;

  Map<String, dynamic> toJson() => {
    'code': code,
    'name': name,
    'value': value,
    'unit': unit,
  };
}

class CustomerMilkTest {
  const CustomerMilkTest({
    required this.milkTestId,
    required this.deliveryId,
    required this.status,
    required this.customerDecision,
    required this.requestedAtUtc,
    required this.completedAtUtc,
    required this.confirmedAtUtc,
    required this.rejectedAtUtc,
    required this.customerRemarks,
    required this.images,
  });

  factory CustomerMilkTest.fromJson(
    Map<String, dynamic> json,
  ) => CustomerMilkTest(
    milkTestId: json['milkTestId'] as String,
    deliveryId: json['deliveryId'] as String,
    status: MilkTestStatus.parse(json['status']),
    customerDecision: MilkTestCustomerDecision.parse(json['customerDecision']),
    requestedAtUtc: _requiredProtocolDate(
      json,
      'requestedAt',
      'requestedAtUtc',
    ),
    completedAtUtc: _optionalProtocolDate(
      json,
      'completedAt',
      'completedAtUtc',
    ),
    confirmedAtUtc: _optionalProtocolDate(
      json,
      'confirmedAt',
      'confirmedAtUtc',
    ),
    rejectedAtUtc: _optionalProtocolDate(json, 'rejectedAt', 'rejectedAtUtc'),
    customerRemarks: json['customerRemarks'] as String?,
    images: _images(json['images']),
  );

  final String milkTestId;
  final String deliveryId;
  final MilkTestStatus status;
  final MilkTestCustomerDecision customerDecision;
  final DateTime requestedAtUtc;
  final DateTime? completedAtUtc;
  final DateTime? confirmedAtUtc;
  final DateTime? rejectedAtUtc;
  final String? customerRemarks;
  final List<MilkTestImage> images;

  bool get canDecide =>
      status == MilkTestStatus.completed && !customerDecision.isTerminal;
}

class StaffMilkTest {
  const StaffMilkTest({
    required this.milkTestId,
    required this.deliveryId,
    required this.status,
    required this.customerDecision,
    required this.requestedAtUtc,
    required this.completedAtUtc,
    required this.staffRemarks,
    required this.confirmedAtUtc,
    required this.rejectedAtUtc,
    required this.customerRemarks,
    required this.parameters,
    required this.images,
  });

  factory StaffMilkTest.fromJson(Map<String, dynamic> json) => StaffMilkTest(
    milkTestId: json['milkTestId'] as String,
    deliveryId: json['deliveryId'] as String,
    status: MilkTestStatus.parse(json['status']),
    customerDecision: MilkTestCustomerDecision.parse(json['customerDecision']),
    requestedAtUtc: _requiredProtocolDate(
      json,
      'requestedAt',
      'requestedAtUtc',
    ),
    completedAtUtc: _optionalProtocolDate(
      json,
      'completedAt',
      'completedAtUtc',
    ),
    staffRemarks: json['staffRemarks'] as String?,
    confirmedAtUtc: _optionalProtocolDate(
      json,
      'confirmedAt',
      'confirmedAtUtc',
    ),
    rejectedAtUtc: _optionalProtocolDate(json, 'rejectedAt', 'rejectedAtUtc'),
    customerRemarks: json['customerRemarks'] as String?,
    parameters: (json['parameters'] as List<dynamic>? ?? const [])
        .cast<Map<String, dynamic>>()
        .map(MilkTestParameter.fromJson)
        .toList(growable: false),
    images: _images(json['images']),
  );

  final String milkTestId;
  final String deliveryId;
  final MilkTestStatus status;
  final MilkTestCustomerDecision customerDecision;
  final DateTime requestedAtUtc;
  final DateTime? completedAtUtc;
  final String? staffRemarks;
  final DateTime? confirmedAtUtc;
  final DateTime? rejectedAtUtc;
  final String? customerRemarks;
  final List<MilkTestParameter> parameters;
  final List<MilkTestImage> images;
}

DateTime _requiredProtocolDate(
  Map<String, dynamic> json,
  String key,
  String fallbackKey,
) {
  final value = json[key] ?? json[fallbackKey];
  if (value is! String || value.isEmpty) {
    throw FormatException('Missing milk-test timestamp: $key.');
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

List<MilkTestImage> _images(Object? value) =>
    (value as List<dynamic>? ?? const [])
        .cast<Map<String, dynamic>>()
        .map(MilkTestImage.fromJson)
        .toList(growable: false);
