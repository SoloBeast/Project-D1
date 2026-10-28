import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/vacation_refund_card.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/vacation_refund_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/vacation_refund_repository.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/vacation_refund_status_section.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('vacation-refund models', () {
    test('parses a consolidated request with seven distinct occurrences',
        () {
      final request = VacationRefundRequest.fromJson(
        vacationRequestJson(status: 'Pending'),
      );

      expect(request.requestNumber, 'VR/000001');
      expect(request.reason, 'Customer vacation');
      expect(request.status, VacationRefundStatus.pending);
      expect(request.status.refundIssued, isFalse);
      expect(request.occurrenceCount, 7);
      expect(request.occurrences, hasLength(7));
      expect(
        request.occurrences.map((o) => o.occurrencePublicId).toSet(),
        hasLength(7),
      );
      // Frozen server basis is preserved exactly; the client never reprices.
      expect(request.grossPerOccurrence, 100.0);
      expect(request.totalAmount, 700.0);
      expect(request.deductionPercentage, 0.0);
      expect(request.payableAmountSnapshot, 800.0);
      expect(request.totalEntitlementSnapshot, 8);
      expect(request.currency, 'INR');
      expect(
        request.occurrences.map((o) => o.allocationAmount),
        everyElement(100.0),
      );
    });

    test('parses six occurrences when one was settled elsewhere', () {
      final request = VacationRefundRequest.fromJson(
        vacationRequestJson(status: 'Pending', occurrenceCount: 6),
      );

      expect(request.occurrenceCount, 6);
      expect(request.occurrences, hasLength(6));
      expect(request.totalAmount, 600.0);
      expect(request.deductionPercentage, 0.0);
    });

    test('status semantics keep requested/approved distinct from completed',
        () {
      expect(VacationRefundStatus.parse('Pending'), VacationRefundStatus.pending);
      expect(
        VacationRefundStatus.parse('Approved'),
        VacationRefundStatus.approved,
      );
      expect(
        VacationRefundStatus.parse('Completed'),
        VacationRefundStatus.completed,
      );
      expect(
        VacationRefundStatus.parse('Rejected'),
        VacationRefundStatus.rejected,
      );
      expect(
        VacationRefundStatus.parse('anything'),
        VacationRefundStatus.unknown,
      );

      expect(VacationRefundStatus.pending.refundIssued, isFalse);
      expect(VacationRefundStatus.approved.refundIssued, isFalse);
      expect(VacationRefundStatus.rejected.refundIssued, isFalse);
      expect(VacationRefundStatus.completed.refundIssued, isTrue);
      expect(VacationRefundStatus.pending.isTerminal, isFalse);
      expect(VacationRefundStatus.approved.isTerminal, isFalse);
      expect(VacationRefundStatus.completed.isTerminal, isTrue);
    });
  });

  group('vacation-refund repository', () {
    test('reads customer and staff views on exact routes', () async {
      final calls = <String>[];
      final client = MockClient((request) async {
        final path = request.url.path;
        calls.add('${request.method} $path');
        expect(request.headers['Authorization'], 'Bearer vacation-token');
        if (path.endsWith('/approve') ||
            path.endsWith('/reject') ||
            path.endsWith('/complete')) {
          expect(jsonDecode(request.body), {'remarks': 'Reviewed'});
          return successResponse(
            vacationRequestJson(status: 'Approved'),
          );
        }
        if (path == '/api/v1/subscriptions/vacation-refunds') {
          return successResponse([vacationRequestJson()]);
        }
        if (path == '/api/v1/vacation-refunds') {
          return successResponse([vacationRequestJson()]);
        }
        return successResponse(vacationRequestJson());
      });
      final repository = VacationRefundRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final customerList = await repository.listForCustomer('vacation-token');
      final customer = await repository.getForCustomer(
        'vacation-token',
        'req-1',
      );
      final staffList = await repository.listForStaff('vacation-token');
      final staff = await repository.getForStaff('vacation-token', 'req-1');
      final approved = await repository.approve(
        'vacation-token',
        'req-1',
        remarks: 'Reviewed',
      );
      await repository.reject('vacation-token', 'req-1', remarks: 'Reviewed');
      await repository.complete(
        'vacation-token',
        'req-1',
        remarks: 'Reviewed',
      );

      expect(customerList.single.requestNumber, 'VR/000001');
      expect(customer?.occurrenceCount, 7);
      expect(staffList.single.requestNumber, 'VR/000001');
      expect(staff?.requestNumber, 'VR/000001');
      expect(approved.status, VacationRefundStatus.approved);
      expect(
        calls,
        containsAll(<String>[
          'GET /api/v1/subscriptions/vacation-refunds',
          'GET /api/v1/subscriptions/vacation-refunds/req-1',
          'GET /api/v1/vacation-refunds',
          'GET /api/v1/vacation-refunds/req-1',
          'POST /api/v1/vacation-refunds/req-1/approve',
          'POST /api/v1/vacation-refunds/req-1/reject',
          'POST /api/v1/vacation-refunds/req-1/complete',
        ]),
      );
    });
  });

  group('vacation-refund card', () {
    testWidgets('pending request never claims a refund was issued',
        (tester) async {
      await tester.pumpWidget(
        materialApp(
          VacationRefundRequest.fromJson(vacationRequestJson()),
        ),
      );

      expect(find.text('VR/000001'), findsOneWidget);
      expect(find.text('Pending'), findsOneWidget);
      expect(
        find.text(
          'Refund requested — under support review. No refund issued yet.',
        ),
        findsOneWidget,
      );
      expect(find.text('Customer vacation'), findsOneWidget);
      expect(find.text('₹700.00 INR'), findsOneWidget);
      expect(find.text('0.00% (vacation)'), findsOneWidget);
      // Seven individually identifiable occurrence rows.
      expect(find.text('02 Aug 2026'), findsOneWidget);
      expect(find.text('08 Aug 2026'), findsOneWidget);
      expect(find.text('credited to your wallet'), findsNothing);
    });

    testWidgets('approved request never claims a refund was issued',
        (tester) async {
      await tester.pumpWidget(
        materialApp(
          VacationRefundRequest.fromJson(
            vacationRequestJson(status: 'Approved'),
          ),
        ),
      );

      expect(find.text('Approved'), findsOneWidget);
      expect(
        find.text(
          'Refund approved — pending completion. No refund issued yet.',
        ),
        findsOneWidget,
      );
      expect(find.text('credited to your wallet'), findsNothing);
    });

    testWidgets('completed request names the wallet credit', (tester) async {
      await tester.pumpWidget(
        materialApp(
          VacationRefundRequest.fromJson(
            vacationRequestJson(status: 'Completed'),
          ),
        ),
      );

      expect(find.text('Completed'), findsOneWidget);
      expect(
        find.text(
          'Refund completed — ₹700.00 credited to your wallet.',
        ),
        findsOneWidget,
      );
      expect(find.text('No refund issued yet'), findsNothing);
    });
  });

  group('vacation-refund status', () {
    test('parses a pending posture with counts and no blocked reason', () {
      final status = VacationRefundSubscriptionStatus.fromJson(
        vacationStatusJson(),
      );

      expect(status.subscriptionPublicId, 'sub-1');
      expect(status.termEnded, isTrue);
      expect(status.processed, isFalse);
      expect(status.request, isNull);
      expect(status.vacationSkippedCount, 5);
      expect(status.unresolvedInTermCount, 1);
      expect(status.hasBlockingTicket, isFalse);
      expect(status.hasBackingPayment, isTrue);
      expect(status.blockedReason, isNull);
    });

    test('parses a blocked posture with the overlap reason', () {
      final status = VacationRefundSubscriptionStatus.fromJson(
        vacationStatusJson(
          hasBlockingTicket: true,
          blockedReason: 'unresolvedTicketOverlap',
        ),
      );

      expect(status.request, isNull);
      expect(status.hasBlockingTicket, isTrue);
      expect(status.blockedReason, 'unresolvedTicketOverlap');
    });

    test('parses a requested posture with its pending request', () {
      final status = VacationRefundSubscriptionStatus.fromJson(
        vacationStatusJson(
          processed: true,
          request: vacationRequestJson(),
        ),
      );

      expect(status.processed, isTrue);
      expect(status.request?.requestNumber, 'VR/000001');
      expect(status.request?.status, VacationRefundStatus.pending);
      expect(status.blockedReason, isNull);
    });

    test('reads the per-subscription status on its exact route', () async {
      final calls = <String>[];
      final repository = statusRepository(vacationStatusJson(), calls);

      final status = await repository.getStatusForCustomer(
        'vacation-token',
        'sub-1',
      );

      expect(status?.vacationSkippedCount, 5);
      expect(
        calls,
        contains(
          'GET /api/v1/subscriptions/vacation-refunds/sub-1/vacation-refund-status',
        ),
      );
    });

    test('fails closed on any route drift from the backend status route',
        () async {
      // Regression: the section silently hides on HTTP errors, so a
      // drifted status URL once made the whole section disappear. The
      // strict mock answers only the real backend route
      // (api/v1/subscriptions/vacation-refunds/{id}/vacation-refund-status)
      // and 404s everything else, exactly like the server.
      final client = MockClient((request) async {
        if (request.url.path ==
            '/api/v1/subscriptions/vacation-refunds/sub-1/vacation-refund-status') {
          return successResponse(vacationStatusJson());
        }
        return http.Response('Not Found', 404);
      });
      final repository = VacationRefundRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      final status = await repository.getStatusForCustomer(
        'vacation-token',
        'sub-1',
      );
      expect(status?.vacationSkippedCount, 5);
    });

    testWidgets('a failed status load renders nothing and breaks nothing',
        (tester) async {
      final client = MockClient(
        (_) async => http.Response('Not Found', 404),
      );
      final repository = VacationRefundRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );
      await pumpStatusSection(tester, repository);
      await tester.pumpAndSettle();

      expect(find.text('Vacation refund pending'), findsNothing);
      expect(find.text('Vacation refund on hold'), findsNothing);
      expect(find.text('VR/000001'), findsNothing);
    });

    testWidgets('pending evaluation explains the wait with counts',
        (tester) async {
      await pumpStatusSection(
        tester,
        statusRepository(vacationStatusJson()),
      );
      await tester.pumpAndSettle();

      expect(find.text('Vacation refund pending'), findsOneWidget);
      expect(
        find.textContaining('5 skipped deliveries'),
        findsOneWidget,
      );
      expect(
        find.textContaining('before any refund is issued'),
        findsOneWidget,
      );
      expect(find.text('VR/000001'), findsNothing);
    });

    testWidgets('blocked evaluation names the hold without card details',
        (tester) async {
      await pumpStatusSection(
        tester,
        statusRepository(
          vacationStatusJson(
            hasBlockingTicket: true,
            blockedReason: 'unresolvedTicketOverlap',
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Vacation refund on hold'), findsOneWidget);
      expect(find.textContaining('related'), findsOneWidget);
      expect(find.text('VR/000001'), findsNothing);
    });

    testWidgets('requested evaluation renders the request card',
        (tester) async {
      await pumpStatusSection(
        tester,
        statusRepository(
          vacationStatusJson(
            processed: true,
            request: vacationRequestJson(),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('VR/000001'), findsOneWidget);
      expect(find.text('Vacation refund pending'), findsNothing);
    });

    testWidgets('renders nothing before the term ends', (tester) async {
      await pumpStatusSection(
        tester,
        statusRepository(
          vacationStatusJson(
            termEnded: false,
            vacationSkippedCount: 5,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Vacation refund pending'), findsNothing);
      expect(find.text('VR/000001'), findsNothing);
    });
  });
}

Widget materialApp(VacationRefundRequest request) => MaterialApp(
  home: Scaffold(body: VacationRefundCard(request: request)),
);

Map<String, dynamic> vacationRequestJson({
  String status = 'Pending',
  int occurrenceCount = 7,
}) {
  final dates = [
    '2026-08-02',
    '2026-08-03',
    '2026-08-04',
    '2026-08-05',
    '2026-08-06',
    '2026-08-07',
    '2026-08-08',
  ].take(occurrenceCount).toList();
  return {
    'requestId': 'req-1',
    'requestNumber': 'VR/000001',
    'subscriptionPublicId': 'sub-1',
    'customerId': 55,
    'customerName': 'Asha Rao',
    'customerMobile': '9876543210',
    'branchId': 7,
    'branchCode': 'BR-07',
    'branchName': 'Bengaluru Dairy',
    'reason': 'Customer vacation',
    'status': status,
    'occurrenceCount': occurrenceCount,
    'grossPerOccurrence': 100.0,
    'totalAmount': 100.0 * occurrenceCount,
    'deductionPercentage': 0.0,
    'payableAmountSnapshot': 800.0,
    'totalEntitlementSnapshot': 8,
    'currency': 'INR',
    'submittedAt': '2026-08-10T09:30:00',
    'decidedByUserId': status == 'Pending' ? null : 9,
    'decidedByName': status == 'Pending' ? null : 'Support Lead',
    'decidedAt': status == 'Pending' ? null : '2026-08-11T09:30:00',
    'decisionRemarks': status == 'Pending' ? null : 'Verified',
    'completedByUserId': status == 'Completed' ? 9 : null,
    'completedByName': status == 'Completed' ? 'Support Lead' : null,
    'completedAt': status == 'Completed' ? '2026-08-12T09:30:00' : null,
    'completionRemarks': status == 'Completed' ? 'Settled' : null,
    'occurrences': [
      for (var i = 0; i < dates.length; i++)
        {
          'occurrencePublicId': 'occ-${i + 1}',
          'scheduledDate': dates[i],
          'quantity': 1.0,
          'allocationAmount': 100.0,
        },
    ],
  };
}

http.Response successResponse(Object? data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

Map<String, dynamic> vacationStatusJson({
  bool termEnded = true,
  bool processed = false,
  Map<String, dynamic>? request,
  int vacationSkippedCount = 5,
  int unresolvedInTermCount = 1,
  bool hasBlockingTicket = false,
  bool hasBackingPayment = true,
  String? blockedReason,
}) => {
  'subscriptionPublicId': 'sub-1',
  'termEnded': termEnded,
  'processed': processed,
  'request': request,
  'vacationSkippedCount': vacationSkippedCount,
  'unresolvedInTermCount': unresolvedInTermCount,
  'hasBlockingTicket': hasBlockingTicket,
  'hasBackingPayment': hasBackingPayment,
  'blockedReason': blockedReason,
};

VacationRefundRepository statusRepository(
  Map<String, dynamic> status, [
  List<String>? calls,
]) {
  final client = MockClient((request) async {
    calls?.add('${request.method} ${request.url.path}');
    return successResponse(status);
  });
  return VacationRefundRepository(
    api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
  );
}

Future<void> pumpStatusSection(
  WidgetTester tester,
  VacationRefundRepository repository,
) =>
    tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          home: Scaffold(
            body: VacationRefundStatusSection(
              subscriptionId: 'sub-1',
              repository: repository,
              token: 'vacation-token',
            ),
          ),
        ),
      ),
    );
