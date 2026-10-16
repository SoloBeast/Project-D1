import 'dart:async';
import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_repository.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('slice 6 models', () {
    test('failed parses with the closed-without-delivery label and terminal semantics', () {
      expect(
        RefundReplacementStatus.parse('Failed'),
        RefundReplacementStatus.failed,
      );
      expect(
        RefundReplacementStatus.parse('failed'),
        RefundReplacementStatus.failed,
      );
      expect(
        RefundReplacementStatus.failed.label,
        'Closed without delivery',
      );
      expect(RefundReplacementStatus.failed.isTerminal, isTrue);
      expect(RefundReplacementStatus.completed.isTerminal, isTrue);
      expect(formatRefundReplacementDate(DateTime.utc(2026, 8, 18)), '18/08/2026');
    });

    test('replacement date parses from the approval response only', () {
      final approved = RefundReplacementRequest.fromJson(
        _requestJson(
          status: 'Approved',
          source: 'DeliveryFailed',
          customerChoice: 'ReDelivery',
          subscriptionDeliveryPublicId: 'sub-1',
          replacementDate: '2026-08-18',
          includeDecision: true,
        ),
      );
      expect(approved.replacementDate, DateTime.utc(2026, 8, 18));
      expect(approved.customerChoice, CustomerChoice.reDelivery);

      final pending = RefundReplacementRequest.fromJson(_requestJson());
      expect(pending.replacementDate, isNull);
    });

    test('proposal parses the server date without timezone shifting the day', () {
      final proposal = ReplacementDateProposal.fromJson({
        'requestId': 'req-1',
        'proposedDate': '2026-08-19',
      });
      expect(proposal.requestId, 'req-1');
      expect(proposal.proposedDate, DateTime.utc(2026, 8, 19));
      expect(formatRefundReplacementDate(proposal.proposedDate), '19/08/2026');
    });

    test('staff-decidable and redelivery-approval eligibility', () {
      RefundReplacementRequest ticket({
        required RefundReplacementStatus status,
        RefundReplacementSource source = RefundReplacementSource.deliveryFailed,
        CustomerChoice? choice = CustomerChoice.reDelivery,
        String? subId = 'sub-1',
      }) => _ticket(
        status: status,
        source: source,
        customerChoice: choice,
        subscriptionDeliveryPublicId: subId,
      );

      expect(ticket(status: RefundReplacementStatus.pending).isStaffDecidable, isTrue);
      expect(
        ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
        ).isStaffDecidable,
        isTrue,
      );
      expect(
        ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          choice: null,
        ).isStaffDecidable,
        isFalse,
      );
      expect(ticket(status: RefundReplacementStatus.failed).isStaffDecidable, isFalse);
      expect(
        ticket(status: RefundReplacementStatus.completed).isStaffDecidable,
        isFalse,
      );
      expect(ticket(status: RefundReplacementStatus.rejected).isStaffDecidable, isFalse);

      // The re-delivery date flow needs a subscription failure with a recorded
      // Re-Delivery choice on a decidable ticket.
      expect(
        ticket(status: RefundReplacementStatus.awaitingCustomerChoice).isRedeliveryApproval,
        isTrue,
      );
      expect(
        ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          choice: CustomerChoice.refund,
        ).isRedeliveryApproval,
        isFalse,
      );
      expect(
        ticket(
          status: RefundReplacementStatus.pending,
          source: RefundReplacementSource.customerPostDelivery,
          choice: null,
          subId: null,
        ).isRedeliveryApproval,
        isFalse,
      );
      expect(
        ticket(
          status: RefundReplacementStatus.failed,
        ).isRedeliveryApproval,
        isFalse,
      );
    });
  });

  group('slice 6 repository', () {
    test('fetches the proposal from the staff route with a bearer token', () async {
      final client = MockClient((request) async {
        expect(request.method, 'GET');
        expect(
          request.url.path,
          '/api/v1/refund-replacements/req-1/replacement-date-proposal',
        );
        expect(request.headers['Authorization'], 'Bearer staff-token');
        return _success({
          'requestId': 'req-1',
          'proposedDate': '2026-08-19',
        });
      });

      final proposal = await _repository(
        client,
      ).getReplacementDateProposal('staff-token', 'req-1');

      expect(proposal.requestId, 'req-1');
      expect(proposal.proposedDate, DateTime.utc(2026, 8, 19));
    });

    test('approves with the replacement date in yyyy-MM-dd wire format', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/refund-replacements/req-1/approve');
        expect(jsonDecode(request.body), {
          'remarks': 'Re-drive Thursday',
          'replacementDate': '2026-08-20',
        });
        return _success(
          _requestJson(status: 'Approved', replacementDate: '2026-08-20'),
        );
      });

      final result = await _repository(client).approve(
        'staff-token',
        'req-1',
        remarks: 'Re-drive Thursday',
        replacementDate: DateTime(2026, 8, 20),
      );

      expect(result.status, RefundReplacementStatus.approved);
      expect(result.replacementDate, DateTime.utc(2026, 8, 20));
    });

    test('refund approval without a date keeps the exact legacy body', () async {
      final client = MockClient((request) async {
        expect(jsonDecode(request.body), {'remarks': 'Reviewed'});
        return _success(_requestJson(status: 'Approved'));
      });

      final result = await _repository(
        client,
      ).approve('staff-token', 'req-1', remarks: 'Reviewed');

      expect(result.status, RefundReplacementStatus.approved);
      expect(result.replacementDate, isNull);
    });

    test('staff list filters by the failed wire value', () async {
      final client = MockClient((request) async {
        expect(request.url.path, '/api/v1/refund-replacements');
        expect(request.url.queryParameters['status'], 'Failed');
        return _success({
          'items': [],
          'page': 1,
          'pageSize': 20,
          'totalCount': 0,
        });
      });

      final page = await _repository(client).listForStaff(
        'staff-token',
        status: RefundReplacementStatus.failed,
      );

      expect(page.items, isEmpty);
    });
  });

  group('slice 6 controller', () {
    test('approve forwards the replacement date and stores the response', () async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(status: RefundReplacementStatus.pending);
      final container = await _staffContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(refundReplacementControllerProvider.notifier);

      expect(
        await controller.approve(
          'req-1',
          remarks: 'Re-drive Thursday',
          replacementDate: DateTime(2026, 8, 20),
        ),
        isTrue,
      );

      expect(repository.approveCalls, 1);
      expect(repository.approvedDate, DateTime(2026, 8, 20));
      expect(repository.approvedRemarks, 'Re-drive Thursday');
      expect(
        container
            .read(refundReplacementControllerProvider)
            .selectedStaffRequest
            ?.replacementDate,
        DateTime.utc(2026, 8, 20),
      );
    });

    test('refund approve without a date sends no replacement date', () async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(status: RefundReplacementStatus.pending);
      final container = await _staffContainer(repository);
      addTearDown(container.dispose);

      expect(
        await container
            .read(refundReplacementControllerProvider.notifier)
            .approve('req-1'),
        isTrue,
      );
      expect(repository.approvedDate, isNull);
    });
  });

  group('slice 6 staff screens', () {
    testWidgets('staff list offers the failed filter', (tester) async {
      final repository = _Slice6FakeRepository()
        ..staffPage = RefundReplacementPage(
          items: [
            _listItem(status: RefundReplacementStatus.failed),
          ],
          page: 1,
          pageSize: 20,
          totalCount: 1,
        );
      // Wide surface so every filter chip (including the trailing Failed
      // one) is hittable without scrolling the filter row.
      await _pumpStaff(
        tester,
        const StaffRefundReplacementListScreen(branchId: 7),
        repository,
        size: const Size(1600, 1200),
      );

      expect(
        find.widgetWithText(ChoiceChip, 'Closed without delivery'),
        findsOneWidget,
      );
      expect(find.text('RR-2026-0001'), findsOneWidget);

      await tester.tap(
        find.widgetWithText(ChoiceChip, 'Closed without delivery'),
      );
      await tester.pumpAndSettle();
      expect(repository.lastStaffStatus, RefundReplacementStatus.failed);
    });

    testWidgets('failed detail explains non-delivery and offers no actions', (
      tester,
    ) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.failed,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: CustomerChoice.reDelivery,
          subscriptionDeliveryPublicId: 'sub-1',
          includeDecision: true,
          completedByName: 'Support Lead',
          completedAtUtc: DateTime.utc(2026, 8, 20, 6),
          completionRemarks: 'Replacement failed twice; refund-only remedy resolved.',
        );
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      expect(find.text('Closed without delivery'), findsWidgets);
      expect(
        find.textContaining('closed without delivery', findRichText: true),
        findsWidgets,
      );
      expect(find.text('Closed'), findsOneWidget);
      expect(find.text('Completed'), findsNothing);
      expect(find.text('Approve'), findsNothing);
      expect(find.text('Reject'), findsNothing);
      expect(find.text('Mark completed'), findsNothing);
      expect(
        find.text('This request is final and no further action is available.'),
        findsOneWidget,
      );
    });

    testWidgets('completed detail shows the approved replacement date', (
      tester,
    ) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.completed,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: CustomerChoice.reDelivery,
          subscriptionDeliveryPublicId: 'sub-1',
          replacementDate: DateTime.utc(2026, 8, 18),
          includeDecision: true,
          completedByName: 'Support Lead',
          completedAtUtc: DateTime.utc(2026, 8, 18, 10),
        );
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      expect(find.text('Completed'), findsWidgets);
      expect(find.text('Replacement date'), findsOneWidget);
      expect(find.text('18/08/2026'), findsOneWidget);
      expect(find.text('Mark completed'), findsNothing);
    });

    testWidgets('redelivery approval confirms the proposed date', (tester) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: CustomerChoice.reDelivery,
          subscriptionDeliveryPublicId: 'sub-1',
        )
        ..proposedDate = DateTime.utc(2026, 8, 19);
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      expect(find.widgetWithText(FilledButton, 'Approve'), findsOneWidget);
      expect(
        find.text('This request is final and no further action is available.'),
        findsNothing,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
      await tester.pump();
      expect(find.text('Loading proposed replacement date...'), findsOneWidget);
      await tester.pumpAndSettle();

      expect(find.text('19/08/2026'), findsWidgets);
      expect(repository.proposalCalls, 1);

      await tester.tap(find.widgetWithText(FilledButton, 'Approve re-delivery'));
      await tester.pumpAndSettle();

      expect(repository.approveCalls, 1);
      expect(repository.approvedDate, DateTime(2026, 8, 19));
      expect(
        find.text('Re-delivery approved for 19/08/2026.'),
        findsOneWidget,
      );
      expect(find.text('Replacement date'), findsOneWidget);
    });

    testWidgets('staff can change the date with the picker before approving', (
      tester,
    ) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: CustomerChoice.reDelivery,
          subscriptionDeliveryPublicId: 'sub-1',
        )
        ..proposedDate = DateTime.utc(2026, 8, 19);
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(OutlinedButton, 'Change date'));
      await tester.pumpAndSettle();

      expect(find.byType(DatePickerDialog), findsOneWidget);
      await tester.tap(find.text('20'));
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      expect(find.text('20/08/2026'), findsWidgets);
      await tester.tap(find.widgetWithText(FilledButton, 'Approve re-delivery'));
      await tester.pumpAndSettle();

      expect(repository.approveCalls, 1);
      expect(repository.approvedDate, DateTime(2026, 8, 20));
    });

    testWidgets('proposal failure shows an error with retry', (tester) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: CustomerChoice.reDelivery,
          subscriptionDeliveryPublicId: 'sub-1',
        )
        ..proposalFailure = const ApiException(
          409,
          'REPLACEMENT_UNAVAILABLE',
          'No eligible replacement date is available right now.',
        );
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
      await tester.pumpAndSettle();

      expect(
        find.text('No eligible replacement date is available right now.'),
        findsOneWidget,
      );
      expect(find.widgetWithText(TextButton, 'Retry'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Approve re-delivery'), findsOneWidget);
      // The submit stays disabled until a proposal loads.
      final submit = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Approve re-delivery'),
      );
      expect(submit.onPressed, isNull);

      repository.proposalFailure = null;
      await tester.tap(find.widgetWithText(TextButton, 'Retry'));
      await tester.pumpAndSettle();
      expect(repository.proposalCalls, 2);
      expect(find.text('19/08/2026'), findsWidgets);
    });

    testWidgets('backend approval validation errors stay in the dialog', (
      tester,
    ) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: CustomerChoice.reDelivery,
          subscriptionDeliveryPublicId: 'sub-1',
        )
        ..proposedDate = DateTime.utc(2026, 8, 19)
        ..approveFailure = const ApiException(
          409,
          'REPLACEMENT_DATE_CONFLICT',
          'The replacement date conflicts with an existing delivery.',
        );
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Approve re-delivery'));
      await tester.pumpAndSettle();

      expect(repository.approveCalls, 1);
      // The controller error also reaches the background decision bar, so
      // scope this assertion to the dialog that staff actually reads.
      expect(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.text(
            'The replacement date conflicts with an existing delivery.',
          ),
        ),
        findsOneWidget,
      );
      // The dialog stays open so staff can pick another date.
      expect(find.text('Approve re-delivery'), findsWidgets);
      expect(find.text('Re-delivery approved for'), findsNothing);
    });

    testWidgets('duplicate approval taps submit only once', (tester) async {
      final gate = Completer<void>();
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: CustomerChoice.reDelivery,
          subscriptionDeliveryPublicId: 'sub-1',
        )
        ..proposedDate = DateTime.utc(2026, 8, 19)
        ..approveGate = gate;
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
      await tester.pumpAndSettle();

      // Two taps before the first frame settles: the dialog's single-flight
      // guard must drop the second one. (After submission starts the button
      // swaps to a spinner, so it cannot be re-found by label.)
      final submit = find.widgetWithText(FilledButton, 'Approve re-delivery');
      await tester.tap(submit);
      await tester.tap(submit);
      await tester.pump();
      expect(repository.approveCalls, 1);

      gate.complete();
      await tester.pumpAndSettle();
      expect(repository.approveCalls, 1);
      expect(
        find.text('Re-delivery approved for 19/08/2026.'),
        findsOneWidget,
      );
    });

    testWidgets('pending refund approval keeps the remarks-only flow', (
      tester,
    ) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(status: RefundReplacementStatus.pending);
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
      await tester.pumpAndSettle();
      expect(find.text('Approve request'), findsOneWidget);
      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(FilledButton, 'Approve'),
        ),
      );
      await tester.pumpAndSettle();

      expect(repository.approveCalls, 1);
      expect(repository.approvedDate, isNull);
      expect(find.text('Request approved.'), findsOneWidget);
    });

    testWidgets('awaiting-choice ticket without a choice offers no decision', (
      tester,
    ) async {
      final repository = _Slice6FakeRepository()
        ..staffRequest = _ticket(
          status: RefundReplacementStatus.awaitingCustomerChoice,
          source: RefundReplacementSource.deliveryFailed,
          customerChoice: null,
          subscriptionDeliveryPublicId: 'sub-1',
        );
      await _pumpStaff(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        repository,
      );

      expect(
        find.text('Waiting for the customer to choose Refund or Re-Delivery.'),
        findsOneWidget,
      );
      expect(find.text('Approve'), findsNothing);
      expect(find.text('Reject'), findsNothing);
    });
  });
}

RefundReplacementRepository _repository(http.Client client) =>
    RefundReplacementRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

http.Response _success(Object? data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

Map<String, dynamic> _requestJson({
  String status = 'Pending',
  String source = 'CustomerPostDelivery',
  String? customerChoice,
  String? subscriptionDeliveryPublicId,
  String? replacementDate,
  bool includeDecision = false,
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
  'source': source,
  'status': status,
  'customerChoice': customerChoice,
  'customerChoiceAt': customerChoice == null
      ? null
      : '2026-08-17T10:00:00Z',
  'subscriptionDeliveryPublicId': subscriptionDeliveryPublicId,
  'reason': 'Milk was sour',
  'remarks': null,
  'submittedAt': '2026-08-17T09:00:00Z',
  'deadline': '2026-08-19T09:00:00Z',
  'decidedByUserId': includeDecision ? 9 : null,
  'decidedByName': includeDecision ? 'Support Lead' : null,
  'decidedAt': includeDecision ? '2026-08-17T11:00:00Z' : null,
  'decisionRemarks': includeDecision ? 'Approved after review' : null,
  'completedByUserId': null,
  'completedByName': null,
  'completedAt': null,
  'completionRemarks': null,
  'proofImageRequired': false,
  'images': const <Map<String, dynamic>>[],
  'replacementDate': replacementDate,
};

RefundReplacementRequest _ticket({
  RefundReplacementStatus status = RefundReplacementStatus.pending,
  RefundReplacementSource source = RefundReplacementSource.customerPostDelivery,
  CustomerChoice? customerChoice,
  String? subscriptionDeliveryPublicId,
  DateTime? replacementDate,
  bool includeDecision = false,
  String? completedByName,
  DateTime? completedAtUtc,
  String? completionRemarks,
}) => RefundReplacementRequest(
  requestId: 'req-1',
  requestNumber: 'RR-2026-0001',
  orderId: 101,
  orderPublicId: 'order-pub-1',
  orderNumber: 'ORD-1001',
  deliveryId: 'delivery-1',
  deliveryNumber: 'DEL-2001',
  customerId: 55,
  customerName: 'Asha Rao',
  customerMobile: '9876543210',
  branchId: 7,
  branchCode: 'BR-07',
  branchName: 'Bengaluru Dairy',
  milkTestId: null,
  milkTestPublicId: null,
  type: RefundReplacementType.refund,
  source: source,
  status: status,
  reason: 'Milk was sour',
  remarks: null,
  submittedAtUtc: DateTime.utc(2026, 8, 17, 9),
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  decidedByUserId: includeDecision ? 9 : null,
  decidedByName: includeDecision ? 'Support Lead' : null,
  decidedAtUtc: includeDecision ? DateTime.utc(2026, 8, 17, 11) : null,
  decisionRemarks: includeDecision ? 'Approved after review' : null,
  completedByUserId: completedAtUtc == null ? null : 9,
  completedByName: completedByName,
  completedAtUtc: completedAtUtc,
  completionRemarks: completionRemarks,
  proofImageRequired: false,
  images: const [],
  subscriptionDeliveryPublicId: subscriptionDeliveryPublicId,
  failedAtUtc: source == RefundReplacementSource.deliveryFailed
      ? DateTime.utc(2026, 8, 17, 6)
      : null,
  failureReasonSnapshot: source == RefundReplacementSource.deliveryFailed
      ? 'Delivery vehicle breakdown'
      : null,
  customerChoice: customerChoice,
  customerChoiceAtUtc: customerChoice == null
      ? null
      : DateTime.utc(2026, 8, 17, 10),
  replacementDate: replacementDate,
);

RefundReplacementListItem _listItem({
  required RefundReplacementStatus status,
}) => RefundReplacementListItem(
  requestId: 'req-1',
  requestNumber: 'RR-2026-0001',
  orderId: 101,
  orderPublicId: 'order-pub-1',
  orderNumber: 'ORD-1001',
  deliveryId: 'delivery-1',
  deliveryNumber: 'DEL-2001',
  customerId: 55,
  customerName: 'Asha Rao',
  customerMobile: '9876543210',
  branchId: 7,
  branchCode: 'BR-07',
  branchName: 'Bengaluru Dairy',
  milkTestId: null,
  milkTestPublicId: null,
  type: RefundReplacementType.refund,
  source: RefundReplacementSource.customerPostDelivery,
  status: status,
  reason: 'Milk was sour',
  remarks: null,
  submittedAtUtc: DateTime.utc(2026, 8, 17, 9),
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  proofImageRequired: false,
  imageCount: 0,
);

final _managerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Support Lead',
    email: null,
    mobile: '9876500000',
    roles: ['SUPPORT'],
    permissions: ['REFUND_REPLACEMENT.MANAGE_BRANCH'],
    branchIds: [7],
  ),
  accessToken: 'staff-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

class _ManagerSessionController extends SessionController {
  @override
  SessionState build() => SessionState.authenticated(_managerSession);
}

class _StaffAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _managerSession;
}

Future<ProviderContainer> _staffContainer(
  RefundReplacementRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_StaffAuthRepository()),
      refundReplacementRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

Future<void> _pumpStaff(
  WidgetTester tester,
  Widget screen,
  _Slice6FakeRepository repository, {
  Size size = const Size(800, 1200),
}) async {
  await tester.binding.setSurfaceSize(size);
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        refundReplacementRepositoryProvider.overrideWithValue(repository),
        sessionControllerProvider.overrideWith(
          () => _ManagerSessionController(),
        ),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pumpAndSettle();
}

/// Fake staff repository backing the slice 6 screens and controller tests.
/// Proposal and approval outcomes are scriptable per test; approve captures
/// its arguments and echoes an approved ticket carrying the confirmed date.
class _Slice6FakeRepository extends RefundReplacementRepository {
  _Slice6FakeRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  RefundReplacementRequest? staffRequest;
  RefundReplacementPage? staffPage;
  DateTime? proposedDate;
  Object? proposalFailure;
  Object? approveFailure;
  Completer<void>? approveGate;
  int proposalCalls = 0;
  int approveCalls = 0;
  DateTime? approvedDate;
  String? approvedRemarks;
  RefundReplacementStatus? lastStaffStatus;

  @override
  Future<RefundReplacementPage> listForStaff(
    String token, {
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) async {
    lastStaffStatus = status;
    return staffPage ??
        const RefundReplacementPage(
          items: [],
          page: 1,
          pageSize: 20,
          totalCount: 0,
        );
  }

  @override
  Future<RefundReplacementRequest> getForStaff(
    String token,
    String requestId,
  ) async => staffRequest ?? _ticket();

  @override
  Future<ReplacementDateProposal> getReplacementDateProposal(
    String token,
    String requestId,
  ) async {
    proposalCalls++;
    if (proposalFailure != null) throw proposalFailure!;
    return ReplacementDateProposal(
      requestId: requestId,
      proposedDate: proposedDate ?? DateTime.utc(2026, 8, 19),
    );
  }

  @override
  Future<RefundReplacementRequest> approve(
    String token,
    String requestId, {
    String? remarks,
    DateTime? replacementDate,
  }) async {
    approveCalls++;
    approvedDate = replacementDate;
    approvedRemarks = remarks;
    final gate = approveGate;
    if (gate != null) await gate.future;
    if (approveFailure != null) throw approveFailure!;
    final current =
        staffRequest ?? _ticket(status: RefundReplacementStatus.pending);
    return RefundReplacementRequest(
      requestId: current.requestId,
      requestNumber: current.requestNumber,
      orderId: current.orderId,
      orderPublicId: current.orderPublicId,
      orderNumber: current.orderNumber,
      deliveryId: current.deliveryId,
      deliveryNumber: current.deliveryNumber,
      customerId: current.customerId,
      customerName: current.customerName,
      customerMobile: current.customerMobile,
      branchId: current.branchId,
      branchCode: current.branchCode,
      branchName: current.branchName,
      milkTestId: current.milkTestId,
      milkTestPublicId: current.milkTestPublicId,
      type: current.type,
      source: current.source,
      status: RefundReplacementStatus.approved,
      reason: current.reason,
      remarks: current.remarks,
      submittedAtUtc: current.submittedAtUtc,
      deadlineUtc: current.deadlineUtc,
      decidedByUserId: 9,
      decidedByName: 'Support Lead',
      decidedAtUtc: DateTime.utc(2026, 8, 17, 11),
      decisionRemarks: remarks,
      completedByUserId: null,
      completedByName: null,
      completedAtUtc: null,
      completionRemarks: null,
      proofImageRequired: current.proofImageRequired,
      images: current.images,
      subscriptionDeliveryPublicId: current.subscriptionDeliveryPublicId,
      failedAtUtc: current.failedAtUtc,
      failureReasonSnapshot: current.failureReasonSnapshot,
      customerChoice: current.customerChoice,
      customerChoiceAtUtc: current.customerChoiceAtUtc,
      replacementDate: replacementDate == null
          ? null
          : DateTime.utc(
              replacementDate.year,
              replacementDate.month,
              replacementDate.day,
            ),
    );
  }

  @override
  Future<RefundReplacementRequest> reject(
    String token,
    String requestId, {
    String? remarks,
  }) async => staffRequest ?? _ticket();

  @override
  Future<RefundReplacementRequest> complete(
    String token,
    String requestId, {
    String? remarks,
  }) async => staffRequest ?? _ticket();
}
