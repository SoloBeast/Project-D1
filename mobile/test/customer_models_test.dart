import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('profile parses nullable fields and builds full name', () {
    final profile = CustomerProfile.fromJson({
      'publicId': '4d63e4c7-9c7c-4b83-9fac-718028534f64',
      'firstName': 'Asha',
      'lastName': 'Sharma',
      'dateOfBirth': '1992-04-09',
      'gender': null,
      'alternateMobile': null,
    });

    expect(profile.fullName, 'Asha Sharma');
    expect(profile.dateOfBirth, DateTime(1992, 4, 9));
    expect(profile.gender, isNull);
  });

  test('profile update serializes date and normalizes optional strings', () {
    const update = UpdateCustomerProfile(
      firstName: '  Asha  ',
      lastName: '   ',
      dateOfBirth: null,
      gender: ' Female ',
      alternateMobile: '',
    );

    expect(update.toJson(), {
      'firstName': 'Asha',
      'lastName': null,
      'dateOfBirth': null,
      'gender': 'Female',
      'alternateMobile': null,
    });

    final dated = UpdateCustomerProfile(dateOfBirth: DateTime(2001, 2, 3));
    expect(dated.toJson()['dateOfBirth'], '2001-02-03');
  });

  test('address parses numeric coordinates and produces update draft', () {
    final address = CustomerAddress.fromJson({
      'publicId': '88434ec4-7e15-4dc9-863f-c5c58fb62638',
      'label': 'Home',
      'addressLine1': '12 Market Road',
      'addressLine2': null,
      'locality': 'Indiranagar',
      'city': 'Bengaluru',
      'state': 'Karnataka',
      'pinCode': '560038',
      'landmark': 'Near Metro',
      'deliveryInstructions': null,
      'contactName': 'Asha Sharma',
      'contactMobile': '+919876543210',
      'latitude': 12,
      'longitude': 77.64,
      'isDefault': false,
      'isActive': true,
    });

    expect(address.latitude, 12.0);
    expect(address.longitude, 77.64);

    final draft = address.toDraft(isDefault: true);
    expect(draft.isDefault, isTrue);
    expect(draft.toJson()['addressLine2'], isNull);
    expect(draft.toJson()['latitude'], 12.0);
  });

  test('address draft trims required values and nulls empty optionals', () {
    const draft = AddressDraft(
      label: ' Home ',
      addressLine1: ' 12 Market Road ',
      addressLine2: ' ',
      locality: ' Indiranagar ',
      city: ' Bengaluru ',
      state: ' Karnataka ',
      pinCode: ' 560038 ',
      landmark: '',
      deliveryInstructions: ' Leave at reception ',
      contactName: ' Asha Sharma ',
      contactMobile: ' +919876543210 ',
      latitude: 12.9716,
      longitude: 77.5946,
      isDefault: true,
    );

    expect(draft.toJson(), {
      'label': 'Home',
      'addressLine1': '12 Market Road',
      'addressLine2': null,
      'locality': 'Indiranagar',
      'city': 'Bengaluru',
      'state': 'Karnataka',
      'pinCode': '560038',
      'landmark': null,
      'deliveryInstructions': 'Leave at reception',
      'contactName': 'Asha Sharma',
      'contactMobile': '+919876543210',
      'latitude': 12.9716,
      'longitude': 77.5946,
      'isDefault': true,
    });
  });

  test('reverse lookup parses provider-neutral response', () {
    final lookup = AddressLookup.fromJson({
      'addressLine1': '12 Market Road',
      'locality': 'Indiranagar',
      'city': 'Bengaluru',
      'state': 'Karnataka',
      'pinCode': '560038',
      'latitude': 12.9716,
      'longitude': 77.5946,
    });

    expect(lookup.city, 'Bengaluru');
    expect(lookup.latitude, 12.9716);
  });
}
