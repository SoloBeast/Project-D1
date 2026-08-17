import 'dart:convert';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_models.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('admin report models', () {
    test('serializes filters for query strings and export JSON', () {
      final filter = ReportFilter(
        search: '  main dairy  ',
        statuses: const ['Pending', 'Failed'],
        fromUtc: DateTime.parse('2026-08-01T05:30:00+05:30'),
        toUtc: DateTime.parse('2026-08-17T23:59:00+05:30'),
        page: 3,
        pageSize: 50,
        sortBy: 'createdAtUtc',
        descending: false,
      );

      expect(filter.toQuery(), {
        'search': 'main dairy',
        'statuses': ['Pending', 'Failed'],
        'dateRange.fromUtc': '2026-08-01T00:00:00.000Z',
        'dateRange.toUtc': '2026-08-17T18:29:00.000Z',
        'page': '3',
        'pageSize': '50',
        'sortBy': 'createdAtUtc',
        'sortDirection': 'Ascending',
      });
      expect(filter.toJson(), {
        'search': 'main dairy',
        'statuses': ['Pending', 'Failed'],
        'dateRange': {
          'fromUtc': '2026-08-01T00:00:00.000Z',
          'toUtc': '2026-08-17T18:29:00.000Z',
        },
        'page': 3,
        'pageSize': 50,
        'sortBy': 'createdAtUtc',
        'sortDirection': 'Ascending',
      });
    });

    test('omits blank optional filters and supports explicit clearing', () {
      const original = ReportFilter(
        search: 'customer',
        statuses: ['Active'],
        sortBy: 'displayName',
      );
      final cleared = original.copyWith(
        statuses: const [],
        clearSearch: true,
        clearSort: true,
      );

      expect(cleared.search, isNull);
      expect(cleared.statuses, isEmpty);
      expect(cleared.sortBy, isNull);
      expect(cleared.toQuery(), {
        'page': '1',
        'pageSize': '25',
        'sortDirection': 'Descending',
      });
    });

    test('parses dashboard numbers from numeric and string values', () {
      final metrics = DashboardMetrics.fromJson({
        'customers': '12',
        'activeCustomers': 10,
        'employees': 4.0,
        'orders': 7,
        'oneTimeOrderRevenue': '1450.75',
        'activeSubscriptions': 3,
        'successfulPayments': 1200,
        'pendingPayments': '250.25',
        'refunds': null,
        'walletBalances': 420.5,
        'deliveries': 8,
        'successfulDeliveries': 6,
        'failedDeliveries': 2,
        'milkProduced': '94.5',
        'milkUsed': 88,
        'pendingMilkTests': 1,
        'availableCameras': 3,
        'notificationFailures': 2,
      });

      expect(metrics.customers, 12);
      expect(metrics.employees, 4);
      expect(metrics.oneTimeOrderRevenue, 1450.75);
      expect(metrics.pendingPayments, 250.25);
      expect(metrics.refunds, 0);
      expect(metrics.milkProduced, 94.5);
    });

    test('parses report pages with defensive numeric defaults', () {
      final page = ReportPageData.fromJson({
        'items': [
          {'orderNumber': 'ORD-1001'},
          'invalid-row',
        ],
        'page': '2',
        'pageSize': 10.0,
        'totalCount': '24',
        'hasNextPage': true,
      });
      final defaults = ReportPageData.fromJson({});

      expect(page.items, [
        {'orderNumber': 'ORD-1001'},
      ]);
      expect(page.page, 2);
      expect(page.pageSize, 10);
      expect(page.totalCount, 24);
      expect(page.hasNextPage, isTrue);
      expect(defaults.page, 1);
      expect(defaults.pageSize, 25);
      expect(defaults.totalCount, 0);
      expect(defaults.hasNextPage, isFalse);
    });

    test('defines every Phase 12 module with its backend row keys', () {
      const expectedColumns = <String, List<String>>{
        'customers': [
          'id',
          'displayName',
          'mobile',
          'isActive',
          'createdAtUtc',
        ],
        'employees': ['id', 'displayName', 'mobile', 'isActive', 'roles'],
        'orders': [
          'orderNumber',
          'customerName',
          'branchName',
          'status',
          'payableAmount',
          'createdAtUtc',
        ],
        'subscriptions': [
          'customerName',
          'productName',
          'branchName',
          'status',
          'startDate',
          'endDate',
          'payableAmount',
        ],
        'payments': [
          'id',
          'status',
          'method',
          'amount',
          'refundedAmount',
          'createdAtUtc',
        ],
        'wallets': [
          'customerName',
          'balance',
          'currency',
          'transactionCount',
          'lastActivityAtUtc',
        ],
        'deliveries': [
          'customerName',
          'branchName',
          'scheduledDate',
          'status',
          'assignedEmployeeName',
          'failureCode',
        ],
        'dairy': [
          'branchName',
          'occurredAtUtc',
          'quantity',
          'unit',
          'status',
          'purpose',
        ],
        'milk-tests': [
          'branchName',
          'requestedAtUtc',
          'status',
          'customerDecision',
          'parameterCount',
          'imageCount',
        ],
        'cameras': [
          'displayName',
          'branchName',
          'isActive',
          'isPublic',
          'streamProtocol',
        ],
        'notifications': [
          'eventType',
          'status',
          'occurredAtUtc',
          'isCritical',
          'failedDeliveryCount',
          'attemptCount',
        ],
        'audit': ['action', 'entityType', 'entityId', 'reason', 'createdAtUtc'],
      };

      expect(reportModules, hasLength(12));
      for (final entry in expectedColumns.entries) {
        final module = reportModuleBySlug(entry.key);
        expect(module, isNotNull, reason: 'Missing ${entry.key} descriptor');
        expect(
          module!.columns.map((column) => column.key),
          entry.value,
          reason: 'Column drift for ${entry.key}',
        );
        expect(module.sorts, isNotEmpty);
        expect(module.permission, startsWith('REPORTS.'));
      }
      expect(reportModuleBySlug('unknown'), isNull);
    });

    test('formats null, list, boolean, date, and scalar values', () {
      expect(displayReportValue(null), '-');
      expect(displayReportValue(['Owner', 'Admin']), 'Owner, Admin');
      expect(displayReportValue(true), 'Yes');
      expect(displayReportValue(false), 'No');
      expect(displayReportValue('plain text'), 'plain text');
      expect(displayReportValue(42), '42');
      expect(displayReportValue('2026-08-17T10:30:00Z'), isNotEmpty);
    });
  });

  group('admin report repository', () {
    test('gets authenticated dashboard metrics', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/reports/dashboard',
        );
        return _response(_dashboardJson);
      });

      final metrics = await repository.getDashboard('report-token');

      expect(metrics.customers, 12);
      expect(metrics.oneTimeOrderRevenue, 1450.75);
      expect(metrics.notificationFailures, 2);
    });

    test('gets a filtered report with repeated status query keys', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/reports/orders',
        );
        expect(request.url.queryParametersAll, {
          'search': ['ORD'],
          'statuses': ['Pending', 'Failed'],
          'dateRange.fromUtc': ['2026-08-01T00:00:00.000Z'],
          'dateRange.toUtc': ['2026-08-17T00:00:00.000Z'],
          'page': ['2'],
          'pageSize': ['50'],
          'sortBy': ['createdAtUtc'],
          'sortDirection': ['Ascending'],
        });
        return _response({
          'items': [
            {'orderNumber': 'ORD-1001', 'status': 'Pending'},
          ],
          'page': 2,
          'pageSize': 50,
          'totalCount': 51,
          'hasNextPage': false,
        });
      });

      final page = await repository.getReport(
        'report-token',
        reportModuleBySlug('orders')!,
        ReportFilter(
          search: ' ORD ',
          statuses: const ['Pending', 'Failed'],
          fromUtc: DateTime.utc(2026, 8),
          toUtc: DateTime.utc(2026, 8, 17),
          page: 2,
          pageSize: 50,
          sortBy: 'createdAtUtc',
          descending: false,
        ),
      );

      expect(page.items.single['orderNumber'], 'ORD-1001');
      expect(page.page, 2);
      expect(page.hasNextPage, isFalse);
    });

    test(
      'exports nested filter JSON and preserves response metadata',
      () async {
        final repository = _repository((request) async {
          _expectRequest(
            request,
            method: 'POST',
            path: '/api/v1/admin/reports/payments/export',
          );
          expect(jsonDecode(request.body), {
            'filter': {
              'statuses': ['Succeeded'],
              'dateRange': {'fromUtc': '2026-08-01T00:00:00.000Z'},
              'page': 1,
              'pageSize': 25,
              'sortBy': 'amount',
              'sortDirection': 'Descending',
            },
            'format': 'Xlsx',
          });
          return http.Response.bytes(
            Uint8List.fromList([80, 75, 3, 4]),
            200,
            headers: {
              'content-type': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
              'content-disposition': 'attachment; filename="payments.xlsx"',
            },
          );
        });

        final file = await repository.export(
          'report-token',
          reportModuleBySlug('payments')!,
          ReportFilter(
            statuses: const ['Succeeded'],
            fromUtc: DateTime.utc(2026, 8),
            sortBy: 'amount',
          ),
          'Xlsx',
        );

        expect(file.bytes, [80, 75, 3, 4]);
        expect(file.fileName, 'payments.xlsx');
        expect(
          file.contentType,
          'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
        );
      },
    );

    test('uses a normalized fallback filename when header is absent', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/reports/audit/export',
        );
        return http.Response.bytes(
          Uint8List.fromList([1, 2]),
          200,
          headers: {'content-type': 'text/csv'},
        );
      });

      final file = await repository.export(
        'report-token',
        reportModuleBySlug('audit')!,
        const ReportFilter(),
        'Csv',
      );

      expect(file.fileName, 'audit.csv');
      expect(file.contentType, 'text/csv');
    });
  });
}

AdminReportRepository _repository(
  Future<http.Response> Function(http.Request request) handler,
) => AdminReportRepository(
  api: ApiClient(
    client: MockClient(handler),
    baseUrl: 'https://api.example.test',
  ),
);

void _expectRequest(
  http.Request request, {
  required String method,
  required String path,
}) {
  expect(request.method, method);
  expect(request.url.path, path);
  expect(request.headers['Authorization'], 'Bearer report-token');
  expect(request.headers['Accept'], 'application/json');
  expect(request.headers['Content-Type'], 'application/json');
}

http.Response _response(Object data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': <Object>[]}),
  200,
  headers: {'content-type': 'application/json'},
);

const _dashboardJson = <String, dynamic>{
  'customers': 12,
  'activeCustomers': 10,
  'employees': 4,
  'orders': 7,
  'oneTimeOrderRevenue': 1450.75,
  'activeSubscriptions': 3,
  'successfulPayments': 1200,
  'pendingPayments': 250.25,
  'refunds': 20,
  'walletBalances': 420.5,
  'deliveries': 8,
  'successfulDeliveries': 6,
  'failedDeliveries': 2,
  'milkProduced': 94.5,
  'milkUsed': 88,
  'pendingMilkTests': 1,
  'availableCameras': 3,
  'notificationFailures': 2,
};
