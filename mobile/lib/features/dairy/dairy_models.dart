import 'package:doodh_direct_mobile/core/time/india_time.dart';

enum MilkBatchStatus {
  available,
  exhausted,
  unknown;

  factory MilkBatchStatus.fromApi(String value) =>
      switch (value.toLowerCase()) {
        'available' => MilkBatchStatus.available,
        'exhausted' => MilkBatchStatus.exhausted,
        _ => MilkBatchStatus.unknown,
      };

  String get apiValue => switch (this) {
    MilkBatchStatus.available => 'Available',
    MilkBatchStatus.exhausted => 'Exhausted',
    MilkBatchStatus.unknown => 'Unknown',
  };

  String get label => switch (this) {
    MilkBatchStatus.available => 'Available',
    MilkBatchStatus.exhausted => 'Exhausted',
    MilkBatchStatus.unknown => 'Unknown',
  };
}

class MilkBatch {
  const MilkBatch({
    required this.publicId,
    required this.batchNumber,
    required this.branchId,
    required this.productionPublicId,
    required this.productionAt,
    required this.quantityProduced,
    required this.availableQuantity,
    required this.unit,
    required this.status,
    required this.createdAt,
  });

  factory MilkBatch.fromJson(Map<String, dynamic> json) => MilkBatch(
    publicId: json['publicId'] as String,
    batchNumber: json['batchNumber'] as String,
    branchId: (json['branchId'] as num).toInt(),
    productionPublicId: json['productionPublicId'] as String,
    productionAt: DateTime.parse(json['productionAt'] as String),
    quantityProduced: (json['quantityProduced'] as num).toDouble(),
    availableQuantity: (json['availableQuantity'] as num).toDouble(),
    unit: json['unit'] as String,
    status: MilkBatchStatus.fromApi(json['status'] as String),
    createdAt: DateTime.parse(json['createdAt'] as String),
  );

  final String publicId;
  final String batchNumber;
  final int branchId;
  final String productionPublicId;
  final DateTime productionAt;
  final double quantityProduced;
  final double availableQuantity;
  final String unit;
  final MilkBatchStatus status;
  final DateTime createdAt;
}

class MilkProduction {
  const MilkProduction({
    required this.publicId,
    required this.branchId,
    required this.productionAt,
    required this.shift,
    required this.buffaloCount,
    required this.quantityProduced,
    required this.unit,
    required this.recordedByUserId,
    required this.remarks,
    required this.createdAt,
    required this.batch,
  });

  factory MilkProduction.fromJson(Map<String, dynamic> json) => MilkProduction(
    publicId: json['publicId'] as String,
    branchId: (json['branchId'] as num).toInt(),
    productionAt: DateTime.parse(json['productionAt'] as String),
    shift: json['shift'] as String?,
    buffaloCount: (json['buffaloCount'] as num).toInt(),
    quantityProduced: (json['quantityProduced'] as num).toDouble(),
    unit: json['unit'] as String,
    recordedByUserId: (json['recordedByUserId'] as num).toInt(),
    remarks: json['remarks'] as String?,
    createdAt: DateTime.parse(json['createdAt'] as String),
    batch: MilkBatch.fromJson(json['batch'] as Map<String, dynamic>),
  );

  final String publicId;
  final int branchId;
  final DateTime productionAt;
  final String? shift;
  final int buffaloCount;
  final double quantityProduced;
  final String unit;
  final int recordedByUserId;
  final String? remarks;
  final DateTime createdAt;
  final MilkBatch batch;
}

class MilkUsage {
  const MilkUsage({
    required this.publicId,
    required this.batchPublicId,
    required this.batchNumber,
    required this.branchId,
    required this.usedAt,
    required this.quantityUsed,
    required this.unit,
    required this.purpose,
    required this.recordedByUserId,
    required this.remarks,
    required this.createdAt,
  });

  factory MilkUsage.fromJson(Map<String, dynamic> json) => MilkUsage(
    publicId: json['publicId'] as String,
    batchPublicId: json['batchPublicId'] as String,
    batchNumber: json['batchNumber'] as String,
    branchId: (json['branchId'] as num).toInt(),
    usedAt: DateTime.parse(json['usedAt'] as String),
    quantityUsed: (json['quantityUsed'] as num).toDouble(),
    unit: json['unit'] as String,
    purpose: json['purpose'] as String,
    recordedByUserId: (json['recordedByUserId'] as num).toInt(),
    remarks: json['remarks'] as String?,
    createdAt: DateTime.parse(json['createdAt'] as String),
  );

  final String publicId;
  final String batchPublicId;
  final String batchNumber;
  final int branchId;
  final DateTime usedAt;
  final double quantityUsed;
  final String unit;
  final String purpose;
  final int recordedByUserId;
  final String? remarks;
  final DateTime createdAt;
}

class MilkAvailability {
  const MilkAvailability({
    required this.branchId,
    required this.quantityProduced,
    required this.quantityUsed,
    required this.availableQuantity,
    required this.unit,
    required this.availableBatchCount,
    required this.calculatedAt,
  });

  factory MilkAvailability.fromJson(Map<String, dynamic> json) =>
      MilkAvailability(
        branchId: (json['branchId'] as num).toInt(),
        quantityProduced: (json['quantityProduced'] as num).toDouble(),
        quantityUsed: (json['quantityUsed'] as num).toDouble(),
        availableQuantity: (json['availableQuantity'] as num).toDouble(),
        unit: json['unit'] as String,
        availableBatchCount: (json['availableBatchCount'] as num).toInt(),
        calculatedAt: DateTime.parse(json['calculatedAt'] as String),
      );

  final int branchId;
  final double quantityProduced;
  final double quantityUsed;
  final double availableQuantity;
  final String unit;
  final int availableBatchCount;
  final DateTime calculatedAt;
}

class DairyDashboard {
  const DairyDashboard({
    required this.branchId,
    required this.productionDate,
    required this.quantityProduced,
    required this.availableQuantity,
    required this.unit,
    required this.productionEntryCount,
    required this.availableBatchCount,
    required this.calculatedAt,
  });

  factory DairyDashboard.fromJson(Map<String, dynamic> json) => DairyDashboard(
    branchId: (json['branchId'] as num).toInt(),
    productionDate: DateTime.parse(json['productionDate'] as String),
    quantityProduced: (json['quantityProduced'] as num).toDouble(),
    availableQuantity: (json['availableQuantity'] as num).toDouble(),
    unit: json['unit'] as String,
    productionEntryCount: (json['productionEntryCount'] as num).toInt(),
    availableBatchCount: (json['availableBatchCount'] as num).toInt(),
    calculatedAt: DateTime.parse(json['calculatedAt'] as String),
  );

  final int branchId;
  final DateTime productionDate;
  final double quantityProduced;
  final double availableQuantity;
  final String unit;
  final int productionEntryCount;
  final int availableBatchCount;
  final DateTime calculatedAt;
}

class RecordMilkProductionRequest {
  const RecordMilkProductionRequest({
    required this.productionAt,
    required this.shift,
    required this.buffaloCount,
    required this.quantityProduced,
    this.remarks,
  });

  final DateTime productionAt;
  final String? shift;
  final int buffaloCount;
  final double quantityProduced;
  final String? remarks;

  Map<String, dynamic> toJson() => {
    'productionAt': indiaToUtc(productionAt).toIso8601String(),
    'shift': shift,
    'buffaloCount': buffaloCount,
    'quantityProduced': quantityProduced,
    'unit': 'L',
    'remarks': remarks,
  };
}

class RecordMilkUsageRequest {
  const RecordMilkUsageRequest({
    required this.usedAt,
    required this.quantityUsed,
    required this.purpose,
    this.remarks,
  });

  final DateTime usedAt;
  final double quantityUsed;
  final String purpose;
  final String? remarks;

  Map<String, dynamic> toJson() => {
    'usedAt': indiaToUtc(usedAt).toIso8601String(),
    'quantityUsed': quantityUsed,
    'purpose': purpose,
    'remarks': remarks,
  };
}

String formatDairyDate(DateTime value) {
  final local = indiaWallClock(value);
  return '${local.day.toString().padLeft(2, '0')}/'
      '${local.month.toString().padLeft(2, '0')}/${local.year}';
}

String formatDairyDateTime(DateTime value) {
  final local = indiaWallClock(value);
  final hour = local.hour % 12 == 0 ? 12 : local.hour % 12;
  final period = local.hour < 12 ? 'AM' : 'PM';
  return '${formatDairyDate(local)} '
      '${hour.toString().padLeft(2, '0')}:'
      '${local.minute.toString().padLeft(2, '0')} $period';
}

String formatApiDairyDate(DateTime value) {
  final local = indiaWallClock(value);
  return '${local.year.toString().padLeft(4, '0')}-'
      '${local.month.toString().padLeft(2, '0')}-'
      '${local.day.toString().padLeft(2, '0')}';
}

String formatMilkQuantity(double value, String unit) {
  final text = value.toStringAsFixed(3).replaceFirst(RegExp(r'\.?0+$'), '');
  return '$text $unit';
}
