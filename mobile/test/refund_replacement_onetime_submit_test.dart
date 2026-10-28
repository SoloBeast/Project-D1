import 'dart:typed_data';

import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

/// Stage 2.1: one-time order delivery failures support Refund only.
/// Replacement/Re-delivery stays available for subscription occurrences.
void main() {
  group('one-time order refund-only submission (stage 2.1)', () {
    testWidgets('a failed one-time order exposes only Refund', (tester) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(
          eligibility: _eligibility(proofImageRequired: false),
        ),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.oneTimeOrder,
        ),
        controller,
      );

      expect(
        find.text('Only Refund is supported for one-time order deliveries.'),
        findsOneWidget,
      );
      // No selectable Replacement option is offered.
      expect(find.text('Replacement'), findsNothing);
      expect(
        find.byType(SegmentedButton<RefundReplacementType>),
        findsNothing,
      );
      expect(find.text('Refund'), findsWidgets);
      expect(find.text('Submit request'), findsOneWidget);
      expect(controller.loadedEligibilityFor, 'delivery-1');
    });

    testWidgets('an already-refunded order explains instead of offering submit', (
      tester,
    ) async {
      const reason =
          'A refund has already been completed for order ORD-1. '
          'If you need further help, contact support.';
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.oneTimeOrder,
        ),
        _SeededRefundReplacementController(
          RefundReplacementState(
            eligibility: _eligibility(
              isEligible: false,
              ineligibleReason: reason,
            ),
          ),
        ),
      );

      expect(find.text('Request not available'), findsOneWidget);
      expect(find.text(reason), findsOneWidget);
      expect(find.text('Submit request'), findsNothing);
    });

    testWidgets('a subscription delivery failure keeps both choices', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(
          eligibility: _eligibility(proofImageRequired: false),
        ),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.subscriptionOccurrence,
        ),
        controller,
      );

      expect(
        find.byType(SegmentedButton<RefundReplacementType>),
        findsOneWidget,
      );
      expect(find.text('Refund'), findsOneWidget);
      expect(find.text('Replacement'), findsOneWidget);
      expect(
        find.text('Only Refund is supported for one-time order deliveries.'),
        findsNothing,
      );
    });

    testWidgets('one-time order submission sends the Refund request', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(
          eligibility: _eligibility(proofImageRequired: false),
        ),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.oneTimeOrder,
        ),
        controller,
      );

      await tester.enterText(find.byType(TextField).first, 'Milk was sour');
      await tester.pump();
      await tester.tap(find.text('Submit request'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));

      expect(controller.submittedType, RefundReplacementType.refund);
      expect(controller.submittedReason, 'Milk was sour');
    });

    testWidgets('subscription choice can select and submit Replacement', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(
          eligibility: _eligibility(proofImageRequired: false),
        ),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.subscriptionOccurrence,
        ),
        controller,
      );

      await tester.tap(find.text('Replacement'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField).first, 'Bottle leaked');
      await tester.pump();
      await tester.tap(find.text('Submit request'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));

      expect(controller.submittedType, RefundReplacementType.replacement);
      expect(controller.submittedReason, 'Bottle leaked');
    });

    testWidgets(
      'an unknown source fails safe to refund-only and submits Refund',
      (tester) async {
        for (final source in <DeliverySourceType?>[
          null,
          DeliverySourceType.unknown,
        ]) {
          final controller = _SeededRefundReplacementController(
            RefundReplacementState(
              eligibility: _eligibility(proofImageRequired: false),
            ),
          );
          await _pump(
            tester,
            CustomerRefundReplacementScreen(
              deliveryId: 'delivery-1',
              sourceType: source,
            ),
            controller,
          );

          // No unsupported Replacement option is shown when the source
          // cannot be determined reliably.
          expect(find.text('Replacement'), findsNothing);
          expect(
            find.byType(SegmentedButton<RefundReplacementType>),
            findsNothing,
          );

          await tester.enterText(
            find.byType(TextField).first,
            'Milk was sour',
          );
          await tester.pump();
          await tester.tap(find.text('Submit request'));
          await tester.pump();
          await tester.pump(const Duration(milliseconds: 100));

          expect(controller.submittedType, RefundReplacementType.refund);
        }
      },
    );

    testWidgets('refund-only mode preserves validation and evidence', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(
          eligibility: _eligibility(proofImageRequired: false),
        ),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.oneTimeOrder,
        ),
        controller,
      );

      // Empty reason still blocks submission.
      await tester.tap(find.text('Submit request'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(find.text('Enter a reason for your request.'), findsOneWidget);
      expect(controller.submittedReason, isNull);

      // Failure evidence is still required when the backend asks for it.
      final proofController = _SeededRefundReplacementController(
        RefundReplacementState(eligibility: _eligibility()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.oneTimeOrder,
        ),
        proofController,
      );
      expect(find.text('Attach proof image'), findsOneWidget);
    });

    testWidgets('refund-only mode preserves auth and loading states', (
      tester,
    ) async {
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.oneTimeOrder,
        ),
        _SeededRefundReplacementController(
          const RefundReplacementState(isLoading: true),
        ),
        settle: false,
      );
      expect(find.byType(CircularProgressIndicator), findsOneWidget);

      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          sourceType: DeliverySourceType.oneTimeOrder,
        ),
        _SeededRefundReplacementController(
          const RefundReplacementState(isUnauthorized: true),
        ),
      );
      expect(find.text('Access denied'), findsOneWidget);
    });
  });
}

RefundReplacementEligibility _eligibility({
  bool isEligible = true,
  String? ineligibleReason,
  bool proofImageRequired = true,
  bool hasActiveRequest = false,
}) => RefundReplacementEligibility(
  isEligible: isEligible,
  ineligibleReason: ineligibleReason,
  windowHours: 48,
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  hasActiveRequest: hasActiveRequest,
  proofImageRequired: proofImageRequired,
  isMilkTestRejectedFlow: false,
);

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededRefundReplacementController controller, {
  bool settle = true,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  // A real GoRouter mirrors production: a successful submit pops the request
  // screen back to the previous route instead of throwing on a bare
  // MaterialApp with no router in the tree.
  final router = GoRouter(
    initialLocation: '/',
    routes: [
      GoRoute(path: '/', builder: (_, _) => const Scaffold(body: Text('home'))),
      GoRoute(path: '/submit', builder: (_, _) => screen),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(),
        ),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
  // Push the request screen so a successful submit can pop back to home,
  // exactly like the production navigation stack.
  router.push('/submit');
  await tester.pump();
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    // Finish the push transition without settling looping indicators.
    await tester.pump(const Duration(seconds: 1));
  }
}

class _SeededSessionController extends SessionController {
  @override
  SessionState build() => SessionState.authenticated(_session);
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

/// Seeds the controller with a fixed state and records submissions, so widget
/// tests never hit the network.
class _SeededRefundReplacementController extends RefundReplacementController {
  _SeededRefundReplacementController(this.seed);

  final RefundReplacementState seed;

  String? loadedEligibilityFor;
  RefundReplacementType? submittedType;
  String? submittedReason;
  Uint8List? submittedImageBytes;
  bool submitResult = true;

  @override
  RefundReplacementState build() => seed;

  @override
  Future<void> loadEligibility(String deliveryId) async {
    loadedEligibilityFor = deliveryId;
  }

  @override
  Future<bool> submitRequest(
    String deliveryId, {
    required RefundReplacementType type,
    required String reason,
    String? remarks,
    String? milkTestId,
    Uint8List? imageBytes,
    String? imageFileName,
    String? imageContentType,
  }) async {
    submittedType = type;
    submittedReason = reason;
    submittedImageBytes = imageBytes;
    return submitResult;
  }
}
