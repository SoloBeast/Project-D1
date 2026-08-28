import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_controller.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_models.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('milk-test controller', () {
    test('loads customer and staff role-specific results', () async {
      final repository = _FakeMilkTestRepository();
      final container = await authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(milkTestControllerProvider.notifier);

      await controller.loadForCustomer('delivery-1');
      expect(
        container.read(milkTestControllerProvider).customerTest,
        isNotNull,
      );
      expect(container.read(milkTestControllerProvider).staffTest, isNull);

      await controller.loadForStaff('delivery-1');
      final state = container.read(milkTestControllerProvider);
      expect(state.customerTest, isNull);
      expect(state.staffTest?.parameters.single.value, 6.5);
      expect(repository.lastToken, 'milk-token');
    });

    test(
      'runs request, upload refresh, completion, and decision lifecycle',
      () async {
        final repository = _FakeMilkTestRepository();
        final container = await authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(milkTestControllerProvider.notifier);

        expect(await controller.request('delivery-1'), isTrue);
        expect(
          container.read(milkTestControllerProvider).customerTest?.status,
          MilkTestStatus.requested,
        );

        expect(
          await controller.uploadImage(
            'delivery-1',
            'test-1',
            bytes: Uint8List.fromList([0xff, 0xd8, 0xff]),
            fileName: 'reading.jpg',
            contentType: 'image/jpeg',
          ),
          isTrue,
        );
        expect(repository.uploadCount, 1);
        expect(repository.staffReadCount, 1);
        expect(
          container.read(milkTestControllerProvider).staffTest?.images,
          hasLength(1),
        );

        expect(
          await controller.complete(
            'test-1',
            parameters: const [
              MilkTestParameter(
                code: 'FAT',
                name: 'Fat',
                value: 6.5,
                unit: '%',
              ),
            ],
            remarks: 'Doorstep reading',
          ),
          isTrue,
        );
        expect(repository.completedParameters.single.code, 'FAT');

        expect(await controller.confirm('test-1', remarks: 'Accepted'), isTrue);
        final state = container.read(milkTestControllerProvider);
        expect(
          state.customerTest?.customerDecision,
          MilkTestCustomerDecision.confirmed,
        );
        expect(state.isSaving, isFalse);
      },
    );

    for (final statusCode in [401, 403]) {
      test('maps $statusCode API failures to unauthorized state', () async {
        final container = await authenticatedContainer(
          _FailingMilkTestRepository(
            ApiException(statusCode, 'FORBIDDEN', 'Milk test access denied.'),
          ),
        );
        addTearDown(container.dispose);

        await container
            .read(milkTestControllerProvider.notifier)
            .loadForStaff('delivery-1');
        final state = container.read(milkTestControllerProvider);

        expect(state.isUnauthorized, isTrue);
        expect(state.isOffline, isFalse);
        expect(state.errorMessage, 'Milk test access denied.');
      });
    }

    for (final statusCode in [422, 500]) {
      test('maps $statusCode API failures to online error state', () async {
        final container = await authenticatedContainer(
          _FailingMilkTestRepository(
            ApiException(statusCode, 'HTTP_ERROR', 'Server response failed.'),
          ),
        );
        addTearDown(container.dispose);

        await container
            .read(milkTestControllerProvider.notifier)
            .loadForCustomer('delivery-1');
        final state = container.read(milkTestControllerProvider);

        expect(state.isOffline, isFalse);
        expect(state.isUnauthorized, isFalse);
        expect(state.errorMessage, 'Server response failed.');
      });
    }

    test('keeps response and model failures online instead of offline', () async {
      final container = await authenticatedContainer(
        _FailingMilkTestRepository(const FormatException('invalid date')),
      );
      addTearDown(container.dispose);

      await container
          .read(milkTestControllerProvider.notifier)
          .loadForCustomer('delivery-1');
      final state = container.read(milkTestControllerProvider);

      expect(state.isOffline, isFalse);
      expect(state.isUnauthorized, isFalse);
      expect(state.errorMessage, contains('could not be processed'));
    });

    test('maps genuine network failures to offline state', () async {
      final container = await authenticatedContainer(
        _FailingMilkTestRepository(
          const ApiNetworkException('socket closed'),
        ),
      );
      addTearDown(container.dispose);

      await container
          .read(milkTestControllerProvider.notifier)
          .loadForCustomer('delivery-1');
      final state = container.read(milkTestControllerProvider);

      expect(state.isOffline, isTrue);
      expect(state.errorMessage, contains('Check your connection'));
    });
  });
}

Future<ProviderContainer> authenticatedContainer(
  MilkTestRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      milkTestRepositoryProvider.overrideWithValue(repository),
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

class _FakeMilkTestRepository extends MilkTestRepository {
  _FakeMilkTestRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;
  int uploadCount = 0;
  int staffReadCount = 0;
  List<MilkTestParameter> completedParameters = const [];

  @override
  Future<CustomerMilkTest?> getForCustomer(
    String token,
    String deliveryId,
  ) async {
    lastToken = token;
    return customerTest();
  }

  @override
  Future<StaffMilkTest?> getForStaff(String token, String deliveryId) async {
    lastToken = token;
    staffReadCount++;
    return staffTest();
  }

  @override
  Future<CustomerMilkTest> request(String token, String deliveryId) async {
    lastToken = token;
    return customerTest(status: MilkTestStatus.requested);
  }

  @override
  Future<MilkTestImage> uploadImage(
    String token,
    String milkTestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    lastToken = token;
    uploadCount++;
    return image;
  }

  @override
  Future<StaffMilkTest> complete(
    String token,
    String milkTestId, {
    required List<MilkTestParameter> parameters,
    String? remarks,
  }) async {
    lastToken = token;
    completedParameters = parameters;
    return staffTest();
  }

  @override
  Future<CustomerMilkTest> confirm(
    String token,
    String milkTestId, {
    String? remarks,
  }) async => customerTest(decision: MilkTestCustomerDecision.confirmed);
}

class _FailingMilkTestRepository extends MilkTestRepository {
  _FailingMilkTestRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<CustomerMilkTest?> getForCustomer(
    String token,
    String deliveryId,
  ) async => throw failure;

  @override
  Future<StaffMilkTest?> getForStaff(String token, String deliveryId) async =>
      throw failure;

  @override
  Future<CustomerMilkTest> reject(
    String token,
    String milkTestId, {
    String? remarks,
  }) async => throw failure;
}

CustomerMilkTest customerTest({
  MilkTestStatus status = MilkTestStatus.completed,
  MilkTestCustomerDecision decision = MilkTestCustomerDecision.pending,
}) => CustomerMilkTest(
  milkTestId: 'test-1',
  deliveryId: 'delivery-1',
  status: status,
  customerDecision: decision,
  requestedAtUtc: DateTime.utc(2026, 8, 17, 9),
  completedAtUtc: status == MilkTestStatus.completed
      ? DateTime.utc(2026, 8, 17, 9, 10)
      : null,
  confirmedAtUtc: decision == MilkTestCustomerDecision.confirmed
      ? DateTime.utc(2026, 8, 17, 9, 12)
      : null,
  rejectedAtUtc: null,
  customerRemarks: decision.isTerminal ? 'Customer decision' : null,
  images: status == MilkTestStatus.completed ? [image] : const [],
);

StaffMilkTest staffTest() => StaffMilkTest(
  milkTestId: 'test-1',
  deliveryId: 'delivery-1',
  status: MilkTestStatus.completed,
  customerDecision: MilkTestCustomerDecision.pending,
  requestedAtUtc: DateTime.utc(2026, 8, 17, 9),
  completedAtUtc: DateTime.utc(2026, 8, 17, 9, 10),
  staffRemarks: 'Doorstep reading',
  confirmedAtUtc: null,
  rejectedAtUtc: null,
  customerRemarks: null,
  parameters: const [
    MilkTestParameter(code: 'FAT', name: 'Fat', value: 6.5, unit: '%'),
  ],
  images: [image],
);

final image = MilkTestImage(
  imageId: 'image-1',
  fileName: 'reading.jpg',
  contentType: 'image/jpeg',
  fileSize: 2048,
  uploadedAtUtc: DateTime.utc(2026, 8, 17, 9, 5),
  contentPath: '/api/v1/milk-tests/test-1/images/image-1/content',
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Delivery Staff',
    email: null,
    mobile: '9999999999',
    roles: ['DELIVERY_STAFF'],
    permissions: ['DELIVERY.READ', 'DELIVERY.MANAGE'],
    branchIds: [7],
  ),
  accessToken: 'milk-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
