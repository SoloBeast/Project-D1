import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_models.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('NumberSeriesResetPolicy', () {
    test('parses backend and camelCase variants with a safe default', () {
      expect(
        NumberSeriesResetPolicy.fromJson('Never'),
        NumberSeriesResetPolicy.never,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('never'),
        NumberSeriesResetPolicy.never,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('Daily'),
        NumberSeriesResetPolicy.daily,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('daily'),
        NumberSeriesResetPolicy.daily,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('Monthly'),
        NumberSeriesResetPolicy.monthly,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('CalendarYear'),
        NumberSeriesResetPolicy.calendarYear,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('calendarYear'),
        NumberSeriesResetPolicy.calendarYear,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('CalendarYearly'),
        NumberSeriesResetPolicy.calendarYear,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('FinancialYear'),
        NumberSeriesResetPolicy.financialYear,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('financialYear'),
        NumberSeriesResetPolicy.financialYear,
      );
      expect(
        NumberSeriesResetPolicy.fromJson('Unknown'),
        NumberSeriesResetPolicy.never,
      );
    });

    test('serializes apiValue and exposes labels and descriptions', () {
      expect(NumberSeriesResetPolicy.never.apiValue, 'Never');
      expect(NumberSeriesResetPolicy.daily.apiValue, 'Daily');
      expect(NumberSeriesResetPolicy.monthly.apiValue, 'Monthly');
      expect(NumberSeriesResetPolicy.calendarYear.apiValue, 'CalendarYear');
      expect(NumberSeriesResetPolicy.financialYear.apiValue, 'FinancialYear');

      expect(NumberSeriesResetPolicy.calendarYear.label, 'Calendar year');
      expect(NumberSeriesResetPolicy.financialYear.label, 'Financial year');
      expect(NumberSeriesResetPolicy.never.description, contains('never resets'));
      expect(
        NumberSeriesResetPolicy.financialYear.description,
        contains('1 April'),
      );
    });
  });

  group('number series models', () {
    test('parses a full series with counters and audit fields', () {
      final series = NumberSeries.fromJson({
        'code': 'CUST',
        'description': 'Customer account numbers',
        'template': 'CUST/{NUMBER:0000}',
        'startingNumber': 1,
        'lastUsedNumber': 1000,
        'incrementBy': 1,
        'resetPolicy': 'Never',
        'isActive': true,
        'scopeKey': '',
        'nextNumber': 'CUST/1001',
        'lastUsedAt': '2026-08-17T09:30:00Z',
        'createdByUserId': 42,
        'updatedByUserId': null,
      });

      expect(series.code, 'CUST');
      expect(series.description, 'Customer account numbers');
      expect(series.template, 'CUST/{NUMBER:0000}');
      expect(series.startingNumber, 1);
      expect(series.lastUsedNumber, 1000);
      expect(series.incrementBy, 1);
      expect(series.resetPolicy, NumberSeriesResetPolicy.never);
      expect(series.isActive, isTrue);
      expect(series.scopeKey, isNull);
      expect(series.nextNumber, 'CUST/1001');
      expect(series.lastUsedAt, DateTime.parse('2026-08-17T09:30:00Z'));
      expect(series.createdByUserId, 42);
      expect(series.updatedByUserId, isNull);
    });

    test('parses a scoped series and exposes the scope key', () {
      final series = NumberSeries.fromJson({
        'code': 'ORD',
        'description': 'Order numbers',
        'template': 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
        'startingNumber': 1,
        'lastUsedNumber': 0,
        'incrementBy': 1,
        'resetPolicy': 'FinancialYear',
        'isActive': true,
        'scopeKey': 'MAIN',
        'lastUsedAt': null,
      });

      expect(series.code, 'ORD');
      expect(series.scopeKey, 'MAIN');
      expect(series.template, 'ORD/{SCOPE}/{FY}/{NUMBER:000000}');
      expect(series.resetPolicy, NumberSeriesResetPolicy.financialYear);
    });

    test('handles fractional JSON numbers and nullable audit fields', () {
      final series = NumberSeries.fromJson({
        'code': 'ORD',
        'description': 'Order numbers',
        'template': 'ORD/{NUMBER:000000}',
        'startingNumber': 1.0,
        'lastUsedNumber': 5000.0,
        'incrementBy': 1.0,
        'resetPolicy': 'FinancialYear',
        'isActive': false,
        'scopeKey': null,
        'lastUsedAt': null,
      });

      expect(series.startingNumber, 1);
      expect(series.lastUsedNumber, 5000);
      expect(series.incrementBy, 1);
      expect(series.resetPolicy, NumberSeriesResetPolicy.financialYear);
      expect(series.isActive, isFalse);
      expect(series.scopeKey, isNull);
      expect(series.nextNumber, isNull);
      expect(series.lastUsedAt, isNull);
      expect(series.createdByUserId, isNull);
      expect(series.updatedByUserId, isNull);
    });

    test('parses a preview payload and optional scope key', () {
      final preview = NumberSeriesPreview.fromJson({
        'code': 'ORD',
        'template': 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
        'nextNumber': 1,
        'formattedNumber': 'ORD/MAIN/26-27/000001',
        'scopeKey': 'MAIN',
      });

      expect(preview.code, 'ORD');
      expect(preview.template, 'ORD/{SCOPE}/{FY}/{NUMBER:000000}');
      expect(preview.nextNumber, 1);
      expect(preview.formattedNumber, 'ORD/MAIN/26-27/000001');
      expect(preview.scopeKey, 'MAIN');
    });

    test('serializes create and update requests with api reset policy', () {
      const create = CreateNumberSeriesRequest(
        code: 'ORD',
        description: 'Main branch order numbers',
        template: 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
        startingNumber: 1,
        incrementBy: 1,
        resetPolicy: NumberSeriesResetPolicy.financialYear,
        scopeKey: 'MAIN',
      );
      expect(create.toJson(), {
        'code': 'ORD',
        'description': 'Main branch order numbers',
        'template': 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
        'startingNumber': 1,
        'incrementBy': 1,
        'resetPolicy': 'FinancialYear',
        'scopeKey': 'MAIN',
      });

      const createGlobal = CreateNumberSeriesRequest(
        code: 'CUST',
        description: 'Customer account numbers',
        template: 'CUST/{NUMBER:0000}',
        startingNumber: 1,
        incrementBy: 1,
        resetPolicy: NumberSeriesResetPolicy.calendarYear,
      );
      expect(createGlobal.toJson(), {
        'code': 'CUST',
        'description': 'Customer account numbers',
        'template': 'CUST/{NUMBER:0000}',
        'startingNumber': 1,
        'incrementBy': 1,
        'resetPolicy': 'CalendarYear',
      });

      const update = UpdateNumberSeriesRequest(
        description: 'Customer account numbers',
        template: 'CUST/{NUMBER:0000}',
        startingNumber: 1,
        incrementBy: 2,
        resetPolicy: NumberSeriesResetPolicy.financialYear,
      );
      expect(update.toJson(), {
        'description': 'Customer account numbers',
        'template': 'CUST/{NUMBER:0000}',
        'startingNumber': 1,
        'incrementBy': 2,
        'resetPolicy': 'FinancialYear',
      });
    });

    test('preview request omits optional fields when null', () {
      const withoutNext = NumberSeriesPreviewRequest(
        code: 'CUST',
        template: 'CUST/{NUMBER:0000}',
      );
      expect(withoutNext.toJson(), {
        'code': 'CUST',
        'template': 'CUST/{NUMBER:0000}',
      });

      const withNext = NumberSeriesPreviewRequest(
        code: 'CUST',
        template: 'CUST/{NUMBER:0000}',
        nextNumber: 1001,
      );
      expect(withNext.toJson(), {
        'code': 'CUST',
        'template': 'CUST/{NUMBER:0000}',
        'nextNumber': 1001,
      });

      const scoped = NumberSeriesPreviewRequest(
        code: 'ORD',
        template: 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
        scope: 'MAIN',
      );
      expect(scoped.toJson(), {
        'code': 'ORD',
        'template': 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
        'scope': 'MAIN',
      });
    });
  });

  group('number series repository', () {
    test('lists series with an authenticated GET', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/setup/number-series',
        );
        return _response([_seriesJson()]);
      });

      final series = await repository.list('number-token');

      expect(series.single.code, 'CUST');
      expect(series.single.resetPolicy, NumberSeriesResetPolicy.never);
    });

    test('gets a single series with an encoded code path', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/setup/number-series/CUST',
        );
        return _response(_seriesJson());
      });

      final series = await repository.get('number-token', 'CUST');

      expect(series.code, 'CUST');
    });

    test('gets a scoped series with a scope query parameter', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/setup/number-series/ORD',
        );
        expect(request.url.queryParameters, {'scope': 'MAIN'});
        return _response(_scopedSeriesJson());
      });

      final series = await repository.get('number-token', 'ORD', scope: 'MAIN');

      expect(series.code, 'ORD');
      expect(series.scopeKey, 'MAIN');
    });

    test('posts a preview request without consuming', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/setup/number-series/preview',
        );
        expect(jsonDecode(request.body), {
          'code': 'CUST',
          'template': 'CUST/{NUMBER:0000}',
          'nextNumber': 1001,
        });
        return _response({
          'code': 'CUST',
          'template': 'CUST/{NUMBER:0000}',
          'nextNumber': 1001,
          'formattedNumber': 'CUST/1001',
        });
      });

      final preview = await repository.preview(
        'number-token',
        const NumberSeriesPreviewRequest(
          code: 'CUST',
          template: 'CUST/{NUMBER:0000}',
          nextNumber: 1001,
        ),
      );

      expect(preview.code, 'CUST');
      expect(preview.nextNumber, 1001);
      expect(preview.formattedNumber, 'CUST/1001');
    });

    test('posts a scoped preview with a scope query parameter', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/setup/number-series/preview',
        );
        expect(request.url.queryParameters, {'scope': 'MAIN'});
        expect(jsonDecode(request.body), {
          'code': 'ORD',
          'template': 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
          'scope': 'MAIN',
        });
        return _response({
          'code': 'ORD',
          'template': 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
          'nextNumber': 1,
          'formattedNumber': 'ORD/MAIN/26-27/000001',
          'scopeKey': 'MAIN',
        });
      });

      final preview = await repository.preview(
        'number-token',
        const NumberSeriesPreviewRequest(
          code: 'ORD',
          template: 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
          scope: 'MAIN',
        ),
      );

      expect(preview.scopeKey, 'MAIN');
      expect(preview.formattedNumber, 'ORD/MAIN/26-27/000001');
    });

    test('creates a series with a POST', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/setup/number-series',
        );
        expect(jsonDecode(request.body), containsPair('code', 'CUST'));
        return _response(_seriesJson());
      });

      final created = await repository.create(
        'number-token',
        const CreateNumberSeriesRequest(
          code: 'CUST',
          description: 'Customer account numbers',
          template: 'CUST/{NUMBER:0000}',
          startingNumber: 1,
          incrementBy: 1,
          resetPolicy: NumberSeriesResetPolicy.never,
        ),
      );

      expect(created.code, 'CUST');
    });

    test('updates a series with a PUT', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/setup/number-series/CUST',
        );
        expect(jsonDecode(request.body), containsPair('incrementBy', 2));
        return _response(_seriesJson());
      });

      final updated = await repository.update(
        'number-token',
        'CUST',
        const UpdateNumberSeriesRequest(
          description: 'Customer account numbers',
          template: 'CUST/{NUMBER:0000}',
          startingNumber: 1,
          incrementBy: 2,
          resetPolicy: NumberSeriesResetPolicy.never,
        ),
      );

      expect(updated.code, 'CUST');
    });

    test('updates a scoped series with a scope query parameter', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/setup/number-series/ORD',
        );
        expect(request.url.queryParameters, {'scope': 'MAIN'});
        return _response(_scopedSeriesJson());
      });

      final updated = await repository.update(
        'number-token',
        'ORD',
        const UpdateNumberSeriesRequest(
          description: 'Main branch order numbers',
          template: 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
          startingNumber: 1,
          incrementBy: 1,
          resetPolicy: NumberSeriesResetPolicy.financialYear,
        ),
        scope: 'MAIN',
      );

      expect(updated.code, 'ORD');
      expect(updated.scopeKey, 'MAIN');
    });

    test('activates and deactivates series via POST endpoints', () async {
      final calls = <String>[];
      final repository = _repository((request) async {
        calls.add(request.url.path);
        _expectRequest(request, method: 'POST', path: request.url.path);
        return _response(
          _seriesJson(isActive: request.url.path.endsWith('activate')),
        );
      });

      await repository.setActive('number-token', 'CUST', true);
      await repository.setActive('number-token', 'CUST', false);

      expect(calls, [
        '/api/v1/admin/setup/number-series/CUST/activate',
        '/api/v1/admin/setup/number-series/CUST/deactivate',
      ]);
    });

    test('setActive targets a scoped series with a scope query parameter', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/setup/number-series/ORD/activate',
        );
        expect(request.url.queryParameters, {'scope': 'MAIN'});
        return _response(_scopedSeriesJson(isActive: true));
      });

      await repository.setActive('number-token', 'ORD', true, scope: 'MAIN');
    });

    test('surfaces validation field errors from the error envelope', () async {
      final repository = _repository((request) async {
        return http.Response(
          jsonEncode({
            'success': false,
            'errors': [
              {
                'code': 'VALIDATION_ERROR',
                'message': 'Template is required.',
                'field': 'Template',
              },
            ],
          }),
          400,
          headers: {'content-type': 'application/json'},
        );
      });

      await expectLater(
        repository.list('number-token'),
        throwsA(
          isA<ApiException>()
              .having((e) => e.statusCode, 'statusCode', 400)
              .having((e) => e.code, 'code', 'VALIDATION_ERROR')
              .having((e) => e.field, 'field', 'Template'),
        ),
      );
    });
  });
}

NumberSeriesRepository _repository(
  Future<http.Response> Function(http.Request request) handler,
) => NumberSeriesRepository(
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
  expect(request.headers['Authorization'], 'Bearer number-token');
  expect(request.headers['Accept'], 'application/json');
  expect(request.headers['Content-Type'], 'application/json');
}

http.Response _response(Object data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': <Object>[]}),
  200,
  headers: {'content-type': 'application/json'},
);

Map<String, dynamic> _seriesJson({bool isActive = true}) => {
  'code': 'CUST',
  'description': 'Customer account numbers',
  'template': 'CUST/{NUMBER:0000}',
  'startingNumber': 1,
  'lastUsedNumber': 1000,
  'incrementBy': 1,
  'resetPolicy': 'Never',
  'isActive': isActive,
  'nextNumber': 'CUST/1001',
};

Map<String, dynamic> _scopedSeriesJson({bool isActive = true}) => {
  'code': 'ORD',
  'description': 'Main branch order numbers',
  'template': 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
  'startingNumber': 1,
  'lastUsedNumber': 0,
  'incrementBy': 1,
  'resetPolicy': 'FinancialYear',
  'isActive': isActive,
  'scopeKey': 'MAIN',
  'nextNumber': 'ORD/MAIN/26-27/000001',
};
