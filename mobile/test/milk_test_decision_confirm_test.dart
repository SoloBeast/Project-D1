import 'dart:async';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_controller.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_models.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_repository.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  group('customer milk-test decision confirmation', () {
    testWidgets('accept shows action-specific text and submits on confirm',
        (tester) async {
      final controller = _DecisionController(
        initialState: MilkTestState(customerTest: _completedTest()),
      );
      await _pump(
          tester,
          const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
          controller);

      await tester.tap(find.text('Confirm'));
      await tester.pumpAndSettle();

      expect(find.text('Confirm milk test?'), findsOneWidget);
      expect(
          find.text('Are you sure you want to accept this milk test result?'),
          findsOneWidget);

      await tester.tap(find.text('Confirm').last);
      await tester.pumpAndSettle();

      expect(controller.confirmCalls, 1);
      expect(controller.rejectCalls, 0);
      expect(controller.confirmedMilkTestId, 'test-1');
    });

    testWidgets('reject shows action-specific text and submits on confirm',
        (tester) async {
      final controller = _DecisionController(
        initialState: MilkTestState(customerTest: _completedTest()),
      );
      await _pump(
          tester,
          const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
          controller);

      await tester.tap(find.text('Reject'));
      await tester.pumpAndSettle();

      expect(find.text('Reject milk test?'), findsOneWidget);
      expect(
          find.text('Are you sure you want to reject this milk test result?'),
          findsOneWidget);

      await tester.tap(find.text('Reject').last);
      await tester.pumpAndSettle();

      expect(controller.rejectCalls, 1);
      expect(controller.confirmCalls, 0);
      expect(controller.rejectedMilkTestId, 'test-1');
      // A successful reject navigates to the refund/replacement stub route.
      expect(find.text('Refund stub'), findsOneWidget);
    });

    testWidgets('cancel dismisses without an API call', (tester) async {
      final controller = _DecisionController(
        initialState: MilkTestState(customerTest: _completedTest()),
      );
      await _pump(
          tester,
          const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
          controller);

      await tester.tap(find.text('Confirm'));
      await tester.pumpAndSettle();
      expect(find.text('Confirm milk test?'), findsOneWidget);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(controller.confirmCalls, 0);
      expect(controller.rejectCalls, 0);
      expect(find.text('Confirm milk test?'), findsNothing);
      // Decision controls remain visible after dismissal.
      expect(find.text('Confirm'), findsOneWidget);
      expect(find.text('Reject'), findsOneWidget);
    });

    testWidgets('prevents duplicate submissions while processing',
        (tester) async {
      final gate = Completer<bool>();
      final controller = _DecisionController(
        initialState: MilkTestState(customerTest: _completedTest()),
        confirmGate: gate.future,
      );
      await _pump(
          tester,
          const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
          controller);

      await tester.tap(find.text('Confirm'));
      await tester.pumpAndSettle();

      // Submit the dialog; the fake controller marks itself saving while the
      // gated call is in flight.
      await tester.tap(find.text('Confirm').last);
      await tester.pump();
      expect(controller.confirmCalls, 1);

      // While saving, the decision buttons are disabled so no second dialog
      // can open and no second call is issued. Fixed pumps are used because
      // the saving spinner animates continuously.
      await tester.tap(find.text('Confirm').first);
      await tester.pump(const Duration(milliseconds: 100));
      await tester.pump(const Duration(milliseconds: 100));
      expect(find.text('Confirm milk test?'), findsNothing);
      expect(controller.confirmCalls, 1);

      gate.complete(true);
      await tester.pumpAndSettle();
      expect(controller.confirmCalls, 1);
    });

    testWidgets('same confirmation applies to subscription deliveries',
        (tester) async {
      final controller = _DecisionController(
        initialState: MilkTestState(customerTest: _completedTest()),
      );
      await _pump(
          tester,
          const CustomerMilkTestScreen(
            deliveryId: 'delivery-1',
            sourceType: DeliverySourceType.subscriptionOccurrence,
          ),
          controller);

      await tester.tap(find.text('Reject'));
      await tester.pumpAndSettle();
      expect(
          find.text('Are you sure you want to reject this milk test result?'),
          findsOneWidget);
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(controller.rejectCalls, 0);

      // After cancelling the reject dialog the screen is still interactive,
      // so the accept path uses the same confirmation UI.
      await tester.tap(find.text('Confirm'));
      await tester.pumpAndSettle();
      expect(
          find.text('Are you sure you want to accept this milk test result?'),
          findsOneWidget);
      await tester.tap(find.text('Confirm').last);
      await tester.pumpAndSettle();
      expect(controller.confirmCalls, 1);
      expect(controller.rejectCalls, 0);
    });
  });
}

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  _DecisionController controller,
) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final router = GoRouter(
    initialLocation: '/test',
    routes: [
      GoRoute(path: '/test', builder: (context, state) => screen),
      GoRoute(
        path: '/deliveries/:deliveryId/refund-replacement',
        builder: (context, state) =>
            const Scaffold(body: Text('Refund stub')),
      ),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        milkTestControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(),
        ),
        milkTestRepositoryProvider.overrideWithValue(_FakeRepository()),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pumpAndSettle();
}

class _DecisionController extends MilkTestController {
  _DecisionController({required this.initialState, this.confirmGate});

  final MilkTestState initialState;
  final Future<bool>? confirmGate;
  int confirmCalls = 0;
  int rejectCalls = 0;
  String? confirmedMilkTestId;
  String? rejectedMilkTestId;

  @override
  MilkTestState build() => initialState;

  @override
  Future<void> loadForCustomer(String deliveryId) async {}

  @override
  Future<void> loadForStaff(String deliveryId) async {}

  @override
  Future<void> loadForBranch(String deliveryId) async {}

  @override
  Future<bool> confirm(String milkTestId, {String? remarks}) async {
    confirmCalls++;
    confirmedMilkTestId = milkTestId;
    final gate = confirmGate;
    if (gate != null) {
      // Mirror the real controller: mark saving while the call is in flight
      // so the screen disables its decision buttons.
      state = state.copyWith(isSaving: true);
      try {
        return await gate;
      } finally {
        state = state.copyWith(isSaving: false);
      }
    }
    return true;
  }

  @override
  Future<bool> reject(String milkTestId, {String? remarks}) async {
    rejectCalls++;
    rejectedMilkTestId = milkTestId;
    return true;
  }
}

class _SeededSessionController extends SessionController {
  @override
  SessionState build() => SessionState.authenticated(_session);
}

class _FakeRepository extends MilkTestRepository {
  _FakeRepository() : super(api: ApiClient(baseUrl: 'http://localhost.test'));
}

CustomerMilkTest _completedTest() => CustomerMilkTest(
      milkTestId: 'test-1',
      deliveryId: 'delivery-1',
      status: MilkTestStatus.completed,
      customerDecision: MilkTestCustomerDecision.pending,
      requestedAtUtc: DateTime.utc(2026, 8, 17, 9),
      completedAtUtc: DateTime.utc(2026, 8, 17, 9, 10),
      confirmedAtUtc: null,
      rejectedAtUtc: null,
      cancelledAtUtc: null,
      customerRemarks: null,
      images: const [],
    );

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Customer',
    email: null,
    mobile: '9999999999',
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'token',
  refreshToken: 'refresh',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
