import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_navigation.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_repository.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('delivery models', () {
    test('parse customer tracking and operational delivery details', () {
      final customer = CustomerDelivery.fromJson(
        customerDeliveryJson(tracking: true),
      );
      final details = DeliveryDetails.fromJson(
        deliveryDetailsJson(status: 'Arrived', otpVerified: true),
      );

      expect(customer.sourceType, DeliverySourceType.oneTimeOrder);
      expect(
        DeliverySourceType.fromApi('SubscriptionOccurrence'),
        DeliverySourceType.subscriptionOccurrence,
      );
      expect(customer.status, DeliveryStatus.outForDelivery);
      expect(customer.latestLocation?.latitude, 18.5204);
      expect(customer.completedAt, isNull);
      expect(details.status, DeliveryStatus.arrived);
      expect(details.otpVerifiedAt, isNotNull);
      expect(details.assignments.single.employeeName, 'Delivery Agent');
      expect(DeliveryStatus.fromApi('unexpected'), DeliveryStatus.unknown);
      expect(
        DeliverySourceType.fromApi('unexpected'),
        DeliverySourceType.unknown,
      );
    });

    test('formats display and API dates independently', () {
      final date = DateTime(2026, 8, 7, 18, 30);

      expect(formatDeliveryDate(date), '07/08/2026');
      expect(formatApiDeliveryDate(date), '2026-08-07');
      expect(const DeliveryNotesRequest().toJson(), {'remarks': null});
    });
  });

  group('delivery repository', () {
    test('uses staff and branch query contracts with bearer auth', () async {
      final requests = <String>[];
      final client = MockClient((request) async {
        requests.add('${request.method} ${request.url}');
        expect(request.headers['Authorization'], 'Bearer delivery-token');
        if (request.url.path.endsWith('/employees')) {
          return successResponse([
            {'employeeId': 'employee-1', 'displayName': 'Agent', 'branchId': 7},
          ]);
        }
        return successResponse([deliveryDetailsJson()]);
      });
      final repository = testRepository(client);

      final today = await repository.getToday(
        'delivery-token',
        DateTime(2026, 8, 7, 20),
      );
      final branch = await repository.getBranch(
        'delivery-token',
        7,
        date: DateTime(2026, 8, 8),
        status: DeliveryStatus.assigned,
      );
      final employees = await repository.getEmployees('delivery-token', 7);

      expect(today.single.deliveryId, 'delivery-1');
      expect(branch.single.status, DeliveryStatus.assigned);
      expect(employees.single.employeeId, 'employee-1');
      expect(requests, [
        'GET https://api.example.test/api/v1/delivery/my-today?date=2026-08-07',
        'GET https://api.example.test/api/v1/delivery-management/branches/7?date=2026-08-08&status=assigned',
        'GET https://api.example.test/api/v1/delivery-management/branches/7/employees',
      ]);
    });

    test('posts the canonical failure reason and optional remarks', () async {
      Map<String, dynamic>? body;
      final client = MockClient((request) async {
        body = jsonDecode(request.body) as Map<String, dynamic>;
        return successResponse(deliveryDetailsJson(status: 'Failed'));
      });
      final repository = testRepository(client);

      final result = await repository.fail(
        'delivery-token',
        'delivery-1',
        reason: DeliveryFailureReasons.customerNotAvailable,
        remarks: 'No response',
      );

      expect(result.status, DeliveryStatus.failed);
      expect(body, {
        'reason': 'Customer not available',
        'remarks': 'No response',
      });
    });

    test('posts lifecycle, assignment, and India-local location payloads', () async {
      final requests = <String>[];
      final bodies = <Map<String, dynamic>>[];
      final client = MockClient((request) async {
        requests.add('${request.method} ${request.url.path}');
        expect(request.headers['Authorization'], 'Bearer delivery-token');
        bodies.add(jsonDecode(request.body) as Map<String, dynamic>);
        if (request.url.path.endsWith('/location')) {
          return successResponse(locationJson());
        }
        return successResponse(deliveryDetailsJson(status: 'Assigned'));
      });
      final repository = testRepository(client);

      await repository.start('delivery-token', 'delivery-1');
      await repository.assign(
        token: 'delivery-token',
        deliveryId: 'delivery-1',
        employeeId: 'employee-2',
        reason: 'Route balancing',
      );
      final location = await repository.recordLocation(
        token: 'delivery-token',
        id: 'delivery-1',
        latitude: 18.5,
        longitude: 73.8,
        accuracyMetres: 4.5,
        recordedAt: DateTime(2026, 8, 16, 12, 30),
      );

      expect(requests, [
        'POST /api/v1/delivery/delivery-1/start',
        'POST /api/v1/delivery-management/delivery-1/assign',
        'POST /api/v1/delivery/delivery-1/location',
      ]);
      expect(bodies[0], isEmpty);
      expect(bodies[1], {
        'employeeId': 'employee-2',
        'reason': 'Route balancing',
      });
      expect(bodies[2], {
        'latitude': 18.5,
        'longitude': 73.8,
        'accuracyMetres': 4.5,
        'recordedAt': '2026-08-16T12:30:00.000',
      });
      expect(location.accuracyMetres, 5.5);
    });
  });

  group('delivery navigation', () {
    test('prefers valid coordinates and excludes internal identifiers', () {
      final uri = deliveryNavigationUri(
        latitude: 18.5204,
        longitude: 73.8567,
        address: '1 Main Street, Pune',
      );

      expect(uri.toString(), contains('destination=18.5204%2C73.8567'));
      expect(uri.toString(), isNot(contains('delivery')));
      expect(uri?.queryParameters['api'], '1');
    });

    test('falls back to an encoded address when coordinates are invalid', () {
      final uri = deliveryNavigationUri(
        latitude: double.nan,
        longitude: double.infinity,
        address: '1 Main Street, Pune & East',
      );

      expect(uri?.queryParameters['query'], '1 Main Street, Pune & East');
      expect(uri?.queryParameters['api'], '1');
    });

    test(
      'returns no destination when location and address are unavailable',
      () {
        expect(
          deliveryNavigationUri(latitude: 91, longitude: 181, address: '  '),
          isNull,
        );
      },
    );
  });

  group('delivery controller', () {
    test(
      'loads branch data and upserts lifecycle results into both lists',
      () async {
        final repository = _FakeDeliveryRepository();
        final container = await authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(deliveryControllerProvider.notifier);

        await controller.loadBranch(7, date: DateTime(2026, 8, 16));
        final changed = await controller.start('delivery-1');
        final state = container.read(deliveryControllerProvider);

        expect(changed, isTrue);
        expect(repository.lastToken, 'delivery-token');
        expect(
          state.managedDeliveries.single.status,
          DeliveryStatus.outForDelivery,
        );
        expect(
          state.staffDeliveries.single.status,
          DeliveryStatus.outForDelivery,
        );
        expect(state.selectedDelivery?.deliveryId, 'delivery-1');
        expect(state.employees.single.displayName, 'Delivery Agent');
        expect(state.isSaving, isFalse);
      },
    );

    test('preserves API failures without marking the state offline', () async {
      final container = await authenticatedContainer(
        _FailingDeliveryRepository(
          ApiException(409, 'DELIVERY_STATE', 'Delivery is not assigned.'),
        ),
      );
      addTearDown(container.dispose);

      await container
          .read(deliveryControllerProvider.notifier)
          .loadCustomerDeliveries();
      final state = container.read(deliveryControllerProvider);

      expect(state.isLoading, isFalse);
      expect(state.isOffline, isFalse);
      expect(state.errorMessage, 'Delivery is not assigned.');
    });

    test('maps transport failures to the offline state', () async {
      final container = await authenticatedContainer(
        _FailingDeliveryRepository(Exception('socket closed')),
      );
      addTearDown(container.dispose);

      await container
          .read(deliveryControllerProvider.notifier)
          .loadCustomerDeliveries();
      final state = container.read(deliveryControllerProvider);

      expect(state.isOffline, isTrue);
      expect(state.errorMessage, contains('Check your connection'));
    });
  });

  group('delivery screens', () {
    testWidgets('customer location is visible only while tracking is active', (
      tester,
    ) async {
      await _pumpDeliveryScreen(
        tester,
        const CustomerDeliveryDetailScreen(deliveryId: 'delivery-1'),
        DeliveryState(
          selectedCustomerDelivery: CustomerDelivery.fromJson(
            customerDeliveryJson(tracking: false),
          ),
        ),
      );

      expect(find.text('Live location'), findsOneWidget);
      expect(find.textContaining('18.52040'), findsNothing);
      await tester.drag(find.byType(ListView), const Offset(0, -400));
      await tester.pumpAndSettle();
      expect(find.text('Doorstep milk test'), findsOneWidget);

      await _pumpDeliveryScreen(
        tester,
        const CustomerDeliveryDetailScreen(deliveryId: 'delivery-1'),
        DeliveryState(
          selectedCustomerDelivery: CustomerDelivery.fromJson(
            customerDeliveryJson(tracking: true),
          ),
        ),
      );

      expect(find.text('Live tracking is active'), findsOneWidget);
      expect(
        find.text(
          'Your delivery partner is currently sharing an updated location.',
        ),
        findsOneWidget,
      );
      expect(find.textContaining('18.52040'), findsNothing);
      expect(find.textContaining('73.85670'), findsNothing);
    });

    testWidgets('staff actions follow server status and OTP verification', (
      tester,
    ) async {
      await _pumpDeliveryScreen(
        tester,
        const StaffDeliveryDetailScreen(deliveryId: 'delivery-1'),
        DeliveryState(
          selectedDelivery: DeliveryDetails.fromJson(
            deliveryDetailsJson(status: 'Arrived'),
          ),
        ),
      );

      expect(find.text('Send delivery OTP'), findsOneWidget);
      expect(find.text('Verify OTP'), findsOneWidget);
      expect(find.text('Complete delivery'), findsNothing);
      expect(find.text('Mark failed'), findsOneWidget);
      expect(find.text('Perform milk test'), findsOneWidget);

      await _pumpDeliveryScreen(
        tester,
        const StaffDeliveryDetailScreen(deliveryId: 'delivery-1'),
        DeliveryState(
          selectedDelivery: DeliveryDetails.fromJson(
            deliveryDetailsJson(status: 'Arrived', otpVerified: true),
          ),
        ),
      );

      expect(find.text('Complete delivery'), findsOneWidget);
      expect(find.text('Verify OTP'), findsNothing);
      expect(find.text('Perform milk test'), findsOneWidget);

      await _pumpDeliveryScreen(
        tester,
        const StaffDeliveryDetailScreen(deliveryId: 'delivery-1'),
        DeliveryState(
          selectedDelivery: DeliveryDetails.fromJson(
            deliveryDetailsJson(status: 'OutForDelivery'),
          ),
        ),
      );

      expect(find.text('Perform milk test'), findsNothing);
    });

    testWidgets('manager can select an employee and submit assignment', (
      tester,
    ) async {
      final controller = _SeededDeliveryController(
        DeliveryState(
          selectedDelivery: DeliveryDetails.fromJson(
            deliveryDetailsJson(status: 'ReadyForAssignment', assigned: false),
          ),
          employees: const [
            DeliveryEmployee(
              employeeId: 'employee-2',
              displayName: 'Second Agent',
              branchId: 7,
            ),
          ],
        ),
      );
      await _pumpDeliveryScreen(
        tester,
        const DeliveryManagementDetailScreen(deliveryId: 'delivery-1'),
        controller.initialState,
        controller: controller,
      );

      await tester.tap(find.text('Assign employee'));
      await tester.pumpAndSettle();
      expect(find.text('Assign employee'), findsNWidgets(2));
      await tester.tap(find.byType(DropdownButtonFormField<String>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Second Agent').last);
      await tester.pumpAndSettle();
      await tester.enterText(
        find.widgetWithText(TextField, 'Reason'),
        'Coverage',
      );
      await tester.tap(find.text('Assign').last);
      await tester.pumpAndSettle();

      expect(controller.assignedDeliveryId, 'delivery-1');
      expect(controller.assignedEmployeeId, 'employee-2');
      expect(controller.assignmentReason, 'Coverage');
    });
  });
}

Map<String, dynamic> locationJson() => {
  'latitude': 18.5204,
  'longitude': 73.8567,
  'accuracyMetres': 5.5,
  'recordedAt': '2026-08-16T10:15:00.000',
};

Map<String, dynamic> customerDeliveryJson({required bool tracking}) => {
  'deliveryId': 'delivery-1',
  'sourceType': 'OneTimeOrder',
  'referenceNumber': 'ORD-1001',
  'status': 'OutForDelivery',
  'scheduledDate': '2026-08-16',
  'destinationAddress': '1 Main Street, Pune',
  'assignedEmployeeId': 'employee-1',
  'assignedEmployeeName': 'Delivery Agent',
  'isTrackingActive': tracking,
  'latestLocation': locationJson(),
  'completedAt': null,
  'failedAt': null,
  'failureReason': null,
};

Map<String, dynamic> deliveryDetailsJson({
  String status = 'Assigned',
  bool otpVerified = false,
  bool assigned = true,
}) => {
  'deliveryId': 'delivery-1',
  'sourceType': 'OneTimeOrder',
  'referenceNumber': 'ORD-1001',
  'status': status,
  'scheduledDate': '2026-08-16',
  'branchId': 7,
  'customerId': 'customer-1',
  'customerName': 'Test Customer',
  'customerMobile': '9999999999',
  'destinationAddress': '1 Main Street, Pune',
  'deliveryInstructions': 'Call at the gate',
  'destinationLatitude': 18.5204,
  'destinationLongitude': 73.8567,
  'assignedEmployeeId': assigned ? 'employee-1' : null,
  'assignedEmployeeName': assigned ? 'Delivery Agent' : null,
  'assignedAt': assigned ? '2026-08-16T08:00:00.000' : null,
  'pickedUpAt': null,
  'outForDeliveryAt': status == 'OutForDelivery'
      ? '2026-08-16T09:00:00.000'
      : null,
  'arrivedAt': status == 'Arrived' ? '2026-08-16T10:00:00.000' : null,
  'otpVerifiedAt': otpVerified ? '2026-08-16T10:10:00.000' : null,
  'completedAt': null,
  'failedAt': null,
  'failureReason': null,
  'remarks': null,
  'operationalNotes': null,
  'isTrackingActive': status == 'OutForDelivery',
  'latestLocation': status == 'OutForDelivery' ? locationJson() : null,
  'assignments': assigned
      ? [
          {
            'employeeId': 'employee-1',
            'employeeName': 'Delivery Agent',
            'assignedByUserId': 'manager-1',
            'assignedAt': '2026-08-16T08:00:00.000',
            'reason': 'Morning route',
          },
        ]
      : <Map<String, dynamic>>[],
};

http.Response successResponse(Object data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

DeliveryRepository testRepository(http.Client client) => DeliveryRepository(
  api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
);

Future<ProviderContainer> authenticatedContainer(
  DeliveryRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      deliveryRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

Future<void> _pumpDeliveryScreen(
  WidgetTester tester,
  Widget screen,
  DeliveryState state, {
  _SeededDeliveryController? controller,
}) async {
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        deliveryControllerProvider.overrideWith(
          () => controller ?? _SeededDeliveryController(state),
        ),
      ],
      child: MaterialApp(home: screen),
    ),
  );
  await tester.pumpAndSettle();
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}

class _FakeDeliveryRepository extends DeliveryRepository {
  _FakeDeliveryRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;

  @override
  Future<List<DeliveryDetails>> getBranch(
    String token,
    int branchId, {
    DateTime? date,
    DeliveryStatus? status,
  }) async {
    lastToken = token;
    return [DeliveryDetails.fromJson(deliveryDetailsJson())];
  }

  @override
  Future<List<DeliveryEmployee>> getEmployees(
    String token,
    int branchId,
  ) async {
    lastToken = token;
    return const [
      DeliveryEmployee(
        employeeId: 'employee-1',
        displayName: 'Delivery Agent',
        branchId: 7,
      ),
    ];
  }

  @override
  Future<DeliveryDetails> start(String token, String id) async {
    lastToken = token;
    return DeliveryDetails.fromJson(
      deliveryDetailsJson(status: 'OutForDelivery'),
    );
  }
}

class _FailingDeliveryRepository extends DeliveryRepository {
  _FailingDeliveryRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<List<CustomerDelivery>> getMine(String token) async => throw failure;
}

class _SeededDeliveryController extends DeliveryController {
  _SeededDeliveryController(this.initialState);

  final DeliveryState initialState;
  String? assignedDeliveryId;
  String? assignedEmployeeId;
  String? assignmentReason;

  @override
  DeliveryState build() => initialState;

  @override
  Future<void> loadCustomerDeliveries() async {}

  @override
  Future<void> loadCustomerDelivery(String id) async {}

  @override
  Future<void> loadToday({DateTime? date}) async {}

  @override
  Future<void> loadStaffDelivery(String id) async {}

  @override
  Future<void> loadManagedDelivery(String id) async {}

  @override
  Future<bool> assign(String id, String employeeId, {String? reason}) async {
    assignedDeliveryId = id;
    assignedEmployeeId = employeeId;
    assignmentReason = reason;
    return true;
  }
}

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'employee-1',
    displayName: 'Delivery Agent',
    email: 'delivery@example.test',
    mobile: null,
    roles: ['DELIVERY_STAFF'],
    permissions: ['DELIVERIES.OPERATE_ASSIGNED'],
    branchIds: [7],
  ),
  accessToken: 'delivery-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
