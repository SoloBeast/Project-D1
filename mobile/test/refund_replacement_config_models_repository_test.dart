import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('RefundReplacementConfiguration', () {
    test('parses a configured result', () {
      final configuration = RefundReplacementConfiguration.fromJson({
        'windowHours': 48,
        'status': 'Configured',
      });

      expect(configuration.windowHours, 48);
      expect(configuration.status, 'Configured');
      expect(configuration.configured, isTrue);
    });

    test('parses a not-configured result', () {
      final configuration = RefundReplacementConfiguration.fromJson({
        'windowHours': 0,
        'status': 'Not Configured',
      });

      expect(configuration.windowHours, 0);
      expect(configuration.configured, isFalse);
    });

    test('parses windowHours from a JSON number or string', () {
      final fromDouble = RefundReplacementConfiguration.fromJson({
        'windowHours': 72.0,
        'status': 'Configured',
      });
      final fromString = RefundReplacementConfiguration.fromJson({
        'windowHours': '72',
        'status': 'Configured',
      });

      expect(fromDouble.windowHours, 72);
      expect(fromString.windowHours, 72);
    });

    test('falls back to the default window and status when absent', () {
      final configuration = RefundReplacementConfiguration.fromJson(
        const <String, dynamic>{},
      );

      expect(configuration.windowHours, kRefundReplacementDefaultWindowHours);
      expect(configuration.status, 'Not Configured');
    });

    test('validates the allowed hours range', () {
      expect(
        RefundReplacementConfiguration.validateWindowHours(0),
        isNotNull,
      );
      expect(
        RefundReplacementConfiguration.validateWindowHours(-5),
        isNotNull,
      );
      expect(
        RefundReplacementConfiguration.validateWindowHours(
          kRefundReplacementMaximumWindowHours + 1,
        ),
        isNotNull,
      );

      expect(
        RefundReplacementConfiguration.validateWindowHours(
          kRefundReplacementMinimumWindowHours,
        ),
        isNull,
      );
      expect(
        RefundReplacementConfiguration.validateWindowHours(48),
        isNull,
      );
      expect(
        RefundReplacementConfiguration.validateWindowHours(
          kRefundReplacementMaximumWindowHours,
        ),
        isNull,
      );
    });
  });

  group('UpdateRefundReplacementConfigurationRequest', () {
    test('serializes the window hours', () {
      const request = UpdateRefundReplacementConfigurationRequest(
        windowHours: 5,
      );

      expect(request.toJson(), {'windowHours': 5});
    });

    test('omits null window hours so the backend leaves it unchanged', () {
      const request = UpdateRefundReplacementConfigurationRequest();

      expect(request.toJson(), isEmpty);
    });
  });

  group('RefundReplacementConfigRepository', () {
    test('loads the current configuration with a GET', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/setup/refund-replacement',
        );
        return _response({
          'windowHours': 48,
          'status': 'Configured',
        });
      });

      final configuration = await repository.get('setup-token');

      expect(configuration.windowHours, 48);
      expect(configuration.status, 'Configured');
      expect(configuration.configured, isTrue);
    });

    test('updates the configuration with a PUT', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/setup/refund-replacement',
        );
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body, containsPair('windowHours', 5));
        return _response({
          'windowHours': 5,
          'status': 'Configured',
        });
      });

      final updated = await repository.update(
        'setup-token',
        const UpdateRefundReplacementConfigurationRequest(windowHours: 5),
      );

      expect(updated.windowHours, 5);
      expect(updated.status, 'Configured');
    });

    test('surfaces validation field errors from the error envelope', () async {
      final repository = _repository((request) async {
        return http.Response(
          jsonEncode({
            'success': false,
            'errors': [
              {
                'code': 'VALIDATION_ERROR',
                'message': 'WindowHours must be between 1 and 8760.',
                'field': 'windowHours',
              },
            ],
          }),
          400,
          headers: {'content-type': 'application/json'},
        );
      });

      await expectLater(
        repository.get('setup-token'),
        throwsA(
          isA<ApiException>()
              .having((e) => e.statusCode, 'statusCode', 400)
              .having((e) => e.code, 'code', 'VALIDATION_ERROR')
              .having((e) => e.field, 'field', 'windowHours'),
        ),
      );
    });
  });
}

RefundReplacementConfigRepository _repository(
  Future<http.Response> Function(http.Request request) handler,
) => RefundReplacementConfigRepository(
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
  expect(request.headers['Authorization'], 'Bearer setup-token');
  expect(request.headers['Accept'], 'application/json');
  expect(request.headers['Content-Type'], 'application/json');
}

http.Response _response(Object data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': <Object>[]}),
  200,
  headers: {'content-type': 'application/json'},
);
