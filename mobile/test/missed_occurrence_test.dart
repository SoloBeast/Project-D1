import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('missed occurrences', () {
    test('parses the branch missed-occurrence queue', () {
      final missed = MissedOccurrence.fromJson(_missedJson());

      expect(missed.occurrenceId, 'occ-25');
      expect(missed.subscriptionNumber, 'SUB/DB0001/26-27/000001');
      expect(missed.scheduledDate, DateTime.parse('2026-10-25'));
      expect(missed.daysOverdue, 3);
      expect(missed.branchId, 7);
      expect(missed.customerName, 'Test Customer');
      expect(missed.estimatedRefundAmount, 100.0);
    });

    test('uses the branch missed-occurrences route with bearer auth', () async {
      final requests = <String>[];
      final client = MockClient((request) async {
        requests.add('${request.method} ${request.url}');
        expect(request.headers['Authorization'], 'Bearer manager-token');
        return _success([_missedJson()]);
      });
      final repository = DeliveryRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final missed = await repository.getMissedOccurrences('manager-token', 7);

      expect(missed.single.occurrenceId, 'occ-25');
      expect(requests, [
        'GET https://api.example.test/api/v1/delivery-management/branches/7/missed-occurrences',
      ]);
    });

    test('posts the manager-confirmed miss to the occurrence route', () async {
      String? path;
      Map<String, dynamic>? body;
      final client = MockClient((request) async {
        path = request.url.path;
        body = jsonDecode(request.body) as Map<String, dynamic>;
        return _success(_deliveryJson());
      });
      final repository = DeliveryRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final result = await repository.failMissedOccurrence(
        token: 'manager-token',
        branchId: 7,
        occurrenceId: 'occ-25',
        reason: 'Vehicle issue',
        remarks: 'Van never left the depot.',
      );

      expect(
        path,
        '/api/v1/delivery-management/branches/7/missed-occurrences/occ-25/fail',
      );
      expect(body, {
        'reason': 'Vehicle issue',
        'remarks': 'Van never left the depot.',
      });
      expect(result.status, DeliveryStatus.failed);
    });

    test('controller loads the queue and drops confirmed misses', () async {
      final repository = _FakeMissedRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
          deliveryRepositoryProvider.overrideWithValue(repository),
        ],
      );
      addTearDown(container.dispose);
      container.read(sessionControllerProvider);
      await Future<void>.delayed(Duration.zero);
      final controller = container.read(deliveryControllerProvider.notifier);

      await controller.loadMissedOccurrences(7);
      expect(
        container.read(deliveryControllerProvider).missedOccurrences.single.occurrenceId,
        'occ-25',
      );

      final confirmed = await controller.failMissedOccurrence(
        7,
        'occ-25',
        reason: 'Vehicle issue',
        remarks: 'Van never left the depot.',
      );

      expect(confirmed, isTrue);
      expect(
        container.read(deliveryControllerProvider).missedOccurrences,
        isEmpty,
      );
      expect(repository.failedOccurrenceId, 'occ-25');
      expect(repository.failedReason, 'Vehicle issue');
      expect(
        container
            .read(deliveryControllerProvider)
            .managedDeliveries
            .single
            .status,
        DeliveryStatus.failed,
      );
    });
  });
}

Map<String, dynamic> _missedJson() => {
  'occurrenceId': 'occ-25',
  'subscriptionId': 'sub-1',
  'subscriptionNumber': 'SUB/DB0001/26-27/000001',
  'scheduledDate': '2026-10-25',
  'daysOverdue': 3,
  'branchId': 7,
  'branchName': 'Main Branch',
  'customerId': 'customer-1',
  'customerName': 'Test Customer',
  'estimatedRefundAmount': 100.0,
};

Map<String, dynamic> _deliveryJson() => {
  'deliveryId': 'delivery-25',
  'sourceType': 'SubscriptionOccurrence',
  'referenceNumber': 'SUB-xxx-20261025',
  'status': 'Failed',
  'scheduledDate': '2026-10-25',
  'branchId': 7,
  'customerId': 'customer-1',
  'customerName': 'Test Customer',
  'customerMobile': '9999999999',
  'destinationAddress': '1 Main Street, Pune',
  'deliveryInstructions': null,
  'destinationLatitude': 18.5204,
  'destinationLongitude': 73.8567,
  'assignedEmployeeId': null,
  'assignedEmployeeName': null,
  'assignedAt': null,
  'pickedUpAt': null,
  'outForDeliveryAt': null,
  'arrivedAt': null,
  'otpVerifiedAt': null,
  'completedAt': null,
  'failedAt': '2026-10-28T09:30:00.000',
  'failureReason': 'Vehicle issue',
  'remarks': 'Van never left the depot.',
  'operationalNotes': null,
  'subscriptionSlot': null,
  'quantity': 1.0,
  'orderSummary': null,
  'subscriptionId': 'sub-1',
  'subscriptionNumber': 'SUB/DB0001/26-27/000001',
  'subscriptionDeliveryId': 'occ-25',
  'subscriptionProductName': 'Fresh Milk',
  'subscriptionUnitOfMeasure': 'litre',
  'isTrackingActive': false,
  'latestLocation': null,
  'assignments': <Map<String, dynamic>>[],
  'batchAllocations': <Map<String, dynamic>>[],
};

http.Response _success(Object data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => AuthSession(
    user: const AuthUser(
      publicUserId: 'manager-1',
      displayName: 'Branch Manager',
      email: 'manager@example.test',
      mobile: null,
      roles: ['DAIRY_MANAGER'],
      permissions: ['DELIVERIES.READ_BRANCH', 'DELIVERIES.ASSIGN_BRANCH'],
      branchIds: [7],
    ),
    accessToken: 'manager-token',
    refreshToken: 'refresh-token',
    accessTokenExpiresAtUtc: DateTime.utc(2099),
    refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
  );
}

class _FakeMissedRepository extends DeliveryRepository {
  _FakeMissedRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? failedOccurrenceId;
  String? failedReason;

  @override
  Future<List<MissedOccurrence>> getMissedOccurrences(
    String token,
    int branchId,
  ) async => [MissedOccurrence.fromJson(_missedJson())];

  @override
  Future<DeliveryDetails> failMissedOccurrence({
    required String token,
    required int branchId,
    required String occurrenceId,
    required String reason,
    String? remarks,
  }) async {
    failedOccurrenceId = occurrenceId;
    failedReason = reason;
    return DeliveryDetails.fromJson(_deliveryJson());
  }
}
