import 'package:doodh_direct_mobile/core/network/api_client.dart';
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
    final api = ApiClient(
      client: client,
      baseUrl: 'https://api.example.test',
    );

    final body = await api.get('/health', accessToken: 'access-token');

    expect(body['success'], isTrue);
    expect((body['data'] as Map<String, dynamic>)['status'], 'healthy');
  });

  test('GET decodes standard API error envelope', () async {
    final client = MockClient((request) async => http.Response(
          '{"success":false,"message":"Access denied.","errors":[{"code":"FORBIDDEN","field":null,"message":"Access denied."}]}',
          403,
          headers: {'content-type': 'application/json'},
        ));
    final api = ApiClient(
      client: client,
      baseUrl: 'https://api.example.test',
    );

    final invocation = api.get('/protected');

    await expectLater(
      invocation,
      throwsA(
        isA<ApiException>()
            .having((error) => error.statusCode, 'statusCode', 403)
            .having((error) => error.code, 'code', 'FORBIDDEN')
            .having((error) => error.message, 'message', 'Access denied.'),
      ),
    );
  });
}
