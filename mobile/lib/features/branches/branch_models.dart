/// Branch record returned by the Branch Management module.
///
/// [branchNumber] is allocated server-side from the centralized `BRANCH`
/// numbering series. The client never generates or submits a branch number;
/// it is always read-only and displayed from the backend response.
class Branch {
  const Branch({
    required this.publicId,
    required this.code,
    required this.name,
    required this.addressLine1,
    required this.addressLine2,
    required this.locality,
    required this.city,
    required this.state,
    required this.pinCode,
    required this.latitude,
    required this.longitude,
    required this.serviceRadiusKm,
    required this.isActive,
    required this.branchNumber,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Branch.fromJson(Map<String, dynamic> json) => Branch(
    publicId: json['publicId'] as String,
    code: json['code'] as String,
    name: json['name'] as String,
    addressLine1: json['addressLine1'] as String?,
    addressLine2: json['addressLine2'] as String?,
    locality: json['locality'] as String?,
    city: json['city'] as String,
    state: json['state'] as String,
    pinCode: json['pinCode'] as String?,
    latitude: (json['latitude'] as num?)?.toDouble() ?? 0,
    longitude: (json['longitude'] as num?)?.toDouble() ?? 0,
    serviceRadiusKm: (json['serviceRadiusKm'] as num?)?.toDouble(),
    isActive: json['isActive'] as bool,
    branchNumber: json['branchNumber'] as String?,
    createdAt: _parseDate(json['createdAt']),
    updatedAt: _parseDate(json['updatedAt']),
  );

  final String publicId;
  final String code;
  final String name;
  final String? addressLine1;
  final String? addressLine2;
  final String? locality;
  final String city;
  final String state;
  final String? pinCode;
  final double latitude;
  final double longitude;
  final double? serviceRadiusKm;
  final bool isActive;
  final String? branchNumber;
  final DateTime? createdAt;
  final DateTime? updatedAt;

  /// Short-form address composed from the non-empty address parts.
  String get addressSummary {
    final parts = <String>[
      if (addressLine1?.trim().isNotEmpty ?? false) addressLine1!.trim(),
      if (addressLine2?.trim().isNotEmpty ?? false) addressLine2!.trim(),
      if (locality?.trim().isNotEmpty ?? false) locality!.trim(),
      city.trim(),
      state.trim(),
      if (pinCode?.trim().isNotEmpty ?? false) pinCode!.trim(),
    ];
    return parts.isEmpty ? 'No address recorded' : parts.join(', ');
  }

  Branch copyWith({bool? isActive}) => Branch(
    publicId: publicId,
    code: code,
    name: name,
    addressLine1: addressLine1,
    addressLine2: addressLine2,
    locality: locality,
    city: city,
    state: state,
    pinCode: pinCode,
    latitude: latitude,
    longitude: longitude,
    serviceRadiusKm: serviceRadiusKm,
    isActive: isActive ?? this.isActive,
    branchNumber: branchNumber,
    createdAt: createdAt,
    updatedAt: updatedAt,
  );
}

/// Request used to create or update a branch.
///
/// [code] is the stable business key referenced by order allocations and
/// scoped numbering series. [branchNumber] is never part of the request — it
/// is allocated by the backend from the `BRANCH` numbering series.
class UpsertBranchRequest {
  const UpsertBranchRequest({
    required this.code,
    required this.name,
    required this.addressLine1,
    required this.addressLine2,
    required this.locality,
    required this.city,
    required this.state,
    required this.pinCode,
    required this.latitude,
    required this.longitude,
    required this.serviceRadiusKm,
  });

  final String code;
  final String name;
  final String? addressLine1;
  final String? addressLine2;
  final String? locality;
  final String city;
  final String state;
  final String? pinCode;
  final double latitude;
  final double longitude;
  final double? serviceRadiusKm;

  Map<String, dynamic> toJson() => {
    'code': code.trim().toUpperCase(),
    'name': name.trim(),
    if (addressLine1?.trim().isNotEmpty ?? false)
      'addressLine1': addressLine1!.trim(),
    if (addressLine2?.trim().isNotEmpty ?? false)
      'addressLine2': addressLine2!.trim(),
    if (locality?.trim().isNotEmpty ?? false) 'locality': locality!.trim(),
    'city': city.trim(),
    'state': state.trim(),
    if (pinCode?.trim().isNotEmpty ?? false) 'pinCode': pinCode!.trim(),
    'latitude': latitude,
    'longitude': longitude,
    if (serviceRadiusKm != null) 'serviceRadiusKm': serviceRadiusKm,
  };
}

DateTime? _parseDate(Object? value) {
  if (value is! String || value.trim().isEmpty) return null;
  return DateTime.tryParse(value)?.toLocal();
}
