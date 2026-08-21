import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_controller.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_models.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_repository.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('dairy models', () {
    test('parse dairy responses and unknown batch statuses', () {
      final batch = MilkBatch.fromJson(batchJson(status: 'unexpected'));
      final production = MilkProduction.fromJson(productionJson());
      final usage = MilkUsage.fromJson(usageJson());
      final availability = MilkAvailability.fromJson(availabilityJson());
      final dashboard = DairyDashboard.fromJson(dashboardJson());

      expect(batch.status, MilkBatchStatus.unknown);
      expect(batch.availableQuantity, 18.75);
      expect(production.batch.status, MilkBatchStatus.available);
      expect(production.shift, 'Morning');
      expect(usage.purpose, 'Dispatch');
      expect(availability.quantityProduced - availability.quantityUsed, 18.75);
      expect(dashboard.productionEntryCount, 2);
      expect(MilkBatchStatus.fromApi('EXHAUSTED'), MilkBatchStatus.exhausted);
    });

    test(
      'serializes request timestamps as UTC and fixes production unit to L',
      () {
        final production = RecordMilkProductionRequest(
          productionAt: DateTime.parse('2026-08-17T08:30:00+05:30'),
          shift: 'Morning',
          buffaloCount: 12,
          quantityProduced: 24.5,
          remarks: 'First collection',
        );
        final usage = RecordMilkUsageRequest(
          usedAt: DateTime.parse('2026-08-17T10:15:00+05:30'),
          quantityUsed: 5.75,
          purpose: 'Dispatch',
          remarks: null,
        );

        expect(production.toJson(), {
          'productionAt': '2026-08-17T03:00:00.000Z',
          'shift': 'Morning',
          'buffaloCount': 12,
          'quantityProduced': 24.5,
          'unit': 'L',
          'remarks': 'First collection',
        });
        expect(usage.toJson(), {
          'usedAt': '2026-08-17T04:45:00.000Z',
          'quantityUsed': 5.75,
          'purpose': 'Dispatch',
          'remarks': null,
        });
      },
    );

    test('formats API dates, display dates, times, and quantities', () {
      final local = DateTime(2026, 8, 7, 18, 5);

      expect(formatDairyDate(local), '07/08/2026');
      expect(formatDairyDateTime(local), '07/08/2026 06:05 PM');
      expect(formatApiDairyDate(local), '2026-08-07');
      expect(formatMilkQuantity(12, 'L'), '12 L');
      expect(formatMilkQuantity(12.125, 'L'), '12.125 L');
    });
  });

  group('dairy repository', () {
    test(
      'uses exact read routes, queries, and bearer authentication',
      () async {
        final requests = <String>[];
        final client = MockClient((request) async {
          requests.add('${request.method} ${request.url}');
          expect(request.headers['Authorization'], 'Bearer dairy-token');

          if (request.url.path.endsWith('/dashboard')) {
            return successResponse(dashboardJson());
          }
          if (request.url.path.endsWith('/production')) {
            return successResponse([productionJson()]);
          }
          if (request.url.path.endsWith('/batches/batch-1')) {
            return successResponse(batchJson());
          }
          if (request.url.path.endsWith('/batches')) {
            return successResponse([batchJson()]);
          }
          if (request.url.path.endsWith('/availability')) {
            return successResponse(availabilityJson());
          }
          return successResponse([usageJson()]);
        });
        final repository = testRepository(client);

        await repository.getDashboard(
          'dairy-token',
          7,
          productionDate: DateTime(2026, 8, 17),
        );
        await repository.getProductionHistory(
          'dairy-token',
          7,
          fromDate: DateTime(2026, 8, 1),
          toDate: DateTime(2026, 8, 17),
        );
        await repository.getBatches(
          'dairy-token',
          7,
          status: MilkBatchStatus.available,
        );
        await repository.getBatch('dairy-token', 'batch-1');
        await repository.getAvailability('dairy-token', 7);
        await repository.getUsageHistory(
          'dairy-token',
          7,
          fromDate: DateTime(2026, 8, 10),
        );

        expect(requests, [
          'GET https://api.example.test/api/v1/dairy/branches/7/dashboard?productionDate=2026-08-17',
          'GET https://api.example.test/api/v1/dairy/branches/7/production?fromDate=2026-08-01&toDate=2026-08-17',
          'GET https://api.example.test/api/v1/dairy/branches/7/batches?status=available',
          'GET https://api.example.test/api/v1/dairy/batches/batch-1',
          'GET https://api.example.test/api/v1/dairy/branches/7/availability',
          'GET https://api.example.test/api/v1/dairy/branches/7/usage?fromDate=2026-08-10',
        ]);
      },
    );

    test(
      'posts production and usage to exact routes with JSON payloads',
      () async {
        final requests = <String>[];
        final bodies = <Map<String, dynamic>>[];
        final client = MockClient((request) async {
          requests.add('${request.method} ${request.url.path}');
          expect(request.headers['Authorization'], 'Bearer dairy-token');
          bodies.add(jsonDecode(request.body) as Map<String, dynamic>);
          return request.url.path.endsWith('/production')
              ? successResponse(productionJson())
              : successResponse(usageJson());
        });
        final repository = testRepository(client);

        await repository.recordProduction(
          'dairy-token',
          7,
          RecordMilkProductionRequest(
            productionAt: DateTime.parse('2026-08-17T08:30:00+05:30'),
            shift: 'Morning',
            buffaloCount: 12,
            quantityProduced: 24.5,
            remarks: 'First collection',
          ),
        );
        await repository.recordUsage(
          'dairy-token',
          'batch-1',
          RecordMilkUsageRequest(
            usedAt: DateTime.parse('2026-08-17T10:15:00+05:30'),
            quantityUsed: 5.75,
            purpose: 'Dispatch',
            remarks: 'Route 1',
          ),
        );

        expect(requests, [
          'POST /api/v1/dairy/branches/7/production',
          'POST /api/v1/dairy/batches/batch-1/usage',
        ]);
        expect(bodies[0], {
          'productionAt': '2026-08-17T03:00:00.000Z',
          'shift': 'Morning',
          'buffaloCount': 12,
          'quantityProduced': 24.5,
          'unit': 'L',
          'remarks': 'First collection',
        });
        expect(bodies[1], {
          'usedAt': '2026-08-17T04:45:00.000Z',
          'quantityUsed': 5.75,
          'purpose': 'Dispatch',
          'remarks': 'Route 1',
        });
      },
    );
  });

  group('dairy controller', () {
    test(
      'loads data and adds successful production and usage results',
      () async {
        final repository = _FakeDairyRepository();
        final container = await authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(dairyControllerProvider.notifier);

        await controller.loadDashboard(7);
        await controller.loadBatches(7);
        final productionSaved = await controller.recordProduction(
          7,
          RecordMilkProductionRequest(
            productionAt: DateTime.utc(2026, 8, 17, 3),
            shift: 'Morning',
            buffaloCount: 12,
            quantityProduced: 24.5,
          ),
        );
        final usageSaved = await controller.recordUsage(
          'batch-1',
          RecordMilkUsageRequest(
            usedAt: DateTime.utc(2026, 8, 17, 4, 45),
            quantityUsed: 5.75,
            purpose: 'Dispatch',
          ),
        );
        final state = container.read(dairyControllerProvider);

        expect(productionSaved, isTrue);
        expect(usageSaved, isTrue);
        expect(repository.lastToken, 'dairy-token');
        expect(state.dashboard?.branchId, 7);
        expect(state.production.single.publicId, 'production-1');
        expect(state.batches.single.publicId, 'batch-1');
        expect(state.usage.single.publicId, 'usage-1');
        expect(state.isSaving, isFalse);
      },
    );

    for (final statusCode in [401, 403]) {
      test('maps $statusCode API failures to unauthorized state', () async {
        final container = await authenticatedContainer(
          _FailingDairyRepository(
            ApiException(statusCode, 'FORBIDDEN', 'Branch access denied.'),
          ),
        );
        addTearDown(container.dispose);

        await container.read(dairyControllerProvider.notifier).loadDashboard(7);
        final state = container.read(dairyControllerProvider);

        expect(state.isUnauthorized, isTrue);
        expect(state.isOffline, isFalse);
        expect(state.errorMessage, 'Branch access denied.');
      });
    }

    test('preserves business-rule API failures as online errors', () async {
      final container = await authenticatedContainer(
        _FailingDairyRepository(
          const ApiException(
            409,
            'INSUFFICIENT_MILK',
            'Only 2 L is available.',
          ),
        ),
      );
      addTearDown(container.dispose);

      final saved = await container
          .read(dairyControllerProvider.notifier)
          .recordUsage(
            'batch-1',
            RecordMilkUsageRequest(
              usedAt: DateTime.utc(2026, 8, 17, 4, 45),
              quantityUsed: 5,
              purpose: 'Dispatch',
            ),
          );
      final state = container.read(dairyControllerProvider);

      expect(saved, isFalse);
      expect(state.isOffline, isFalse);
      expect(state.isUnauthorized, isFalse);
      expect(state.errorMessage, 'Only 2 L is available.');
    });

    test('maps transport failures to offline state', () async {
      final container = await authenticatedContainer(
        _FailingDairyRepository(Exception('socket closed')),
      );
      addTearDown(container.dispose);

      await container.read(dairyControllerProvider.notifier).loadDashboard(7);
      final state = container.read(dairyControllerProvider);

      expect(state.isOffline, isTrue);
      expect(state.errorMessage, contains('Check your connection'));
    });
  });

  group('dairy screens', () {
    testWidgets('available batch action opens usage entry for the same batch', (
      tester,
    ) async {
      final controller = _SeededDairyController(
        DairyState(selectedBatch: MilkBatch.fromJson(batchJson())),
      );
      final router = GoRouter(
        initialLocation: '/batch',
        routes: [
          GoRoute(
            path: '/batch',
            builder: (_, _) => const DairyBatchDetailScreen(batchId: 'batch-1'),
          ),
          GoRoute(
            path: '/dairy/batches/:batchId/usage/new',
            builder: (_, state) => Text(
              'Usage entry ${state.pathParameters['batchId']}',
              textDirection: TextDirection.ltr,
            ),
          ),
        ],
      );
      addTearDown(router.dispose);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [dairyControllerProvider.overrideWith(() => controller)],
          child: MaterialApp.router(routerConfig: router),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Record usage'));
      await tester.pumpAndSettle();

      expect(find.text('Usage entry batch-1'), findsOneWidget);
      expect(controller.loadedBatchId, 'batch-1');
    });

    testWidgets(
      'usage entry validates required values and submits trimmed data',
      (tester) async {
        final controller = _SeededDairyController(const DairyState());
        await tester.pumpWidget(
          ProviderScope(
            overrides: [dairyControllerProvider.overrideWith(() => controller)],
            child: const MaterialApp(
              home: DairyUsageEntryScreen(batchId: 'batch-1'),
            ),
          ),
        );
        await tester.pumpAndSettle();

        await tester.tap(find.text('Save usage'));
        await tester.pump();
        expect(find.text('Enter a positive quantity'), findsOneWidget);
        expect(find.text('Enter a purpose'), findsOneWidget);

        await tester.enterText(
          find.widgetWithText(TextFormField, 'Quantity used (L)'),
          '4.25',
        );
        await tester.enterText(
          find.widgetWithText(TextFormField, 'Purpose'),
          '  Dispatch  ',
        );
        await tester.enterText(
          find.widgetWithText(TextFormField, 'Remarks (optional)'),
          '  Route 1  ',
        );
        await tester.tap(find.text('Save usage'));
        await tester.pump();

        expect(controller.recordedBatchId, 'batch-1');
        expect(controller.recordedUsage?.quantityUsed, 4.25);
        expect(controller.recordedUsage?.purpose, 'Dispatch');
        expect(controller.recordedUsage?.remarks, 'Route 1');
        final usedAt = controller.recordedUsage!.usedAt;
        expect(usedAt.isUtc, isFalse);
        expect(
          DateTime.parse(
            controller.recordedUsage!.toJson()['usedAt'] as String,
          ),
          indiaToUtc(usedAt),
        );
      },
    );
  });
}

Map<String, dynamic> batchJson({String status = 'Available'}) => {
  'publicId': 'batch-1',
  'batchNumber': 'MB-20260817-001',
  'branchId': 7,
  'productionPublicId': 'production-1',
  'productionAt': '2026-08-17T03:00:00Z',
  'quantityProduced': 24.5,
  'availableQuantity': 18.75,
  'unit': 'L',
  'status': status,
  'createdAt': '2026-08-17T03:01:00Z',
};

Map<String, dynamic> productionJson() => {
  'publicId': 'production-1',
  'branchId': 7,
  'productionAt': '2026-08-17T03:00:00Z',
  'shift': 'Morning',
  'buffaloCount': 12,
  'quantityProduced': 24.5,
  'unit': 'L',
  'recordedByUserId': 51,
  'remarks': 'First collection',
  'createdAt': '2026-08-17T03:01:00Z',
  'batch': batchJson(),
};

Map<String, dynamic> usageJson() => {
  'publicId': 'usage-1',
  'batchPublicId': 'batch-1',
  'batchNumber': 'MB-20260817-001',
  'branchId': 7,
  'usedAt': '2026-08-17T04:45:00Z',
  'quantityUsed': 5.75,
  'unit': 'L',
  'purpose': 'Dispatch',
  'recordedByUserId': 51,
  'remarks': 'Route 1',
  'createdAt': '2026-08-17T04:46:00Z',
};

Map<String, dynamic> availabilityJson() => {
  'branchId': 7,
  'quantityProduced': 24.5,
  'quantityUsed': 5.75,
  'availableQuantity': 18.75,
  'unit': 'L',
  'availableBatchCount': 1,
  'calculatedAt': '2026-08-17T05:00:00',
};

Map<String, dynamic> dashboardJson() => {
  'branchId': 7,
  'productionDate': '2026-08-17',
  'quantityProduced': 24.5,
  'availableQuantity': 18.75,
  'unit': 'L',
  'productionEntryCount': 2,
  'availableBatchCount': 1,
  'calculatedAt': '2026-08-17T05:00:00',
};

http.Response successResponse(Object data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

DairyRepository testRepository(http.Client client) => DairyRepository(
  api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
);

Future<ProviderContainer> authenticatedContainer(
  DairyRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      dairyRepositoryProvider.overrideWithValue(repository),
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

class _FakeDairyRepository extends DairyRepository {
  _FakeDairyRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;

  @override
  Future<DairyDashboard> getDashboard(
    String token,
    int branchId, {
    DateTime? productionDate,
  }) async {
    lastToken = token;
    return DairyDashboard.fromJson(dashboardJson());
  }

  @override
  Future<List<MilkBatch>> getBatches(
    String token,
    int branchId, {
    MilkBatchStatus? status,
  }) async {
    lastToken = token;
    return [MilkBatch.fromJson(batchJson())];
  }

  @override
  Future<MilkProduction> recordProduction(
    String token,
    int branchId,
    RecordMilkProductionRequest request,
  ) async {
    lastToken = token;
    return MilkProduction.fromJson(productionJson());
  }

  @override
  Future<MilkUsage> recordUsage(
    String token,
    String batchId,
    RecordMilkUsageRequest request,
  ) async {
    lastToken = token;
    return MilkUsage.fromJson(usageJson());
  }
}

class _FailingDairyRepository extends DairyRepository {
  _FailingDairyRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<DairyDashboard> getDashboard(
    String token,
    int branchId, {
    DateTime? productionDate,
  }) async => throw failure;

  @override
  Future<MilkUsage> recordUsage(
    String token,
    String batchId,
    RecordMilkUsageRequest request,
  ) async => throw failure;
}

class _SeededDairyController extends DairyController {
  _SeededDairyController(this.initialState);

  final DairyState initialState;
  String? loadedBatchId;
  String? recordedBatchId;
  RecordMilkUsageRequest? recordedUsage;

  @override
  DairyState build() => initialState;

  @override
  Future<void> loadBatch(String batchId) async {
    loadedBatchId = batchId;
  }

  @override
  Future<bool> recordUsage(
    String batchId,
    RecordMilkUsageRequest request,
  ) async {
    recordedBatchId = batchId;
    recordedUsage = request;
    return false;
  }
}

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'manager-1',
    displayName: 'Dairy Manager',
    email: 'dairy@example.test',
    mobile: null,
    roles: ['DAIRY_MANAGER'],
    permissions: ['DAIRY.READ', 'DAIRY.MANAGE'],
    branchIds: [7],
  ),
  accessToken: 'dairy-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
