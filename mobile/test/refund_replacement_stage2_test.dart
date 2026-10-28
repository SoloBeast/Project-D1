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
  group('stage 2 models', () {
    test('parses the persisted deduction decision with legacy defaults', () {
      final decided = RefundReplacementRequest.fromJson(
        _requestJson(
          appliedDeductionPercentage: 10,
          deductionWaived: false,
        ),
      );
      expect(decided.appliedDeductionPercentage, 10);
      expect(decided.deductionWaived, isFalse);
      expect(decided.deductionWaiverReason, isNull);
      expect(decided.isOrderRefundTicket, isTrue);

      final waived = RefundReplacementRequest.fromJson(
        _requestJson(
          appliedDeductionPercentage: 0,
          deductionWaived: true,
          deductionWaiverReason: 'First failure goodwill',
        ),
      );
      expect(waived.appliedDeductionPercentage, 0);
      expect(waived.deductionWaived, isTrue);
      expect(waived.deductionWaiverReason, 'First failure goodwill');

      // Payloads predating Stage 1 keep working with safe defaults.
      final legacy = RefundReplacementRequest.fromJson(_requestJson());
      expect(legacy.appliedDeductionPercentage, isNull);
      expect(legacy.deductionWaived, isFalse);
      expect(legacy.deductionWaiverReason, isNull);
      expect(legacy.isOrderRefundTicket, isTrue);
    });

    test('isOrderRefundTicket is false for replacement requests', () {
      final replacement = RefundReplacementRequest.fromJson(
        _requestJson(type: 'Replacement'),
      );
      expect(replacement.type, RefundReplacementType.replacement);
      expect(replacement.isOrderRefundTicket, isFalse);
    });

    test('parses the authoritative preview verbatim with no-refund signal', () {
      final preview = OrderRefundPreview.fromJson(_previewJson());
      expect(preview.grossBasis, 200);
      expect(preview.appliedPercentage, 10);
      expect(preview.deductionAmount, 20);
      expect(preview.eligiblePaid, 200);
      expect(preview.proposedRefund, 180);
      expect(preview.currency, 'INR');
      expect(preview.hasNoRefund, isFalse);

      final empty = OrderRefundPreview.fromJson(
        _previewJson(proposedRefund: 0, eligiblePaid: 0),
      );
      expect(empty.hasNoRefund, isTrue);
    });
  });

  group('stage 2 repository wire format', () {
    test('approve sends the decision contract and previews the exact route', () async {
      final bodies = <String, Map<String, dynamic>>{};
      final queries = <String>[];
      final client = MockClient((request) async {
        if (request.method == 'GET') {
          queries.add(request.url.toString());
          return _success(_previewJson());
        }
        bodies[request.url.path] = jsonDecode(request.body);
        return _success(_requestJson(appliedDeductionPercentage: 10));
      });
      final repository = RefundReplacementRepository(
        api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
      );

      await repository.approve(
        'token',
        'req-1',
        applyDeduction: true,
      );
      expect(bodies['/api/v1/refund-replacements/req-1/approve'], {
        'remarks': null,
        'applyDeduction': true,
      });

      await repository.approve(
        'token',
        'req-1',
        applyDeduction: false,
        waiverReason: 'Goodwill',
      );
      expect(bodies['/api/v1/refund-replacements/req-1/approve'], {
        'remarks': null,
        'applyDeduction': false,
        'waiverReason': 'Goodwill',
      });

      // Legacy (subscription) approvals stay byte-identical: no new keys.
      await repository.approve('token', 'req-1', remarks: 'Reviewed');
      expect(bodies['/api/v1/refund-replacements/req-1/approve'], {
        'remarks': 'Reviewed',
      });

      final preview = await repository.getOrderRefundPreview(
        'token',
        'req-1',
        applyDeduction: true,
      );
      expect(
        queries.single,
        'https://api.example.test/api/v1/refund-replacements/req-1/order-refund-preview?applyDeduction=true',
      );
      expect(preview.proposedRefund, 180);

      await repository.getOrderRefundPreview(
        'token',
        'req-1',
        applyDeduction: false,
      );
      expect(
        queries.last,
        endsWith('order-refund-preview?applyDeduction=false'),
      );
    });
  });

  group('stage 2 controller', () {
    test('approve forwards the decision and preview caches authoritatively', () async {
      final repository = _FakeStage2Repository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(
        refundReplacementControllerProvider.notifier,
      );

      expect(
        await controller.approve(
          'req-1',
          applyDeduction: true,
        ),
        isTrue,
      );
      expect(repository.lastApplyDeduction, isTrue);
      expect(repository.lastWaiverReason, isNull);

      expect(
        await controller.approve(
          'req-1',
          applyDeduction: false,
          waiverReason: 'Goodwill',
        ),
        isTrue,
      );
      expect(repository.lastApplyDeduction, isFalse);
      expect(repository.lastWaiverReason, 'Goodwill');

      final preview = await controller.previewOrderRefund(
        'req-1',
        applyDeduction: true,
      );
      expect(preview?.proposedRefund, 180);
      expect(
        container
            .read(refundReplacementControllerProvider)
            .orderRefundPreview
            ?.appliedPercentage,
        10,
      );
      expect(
        container.read(refundReplacementControllerProvider).isPreviewLoading,
        isFalse,
      );
      expect(repository.previewDecisions, ['apply']);
    });

    test('preview failure surfaces the server message without caching', () async {
      final repository = _FakeStage2Repository()
        ..previewFailure = const ApiException(
          409,
          'CONFLICT',
          'The request was changed by another operation.',
        );
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(
        refundReplacementControllerProvider.notifier,
      );

      expect(
        await controller.previewOrderRefund('req-1', applyDeduction: true),
        isNull,
      );
      final state = container.read(refundReplacementControllerProvider);
      expect(state.orderRefundPreview, isNull);
      expect(state.isPreviewLoading, isFalse);
      expect(
        state.errorMessage,
        'The request was changed by another operation.',
      );
    });
  });

  group('stage 2 staff widgets', () {
    testWidgets('waive requires a reason and submits 0% semantics', (
      tester,
    ) async {
      final repository = _FakeStage2Repository();
      final controller = _SeededStage2Controller(
        RefundReplacementState(
          selectedStaffRequest: _orderTicket(
            status: RefundReplacementStatus.pending,
          ),
        ),
      );
      await _pumpStaff(tester, controller, repository: repository);

      await tester.tap(
        find.widgetWithText(FilledButton, 'Approve refund decision'),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Waive deduction'));
      await tester.pumpAndSettle();
      // Waived preview renders 0% with the full-amount proposal.
      expect(find.textContaining('0.00%'), findsOneWidget);
      expect(find.textContaining('200.00 INR'), findsWidgets);
      // No settlement method is preselected: support must consciously
      // choose one before the approval can be submitted.
      await tester.tap(find.text('Wallet refund'));
      await tester.pumpAndSettle();

      // Submitting without a waiver reason is refused locally.
      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(
            FilledButton,
            'Approve refund decision',
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(
        find.text(
          'A waiver reason is required when waiving the deduction.',
        ),
        findsOneWidget,
      );
      expect(controller.decidedAction, isNull);

      await tester.enterText(
        find.widgetWithText(TextField, 'Waiver reason (required)'),
        'First failure goodwill',
      );
      await tester.pump();
      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(
            FilledButton,
            'Approve refund decision',
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(controller.decidedAction, 'approve');
      expect(controller.decidedApplyDeduction, isFalse);
      expect(controller.decidedWaiverReason, 'First failure goodwill');
      expect(
        find.textContaining('Refund decision approved.'),
        findsOneWidget,
      );
    });

    testWidgets('switching decisions refreshes the preview', (tester) async {
      final repository = _FakeStage2Repository();
      final controller = _SeededStage2Controller(
        RefundReplacementState(
          selectedStaffRequest: _orderTicket(
            status: RefundReplacementStatus.pending,
          ),
        ),
      );
      await _pumpStaff(tester, controller, repository: repository);

      await tester.tap(
        find.widgetWithText(FilledButton, 'Approve refund decision'),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Apply deduction'));
      await tester.pumpAndSettle();
      expect(find.textContaining('180.00 INR'), findsOneWidget);

      await tester.tap(find.text('Waive deduction'));
      await tester.pumpAndSettle();
      expect(find.textContaining('200.00 INR'), findsWidgets);
      expect(repository.previewDecisions, ['apply', 'waive']);
    });

    testWidgets('zero preview explains without false success', (tester) async {
      final repository = _FakeStage2Repository()..zeroPreview = true;
      final controller = _SeededStage2Controller(
        RefundReplacementState(
          selectedStaffRequest: _orderTicket(
            status: RefundReplacementStatus.pending,
          ),
        ),
      );
      await _pumpStaff(tester, controller, repository: repository);

      await tester.tap(
        find.widgetWithText(FilledButton, 'Approve refund decision'),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Apply deduction'));
      await tester.pumpAndSettle();

      expect(
        find.textContaining('shows no refundable amount'),
        findsOneWidget,
      );
      expect(find.textContaining('Proposed wallet refund'), findsOneWidget);
      // The dialog never claims a completed credit for a zero preview.
      expect(find.textContaining('credited to your wallet'), findsNothing);
    });

    testWidgets('persisted decision and completion show after reload', (
      tester,
    ) async {
      await _pumpStaff(
        tester,
        _SeededStage2Controller(
          RefundReplacementState(
            selectedStaffRequest: _orderTicket(
              status: RefundReplacementStatus.approved,
              appliedDeductionPercentage: 10,
              includeDecision: true,
            ),
          ),
        ),
      );

      expect(find.textContaining('10.00% applied'), findsOneWidget);
      expect(find.text('Complete and credit wallet'), findsOneWidget);
      // Approval alone is never presented as a wallet credit.
      expect(find.textContaining('credited to your wallet'), findsNothing);

      await _pumpStaff(
        tester,
        _SeededStage2Controller(
          RefundReplacementState(
            selectedStaffRequest: _orderTicket(
              status: RefundReplacementStatus.completed,
              appliedDeductionPercentage: 0,
              deductionWaived: true,
              deductionWaiverReason: 'Goodwill',
              includeDecision: true,
              completedAtUtc: DateTime.utc(2026, 8, 18, 10),
              completionRemarks: 'Refund of 200.00 credited to wallet.',
            ),
          ),
        ),
      );
      expect(find.text('Customer-fault deduction'), findsOneWidget);
      expect(find.textContaining('Waived (0%)'), findsOneWidget);
      expect(find.textContaining('credited to wallet'), findsWidgets);
    });

    testWidgets('failed settlement keeps the ticket and shows the error once', (
      tester,
    ) async {
      final controller = _SeededStage2Controller(
        RefundReplacementState(
          selectedStaffRequest: _orderTicket(
            status: RefundReplacementStatus.approved,
            appliedDeductionPercentage: 10,
            includeDecision: true,
          ),
        ),
      )..completeFailure = const ApiException(
        409,
        'CONFLICT',
        'The request was changed by another operation. Refresh and try again.',
      );
      await _pumpStaff(tester, controller);

      await tester.tap(find.text('Complete and credit wallet'));
      await tester.pumpAndSettle();
      // Confirm dialog names the settlement explicitly.
      expect(find.text('Complete and credit wallet'), findsWidgets);
      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(
            FilledButton,
            'Complete and credit wallet',
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Exactly one attempt (no indefinite retry), error shown verbatim, and
      // the ticket stays Approved — still offering completion.
      expect(controller.completeCalls, 1);
      expect(
        find.textContaining('changed by another operation'),
        findsOneWidget,
      );
      expect(find.text('Complete and credit wallet'), findsOneWidget);
      expect(find.textContaining('credited to your wallet'), findsNothing);
    });

    testWidgets('order tickets never offer replacement actions', (
      tester,
    ) async {
      await _pumpStaff(
        tester,
        _SeededStage2Controller(
          RefundReplacementState(
            selectedStaffRequest: _orderTicket(
              status: RefundReplacementStatus.pending,
            ),
          ),
        ),
      );

      expect(find.textContaining('Refund only'), findsWidgets);
      expect(find.text('Replacement'), findsNothing);
      expect(find.text('Re-delivery'), findsNothing);
      expect(find.text('Re-Delivery'), findsNothing);
    });
  });
}

Map<String, dynamic> _previewJson({
  double proposedRefund = 180,
  double eligiblePaid = 200,
}) => {
  'requestId': 'req-1',
  'requestNumber': 'RR-2026-0001',
  'orderPublicId': 'order-pub-1',
  'orderNumber': 'ORD-1001',
  'grossBasis': 200,
  'appliedPercentage': 10,
  'deductionAmount': 20,
  'eligiblePaid': eligiblePaid,
  'proposedRefund': proposedRefund,
  'currency': 'INR',
};

Map<String, dynamic> _requestJson({
  String type = 'Refund',
  double? appliedDeductionPercentage,
  bool? deductionWaived,
  String? deductionWaiverReason,
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
  'type': type,
  'source': 'CustomerPostDelivery',
  'status': 'Pending',
  'reason': 'Customer missed the delivery call',
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
  'images': const <Map<String, dynamic>>[],
  if (appliedDeductionPercentage case final double percentage)
    'appliedDeductionPercentage': percentage,
  if (deductionWaived case final bool waived) 'deductionWaived': waived,
  if (deductionWaiverReason case final String waiverReason)
    'deductionWaiverReason': waiverReason,
};

http.Response _success(Object? data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

RefundReplacementRequest _orderTicket({
  RefundReplacementStatus status = RefundReplacementStatus.pending,
  double? appliedDeductionPercentage,
  bool deductionWaived = false,
  String? deductionWaiverReason,
  bool includeDecision = false,
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
  source: RefundReplacementSource.customerPostDelivery,
  status: status,
  reason: 'Customer missed the delivery call',
  remarks: null,
  submittedAtUtc: DateTime.utc(2026, 8, 17, 9),
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  decidedByUserId: includeDecision ? 9 : null,
  decidedByName: includeDecision ? 'Support Lead' : null,
  decidedAtUtc: includeDecision ? DateTime.utc(2026, 8, 17, 11) : null,
  decisionRemarks: includeDecision ? 'Reviewed by support' : null,
  completedByUserId: completedAtUtc == null ? null : 9,
  completedByName: completedAtUtc == null ? null : 'Support Lead',
  completedAtUtc: completedAtUtc,
  completionRemarks: completionRemarks,
  proofImageRequired: false,
  images: const [],
  appliedDeductionPercentage: appliedDeductionPercentage,
  deductionWaived: deductionWaived,
  deductionWaiverReason: deductionWaiverReason,
);

Future<ProviderContainer> _authenticatedContainer(
  RefundReplacementRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      refundReplacementRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _stage2Session;
}

final _stage2Session = AuthSession(
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

class _FakeStage2Repository extends RefundReplacementRepository {
  _FakeStage2Repository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  bool? lastApplyDeduction;
  String? lastWaiverReason;
  String? lastSettlementMethod;
  bool zeroPreview = false;
  Object? previewFailure;
  final List<String> previewDecisions = [];

  OrderRefundPreview _previewFor(bool apply) => OrderRefundPreview(
    requestId: 'req-1',
    requestNumber: 'RR-2026-0001',
    orderPublicId: 'order-pub-1',
    orderNumber: 'ORD-1001',
    grossBasis: 200,
    appliedPercentage: apply ? 10 : 0,
    deductionAmount: apply ? 20 : 0,
    eligiblePaid: zeroPreview ? 0 : 200,
    proposedRefund: zeroPreview ? 0 : (apply ? 180 : 200),
    currency: 'INR',
  );

  @override
  Future<RefundReplacementRequest> approve(
    String token,
    String requestId, {
    String? remarks,
    DateTime? replacementDate,
    bool? applyDeduction,
    String? waiverReason,
    String? settlementMethod,
  }) async {
    lastApplyDeduction = applyDeduction;
    lastWaiverReason = waiverReason;
    lastSettlementMethod = settlementMethod;
    return _orderTicket(status: RefundReplacementStatus.approved);
  }

  @override
  Future<OrderRefundPreview> getOrderRefundPreview(
    String token,
    String requestId, {
    required bool applyDeduction,
  }) async {
    previewDecisions.add(applyDeduction ? 'apply' : 'waive');
    final failure = previewFailure;
    if (failure != null) throw failure;
    return _previewFor(applyDeduction);
  }
}

class _SeededStage2Controller extends RefundReplacementController {
  _SeededStage2Controller(this.seed);

  final RefundReplacementState seed;

  String? decidedAction;
  bool? decidedApplyDeduction;
  String? decidedWaiverReason;
  String? decidedSettlementMethod;
  int completeCalls = 0;
  Object? completeFailure;

  @override
  RefundReplacementState build() => seed;

  @override
  Future<bool> approve(
    String requestId, {
    String? remarks,
    DateTime? replacementDate,
    bool? applyDeduction,
    String? waiverReason,
    String? settlementMethod,
  }) async {
    decidedAction = 'approve';
    decidedApplyDeduction = applyDeduction;
    decidedWaiverReason = waiverReason;
    decidedSettlementMethod = settlementMethod;
    return true;
  }

  @override
  Future<bool> complete(String requestId, {String? remarks}) async {
    completeCalls++;
    final failure = completeFailure;
    if (failure != null) {
      state = state.copyWith(
        errorMessage: failure is ApiException
            ? failure.message
            : 'The refund could not be completed.',
      );
      return false;
    }
    decidedAction = 'complete';
    return true;
  }
}

Future<void> _pumpStaff(
  WidgetTester tester,
  _SeededStage2Controller controller, {
  _FakeStage2Repository? repository,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededStage2SessionController(),
        ),
        refundReplacementRepositoryProvider.overrideWithValue(
          repository ?? _FakeStage2Repository(),
        ),
      ],
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

class _SeededStage2SessionController extends SessionController {
  @override
  SessionState build() => SessionState.authenticated(_stage2Session);
}
