/// A Tax & Charges master record visible to Setup → Tax & Charges.
///
/// Configuration data only: every `isActive` record is applied to one-time
/// customer checkout by the server; inactive records are ignored. Labels and
/// percentages live here — the client never hard-codes charge names.
class Charge {
  const Charge({
    required this.publicId,
    required this.chargeType,
    required this.chargeCode,
    required this.description,
    required this.percentage,
    required this.isActive,
    this.applicableOnAll = true,
    required this.isUsed,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Charge.fromJson(Map<String, dynamic> json) => Charge(
    publicId: json['publicId'] as String,
    chargeType: json['chargeType'] as String,
    chargeCode: json['chargeCode'] as String,
    description: json['description'] == null
        ? null
        : json['description'] as String,
    percentage: (json['percentage'] as num).toDouble(),
    isActive: json['isActive'] as bool,
    // Legacy payloads (and older fakes) omit the flag; the server default for
    // every charge is globally applicable, so fall back to true.
    applicableOnAll: (json['applicableOnAll'] as bool?) ?? true,
    isUsed: json['isUsed'] as bool,
    createdAt: DateTime.tryParse(json['createdAt'] as String? ?? ''),
    updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? ''),
  );

  final String publicId;
  final String chargeType;
  final String chargeCode;
  final String? description;
  final double percentage;
  final bool isActive;

  /// Applicability mode. `true` = global (applies to every eligible purchase);
  /// `false` = item-level (may be assigned to individual products). The server
  /// refuses switching back to global while any product assignment exists, so
  /// the UI surfaces that business rule instead of faking success.
  final bool applicableOnAll;

  /// True once any order snapshot references this charge's code — the backend
  /// then refuses deletes and type edits, so the UI mirrors that.
  final bool isUsed;
  final DateTime? createdAt;
  final DateTime? updatedAt;
}

/// Payload for creating a charge. `Active` is implied (new charges start
/// active on the backend); activation is toggled afterwards through the
/// dedicated activate/deactivate endpoints. The applicability mode is set at
/// creation and toggled later through the dedicated applicability endpoint —
/// the generic update endpoint never carries it.
class CreateChargeRequest {
  const CreateChargeRequest({
    required this.chargeType,
    required this.chargeCode,
    required this.description,
    required this.percentage,
    this.applicableOnAll = true,
  });

  final String chargeType;
  final String chargeCode;
  final String? description;
  final double percentage;
  final bool applicableOnAll;

  Map<String, dynamic> toJson() => {
    'chargeType': chargeType,
    'chargeCode': chargeCode,
    'description': description,
    'percentage': percentage,
    'applicableOnAll': applicableOnAll,
  };
}

/// Payload for updating a charge. ChargeCode is server-immutable once used;
/// ChargeType likewise after first use (the backend rejects the change).
class UpdateChargeRequest {
  const UpdateChargeRequest({
    required this.chargeType,
    required this.description,
    required this.percentage,
  });

  final String chargeType;
  final String? description;
  final double percentage;

  Map<String, dynamic> toJson() => {
    'chargeType': chargeType,
    'description': description,
    'percentage': percentage,
  };
}
