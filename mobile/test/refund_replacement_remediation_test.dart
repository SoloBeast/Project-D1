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
import 'package:go_router/go_router.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('legacy remediation status', () {
    test('parses Superseded explicitly instead of unknown', () {
      expect(RefundReplacementStatus.parse('Superseded'),
          RefundReplacementStatus.superseded);
      expect(RefundReplacementStatus.parse('superseded'),
          RefundReplacementStatus.superseded);
      // No regression: genuinely unknown values still map to unknown.
      expect(RefundReplacementStatus.parse('Archived'),
          RefundReplacementStatus.unknown);
    });

    test('labels Superseded by Refund and treats it as terminal', () {
      expect(RefundReplacementStatus.superseded.label, 'Superseded by Refund');
      expect(RefundReplacementStatus.superseded.isTerminal, isTrue);
      // Never confused with the neighbouring terminal states.
      expect(RefundReplacementStatus.completed.label, 'Completed');
      expect(RefundReplacementStatus.failed.label, 'Closed without delivery');
      expect(RefundReplacementStatus.rejected.label, 'Rejected');
    });

    test('parses the remediation link fields from the detail API', () {
      final ticket = RefundReplacementRequest.fromJson(
        _ticketJson({
          'status': 'Superseded',
          'type': 'Replacement',
          'supersededByRequestNumber': 'RR/000011',
        }),
      );
      expect(ticket.isSuperseded, isTrue);
      expect(ticket.supersededByRequestNumber, 'RR/000011');
      expect(ticket.supersedesRequestNumber, isNull);
      expect(ticket.isRemediationRefund, isFalse);

      final refund = RefundReplacementRequest.fromJson(
        _ticketJson({
          'status': 'Pending',
          'type': 'Refund',
          'supersedesRequestNumber': 'RR/000002',
          'supersedesRequestPublicId': 'legacy-public-id',
        }),
      );
      expect(refund.isSuperseded, isFalse);
      expect(refund.isRemediationRefund, isTrue);
      expect(refund.supersedesRequestNumber, 'RR/000002');
      expect(refund.supersedesRequestPublicId, 'legacy-public-id');
    });

    test('parses the remediation link on staff list rows', () {
      final item = RefundReplacementListItem.fromJson(
        _listItemJson({
          'status': 'Pending',
          'type': 'Refund',
          'supersedesRequestNumber': 'RR/000002',
        }),
      );
      expect(item.isRemediationRefund, isTrue);
      expect(item.supersedesRequestNumber, 'RR/000002');
      expect(item.isSuperseded, isFalse);

      final legacy = RefundReplacementListItem.fromJson(
        _listItemJson({'status': 'Superseded', 'type': 'Replacement'}),
      );
      expect(legacy.isSuperseded, isTrue);
      expect(legacy.isRemediationRefund, isFalse);
    });
  });

  group('remediation eligibility', () {
    test('is offered only for approved order-backed replacements', () {
      // Eligible: approved, Replacement, order-backed, Failed delivery,
      // not superseded.
      expect(
        _ticket(
          status: RefundReplacementStatus.approved,
          type: RefundReplacementType.replacement,
        ).isLegacyRemediationEligible,
        isTrue,
      );
      // Delivered (or any non-Failed) delivery: hidden.
      for (final delivery in [
        DeliveryStatus.delivered,
        DeliveryStatus.outForDelivery,
        DeliveryStatus.cancelled,
        DeliveryStatus.readyForAssignment,
      ]) {
        expect(
          _ticket(
            status: RefundReplacementStatus.approved,
            type: RefundReplacementType.replacement,
            deliveryStatus: delivery,
          ).isLegacyRemediationEligible,
          isFalse,
        );
      }
      // Missing or unrecognized delivery status fails closed.
      expect(
        _ticket(
          status: RefundReplacementStatus.approved,
          type: RefundReplacementType.replacement,
          deliveryStatus: null,
        ).isLegacyRemediationEligible,
        isFalse,
      );
      expect(
        _ticket(
          status: RefundReplacementStatus.approved,
          type: RefundReplacementType.replacement,
          deliveryStatus: DeliveryStatus.unknown,
        ).isLegacyRemediationEligible,
        isFalse,
      );
      // Already superseded: never offered again.
      expect(
        _ticket(
          status: RefundReplacementStatus.superseded,
          type: RefundReplacementType.replacement,
        ).isLegacyRemediationEligible,
        isFalse,
      );
      // Not yet decided.
      expect(
        _ticket(
          status: RefundReplacementStatus.pending,
          type: RefundReplacementType.replacement,
        ).isLegacyRemediationEligible,
        isFalse,
      );
      // Refund tickets use the Apply/Waive workflow, not remediation.
      expect(
        _ticket(
          status: RefundReplacementStatus.approved,
          type: RefundReplacementType.refund,
        ).isLegacyRemediationEligible,
        isFalse,
      );
      // Terminal states are never eligible.
      for (final status in [
        RefundReplacementStatus.completed,
        RefundReplacementStatus.failed,
        RefundReplacementStatus.rejected,
        RefundReplacementStatus.cancelled,
      ]) {
        expect(
          _ticket(
            status: status,
            type: RefundReplacementType.replacement,
          ).isLegacyRemediationEligible,
          isFalse,
        );
      }
    });

    test('requires an order link and never infers from ids alone', () {
      // Subscription-backed approved replacement: no order link, no action.
      final subscriptionTicket = RefundReplacementRequest(
        requestId: 'req-sub',
        requestNumber: 'RR/000021',
        orderId: null,
        orderPublicId: null,
        orderNumber: null,
        deliveryId: 'delivery-1',
        deliveryNumber: 'DLV-1',
        customerId: 55,
        customerName: 'Asha Rao',
        customerMobile: '9876543210',
        branchId: 7,
        branchCode: 'BR-07',
        branchName: 'Bengaluru Dairy',
        milkTestId: null,
        milkTestPublicId: null,
        type: RefundReplacementType.replacement,
        source: RefundReplacementSource.customerPostDelivery,
        status: RefundReplacementStatus.approved,
        reason: 'Missed call',
        remarks: null,
        submittedAtUtc: DateTime.utc(2026, 8, 17, 9),
        deadlineUtc: null,
        decidedByUserId: 9,
        decidedByName: 'Support Lead',
        decidedAtUtc: DateTime.utc(2026, 8, 17, 11),
        decisionRemarks: null,
        completedByUserId: null,
        completedByName: null,
        completedAtUtc: null,
        completionRemarks: null,
        proofImageRequired: false,
        images: const [],
      );
      expect(subscriptionTicket.isLegacyRemediationEligible, isFalse);
    });
  });

  group('remediation repository', () {
    test('posts the reason to the remediate route and parses the pair',
        () async {
      http.Request? captured;
      final repository = RefundReplacementRepository(
        api: ApiClient(
          client: MockClient((request) async {
            captured = request;
            return _ok({
              'legacyTicket': _ticketJson({
                'requestId': 'legacy-public-id',
                'requestNumber': 'RR/000002',
                'status': 'Superseded',
                'type': 'Replacement',
                'supersededByRequestNumber': 'RR/000011',
              }),
              'refundTicket': _ticketJson({
                'requestId': 'refund-public-id',
                'requestNumber': 'RR/000011',
                'status': 'Pending',
                'type': 'Refund',
                'supersedesRequestNumber': 'RR/000002',
                'supersedesRequestPublicId': 'legacy-public-id',
              }),
            });
          }),
          baseUrl: 'https://api.example.test',
        ),
      );

      final pair = await repository.remediate(
        'staff-token',
        'legacy-public-id',
        reason: 'Replacement unsupported for one-time orders.',
      );

      expect(captured?.method, 'POST');
      expect(captured?.url.path,
          '/api/v1/refund-replacements/legacy-public-id/remediate');
      expect(
        jsonDecode(captured!.body) as Map<String, dynamic>,
        {'reason': 'Replacement unsupported for one-time orders.'},
      );
      expect(pair.legacyTicket.requestNumber, 'RR/000002');
      expect(pair.legacyTicket.isSuperseded, isTrue);
      expect(pair.refundTicket.requestNumber, 'RR/000011');
      expect(pair.refundTicket.status, RefundReplacementStatus.pending);
      expect(pair.refundTicket.isRemediationRefund, isTrue);
    });

    test('surfaces a 409 conflict with the server message', () async {
      final repository = RefundReplacementRepository(
        api: ApiClient(
          client: MockClient((_) async => http.Response(
            jsonEncode({
              'success': false,
              'data': null,
              'errors': [
                {'message': 'Ticket RR/000002 can no longer be remediated.'},
              ],
            }),
            409,
            headers: {'content-type': 'application/json'},
          )),
          baseUrl: 'https://api.example.test',
        ),
      );

      Object? error;
      try {
        await repository.remediate(
          'staff-token',
          'legacy-public-id',
          reason: 'Late.',
        );
      } on Object catch (e) {
        error = e;
      }
      expect(error, isA<ApiException>());
      expect((error! as ApiException).statusCode, 409);
    });
  });

  group('remediation controller', () {
    test('remediates, selects the legacy ticket, and forwards the reason',
        () async {
      final repository = _FakeRemediationRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller =
          container.read(refundReplacementControllerProvider.notifier);

      final pair = await controller.remediateLegacyReplacement(
        'legacy-public-id',
        reason: 'Replacement unsupported for one-time orders.',
      );

      expect(pair, isNotNull);
      expect(pair!.refundTicket.requestNumber, 'RR/000011');
      expect(repository.lastReason,
          'Replacement unsupported for one-time orders.');
      expect(repository.lastRequestId, 'legacy-public-id');
      final state = container.read(refundReplacementControllerProvider);
      expect(state.selectedStaffRequest?.requestNumber, 'RR/000002');
      expect(state.isSaving, isFalse);
      expect(state.errorMessage, isNull);
    });

    test('maps a 409 conflict to the server message without selecting',
        () async {
      final repository = _FakeRemediationRepository(
        failure: const ApiException(
            409, 'Conflict', 'Ticket RR/000002 can no longer be remediated.'),
      );
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);

      final pair = await container
          .read(refundReplacementControllerProvider.notifier)
          .remediateLegacyReplacement('legacy-public-id', reason: 'Late.');

      expect(pair, isNull);
      final state = container.read(refundReplacementControllerProvider);
      expect(
        state.errorMessage,
        'Ticket RR/000002 can no longer be remediated.',
      );
      expect(state.selectedStaffRequest, isNull);
      expect(state.isSaving, isFalse);
    });

    test('maps network failures to the offline flag', () async {
      final repository = _FakeRemediationRepository(
        failure: const ApiNetworkException('unreachable'),
      );
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);

      final pair = await container
          .read(refundReplacementControllerProvider.notifier)
          .remediateLegacyReplacement('legacy-public-id', reason: 'Retry.');

      expect(pair, isNull);
      final state = container.read(refundReplacementControllerProvider);
      expect(state.isOffline, isTrue);
      expect(state.isSaving, isFalse);
    });

    test('blocks a duplicate submission while a save is in flight', () async {
      final repository = _FakeRemediationRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(_StaffAuthRepository()),
          refundReplacementRepositoryProvider.overrideWithValue(repository),
          refundReplacementControllerProvider.overrideWith(
            () => _SavingRemediationController(),
          ),
        ],
      );
      container.read(sessionControllerProvider);
      await Future<void>.delayed(Duration.zero);
      addTearDown(container.dispose);

      final pair = await container
          .read(refundReplacementControllerProvider.notifier)
          .remediateLegacyReplacement('legacy-public-id', reason: 'Twice.');

      expect(pair, isNull);
      expect(repository.calls, 0);
    });
  });

  group('staff remediation UI', () {
    testWidgets('offers the action only for eligible tickets', (tester) async {
      // Eligible: approved order-backed Replacement.
      await _pumpStaffDetail(
        tester,
        _ticket(
          status: RefundReplacementStatus.approved,
          type: RefundReplacementType.replacement,
          includeDecision: true,
        ),
      );
      expect(
        find.text('Replace unsupported request with Refund request'),
        findsOneWidget,
      );

      // Authoritative delivery status Delivered: approved and order-backed
      // but the action stays hidden (fails closed on non-Failed).
      await _pumpStaffDetail(
        tester,
        _ticket(
          status: RefundReplacementStatus.approved,
          type: RefundReplacementType.replacement,
          includeDecision: true,
          deliveryStatus: DeliveryStatus.delivered,
        ),
      );
      expect(
        find.text('Replace unsupported request with Refund request'),
        findsNothing,
      );

      // Superseded: linkage shown inline, no action offered.
      await _pumpStaffDetail(
        tester,
        _ticket(
          status: RefundReplacementStatus.superseded,
          type: RefundReplacementType.replacement,
          includeDecision: true,
          supersededByRequestNumber: 'RR/000011',
        ),
      );
      expect(find.text('Superseded by Refund'), findsOneWidget);
      expect(find.text('RR/000011'), findsWidgets);
      expect(
        find.textContaining(
            'was superseded by a Refund request for the same delivery'),
        findsOneWidget,
      );
      expect(
        find.text('Replace unsupported request with Refund request'),
        findsNothing,
      );
      // The superseded ticket is never described as refunded or completed.
      expect(find.text('Completed'), findsNothing);
      expect(find.text('Refunded'), findsNothing);
    });

    testWidgets('requires a nonblank justification before confirming',
        (tester) async {
      final controller = _RecordingRemediationController(
        RefundReplacementState(
          selectedStaffRequest: _ticket(
            status: RefundReplacementStatus.approved,
            type: RefundReplacementType.replacement,
            includeDecision: true,
          ),
        ),
      );
      await _pumpStaffDetail(tester, null, controller: controller);

      await tester.tap(
        find.text('Replace unsupported request with Refund request'),
      );
      await tester.pumpAndSettle();

      // Confirmation names the order, delivery, and ticket, explains the
      // policy, and disclaims any refund.
      expect(find.text('Replace with Refund request'), findsWidgets);
      expect(find.text('Order ORD-1001'), findsOneWidget);
      expect(find.text('Delivery DEL-2001'), findsOneWidget);
      expect(find.text('Ticket RR-2026-0001'), findsOneWidget);
      expect(
        find.textContaining('does not issue a refund'),
        findsOneWidget,
      );
      final confirm = find.widgetWithText(FilledButton, 'Replace with Refund');
      expect(confirm, findsOneWidget);
      // Disabled until staff type a justification.
      expect(tester.widget<FilledButton>(confirm).onPressed, isNull);

      await tester.enterText(find.byType(TextField), '   ');
      await tester.pump();
      expect(tester.widget<FilledButton>(confirm).onPressed, isNull);

      await tester.enterText(
        find.byType(TextField),
        'Replacement unsupported for one-time orders.',
      );
      await tester.pump();
      expect(tester.widget<FilledButton>(confirm).onPressed, isNotNull);
    });

    testWidgets('cancelling the dialog runs nothing', (tester) async {
      final controller = _RecordingRemediationController(
        RefundReplacementState(
          selectedStaffRequest: _ticket(
            status: RefundReplacementStatus.approved,
            type: RefundReplacementType.replacement,
            includeDecision: true,
          ),
        ),
      );
      await _pumpStaffDetail(tester, null, controller: controller);

      await tester.tap(
        find.text('Replace unsupported request with Refund request'),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(controller.reasons, isEmpty);
    });

    testWidgets('successful remediation shows the new Refund number',
        (tester) async {
      final controller = _RecordingRemediationController(
        RefundReplacementState(
          selectedStaffRequest: _ticket(
            status: RefundReplacementStatus.approved,
            type: RefundReplacementType.replacement,
            includeDecision: true,
          ),
        ),
      );
      await _pumpStaffDetail(tester, null, controller: controller);
      // Discard the mount load so the assertions below cover only the
      // remediation flow: legacy reload, then the new ticket's own load.
      controller.reloadedStaffRequestIds.clear();

      await tester.tap(
        find.text('Replace unsupported request with Refund request'),
      );
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byType(TextField),
        'Replacement unsupported for one-time orders.',
      );
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Replace with Refund'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));

      expect(
        controller.reasons,
        ['Replacement unsupported for one-time orders.'],
      );
      // The legacy detail reloads, then the new Refund ticket opens
      // (whose own detail load follows on mount).
      expect(controller.reloadedStaffRequestIds, ['req-1', 'req-new']);
      // Success names the new Refund number and disclaims any payout.
      expect(find.textContaining('RR/000011'), findsWidgets);
      expect(
        find.textContaining('no refund has been issued yet'),
        findsOneWidget,
      );
    });

    testWidgets('conflict reloads authoritative state for another attempt',
        (tester) async {
      final controller = _RecordingRemediationController(
        RefundReplacementState(
          selectedStaffRequest: _ticket(
            status: RefundReplacementStatus.approved,
            type: RefundReplacementType.replacement,
            includeDecision: true,
          ),
          errorMessage: 'Ticket RR-2026-0001 can no longer be remediated.',
        ),
        pair: null,
      );
      await _pumpStaffDetail(tester, null, controller: controller);
      controller.reloadedStaffRequestIds.clear();

      await tester.tap(
        find.text('Replace unsupported request with Refund request'),
      );
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), 'Too late.');
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Replace with Refund'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));

      // The conflict message surfaces and the detail reloads so the next
      // attempt starts from authoritative state — never retried blindly.
      expect(
        find.text('Ticket RR-2026-0001 can no longer be remediated.'),
        findsWidgets,
      );
      expect(controller.reloadedStaffRequestIds, ['req-1']);
      FocusManager.instance.primaryFocus?.unfocus();
      await tester.pump();
    });

    testWidgets('refund half links back to the superseded ticket',
        (tester) async {
      await _pumpStaffDetail(
        tester,
        _ticket(
          status: RefundReplacementStatus.pending,
          type: RefundReplacementType.refund,
          supersedesRequestNumber: 'RR/000002',
          supersedesRequestPublicId: 'req-legacy',
        ),
      );
      expect(find.text('Supersedes'), findsOneWidget);
      expect(find.text('RR/000002'), findsOneWidget);
      // The standard Pending order-refund approval affordances are
      // unchanged: deduction decision first, wallet only on completion.
      expect(find.text('Approve refund decision'), findsOneWidget);
      expect(find.text('Reject'), findsOneWidget);
    });
  });

  group('customer remediation display', () {
    testWidgets('superseded ticket explains the Refund remedy', (tester) async {
      await _pumpCustomerDetail(
        tester,
        _ticket(
          status: RefundReplacementStatus.superseded,
          type: RefundReplacementType.replacement,
          includeDecision: true,
          supersededByRequestNumber: 'RR/000011',
        ),
      );
      expect(find.text('Superseded by Refund'), findsOneWidget);
      expect(find.text('RR/000011'), findsOneWidget);
      expect(
        find.textContaining('was superseded by a Refund request'),
        findsOneWidget,
      );
      // Never presented as a successful refund: the only mention of an
      // issued refund is the explicit disclaimer that none was issued.
      expect(
        find.textContaining('No refund has been issued by this change'),
        findsOneWidget,
      );
      expect(find.textContaining('refunded to your wallet'), findsNothing);
    });
  });
}

/// Full detail JSON with per-test overrides, mirroring the backend's
/// `RefundReplacementRequestResult` shape.
Map<String, dynamic> _ticketJson(Map<String, dynamic> overrides) => {
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
      'reason': 'Missed the delivery call',
      'remarks': null,
      'submittedAt': '2026-08-17T09:00:00Z',
      'deadline': '2026-08-19T09:00:00Z',
      'decidedByUserId': null,
      'decidedByName': null,
      'decidedAt': null,
      'decisionRemarks': null,
      'completedByUserId': null,
      'completedByName': null,
      'completedAt': null,
      'completionRemarks': null,
      'proofImageRequired': false,
      'images': const [],
      ...overrides,
    };

Map<String, dynamic> _listItemJson(Map<String, dynamic> overrides) => {
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
      'reason': 'Missed the delivery call',
      'remarks': null,
      'submittedAt': '2026-08-17T09:00:00Z',
      'deadline': '2026-08-19T09:00:00Z',
      'proofImageRequired': false,
      'imageCount': 0,
      ...overrides,
    };

RefundReplacementRequest _ticket({
  RefundReplacementStatus status = RefundReplacementStatus.pending,
  RefundReplacementType type = RefundReplacementType.refund,
  bool includeDecision = false,
  String? supersedesRequestNumber,
  String? supersedesRequestPublicId,
  String? supersededByRequestNumber,
  // Stage 2.1: the linked delivery's authoritative status. Defaults to
  // Failed (the remediation shape); null omits the key to simulate a
  // predated payload, which must fail closed.
  DeliveryStatus? deliveryStatus = DeliveryStatus.failed,
}) =>
    RefundReplacementRequest.fromJson(_ticketJson({
      'status': _wireStatus(status),
      'type': type == RefundReplacementType.refund ? 'Refund' : 'Replacement',
      'deliveryStatus': deliveryStatus == null
          ? null
          : _wireDeliveryStatus(deliveryStatus),
      if (includeDecision) 'decidedByUserId': 9,
      if (includeDecision) 'decidedByName': 'Support Lead',
      if (includeDecision) 'decidedAt': '2026-08-17T11:00:00Z',
      'supersedesRequestNumber': supersedesRequestNumber,
      'supersedesRequestPublicId': supersedesRequestPublicId,
      'supersededByRequestNumber': supersededByRequestNumber,
    }));

String _wireDeliveryStatus(DeliveryStatus status) => switch (status) {
      DeliveryStatus.readyForAssignment => 'ReadyForAssignment',
      DeliveryStatus.assigned => 'Assigned',
      DeliveryStatus.pickedUp => 'PickedUp',
      DeliveryStatus.outForDelivery => 'OutForDelivery',
      DeliveryStatus.arrived => 'Arrived',
      DeliveryStatus.delivered => 'Delivered',
      DeliveryStatus.failed => 'Failed',
      DeliveryStatus.cancelled => 'Cancelled',
      DeliveryStatus.unknown => 'Archived',
    };

String _wireStatus(RefundReplacementStatus status) => switch (status) {
      RefundReplacementStatus.pending => 'Pending',
      RefundReplacementStatus.awaitingCustomerChoice =>
        'AwaitingCustomerChoice',
      RefundReplacementStatus.approved => 'Approved',
      RefundReplacementStatus.rejected => 'Rejected',
      RefundReplacementStatus.completed => 'Completed',
      RefundReplacementStatus.failed => 'Failed',
      RefundReplacementStatus.cancelled => 'Cancelled',
      RefundReplacementStatus.superseded => 'Superseded',
      RefundReplacementStatus.unknown => 'Pending',
    };

RemediationPair _pair() => RemediationPair(
      legacyTicket: RefundReplacementRequest.fromJson(_ticketJson({
        'requestId': 'req-legacy',
        'requestNumber': 'RR/000002',
        'status': 'Superseded',
        'type': 'Replacement',
        'decidedByUserId': 9,
        'decidedByName': 'Support Lead',
        'decidedAt': '2026-08-17T11:00:00Z',
        'supersededByRequestNumber': 'RR/000011',
      })),
      refundTicket: RefundReplacementRequest.fromJson(_ticketJson({
        'requestId': 'req-new',
        'requestNumber': 'RR/000011',
        'status': 'Pending',
        'type': 'Refund',
        'supersedesRequestNumber': 'RR/000002',
        'supersedesRequestPublicId': 'req-legacy',
      })),
    );

http.Response _ok(Object data) => http.Response(
      jsonEncode({'success': true, 'data': data, 'errors': const []}),
      200,
      headers: {'content-type': 'application/json'},
    );

class _FakeRemediationRepository extends RefundReplacementRepository {
  _FakeRemediationRepository({this.failure})
      : super(api: _unusedApi);

  final Object? failure;

  static final _unusedApi = ApiClient(
    client: MockClient((_) async => _ok({})),
    baseUrl: 'https://api.example.test',
  );

  String? lastRequestId;
  String? lastReason;
  int calls = 0;

  @override
  Future<RemediationPair> remediate(
    String token,
    String requestId, {
    required String reason,
  }) async {
    calls++;
    lastRequestId = requestId;
    lastReason = reason;
    if (failure != null) throw failure!;
    return _pair();
  }

  @override
  Future<RefundReplacementPage> listForStaff(
    String token, {
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) =>
      throw UnimplementedError();
}

class _StaffAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _managerSession;
}

/// Controller stuck mid-save to prove duplicate submissions are refused.
class _SavingRemediationController extends RefundReplacementController {
  @override
  RefundReplacementState build() =>
      const RefundReplacementState(isSaving: true);
}

Future<ProviderContainer> _authenticatedContainer(
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

Future<void> _pumpStaffDetail(
  WidgetTester tester,
  RefundReplacementRequest? seed, {
  _RecordingRemediationController? controller,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  // Release dialog text-field focus so the next pump starts clean.
  addTearDown(() => FocusManager.instance.primaryFocus?.unfocus());
  final seeded = controller ??
      _RecordingRemediationController(
        RefundReplacementState(selectedStaffRequest: seed),
      );
  final router = GoRouter(
    initialLocation: '/staff/refund-replacements/req-1',
    routes: [
      GoRoute(
        path: '/staff/refund-replacements/:requestId',
        builder: (context, state) => StaffRefundReplacementDetailScreen(
          requestId: state.pathParameters['requestId']!,
        ),
      ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      // Fresh key per pump so re-seeded state is actually applied.
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(() => seeded),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: _managerSession),
        ),
      ],
      child: MaterialApp.router(routerConfig: router),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _pumpCustomerDetail(
  WidgetTester tester,
  RefundReplacementRequest request,
) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  addTearDown(() => FocusManager.instance.primaryFocus?.unfocus());
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(
          () => _RecordingRemediationController(
            RefundReplacementState(selectedCustomerRequest: request),
          ),
        ),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: _customerSession),
        ),
      ],
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: const CustomerRefundReplacementDetailScreen(
          requestId: 'req-1',
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

/// Seeded controller that records remediation calls and reloads instead of
/// touching the network.
class _RecordingRemediationController extends RefundReplacementController {
  _RecordingRemediationController(this.seed, {this.pair = _unset});

  final RefundReplacementState seed;

  /// Null [pair] simulates a backend conflict/failure (uses the seed's
  /// error message); [_unset] builds the success pair lazily.
  static const _unset = 'unset';
  final Object? pair;

  final List<String> reasons = [];
  final List<String> reloadedStaffRequestIds = [];

  @override
  RefundReplacementState build() => seed;

  @override
  Future<void> loadStaffRequest(String requestId) async {
    reloadedStaffRequestIds.add(requestId);
  }

  @override
  Future<RemediationPair?> remediateLegacyReplacement(
    String requestId, {
    required String reason,
  }) async {
    reasons.add(reason);
    if (identical(pair, _unset)) return _pair();
    return pair as RemediationPair?;
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({required this.session});

  final AuthSession session;

  @override
  SessionState build() => SessionState.authenticated(session);
}

final _managerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Support Lead',
    email: null,
    mobile: '9999999999',
    roles: ['CUSTOMER_SUPPORT'],
    permissions: [
      'REFUND_REPLACEMENT.READ',
      'REFUND_REPLACEMENT.MANAGE_BRANCH',
    ],
    branchIds: [7],
  ),
  accessToken: 'manager-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _customerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'cust-1',
    displayName: 'Asha Rao',
    email: null,
    mobile: '9876543210',
    roles: ['CUSTOMER'],
    permissions: ['REFUND_REPLACEMENT.READ'],
    branchIds: [7],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
