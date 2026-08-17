import 'dart:convert';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_models.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('milk-test models', () {
    test('customer model parses only customer-visible result data', () {
      final customer = CustomerMilkTest.fromJson({
        ...customerMilkTestJson(),
        'parameters': [parameterJson()],
      });

      expect(customer.status, MilkTestStatus.completed);
      expect(customer.customerDecision, MilkTestCustomerDecision.pending);
      expect(customer.canDecide, isTrue);
      expect(customer.images.single.imageId, 'image-1');
      expect(customer.completedAtUtc?.isUtc, isTrue);
    });

    test('staff model parses configurable numeric parameters', () {
      final staff = StaffMilkTest.fromJson(staffMilkTestJson());

      expect(staff.parameters.single.code, 'FAT');
      expect(staff.parameters.single.name, 'Fat');
      expect(staff.parameters.single.value, 6.5);
      expect(staff.parameters.single.unit, '%');
      expect(staff.staffRemarks, 'Reading recorded at doorstep');
    });

    test('terminal decisions cannot be decided again', () {
      final confirmed = CustomerMilkTest.fromJson(
        customerMilkTestJson(decision: 'Confirmed'),
      );
      final rejected = CustomerMilkTest.fromJson(
        customerMilkTestJson(decision: 'Rejected'),
      );

      expect(confirmed.canDecide, isFalse);
      expect(rejected.canDecide, isFalse);
      expect(confirmed.customerDecision.isTerminal, isTrue);
      expect(rejected.customerDecision.isTerminal, isTrue);
    });
  });

  group('milk-test repository', () {
    test(
      'uses exact customer and staff read routes with bearer token',
      () async {
        final requests = <String>[];
        final client = MockClient((request) async {
          requests.add('${request.method} ${request.url}');
          expect(request.headers['Authorization'], 'Bearer milk-token');
          return successResponse(
            request.url.path.startsWith('/api/v1/delivery/')
                ? staffMilkTestJson()
                : customerMilkTestJson(),
          );
        });
        final repository = testRepository(client);

        final customer = await repository.getForCustomer(
          'milk-token',
          'delivery-1',
        );
        final staff = await repository.getForStaff('milk-token', 'delivery-1');

        expect(customer?.milkTestId, 'test-1');
        expect(staff?.parameters.single.value, 6.5);
        expect(requests, [
          'GET https://api.example.test/api/v1/deliveries/delivery-1/milk-test',
          'GET https://api.example.test/api/v1/delivery/delivery-1/milk-test',
        ]);
      },
    );

    test('returns null when a role-specific read has no test', () async {
      final repository = testRepository(
        MockClient((_) async => successResponse(null)),
      );

      expect(
        await repository.getForCustomer('milk-token', 'delivery-1'),
        isNull,
      );
      expect(await repository.getForStaff('milk-token', 'delivery-1'), isNull);
    });

    test('requests a test through the customer delivery route', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/deliveries/delivery-1/milk-test');
        expect(request.headers['Authorization'], 'Bearer milk-token');
        expect(jsonDecode(request.body), <String, dynamic>{});
        return successResponse(
          customerMilkTestJson(status: 'Requested', includeImage: false),
        );
      });

      final result = await testRepository(client)
          .request('milk-token', 'delivery-1');

      expect(result.status, MilkTestStatus.requested);
      expect(result.images, isEmpty);
    });

    test(
      'uploads bytes using the image multipart field and MIME metadata',
      () async {
        final client = MockClient((request) async {
          expect(request.method, 'POST');
          expect(request.url.path, '/api/v1/milk-tests/test-1/images');
          expect(request.headers['Authorization'], 'Bearer milk-token');
          expect(
            request.headers['Content-Type'],
            startsWith('multipart/form-data;'),
          );
          final body = utf8.decode(request.bodyBytes);
          expect(body, contains('name="image"; filename="reading.jpg"'));
          expect(body.toLowerCase(), contains('content-type: image/jpeg'));
          expect(body, contains('jpeg-bytes'));
          return successResponse(imageJson());
        });

        final image = await testRepository(client).uploadImage(
          'milk-token',
          'test-1',
          bytes: Uint8List.fromList(utf8.encode('jpeg-bytes')),
          fileName: 'reading.jpg',
          contentType: 'image/jpeg',
        );

        expect(image.imageId, 'image-1');
      },
    );

    test('completes with configurable parameters and remarks', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/milk-tests/test-1/complete');
        expect(request.headers['Authorization'], 'Bearer milk-token');
        expect(jsonDecode(request.body), {
          'parameters': [parameterJson()],
          'remarks': 'Reading recorded at doorstep',
        });
        return successResponse(staffMilkTestJson());
      });

      final result = await testRepository(client).complete(
        'milk-token',
        'test-1',
        parameters: const [
          MilkTestParameter(code: 'FAT', name: 'Fat', value: 6.5, unit: '%'),
        ],
        remarks: 'Reading recorded at doorstep',
      );

      expect(result.status, MilkTestStatus.completed);
    });

    test('posts customer confirm and reject decisions with remarks', () async {
      final requests = <String>[];
      final bodies = <Map<String, dynamic>>[];
      final client = MockClient((request) async {
        requests.add('${request.method} ${request.url.path}');
        bodies.add(jsonDecode(request.body) as Map<String, dynamic>);
        final decision = request.url.path.endsWith('/confirm')
            ? 'Confirmed'
            : 'Rejected';
        return successResponse(customerMilkTestJson(decision: decision));
      });
      final repository = testRepository(client);

      final confirmed = await repository.confirm(
        'milk-token',
        'test-1',
        remarks: 'Looks correct',
      );
      final rejected = await repository.reject(
        'milk-token',
        'test-1',
        remarks: 'Image is unclear',
      );

      expect(requests, [
        'POST /api/v1/milk-tests/test-1/confirm',
        'POST /api/v1/milk-tests/test-1/reject',
      ]);
      expect(bodies, [
        {'remarks': 'Looks correct'},
        {'remarks': 'Image is unclear'},
      ]);
      expect(confirmed.customerDecision, MilkTestCustomerDecision.confirmed);
      expect(rejected.customerDecision, MilkTestCustomerDecision.rejected);
    });
  });
}

Map<String, dynamic> customerMilkTestJson({
  String status = 'Completed',
  String decision = 'Pending',
  bool includeImage = true,
}) => {
  'milkTestId': 'test-1',
  'deliveryId': 'delivery-1',
  'status': status,
  'customerDecision': decision,
  'requestedAtUtc': '2026-08-17T09:00:00Z',
  'completedAtUtc': status == 'Completed' ? '2026-08-17T09:10:00Z' : null,
  'confirmedAtUtc': decision == 'Confirmed' ? '2026-08-17T09:12:00Z' : null,
  'rejectedAtUtc': decision == 'Rejected' ? '2026-08-17T09:12:00Z' : null,
  'customerRemarks': decision == 'Pending' ? null : 'Customer decision',
  'images': includeImage ? [imageJson()] : <Map<String, dynamic>>[],
};

Map<String, dynamic> staffMilkTestJson() => {
  ...customerMilkTestJson(),
  'staffRemarks': 'Reading recorded at doorstep',
  'parameters': [parameterJson()],
};

Map<String, dynamic> parameterJson() => {
  'code': 'FAT',
  'name': 'Fat',
  'value': 6.5,
  'unit': '%',
};

Map<String, dynamic> imageJson() => {
  'imageId': 'image-1',
  'fileName': 'reading.jpg',
  'contentType': 'image/jpeg',
  'fileSize': 2048,
  'uploadedAtUtc': '2026-08-17T09:05:00Z',
  'contentPath': '/api/v1/milk-tests/test-1/images/image-1/content',
};

http.Response successResponse(Object? data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

MilkTestRepository testRepository(http.Client client) => MilkTestRepository(
  api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
);
