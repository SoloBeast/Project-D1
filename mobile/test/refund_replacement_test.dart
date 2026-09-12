import 'dart:convert';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('refund/replacement models', () {
    test(
      'parses type, status, and source with labels and terminal semantics',
      () {
        expect(
          RefundReplacementType.parse('Refund'),
          RefundReplacementType.refund,
        );
        expect(
          RefundReplacementType.parse('replacement'),
          RefundReplacementType.replacement,
        );
        expect(
          RefundReplacementType.parse('anything'),
          RefundReplacementType.unknown,
        );
        expect(RefundReplacementType.replacement.label, 'Replacement');

        expect(
          RefundReplacementStatus.parse('Pending'),
          RefundReplacementStatus.pending,
        );
        expect(
          RefundReplacementStatus.parse('Approved'),
          RefundReplacementStatus.approved,
        );
        expect(
          RefundReplacementStatus.parse('Rejected'),
          RefundReplacementStatus.rejected,
        );
        expect(
          RefundReplacementStatus.parse('Completed'),
          RefundReplacementStatus.completed,
        );
        expect(
          RefundReplacementStatus.parse(null),
          RefundReplacementStatus.unknown,
        );
        expect(RefundReplacementStatus.pending.isTerminal, isFalse);
        expect(RefundReplacementStatus.approved.isTerminal, isFalse);
        expect(RefundReplacementStatus.rejected.isTerminal, isTrue);
        expect(RefundReplacementStatus.completed.isTerminal, isTrue);

        expect(
          RefundReplacementSource.parse('CustomerPostDelivery'),
          RefundReplacementSource.customerPostDelivery,
        );
        expect(
          RefundReplacementSource.parse('MilkTestRejected'),
          RefundReplacementSource.milkTestRejected,
        );
        expect(
          RefundReplacementSource.customerPostDelivery.label,
          'Requested after delivery',
        );
        expect(
          RefundReplacementSource.milkTestRejected.label,
          'Milk test rejected',
        );
      },
    );

    test('parses the server-authoritative eligibility answer', () {
      final eligible = RefundReplacementEligibility.fromJson(
        eligibilityJson(),
      );

      expect(eligible.isEligible, isTrue);
      expect(eligible.ineligibleReason, isNull);
      expect(eligible.windowHours, 48);
      expect(eligible.deadlineUtc?.isUtc, isTrue);
      expect(eligible.deadlineUtc, DateTime.utc(2026, 8, 19, 9));
      expect(eligible.hasActiveRequest, isFalse);
      expect(eligible.proofImageRequired, isTrue);
      expect(eligible.isMilkTestRejectedFlow, isFalse);

      final empty = RefundReplacementEligibility.fromJson(const {});
      expect(empty.isEligible, isFalse);
      expect(empty.windowHours, 0);
      expect(empty.deadlineUtc, isNull);
      expect(empty.proofImageRequired, isFalse);
    });

    test('parses a request with milk-test linkage, decisions, and images', () {
      final rejected = RefundReplacementRequest.fromJson(
        requestJson(
          source: 'MilkTestRejected',
          type: 'Replacement',
          milkTestId: 42,
          milkTestPublicId: 'milk-test-pub-42',
        ),
      );

      expect(rejected.type, RefundReplacementType.replacement);
      expect(rejected.source, RefundReplacementSource.milkTestRejected);
      expect(rejected.status, RefundReplacementStatus.pending);
      expect(rejected.requestNumber, 'RR-2026-0001');
      expect(rejected.milkTestId, 42);
      expect(rejected.milkTestPublicId, 'milk-test-pub-42');
      expect(rejected.submittedAtUtc.isUtc, isTrue);
      expect(rejected.deadlineUtc?.isUtc, isTrue);
      expect(rejected.decidedByName, isNull);
      expect(rejected.images.single.imageId, 'image-1');
      expect(rejected.images.single.uploadedAtUtc.isUtc, isTrue);

      final decided = RefundReplacementRequest.fromJson(
        requestJson(
          status: 'Approved',
          includeDecision: true,
          images: false,
        ),
      );
      expect(decided.status, RefundReplacementStatus.approved);
      expect(decided.decidedByName, 'Support Lead');
      expect(decided.decidedAtUtc?.isUtc, isTrue);
      expect(decided.decisionRemarks, 'Approved after review');
      expect(decided.images, isEmpty);
    });

    test('parses the staff list page rows with proof flags and paging', () {
      final page = RefundReplacementPage.fromJson({
        'items': [listItemJson(proofImageRequired: true, imageCount: 0)],
        'page': 2,
        'pageSize': 10,
        'totalCount': 21,
      });

      expect(page.page, 2);
      expect(page.pageSize, 10);
      expect(page.totalCount, 21);
      expect(page.items.single.requestNumber, 'RR-2026-0001');
      expect(page.items.single.customerName, 'Asha Rao');
      expect(page.items.single.branchCode, 'BR-07');
      expect(page.items.single.proofImageRequired, isTrue);
      expect(page.items.single.imageCount, 0);
      expect(page.items.single.submittedAtUtc.isUtc, isTrue);

      final empty = RefundReplacementPage.fromJson({
        'page': 1,
        'pageSize': 20,
        'totalCount': 0,
      });
      expect(empty.items, isEmpty);
    });

    test('throws for a missing required timestamp and nulls optional ones', () {
      expect(
        () => RefundReplacementRequest.fromJson(
          requestJson()..remove('submittedAt'),
        ),
        throwsA(isA<FormatException>()),
      );
      expect(
        () => RefundReplacementImage.fromJson(
          imageJson()..['uploadedAt'] = null,
        ),
        throwsA(isA<FormatException>()),
      );

      final noDeadline = RefundReplacementRequest.fromJson(
        requestJson(deadline: false),
      );
      expect(noDeadline.deadlineUtc, isNull);
    });
  });

  group('refund/replacement repository', () {
    test('reads eligibility through the delivery route with a bearer token', () async {
      final client = MockClient((request) async {
        expect(request.method, 'GET');
        expect(
          request.url.path,
          '/api/v1/deliveries/delivery-1/refund-replacement/eligibility',
        );
        expect(request.headers['Authorization'], 'Bearer refund-token');
        return successResponse(eligibilityJson());
      });

      final eligibility = await testRepository(
        client,
      ).getEligibility('refund-token', 'delivery-1');

      expect(eligibility.isEligible, isTrue);
      expect(eligibility.windowHours, 48);
    });

    test('submits a request with the wire type and milk-test linkage', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(
          request.url.path,
          '/api/v1/deliveries/delivery-1/refund-replacement',
        );
        expect(request.headers['Authorization'], 'Bearer refund-token');
        expect(jsonDecode(request.body), {
          'type': 'Replacement',
          'reason': 'Milk was sour',
          'remarks': 'Delivered at 6am',
          'milkTestId': 'test-1',
        });
        return successResponse(
          requestJson(
            source: 'MilkTestRejected',
            type: 'Replacement',
            milkTestId: 7,
            milkTestPublicId: 'test-1',
          ),
        );
      });

      final result = await testRepository(client).submit(
        'refund-token',
        'delivery-1',
        type: RefundReplacementType.replacement,
        reason: 'Milk was sour',
        remarks: 'Delivered at 6am',
        milkTestId: 'test-1',
      );

      expect(result.type, RefundReplacementType.replacement);
      expect(result.source, RefundReplacementSource.milkTestRejected);
    });

    test(
      'reads customer and staff views, uploads proof, and posts decisions on exact routes',
      () async {
        final calls = <String>[];
        final client = MockClient((request) async {
          final path = request.url.path;
          calls.add('${request.method} $path');
          expect(request.headers['Authorization'], 'Bearer refund-token');

          if (path.endsWith('/approve') ||
              path.endsWith('/reject') ||
              path.endsWith('/complete')) {
            expect(jsonDecode(request.body), {'remarks': 'Reviewed'});
            return successResponse(requestJson(status: 'Approved'));
          }
          if (path == '/api/v1/refund-replacements/req-1/images') {
            expect(
              request.headers['Content-Type'],
              startsWith('multipart/form-data;'),
            );
            final body = utf8.decode(request.bodyBytes);
            expect(body, contains('name="image"; filename="proof.jpg"'));
            expect(body.toLowerCase(), contains('content-type: image/jpeg'));
            return successResponse(imageJson());
          }
          if (path == '/api/v1/refund-replacements/req-1/images/image-1/content') {
            return http.Response.bytes(
              Uint8List.fromList(const [1, 2, 3, 4]),
              200,
              headers: {'content-type': 'image/png'},
            );
          }
          if (path == '/api/v1/customer/refund-replacements') {
            return successResponse([requestJson()]);
          }
          if (path == '/api/v1/refund-replacements') {
            return successResponse({
              'items': [listItemJson()],
              'page': 2,
              'pageSize': 10,
              'totalCount': 1,
            });
          }
          return successResponse(requestJson());
        });
        final repository = testRepository(client);

        final customerList = await repository.listForCustomer('refund-token');
        final customer = await repository.getForCustomer(
          'refund-token',
          'req-1',
        );
        final bytes = await repository.getImageContent(
          'refund-token',
          'req-1',
          'image-1',
        );
        final uploaded = await repository.uploadImage(
          'refund-token',
          'req-1',
          bytes: Uint8List.fromList(utf8.encode('jpeg-bytes')),
          fileName: 'proof.jpg',
          contentType: 'image/jpeg',
        );
        final staffPage = await repository.listForStaff(
          'refund-token',
          branchId: 7,
          status: RefundReplacementStatus.pending,
          page: 2,
          pageSize: 10,
        );
        final staff = await repository.getForStaff('refund-token', 'req-1');
        await repository.approve('refund-token', 'req-1', remarks: 'Reviewed');
        await repository.reject('refund-token', 'req-1', remarks: 'Reviewed');
        await repository.complete('refund-token', 'req-1', remarks: 'Reviewed');

        expect(customerList.single.requestId, 'req-1');
        expect(customer?.requestNumber, 'RR-2026-0001');
        expect(bytes.bytes, [1, 2, 3, 4]);
        expect(bytes.contentType, 'image/png');
        expect(uploaded.imageId, 'image-1');
        expect(staffPage.totalCount, 1);
        expect(staffPage.page, 2);
        expect(staff.requestId, 'req-1');
        expect(calls, containsAll(<String>[
          'GET /api/v1/customer/refund-replacements',
          'GET /api/v1/customer/refund-replacements/req-1',
          'GET /api/v1/refund-replacements/req-1/images/image-1/content',
          'POST /api/v1/refund-replacements/req-1/images',
          'GET /api/v1/refund-replacements',
          'GET /api/v1/refund-replacements/req-1',
          'POST /api/v1/refund-replacements/req-1/approve',
          'POST /api/v1/refund-replacements/req-1/reject',
          'POST /api/v1/refund-replacements/req-1/complete',
        ]));
      },
    );
  });
}

Map<String, dynamic> eligibilityJson({
  bool isEligible = true,
  bool proofImageRequired = true,
  bool hasActiveRequest = false,
  bool milkTestRejectedFlow = false,
}) => {
  'isEligible': isEligible,
  'ineligibleReason': isEligible ? null : 'The request window has closed.',
  'windowHours': 48,
  'deadline': '2026-08-19T09:00:00Z',
  'hasActiveRequest': hasActiveRequest,
  'proofImageRequired': proofImageRequired,
  'isMilkTestRejectedFlow': milkTestRejectedFlow,
};

Map<String, dynamic> requestJson({
  String status = 'Pending',
  String type = 'Refund',
  String source = 'CustomerPostDelivery',
  bool proofImageRequired = true,
  bool includeDecision = false,
  bool images = true,
  bool deadline = true,
  int? milkTestId,
  String? milkTestPublicId,
}) => {
  'requestId': 'req-1',
  'requestNumber': 'RR-2026-0001',
  'orderId': 101,
  'orderPublicId': 'order-pub-1',
  'orderNumber': 'ORD-1001',
  'deliveryId': 'delivery-1',
  'deliveryNumber': 'DEL-2001',
  'customerId': 55,
  'customerName': 'Asha Rao',
  'customerMobile': '9876543210',
  'branchId': 7,
  'branchCode': 'BR-07',
  'branchName': 'Bengaluru Dairy',
  'milkTestId': milkTestId,
  'milkTestPublicId': milkTestPublicId,
  'type': type,
  'source': source,
  'status': status,
  'reason': 'Milk was sour',
  'remarks': 'Delivered at 6am',
  'submittedAt': '2026-08-17T09:00:00Z',
  'deadline': deadline ? '2026-08-19T09:00:00Z' : null,
  'decidedByUserId': includeDecision ? 9 : null,
  'decidedByName': includeDecision ? 'Support Lead' : null,
  'decidedAt': includeDecision ? '2026-08-17T11:00:00Z' : null,
  'decisionRemarks': includeDecision ? 'Approved after review' : null,
  'completedByUserId': null,
  'completedByName': null,
  'completedAt': null,
  'completionRemarks': null,
  'proofImageRequired': proofImageRequired,
  'images': images ? [imageJson()] : <Map<String, dynamic>>[],
};

Map<String, dynamic> listItemJson({
  bool proofImageRequired = false,
  int imageCount = 1,
}) => {
  'requestId': 'req-1',
  'requestNumber': 'RR-2026-0001',
  'orderId': 101,
  'orderPublicId': 'order-pub-1',
  'orderNumber': 'ORD-1001',
  'deliveryId': 'delivery-1',
  'deliveryNumber': 'DEL-2001',
  'customerId': 55,
  'customerName': 'Asha Rao',
  'customerMobile': '9876543210',
  'branchId': 7,
  'branchCode': 'BR-07',
  'branchName': 'Bengaluru Dairy',
  'milkTestId': null,
  'milkTestPublicId': null,
  'type': 'Refund',
  'source': 'CustomerPostDelivery',
  'status': 'Pending',
  'reason': 'Milk was sour',
  'remarks': null,
  'submittedAt': '2026-08-17T09:00:00Z',
  'deadline': '2026-08-19T09:00:00Z',
  'proofImageRequired': proofImageRequired,
  'imageCount': imageCount,
};

Map<String, dynamic> imageJson() => {
  'imageId': 'image-1',
  'fileName': 'proof.jpg',
  'contentType': 'image/jpeg',
  'fileSize': 2048,
  'uploadedAt': '2026-08-17T09:05:00Z',
};

http.Response successResponse(Object? data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

RefundReplacementRepository testRepository(http.Client client) =>
    RefundReplacementRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );
