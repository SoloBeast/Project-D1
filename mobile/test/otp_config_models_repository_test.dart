import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/otp_config/otp_config_models.dart';
import 'package:doodh_direct_mobile/features/otp_config/otp_config_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('OtpProviderEnvironment', () {
    test('parses backend and camelCase variants', () {
      expect(
        OtpProviderEnvironment.fromJson('Test'),
        OtpProviderEnvironment.test,
      );
      expect(
        OtpProviderEnvironment.fromJson('test'),
        OtpProviderEnvironment.test,
      );
      expect(
        OtpProviderEnvironment.fromJson('Production'),
        OtpProviderEnvironment.production,
      );
      expect(
        OtpProviderEnvironment.fromJson('production'),
        OtpProviderEnvironment.production,
      );
    });

    test('returns null for unknown values', () {
      expect(OtpProviderEnvironment.fromJson('Unknown'), isNull);
      expect(OtpProviderEnvironment.fromJson(null), isNull);
    });

    test('serializes apiValue and exposes labels', () {
      expect(OtpProviderEnvironment.test.apiValue, 'Test');
      expect(OtpProviderEnvironment.production.apiValue, 'Production');
      expect(OtpProviderEnvironment.test.label, 'Test');
      expect(OtpProviderEnvironment.production.label, 'Production');
    });
  });

  group('OTP provider models', () {
    test('parses a configured provider without an authKey', () {
      final configuration = OtpProviderConfiguration.fromJson({
        'provider': 'MSG91',
        'enabled': true,
        'widgetId': '6f2c4a1b9e3d',
        'environment': 'Test',
        'configured': true,
        'status': 'Ready to send OTPs',
      });

      expect(configuration.provider, 'MSG91');
      expect(configuration.enabled, isTrue);
      expect(configuration.widgetId, '6f2c4a1b9e3d');
      expect(configuration.environment, OtpProviderEnvironment.test);
      expect(configuration.configured, isTrue);
      expect(configuration.status, 'Ready to send OTPs');
    });

    test('parses a production provider with camelCase values', () {
      final configuration = OtpProviderConfiguration.fromJson({
        'provider': 'MSG91',
        'enabled': true,
        'widgetId': 'widget-1',
        'environment': 'Production',
        'configured': true,
        'status': 'Production ready',
      });

      expect(configuration.environment, OtpProviderEnvironment.production);
      expect(configuration.widgetId, 'widget-1');
    });

    test('parses an unconfigured provider with safe defaults', () {
      final configuration = OtpProviderConfiguration.fromJson({
        'provider': 'MSG91',
        'enabled': false,
        'widgetId': '',
        'environment': null,
        'configured': false,
        'status': 'Not Configured',
      });

      expect(configuration.enabled, isFalse);
      expect(configuration.widgetId, isNull);
      expect(configuration.environment, isNull);
      expect(configuration.configured, isFalse);
      expect(configuration.status, 'Not Configured');
    });

    test('serializes only explicitly provided update fields', () {
      final request = UpdateOtpProviderConfigurationRequest(
        enabled: true,
        environment: OtpProviderEnvironment.production,
      );

      expect(request.toJson(), {
        'enabled': true,
        'environment': 'Production',
      });
    });

    test('omits empty widgetId and authKey from the update payload', () {
      final request = UpdateOtpProviderConfigurationRequest(
        enabled: false,
        widgetId: '   ',
        authKey: '',
        environment: null,
      );

      expect(request.toJson(), {'enabled': false});
    });

    test('trims widgetId and authKey before serializing', () {
      final request = UpdateOtpProviderConfigurationRequest(
        widgetId: '  widget-1  ',
        authKey: '  secret-key  ',
      );

      expect(request.toJson(), {
        'widgetId': 'widget-1',
        'authKey': 'secret-key',
      });
    });

    test('parses a test result', () {
      final result = OtpProviderTestResult.fromJson({
        'success': true,
        'message': 'Test OTP sent to 0000000000.',
      });

      expect(result.success, isTrue);
      expect(result.message, 'Test OTP sent to 0000000000.');
    });

    test('parses a failed test result with defaults', () {
      final result = OtpProviderTestResult.fromJson({
        'success': false,
        'message': '',
      });

      expect(result.success, isFalse);
      expect(result.message, '');
    });
  });

  group('OtpConfigRepository', () {
    test('loads the current configuration with a GET', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/setup/otp-provider',
        );
        return _response(_configurationJson());
      });

      final configuration = await repository.get('number-token');

      expect(configuration.provider, 'MSG91');
      expect(configuration.enabled, isTrue);
      expect(configuration.widgetId, '6f2c4a1b9e3d');
      expect(configuration.environment, OtpProviderEnvironment.test);
      expect(configuration.configured, isTrue);
    });

    test('updates the configuration with a PUT', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/setup/otp-provider',
        );
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body, containsPair('enabled', true));
        expect(body, containsPair('environment', 'Production'));
        expect(body.containsKey('authKey'), isFalse);
        return _response(
          _configurationJson(
            enabled: true,
            environment: 'Production',
          ),
        );
      });

      final updated = await repository.update(
        'number-token',
        const UpdateOtpProviderConfigurationRequest(
          enabled: true,
          environment: OtpProviderEnvironment.production,
        ),
      );

      expect(updated.enabled, isTrue);
      expect(updated.environment, OtpProviderEnvironment.production);
    });

    test('sends the authKey only when provided in the update', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/setup/otp-provider',
        );
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body, containsPair('authKey', 'secret-key'));
        return _response(_configurationJson());
      });

      final updated = await repository.update(
        'number-token',
        const UpdateOtpProviderConfigurationRequest(authKey: 'secret-key'),
      );

      expect(updated.provider, 'MSG91');
    });

    test('sends a test OTP via the test endpoint', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/setup/otp-provider/test',
        );
        return _response({
          'success': true,
          'message': 'Test OTP sent to 0000000000.',
        });
      });

      final result = await repository.test('number-token');

      expect(result.success, isTrue);
      expect(result.message, 'Test OTP sent to 0000000000.');
    });

    test('surfaces validation field errors from the error envelope', () async {
      final repository = _repository((request) async {
        return http.Response(
          jsonEncode({
            'success': false,
            'errors': [
              {
                'code': 'VALIDATION_ERROR',
                'message': 'WidgetId is required.',
                'field': 'WidgetId',
              },
            ],
          }),
          400,
          headers: {'content-type': 'application/json'},
        );
      });

      await expectLater(
        repository.get('number-token'),
        throwsA(
          isA<ApiException>()
              .having((e) => e.statusCode, 'statusCode', 400)
              .having((e) => e.code, 'code', 'VALIDATION_ERROR')
              .having((e) => e.field, 'field', 'WidgetId'),
        ),
      );
    });
  });
}

OtpConfigRepository _repository(
  Future<http.Response> Function(http.Request request) handler,
) => OtpConfigRepository(
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

Map<String, dynamic> _configurationJson({
  bool enabled = true,
  String? environment = 'Test',
}) => {
  'provider': 'MSG91',
  'enabled': enabled,
  'widgetId': '6f2c4a1b9e3d',
  'environment': environment,
  'configured': true,
  'status': 'Ready to send OTPs',
};
