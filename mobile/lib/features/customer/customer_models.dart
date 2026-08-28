class CustomerProfile {
  const CustomerProfile({
    required this.publicId,
    required this.firstName,
    required this.lastName,
    required this.dateOfBirth,
    required this.gender,
    required this.alternateMobile,
    this.customerNumber,
  });

  factory CustomerProfile.fromJson(Map<String, dynamic> json) =>
      CustomerProfile(
        publicId: json['publicId'] as String,
        firstName: json['firstName'] as String?,
        lastName: json['lastName'] as String?,
        dateOfBirth: json['dateOfBirth'] == null
            ? null
            : DateTime.parse(json['dateOfBirth'] as String),
        gender: json['gender'] as String?,
        alternateMobile: json['alternateMobile'] as String?,
        customerNumber: json['customerNumber'] as String?,
      );

  final String publicId;
  final String? firstName;
  final String? lastName;
  final DateTime? dateOfBirth;
  final String? gender;
  final String? alternateMobile;
  final String? customerNumber;

  String get fullName {
    final parts = [
      firstName,
      lastName,
    ].whereType<String>().where((value) => value.trim().isNotEmpty);
    return parts.join(' ');
  }
}

class UpdateCustomerProfile {
  const UpdateCustomerProfile({
    this.firstName,
    this.lastName,
    this.dateOfBirth,
    this.gender,
    this.alternateMobile,
  });

  final String? firstName;
  final String? lastName;
  final DateTime? dateOfBirth;
  final String? gender;
  final String? alternateMobile;

  Map<String, dynamic> toJson() => {
    'firstName': _optional(firstName),
    'lastName': _optional(lastName),
    'dateOfBirth': dateOfBirth == null
        ? null
        : '${dateOfBirth!.year.toString().padLeft(4, '0')}-'
              '${dateOfBirth!.month.toString().padLeft(2, '0')}-'
              '${dateOfBirth!.day.toString().padLeft(2, '0')}',
    'gender': _optional(gender),
    'alternateMobile': _optional(alternateMobile),
  };
}

class CustomerAddress {
  const CustomerAddress({
    required this.publicId,
    required this.label,
    required this.addressLine1,
    required this.addressLine2,
    required this.locality,
    required this.city,
    required this.state,
    required this.pinCode,
    required this.landmark,
    required this.deliveryInstructions,
    required this.contactName,
    required this.contactMobile,
    required this.latitude,
    required this.longitude,
    required this.isDefault,
    required this.isActive,
  });

  factory CustomerAddress.fromJson(Map<String, dynamic> json) =>
      CustomerAddress(
        publicId: json['publicId'] as String,
        label: json['label'] as String,
        addressLine1: json['addressLine1'] as String,
        addressLine2: json['addressLine2'] as String?,
        locality: json['locality'] as String,
        city: json['city'] as String,
        state: json['state'] as String,
        pinCode: json['pinCode'] as String,
        landmark: json['landmark'] as String?,
        deliveryInstructions: json['deliveryInstructions'] as String?,
        contactName: json['contactName'] as String,
        contactMobile: json['contactMobile'] as String,
        latitude: (json['latitude'] as num).toDouble(),
        longitude: (json['longitude'] as num).toDouble(),
        isDefault: json['isDefault'] as bool,
        isActive: json['isActive'] as bool,
      );

  final String publicId;
  final String label;
  final String addressLine1;
  final String? addressLine2;
  final String locality;
  final String city;
  final String state;
  final String pinCode;
  final String? landmark;
  final String? deliveryInstructions;
  final String contactName;
  final String contactMobile;
  final double latitude;
  final double longitude;
  final bool isDefault;
  final bool isActive;

  AddressDraft toDraft({bool? isDefault}) => AddressDraft(
    label: label,
    addressLine1: addressLine1,
    addressLine2: addressLine2,
    locality: locality,
    city: city,
    state: state,
    pinCode: pinCode,
    landmark: landmark,
    deliveryInstructions: deliveryInstructions,
    contactName: contactName,
    contactMobile: contactMobile,
    latitude: latitude,
    longitude: longitude,
    isDefault: isDefault ?? this.isDefault,
  );
}

class AddressDraft {
  const AddressDraft({
    required this.label,
    required this.addressLine1,
    this.addressLine2,
    required this.locality,
    required this.city,
    required this.state,
    required this.pinCode,
    this.landmark,
    this.deliveryInstructions,
    required this.contactName,
    required this.contactMobile,
    required this.latitude,
    required this.longitude,
    required this.isDefault,
  });

  final String label;
  final String addressLine1;
  final String? addressLine2;
  final String locality;
  final String city;
  final String state;
  final String pinCode;
  final String? landmark;
  final String? deliveryInstructions;
  final String contactName;
  final String contactMobile;
  final double latitude;
  final double longitude;
  final bool isDefault;

  Map<String, dynamic> toJson() => {
    'label': label.trim(),
    'addressLine1': addressLine1.trim(),
    'addressLine2': _optional(addressLine2),
    'locality': locality.trim(),
    'city': city.trim(),
    'state': state.trim(),
    'pinCode': pinCode.trim(),
    'landmark': _optional(landmark),
    'deliveryInstructions': _optional(deliveryInstructions),
    'contactName': contactName.trim(),
    'contactMobile': contactMobile.trim(),
    'latitude': latitude,
    'longitude': longitude,
    'isDefault': isDefault,
  };
}

class AddressLookup {
  const AddressLookup({
    required this.addressLine1,
    required this.locality,
    required this.city,
    required this.state,
    required this.pinCode,
    required this.latitude,
    required this.longitude,
    this.landmark,
    this.country,
  });

  factory AddressLookup.fromJson(Map<String, dynamic> json) => AddressLookup(
    addressLine1: json['addressLine1'] as String?,
    locality: json['locality'] as String?,
    city: json['city'] as String?,
    state: json['state'] as String?,
    pinCode: json['pinCode'] as String?,
    landmark: json['landmark'] as String?,
    country: json['country'] as String?,
    latitude: (json['latitude'] as num).toDouble(),
    longitude: (json['longitude'] as num).toDouble(),
  );

  final String? addressLine1;
  final String? locality;
  final String? city;
  final String? state;
  final String? pinCode;
  final String? landmark;
  final String? country;
  final double latitude;
  final double longitude;
}

String? _optional(String? value) {
  final normalized = value?.trim();
  return normalized == null || normalized.isEmpty ? null : normalized;
}
