import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test('fetches google maps web client key from client-config endpoint', () async {
    final client = MockClient((request) async {
      expect(request.method, 'GET');
      expect(
        request.url.toString(),
        'https://api.example.test/api/v1/client-config',
      );
      expect(request.headers['Authorization'], 'Bearer customer-token');
      return http.Response(
        jsonEncode({
          'success': true,
          'data': {'googleMapsWebClientKey': 'web-key-123'},
          'errors': <Object>[],
        }),
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = ClientConfigurationRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    final configuration = await repository.get('customer-token');

    expect(configuration.googleMapsWebClientKey, 'web-key-123');
  });

  test('treats absent or null data and key as not configured', () async {
    final client = MockClient((request) async {
      expect(request.method, 'GET');
      expect(
        request.url.toString(),
        'https://api.example.test/api/v1/client-config',
      );
      expect(request.headers['Authorization'], 'Bearer customer-token');
      return http.Response(
        jsonEncode({'success': true, 'data': null, 'errors': <Object>[]}),
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = ClientConfigurationRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    final configuration = await repository.get('customer-token');

    expect(configuration.googleMapsWebClientKey, isNull);
  });

  test('treats empty web client key as not configured', () async {
    final client = MockClient((request) async {
      return http.Response(
        jsonEncode({
          'success': true,
          'data': {'googleMapsWebClientKey': ''},
          'errors': <Object>[],
        }),
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = ClientConfigurationRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    final configuration = await repository.get('customer-token');

    expect(configuration.googleMapsWebClientKey, isNull);
  });

  test('propagates server error envelope as ApiException', () async {
    final client = MockClient((request) async {
      expect(request.method, 'GET');
      expect(
        request.url.toString(),
        'https://api.example.test/api/v1/client-config',
      );
      return http.Response(
        jsonEncode({
          'success': false,
          'message': 'Access token is invalid or expired.',
          'errors': [
            {'code': 'AUTH_TOKEN_INVALID', 'message': 'Token invalid.'},
          ],
        }),
        401,
        headers: {'content-type': 'application/json'},
      );
    });
    final repository = ClientConfigurationRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

    await expectLater(
      repository.get('expired-token'),
      throwsA(
        isA<ApiException>()
            .having((e) => e.statusCode, 'statusCode', 401)
            .having((e) => e.code, 'code', 'AUTH_TOKEN_INVALID'),
      ),
    );
  });
}
