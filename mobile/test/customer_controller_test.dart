import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('customer controller', () {
    test(
      'maps a profile field validation error to the form field key',
      () async {
        final container = await _authenticatedContainer(
          _FailingCustomerRepository(
            const ApiException(
              422,
              'VALIDATION_ERROR',
              'Mobile number format is invalid.',
              field: 'AlternateMobile',
            ),
          ),
        );
        addTearDown(container.dispose);

        final saved = await container
            .read(customerControllerProvider.notifier)
            .saveProfile(
              const UpdateCustomerProfile(alternateMobile: '1234567890'),
            );
        final state = container.read(customerControllerProvider);

        expect(saved, isFalse);
        expect(state.isSaving, isFalse);
        expect(state.errorMessage, 'Mobile number format is invalid.');
        expect(state.fieldErrors, {
          'alternateMobile': 'Mobile number format is invalid.',
        });
      },
    );

    test(
      'returns reverse lookup metadata for authenticated customers',
      () async {
        const lookup = AddressLookup(
          addressLine1: '12 Market Road',
          locality: 'Indiranagar',
          city: 'Bengaluru',
          state: 'Karnataka',
          pinCode: '560038',
          latitude: 12.9716,
          longitude: 77.5946,
          landmark: 'Metro station',
          country: 'India',
        );
        final repository = _LookupCustomerRepository(result: lookup);
        final container = await _authenticatedContainer(repository);
        addTearDown(container.dispose);

        final result = await container
            .read(customerControllerProvider.notifier)
            .reverseLookup(12.9716, 77.5946);

        expect(result, same(lookup));
        expect(repository.lastToken, 'customer-token');
        expect(repository.lastLatitude, 12.9716);
        expect(repository.lastLongitude, 77.5946);
        expect(result?.landmark, 'Metro station');
        expect(result?.country, 'India');
      },
    );

    test('returns null without calling lookup when unauthenticated', () async {
      final repository = _LookupCustomerRepository(
        result: const AddressLookup(
          addressLine1: null,
          locality: null,
          city: null,
          state: null,
          pinCode: null,
          latitude: 12.9716,
          longitude: 77.5946,
        ),
      );
      final container = await _unauthenticatedContainer(repository);
      addTearDown(container.dispose);

      final result = await container
          .read(customerControllerProvider.notifier)
          .reverseLookup(12.9716, 77.5946);

      expect(result, isNull);
      expect(repository.callCount, 0);
    });

    test('returns null when reverse lookup provider fails', () async {
      final repository = _LookupCustomerRepository(
        failure: Exception('provider unavailable'),
      );
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);

      final result = await container
          .read(customerControllerProvider.notifier)
          .reverseLookup(12.9716, 77.5946);

      expect(result, isNull);
      expect(repository.callCount, 1);
      expect(container.read(customerControllerProvider).errorMessage, isNull);
    });

    test(
      'maps an address field validation error to the form field key',
      () async {
        final container = await _authenticatedContainer(
          _FailingCustomerRepository(
            const ApiException(
              422,
              'VALIDATION_ERROR',
              'Mobile number format is invalid.',
              field: 'ContactMobile',
            ),
          ),
        );
        addTearDown(container.dispose);

        final saved = await container
            .read(customerControllerProvider.notifier)
            .saveAddress(_addressDraft());
        final state = container.read(customerControllerProvider);

        expect(saved, isFalse);
        expect(state.isSaving, isFalse);
        expect(state.fieldErrors, {
          'contactMobile': 'Mobile number format is invalid.',
        });
      },
    );
  });
}

Future<ProviderContainer> _authenticatedContainer(
  CustomerRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      customerRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

Future<ProviderContainer> _unauthenticatedContainer(
  CustomerRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_UnauthenticatedRepository()),
      customerRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}

class _UnauthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;
}

class _LookupCustomerRepository extends CustomerRepository {
  _LookupCustomerRepository({this.result, this.failure})
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final AddressLookup? result;
  final Object? failure;
  int callCount = 0;
  String? lastToken;
  double? lastLatitude;
  double? lastLongitude;

  @override
  Future<AddressLookup?> reverseLookup(
    String token,
    double latitude,
    double longitude,
  ) async {
    callCount++;
    lastToken = token;
    lastLatitude = latitude;
    lastLongitude = longitude;
    if (failure != null) throw failure!;
    return result;
  }
}

class _FailingCustomerRepository extends CustomerRepository {
  _FailingCustomerRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final ApiException failure;

  @override
  Future<CustomerProfile> updateProfile(
    String token,
    UpdateCustomerProfile request,
  ) async => throw failure;

  @override
  Future<CustomerAddress> createAddress(
    String token,
    AddressDraft request,
  ) async => throw failure;
}

AddressDraft _addressDraft() => const AddressDraft(
  label: 'Home',
  addressLine1: '12 Market Road',
  locality: 'Indiranagar',
  city: 'Bengaluru',
  state: 'Karnataka',
  pinCode: '560038',
  contactName: 'Asha Sharma',
  contactMobile: '1234567890',
  latitude: 12.9716,
  longitude: 77.5946,
  isDefault: true,
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Asha Sharma',
    email: 'asha@example.test',
    mobile: '9876543210',
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
