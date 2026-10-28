import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_controller.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_models.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_repository.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('production history date filter', () {
    testWidgets('forwards the picked range and clears it', (tester) async {
      final repository = _CapturingDairyRepository();
      await _pump(
        tester,
        repository,
        const DairyProductionHistoryScreen(branchId: 7),
      );

      expect(find.text('From date'), findsOneWidget);
      expect(find.text('To date'), findsOneWidget);
      expect(repository.productionCalls, 1);
      expect(repository.productionFrom, isNull);
      expect(repository.productionTo, isNull);

      await _pickDay(tester, label: 'From date', day: '15');
      expect(repository.productionFrom?.day, 15);
      expect(find.textContaining('From 15/'), findsOneWidget);

      await _pickDay(tester, label: 'To date', day: '20');
      expect(repository.productionTo?.day, 20);

      await tester.tap(find.byTooltip('Clear date filter'));
      await tester.pumpAndSettle();
      expect(repository.productionFrom, isNull);
      expect(repository.productionTo, isNull);
      expect(find.text('From date'), findsOneWidget);
    });
  });

  group('usage and dispatch date filter', () {
    testWidgets('forwards the picked range and clears it', (tester) async {
      final repository = _CapturingDairyRepository();
      await _pump(
        tester,
        repository,
        const DairyUsageScreen(branchId: 7),
      );

      expect(find.text('From date'), findsOneWidget);
      expect(find.text('To date'), findsOneWidget);

      await _pickDay(tester, label: 'From date', day: '15');
      expect(repository.usageFrom?.day, 15);
      expect(find.textContaining('From 15/'), findsOneWidget);

      await tester.tap(find.byTooltip('Clear date filter'));
      await tester.pumpAndSettle();
      expect(repository.usageFrom, isNull);
      expect(repository.usageTo, isNull);
    });
  });

  group('milk batches date filter', () {
    testWidgets('filters locally by production day', (tester) async {
      final repository = _CapturingDairyRepository();
      await _pump(
        tester,
        repository,
        const DairyBatchListScreen(branchId: 7),
      );

      expect(find.text('MB-010'), findsOneWidget);
      expect(find.text('MB-020'), findsOneWidget);

      await _pickDay(tester, label: 'From date', day: '15');
      expect(find.text('MB-010'), findsNothing);
      expect(find.text('MB-020'), findsOneWidget);
      // The batches endpoint has no date query: no reload happens.
      expect(repository.batchCalls, 1);

      await tester.tap(find.byTooltip('Clear date filter'));
      await tester.pumpAndSettle();
      expect(find.text('MB-010'), findsOneWidget);
      expect(find.text('MB-020'), findsOneWidget);
    });
  });
}

/// Taps a From/To filter button, picks [day] in the calendar dialog and
/// confirms with OK.
Future<void> _pickDay(
  WidgetTester tester, {
  required String label,
  required String day,
}) async {
  await tester.tap(find.text(label));
  await tester.pumpAndSettle();
  expect(find.byType(CalendarDatePicker), findsOneWidget);
  await tester.tap(find.text(day));
  await tester.pump();
  await tester.tap(find.text('OK'));
  await tester.pumpAndSettle();
}

Future<void> _pump(
  WidgetTester tester,
  DairyRepository repository,
  Widget screen,
) async {
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(),
        ),
        dairyRepositoryProvider.overrideWithValue(repository),
      ],
      child: MaterialApp(home: screen),
    ),
  );
  await tester.pumpAndSettle();
}

class _SeededSessionController extends SessionController {
  @override
  SessionState build() => SessionState.authenticated(_session);
}

class _CapturingDairyRepository extends DairyRepository {
  _CapturingDairyRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  DateTime? productionFrom;
  DateTime? productionTo;
  int productionCalls = 0;

  DateTime? usageFrom;
  DateTime? usageTo;
  int usageCalls = 0;

  int batchCalls = 0;

  @override
  Future<List<MilkProduction>> getProductionHistory(
    String token,
    int branchId, {
    DateTime? fromDate,
    DateTime? toDate,
  }) async {
    productionFrom = fromDate;
    productionTo = toDate;
    productionCalls++;
    return [
      MilkProduction.fromJson(
        _productionJson('2026-08-17T03:00:00Z', 'production-1', 'MB-001'),
      ),
    ];
  }

  @override
  Future<List<MilkUsage>> getUsageHistory(
    String token,
    int branchId, {
    DateTime? fromDate,
    DateTime? toDate,
  }) async {
    usageFrom = fromDate;
    usageTo = toDate;
    usageCalls++;
    return [
      MilkUsage.fromJson(
        _usageJson('2026-08-17T04:45:00Z', 'usage-1', 'MB-001'),
      ),
    ];
  }

  @override
  Future<List<MilkBatch>> getBatches(
    String token,
    int branchId, {
    MilkBatchStatus? status,
  }) async {
    batchCalls++;
    final now = DateTime.now();
    return [
      MilkBatch.fromJson(
        _batchJson(
          publicId: 'batch-10',
          batchNumber: 'MB-010',
          productionAt: DateTime.utc(now.year, now.month, 10, 3),
        ),
      ),
      MilkBatch.fromJson(
        _batchJson(
          publicId: 'batch-20',
          batchNumber: 'MB-020',
          productionAt: DateTime.utc(now.year, now.month, 20, 3),
        ),
      ),
    ];
  }
}

Map<String, dynamic> _batchJson({
  required String publicId,
  required String batchNumber,
  required DateTime productionAt,
}) => {
      'publicId': publicId,
      'batchNumber': batchNumber,
      'branchId': 7,
      'productionPublicId': 'production-1',
      'productionAt': productionAt.toIso8601String(),
      'quantityProduced': 24.5,
      'availableQuantity': 18.75,
      'unit': 'L',
      'status': 'Available',
      'createdAt': productionAt.toIso8601String(),
    };

Map<String, dynamic> _productionJson(
  String productionAt,
  String publicId,
  String batchNumber,
) => {
      'publicId': publicId,
      'branchId': 7,
      'productionAt': productionAt,
      'shift': 'Morning',
      'buffaloCount': 12,
      'quantityProduced': 24.5,
      'unit': 'L',
      'recordedByUserId': 51,
      'remarks': null,
      'createdAt': productionAt,
      'batch': _batchJson(
        publicId: 'batch-1',
        batchNumber: batchNumber,
        productionAt: DateTime.parse(productionAt),
      ),
    };

Map<String, dynamic> _usageJson(
  String usedAt,
  String publicId,
  String batchNumber,
) => {
      'publicId': publicId,
      'batchPublicId': 'batch-1',
      'batchNumber': batchNumber,
      'branchId': 7,
      'usedAt': usedAt,
      'quantityUsed': 5.75,
      'unit': 'L',
      'purpose': 'Dispatch',
      'recordedByUserId': 51,
      'remarks': null,
      'createdAt': usedAt,
      'source': 'Manual',
      'orderNumber': null,
      'deliveryNumber': null,
      'productName': null,
    };

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
