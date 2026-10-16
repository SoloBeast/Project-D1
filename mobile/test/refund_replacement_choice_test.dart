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
  group('D1-B customer choice models', () {
    test('parses awaiting status and the explicit customer choice', () {
      expect(
        RefundReplacementStatus.parse('AwaitingCustomerChoice'),
        RefundReplacementStatus.awaitingCustomerChoice,
      );
      expect(
        RefundReplacementStatus.awaitingCustomerChoice.label,
        'Awaiting choice',
      );
      expect(
        RefundReplacementStatus.awaitingCustomerChoice.isAwaitingChoice,
        isTrue,
      );
      expect(RefundReplacementStatus.pending.isAwaitingChoice, isFalse);
      expect(
        RefundReplacementStatus.awaitingCustomerChoice.isTerminal,
        isFalse,
      );

      expect(CustomerChoice.parse('Refund'), CustomerChoice.refund);
      expect(CustomerChoice.parse('ReDelivery'), CustomerChoice.reDelivery);
      expect(CustomerChoice.parse(null), isNull);
      expect(CustomerChoice.parse('AnythingElse'), CustomerChoice.unknown);
      expect(CustomerChoice.refund.label, 'Refund');
      expect(CustomerChoice.reDelivery.label, 'Re-delivery');
      expect(CustomerChoice.refund.wireValue, 'Refund');
      expect(CustomerChoice.reDelivery.wireValue, 'ReDelivery');

      expect(
        RefundReplacementSource.parse('DeliveryFailed'),
        RefundReplacementSource.deliveryFailed,
      );
      expect(RefundReplacementSource.deliveryFailed.label, 'Delivery failed');
    });

    test('parses a subscription delivery-fail ticket with nullable order', () {
      final ticket = RefundReplacementRequest.fromJson(
        _deliveryFailJson(customerChoice: null),
      );

      expect(ticket.status, RefundReplacementStatus.awaitingCustomerChoice);
      expect(ticket.orderId, isNull);
      expect(ticket.orderPublicId, isNull);
      expect(ticket.orderNumber, isNull);
      expect(ticket.deliveryNumber, 'DEL-2001');
      expect(ticket.customerChoice, isNull);
      expect(ticket.customerChoiceAtUtc, isNull);
      expect(ticket.failureReasonSnapshot, 'Customer not available at door');
      expect(ticket.source, RefundReplacementSource.deliveryFailed);
      expect(ticket.isAwaitingChoice, isTrue);
      expect(ticket.isChoiceExpired, isFalse);

      final chosen = RefundReplacementRequest.fromJson(
        _deliveryFailJson(customerChoice: 'Refund'),
      );
      expect(chosen.customerChoice, CustomerChoice.refund);
      expect(chosen.customerChoiceAtUtc?.isUtc, isTrue);
      expect(chosen.isAwaitingChoice, isFalse);
    });

    test('flags an expired choice window without resolving the ticket', () {
      final expired = RefundReplacementRequest.fromJson(
        _deliveryFailJson(
          customerChoice: null,
          deadline: '2020-01-01T00:00:00Z',
          status: 'AwaitingCustomerChoice',
        ),
      );
      expect(expired.isChoiceExpired, isTrue);
      expect(expired.isAwaitingChoice, isTrue);
      expect(expired.status, RefundReplacementStatus.awaitingCustomerChoice);
    });
  });

  group('D1-B choice repository', () {
    test('records Refund on the exact customer choose route', () async {
      String? path;
      Map<String, dynamic>? body;
      final client = MockClient((request) async {
        path = request.url.path;
        expect(request.method, 'POST');
        expect(request.headers['Authorization'], 'Bearer refund-token');
        body = jsonDecode(request.body) as Map<String, dynamic>;
        return _success(_deliveryFailJson(customerChoice: 'Refund'));
      });

      final result = await _repository(client)
          .choose('refund-token', 'req-1', CustomerChoice.refund);

      expect(path, '/api/v1/customer/refund-replacements/req-1/choose');
      expect(body, {'choice': 'Refund'});
      expect(result.customerChoice, CustomerChoice.refund);
    });

    test('records Re-Delivery with the backend wire value', () async {
      Map<String, dynamic>? body;
      final client = MockClient((request) async {
        body = jsonDecode(request.body) as Map<String, dynamic>;
        return _success(_deliveryFailJson(customerChoice: 'ReDelivery'));
      });

      final result = await _repository(client)
          .choose('refund-token', 'req-1', CustomerChoice.reDelivery);

      expect(body, {'choice': 'ReDelivery'});
      expect(result.customerChoice, CustomerChoice.reDelivery);
    });
  });

  group('D1-B choice controller', () {
    test('choosing updates the detail selection and its list row', () async {
      final repository = _ChooseFakeRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(
        refundReplacementControllerProvider.notifier,
      );

      await controller.loadCustomerRequests();
      expect(
        container
            .read(refundReplacementControllerProvider)
            .customerRequests
            .single
            .customerChoice,
        isNull,
      );

      expect(
        await controller.chooseCustomerChoice('req-1', CustomerChoice.refund),
        isTrue,
      );

      final state = container.read(refundReplacementControllerProvider);
      expect(state.isSaving, isFalse);
      expect(state.errorMessage, isNull);
      expect(
        state.selectedCustomerRequest?.customerChoice,
        CustomerChoice.refund,
      );
      expect(
        state.customerRequests.single.customerChoice,
        CustomerChoice.refund,
      );
      expect(repository.lastChoice, CustomerChoice.refund);
      expect(repository.chooseCalls, 1);
    });

    test(
      'a failed choice returns false and surfaces the server error',
      () async {
        final repository = _ChooseFakeRepository()
          ..chooseFailure = const ApiException(
            409,
            'CONFLICT',
            'Choice expired.',
          );
        final container = await _authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(
          refundReplacementControllerProvider.notifier,
        );

        expect(
          await controller.chooseCustomerChoice(
            'req-1',
            CustomerChoice.reDelivery,
          ),
          isFalse,
        );

        final state = container.read(refundReplacementControllerProvider);
        expect(state.isSaving, isFalse);
        expect(state.errorMessage, 'Choice expired.');
        expect(state.selectedCustomerRequest?.customerChoice, isNull);
      },
    );
  });

  group('D1-B choice screens', () {
    testWidgets(
      'awaiting ticket shows failure info, deadline, and both choices',
      (tester) async {
        await _pump(
          tester,
          const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededChoiceController(
            RefundReplacementState(selectedCustomerRequest: _awaitingTicket()),
          ),
        );

        expect(find.text('RR-2026-0001'), findsOneWidget);
        expect(find.text('DEL-2001'), findsOneWidget);
        expect(find.text('Customer not available at door'), findsOneWidget);
        expect(find.textContaining('Choose before'), findsOneWidget);
        expect(find.text('Refund'), findsOneWidget);
        expect(find.text('Re-Delivery'), findsOneWidget);
      },
    );

    testWidgets('selecting Refund records the choice and locks the panel', (
      tester,
    ) async {
      final controller = _SeededChoiceController(
        RefundReplacementState(selectedCustomerRequest: _awaitingTicket()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        controller,
      );

      await tester.tap(find.text('Refund'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));

      expect(controller.chosen, CustomerChoice.refund);
      expect(
        find.text('Your choice has been recorded. Waiting for approval.'),
        findsOneWidget,
      );
    });

    testWidgets('selecting Re-Delivery records the choice', (tester) async {
      final controller = _SeededChoiceController(
        RefundReplacementState(selectedCustomerRequest: _awaitingTicket()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        controller,
      );

      await tester.tap(find.text('Re-Delivery'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));

      expect(controller.chosen, CustomerChoice.reDelivery);
    });

    testWidgets('loading state shows progress and disables both choices', (
      tester,
    ) async {
      final controller = _SeededChoiceController(
        const RefundReplacementState(isSaving: true),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        controller,
        seedRequest: _awaitingTicket(),
        settle: false,
      );

      expect(find.byType(CircularProgressIndicator), findsWidgets);
      final refund = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Refund'),
      );
      final redelivery = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Re-Delivery'),
      );
      expect(refund.onPressed, isNull);
      expect(redelivery.onPressed, isNull);
      expect(controller.chosen, isNull);
    });

    testWidgets('recorded choice locks the actions and waits for approval', (
      tester,
    ) async {
      final controller = _SeededChoiceController(
        RefundReplacementState(
          selectedCustomerRequest: _awaitingTicket(
            choice: CustomerChoice.refund,
          ),
        ),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        controller,
      );

      expect(find.text('You chose Refund.'), findsOneWidget);
      expect(find.text('Waiting for approval'), findsOneWidget);
      final locked = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Refund'),
      );
      expect(locked.onPressed, isNull);
      final redelivery = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Re-Delivery'),
      );
      expect(redelivery.onPressed, isNull);
      expect(controller.chosen, isNull);
    });

    testWidgets('expired ticket disables choices without resolving', (
      tester,
    ) async {
      final controller = _SeededChoiceController(
        RefundReplacementState(
          selectedCustomerRequest: _awaitingTicket(
            deadlineUtc: DateTime.utc(2020, 1, 1),
          ),
        ),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        controller,
      );

      expect(
        find.textContaining('selection window has expired'),
        findsOneWidget,
      );
      final refund = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Refund'),
      );
      expect(refund.onPressed, isNull);
      expect(controller.chosen, isNull);
    });

    testWidgets('server error is shown inline on the choice panel', (
      tester,
    ) async {
      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededChoiceController(
          RefundReplacementState(
            selectedCustomerRequest: _awaitingTicket(),
            errorMessage: 'The customer choice window has expired.',
          ),
        ),
      );

      expect(
        find.text('The customer choice window has expired.'),
        findsOneWidget,
      );
    });

    testWidgets('deep link request id opens the matching ticket', (
      tester,
    ) async {
      final controller = _SeededChoiceController(
        RefundReplacementState(selectedCustomerRequest: _awaitingTicket()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        controller,
      );

      expect(find.text('RR-2026-0001'), findsOneWidget);
      expect(controller.loadedCustomerRequestId, 'req-1');
    });

    testWidgets('staff detail shows the recorded customer choice', (
      tester,
    ) async {
      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededChoiceController(
          RefundReplacementState(
            selectedStaffRequest: _awaitingTicket(
              choice: CustomerChoice.reDelivery,
            ),
          ),
        ),
        session: _managerSession,
      );

      expect(find.textContaining('Re-delivery'), findsWidgets);
    });
  });
}

Map<String, dynamic> _deliveryFailJson({
  String? customerChoice,
  String status = 'AwaitingCustomerChoice',
  String? deadline,
}) => {
  'requestId': 'req-1',
  'requestNumber': 'RR-2026-0001',
  'orderId': null,
  'orderPublicId': null,
  'orderNumber': null,
  'subscriptionDeliveryPublicId': 'sub-delivery-1',
  'failedAt': '2026-09-30T05:00:00Z',
  'failureReasonSnapshot': 'Customer not available at door',
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
  'source': 'DeliveryFailed',
  'status': status,
  'customerChoice': customerChoice,
  'customerChoiceAt': customerChoice == null ? null : '2026-09-30T06:00:00Z',
  'reason': 'Delivery failed: customer not available',
  'remarks': null,
  'submittedAt': '2026-09-30T05:05:00Z',
  'deadline': deadline ?? '2099-10-02T00:00:00Z',
  'decidedByUserId': null,
  'decidedByName': null,
  'decidedAt': null,
  'decisionRemarks': null,
  'completedByUserId': null,
  'completedByName': null,
  'completedAt': null,
  'completionRemarks': null,
  'proofImageRequired': true,
  'images': const [],
};

RefundReplacementRequest _awaitingTicket({
  CustomerChoice? choice,
  DateTime? deadlineUtc,
}) => RefundReplacementRequest(
  requestId: 'req-1',
  requestNumber: 'RR-2026-0001',
  orderId: null,
  orderPublicId: null,
  orderNumber: null,
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
  type: choice == CustomerChoice.reDelivery
      ? RefundReplacementType.replacement
      : RefundReplacementType.refund,
  source: RefundReplacementSource.deliveryFailed,
  status: RefundReplacementStatus.awaitingCustomerChoice,
  reason: 'Delivery failed: customer not available',
  remarks: null,
  submittedAtUtc: DateTime.utc(2026, 9, 30, 5, 5),
  deadlineUtc: deadlineUtc ?? DateTime.utc(2099, 10, 2),
  decidedByUserId: null,
  decidedByName: null,
  decidedAtUtc: null,
  decisionRemarks: null,
  completedByUserId: null,
  completedByName: null,
  completedAtUtc: null,
  completionRemarks: null,
  proofImageRequired: true,
  images: const [],
  subscriptionDeliveryPublicId: 'sub-delivery-1',
  failedAtUtc: DateTime.utc(2026, 9, 30, 5),
  failureReasonSnapshot: 'Customer not available at door',
  customerChoice: choice,
  customerChoiceAtUtc: choice == null ? null : DateTime.utc(2026, 9, 30, 6),
);

http.Response _success(Object? data) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': []}),
  200,
  headers: {'content-type': 'application/json'},
);

RefundReplacementRepository _repository(http.Client client) =>
    RefundReplacementRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
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
  Future<AuthSession?> restore() async => _session;
}

class _ChooseFakeRepository extends RefundReplacementRepository {
  _ChooseFakeRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int chooseCalls = 0;
  CustomerChoice? lastChoice;
  Object? chooseFailure;

  @override
  Future<List<RefundReplacementRequest>> listForCustomer(String token) async =>
      [_awaitingTicket()];

  @override
  Future<RefundReplacementRequest?> getForCustomer(
    String token,
    String requestId,
  ) async => _awaitingTicket();

  @override
  Future<RefundReplacementRequest> choose(
    String token,
    String requestId,
    CustomerChoice choice,
  ) async {
    chooseCalls++;
    lastChoice = choice;
    final failure = chooseFailure;
    if (failure != null) throw failure;
    return _awaitingTicket(choice: choice);
  }
}

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededChoiceController controller, {
  AuthSession? session,
  RefundReplacementRequest? seedRequest,
  bool settle = true,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final seed = controller.seed;
  final effective = seedRequest == null
      ? seed
      : seed.copyWith(selectedCustomerRequest: seedRequest);
  controller.seed = effective;
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: session),
        ),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pump();
  if (settle) {
    await tester.pumpAndSettle();
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({this.session});

  final AuthSession? session;

  @override
  SessionState build() => SessionState.authenticated(session ?? _session);
}

class _SeededChoiceController extends RefundReplacementController {
  _SeededChoiceController(this.seed);

  RefundReplacementState seed;
  CustomerChoice? chosen;
  String? loadedCustomerRequestId;

  @override
  RefundReplacementState build() => seed;

  @override
  Future<void> loadCustomerRequest(String requestId) async {
    loadedCustomerRequestId = requestId;
  }

  @override
  Future<void> loadStaffRequest(String requestId) async {
    loadedCustomerRequestId = requestId;
  }

  @override
  Future<bool> chooseCustomerChoice(
    String requestId,
    CustomerChoice choice,
  ) async {
    chosen = choice;
    return true;
  }
}

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'cust-1',
    displayName: 'Asha Rao',
    email: null,
    mobile: '9876543210',
    roles: ['CUSTOMER'],
    permissions: ['REFUND_REPLACEMENT.READ'],
    branchIds: [7],
  ),
  accessToken: 'refund-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

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
