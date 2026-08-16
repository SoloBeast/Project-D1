import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test('GET sends bearer token and decodes success envelope', () async {
    final client = MockClient((request) async {
      expect(request.url.toString(), 'https://api.example.test/health');
      expect(request.headers['Authorization'], 'Bearer access-token');
      expect(request.headers['Accept'], 'application/json');
      return http.Response(
        '{"success":true,"data":{"status":"healthy"},"errors":[]}',
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final api = ApiClient(client: client, baseUrl: 'https://api.example.test');

    final body = await api.get('/health', accessToken: 'access-token');

    expect(body['success'], isTrue);
    expect((body['data'] as Map<String, dynamic>)['status'], 'healthy');
  });

  test('Development registration uses the centralized API URL', () async {
    final client = MockClient((request) async {
      expect(request.method, 'POST');
      expect(
        request.url.toString(),
        'http://localhost:5209/api/v1/auth/register',
      );
      return http.Response(
        '{"success":true,"data":null,"errors":[]}',
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final api = ApiClient(client: client, baseUrl: apiBaseUrl);

    await api.post('/api/v1/auth/register', body: const {});
  });

  test('POST sends JSON body and bearer token', () async {
    final client = MockClient((request) async {
      expect(request.method, 'POST');
      expect(
        request.url.toString(),
        'https://api.example.test/api/v1/auth/logout',
      );
      expect(request.headers['Authorization'], 'Bearer access-token');
      expect(request.headers['Content-Type'], 'application/json');
      expect(request.body, '{"deviceIdentifier":"device-1"}');
      return http.Response(
        '{"success":true,"data":null,"errors":[]}',
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final api = ApiClient(client: client, baseUrl: 'https://api.example.test');

    final body = await api.post(
      '/api/v1/auth/logout',
      body: {'deviceIdentifier': 'device-1'},
      accessToken: 'access-token',
    );

    expect(body['success'], isTrue);
  });

  test('PATCH sends JSON body and bearer token', () async {
    final client = MockClient((request) async {
      expect(request.method, 'PATCH');
      expect(request.url.toString(), 'https://api.example.test/profile');
      expect(request.headers['Authorization'], 'Bearer access-token');
      expect(request.body, '{"firstName":"Asha"}');
      return http.Response(
        '{"success":true,"data":null,"errors":[]}',
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final api = ApiClient(client: client, baseUrl: 'https://api.example.test');

    final body = await api.patch(
      '/profile',
      body: {'firstName': 'Asha'},
      accessToken: 'access-token',
    );

    expect(body['success'], isTrue);
  });

  test('DELETE sends bearer token', () async {
    final client = MockClient((request) async {
      expect(request.method, 'DELETE');
      expect(request.url.toString(), 'https://api.example.test/address');
      expect(request.headers['Authorization'], 'Bearer access-token');
      return http.Response(
        '{"success":true,"data":null,"errors":[]}',
        200,
        headers: {'content-type': 'application/json'},
      );
    });
    final api = ApiClient(client: client, baseUrl: 'https://api.example.test');

    final body = await api.delete('/address', accessToken: 'access-token');

    expect(body['success'], isTrue);
  });

  test('POST decodes standard API error envelope', () async {
    final client = MockClient(
      (request) async => http.Response(
        '{"success":false,"message":"Access denied.","errors":[{"code":"FORBIDDEN","field":null,"message":"Access denied."}]}',
        403,
        headers: {'content-type': 'application/json'},
      ),
    );
    final api = ApiClient(client: client, baseUrl: 'https://api.example.test');

    await expectLater(
      api.post('/protected'),
      throwsA(
        isA<ApiException>()
            .having((error) => error.statusCode, 'statusCode', 403)
            .having((error) => error.code, 'code', 'FORBIDDEN')
            .having((error) => error.message, 'message', 'Access denied.'),
      ),
    );
  });

  test(
    'GET uses standard fallback when an error has no structured details',
    () async {
      final client = MockClient((request) async => http.Response('', 500));
      final api = ApiClient(
        client: client,
        baseUrl: 'https://api.example.test',
      );

      await expectLater(
        api.get('/health'),
        throwsA(
          isA<ApiException>()
              .having((error) => error.statusCode, 'statusCode', 500)
              .having((error) => error.code, 'code', 'HTTP_ERROR')
              .having(
                (error) => error.message,
                'message',
                'The request could not be completed.',
              ),
        ),
      );
    },
  );
}
