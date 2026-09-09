import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/integrations/integrations_models.dart';
import 'package:doodh_direct_mobile/features/integrations/integrations_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('IntegrationConfiguration', () {
    test('parses a fully configured result', () {
      final configuration = IntegrationConfiguration.fromJson(
        _configurationJson(),
      );

      expect(configuration.emailFromAddress, 'no-reply@example.com');
      expect(configuration.emailFromName, 'DoodhDirect');
      expect(configuration.emailHost, 'smtp.example.com');
      expect(configuration.emailPort, 587);
      expect(configuration.emailUserName, 'smtp-user');
      expect(configuration.emailUseSsl, isTrue);
      expect(configuration.emailPasswordConfigured, isTrue);
      expect(configuration.emailIsConfigured, isTrue);
      expect(configuration.inviteUrlBase, 'https://app.example.com');
      expect(configuration.razorpayKeyId, 'rzp_test_1234567890');
      expect(configuration.razorpayKeySecretConfigured, isTrue);
      expect(configuration.razorpayWebhookSecretConfigured, isTrue);
      expect(configuration.razorpayIsConfigured, isTrue);
      expect(
        configuration.googleMapsBaseUrl,
        'https://maps.googleapis.com/maps/api',
      );
      expect(configuration.googleMapsApiKeyConfigured, isTrue);
      expect(configuration.googleMapsIsConfigured, isTrue);
      // The web client key is client-visible, so the backend returns it and
      // the model carries it (unlike the write-only server-side key).
      expect(
        configuration.googleMapsWebClientKey,
        'AIza-web-client-key-123',
      );

      expect(configuration.isEmailConfigured, isTrue);
      expect(configuration.isRazorpayConfigured, isTrue);
      expect(configuration.isGoogleMapsConfigured, isTrue);
    });

    test('uses safe defaults and never parses secrets', () {
      // The backend never returns secret values — only the Configured flags.
      final configuration = IntegrationConfiguration.fromJson({
        'emailFromAddress': '',
        'emailPort': null,
        'emailUseSsl': null,
        'emailPasswordConfigured': false,
        'emailIsConfigured': false,
        'razorpayKeySecretConfigured': false,
        'razorpayWebhookSecretConfigured': false,
        'razorpayIsConfigured': false,
        'googleMapsApiKeyConfigured': false,
        'googleMapsIsConfigured': false,
        'googleMapsWebClientKey': '',
      });

      expect(configuration.emailFromAddress, isNull);
      expect(configuration.emailPort, 587);
      expect(configuration.emailUseSsl, isTrue);
      expect(configuration.emailPasswordConfigured, isFalse);
      expect(configuration.emailIsConfigured, isFalse);
      expect(configuration.razorpayIsConfigured, isFalse);
      expect(configuration.googleMapsIsConfigured, isFalse);
      // An empty/absent web client key parses to null (never an empty string).
      expect(configuration.googleMapsWebClientKey, isNull);
      expect(configuration.isEmailConfigured, isFalse);
      expect(configuration.isRazorpayConfigured, isFalse);
      expect(configuration.isGoogleMapsConfigured, isFalse);
    });
  });

  group('UpdateIntegrationConfigurationRequest', () {
    test('omits null fields so the backend leaves them untouched', () {
      final request = const UpdateIntegrationConfigurationRequest(
        emailHost: 'smtp.example.com',
        emailPort: 465,
        emailUseSsl: false,
      );

      expect(request.toJson(), {
        'emailHost': 'smtp.example.com',
        'emailPort': 465,
        'emailUseSsl': false,
      });
    });

    test('sends empty strings to clear database overrides', () {
      final request = const UpdateIntegrationConfigurationRequest(
        emailFromAddress: '',
        emailHost: '   ',
        inviteUrlBase: '',
      );

      expect(request.toJson(), {
        'emailFromAddress': '',
        'emailHost': '',
        'inviteUrlBase': '',
      });
    });

    test('trims non-empty values before serializing', () {
      final request = const UpdateIntegrationConfigurationRequest(
        emailFromAddress: '  no-reply@example.com  ',
        emailPassword: '  secret  ',
        razorpayKeySecret: ' key-secret ',
        googleMapsApiKey: 'maps-key',
        googleMapsWebClientKey: ' web-client-key ',
      );

      expect(request.toJson(), {
        'emailFromAddress': 'no-reply@example.com',
        'emailPassword': 'secret',
        'razorpayKeySecret': 'key-secret',
        'googleMapsApiKey': 'maps-key',
        'googleMapsWebClientKey': 'web-client-key',
      });
    });

    test('serializes an empty request as an empty payload', () {
      const request = UpdateIntegrationConfigurationRequest();
      expect(request.toJson(), isEmpty);
    });
  });

  group('IntegrationTestResult', () {
    test('parses a successful test result', () {
      final result = IntegrationTestResult.fromJson({
        'success': true,
        'message': 'Configuration verified. A test email was sent to '
            'no-reply@example.com.',
      });

      expect(result.success, isTrue);
      expect(
        result.message,
        'Configuration verified. A test email was sent to '
        'no-reply@example.com.',
      );
    });

    test('parses a failed test result with defaults', () {
      final result = IntegrationTestResult.fromJson({
        'success': false,
        'message': '',
      });

      expect(result.success, isFalse);
      expect(result.message, '');
    });
  });

  group('IntegrationsRepository', () {
    test('loads the current configuration with a GET', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/admin/setup/integrations',
        );
        return _response(_configurationJson());
      });

      final configuration = await repository.get('number-token');

      expect(configuration.emailFromAddress, 'no-reply@example.com');
      expect(configuration.emailHost, 'smtp.example.com');
      expect(configuration.razorpayIsConfigured, isTrue);
      expect(configuration.googleMapsIsConfigured, isTrue);
      expect(
        configuration.googleMapsWebClientKey,
        'AIza-web-client-key-123',
      );
    });

    test('updates the configuration with a PUT', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/setup/integrations',
        );
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body, containsPair('emailHost', 'smtp.example.com'));
        expect(body, containsPair('emailPort', 465));
        expect(body.containsKey('emailPassword'), isFalse);
        expect(body.containsKey('razorpayKeySecret'), isFalse);
        return _response(_configurationJson(emailPort: 465));
      });

      final updated = await repository.update(
        'number-token',
        const UpdateIntegrationConfigurationRequest(
          emailHost: 'smtp.example.com',
          emailPort: 465,
        ),
      );

      expect(updated.emailHost, 'smtp.example.com');
      expect(updated.emailPort, 465);
    });

    test('sends newly typed secrets only when provided', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'PUT',
          path: '/api/v1/admin/setup/integrations',
        );
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body, containsPair('emailPassword', 'new-smtp-secret'));
        expect(body, containsPair('razorpayKeySecret', 'new-key-secret'));
        return _response(_configurationJson());
      });

      final updated = await repository.update(
        'number-token',
        const UpdateIntegrationConfigurationRequest(
          emailPassword: 'new-smtp-secret',
          razorpayKeySecret: 'new-key-secret',
        ),
      );

      expect(updated.emailFromAddress, 'no-reply@example.com');
    });

    test('sends a test email via the test endpoint', () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'POST',
          path: '/api/v1/admin/setup/integrations/test',
        );
        return _response({
          'success': true,
          'message': 'Configuration verified. A test email was sent to '
              'no-reply@example.com.',
        });
      });

      final result = await repository.test('number-token');

      expect(result.success, isTrue);
      expect(
        result.message,
        'Configuration verified. A test email was sent to '
        'no-reply@example.com.',
      );
    });

    test('surfaces validation field errors from the error envelope', () async {
      final repository = _repository((request) async {
        return http.Response(
          jsonEncode({
            'success': false,
            'errors': [
              {
                'code': 'VALIDATION_ERROR',
                'message': 'EmailFromAddress must be a valid email address.',
                'field': 'EmailFromAddress',
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
              .having((e) => e.field, 'field', 'EmailFromAddress'),
        ),
      );
    });
  });
}

IntegrationsRepository _repository(
  Future<http.Response> Function(http.Request request) handler,
) => IntegrationsRepository(
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

Map<String, dynamic> _configurationJson({int emailPort = 587}) => {
  'emailFromAddress': 'no-reply@example.com',
  'emailFromName': 'DoodhDirect',
  'emailHost': 'smtp.example.com',
  'emailPort': emailPort,
  'emailUserName': 'smtp-user',
  'emailUseSsl': true,
  'emailPasswordConfigured': true,
  'emailIsConfigured': true,
  'inviteUrlBase': 'https://app.example.com',
  'razorpayKeyId': 'rzp_test_1234567890',
  'razorpayKeySecretConfigured': true,
  'razorpayWebhookSecretConfigured': true,
  'razorpayIsConfigured': true,
  'googleMapsBaseUrl': 'https://maps.googleapis.com/maps/api',
  'googleMapsApiKeyConfigured': true,
  'googleMapsIsConfigured': true,
  'googleMapsWebClientKey': 'AIza-web-client-key-123',
};
