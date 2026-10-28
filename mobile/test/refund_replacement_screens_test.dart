import 'dart:typed_data';

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
import 'package:image_picker/image_picker.dart';

void main() {
  group('customer refund/replacement screens', () {
    testWidgets(
      'renders the customer request screen across eligibility states',
      (tester) async {
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
          _SeededRefundReplacementController(
            const RefundReplacementState(isLoading: true),
          ),
          settle: false,
        );
        expect(find.byType(CircularProgressIndicator), findsOneWidget);

        final ineligibleController = _SeededRefundReplacementController(
          RefundReplacementState(
            eligibility: eligibility(
              isEligible: false,
              ineligibleReason: 'The request window has closed.',
            ),
          ),
        );
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
          ineligibleController,
        );
        expect(find.text('Request not available'), findsOneWidget);
        expect(find.text('The request window has closed.'), findsOneWidget);

        await tester.tap(find.text('Refresh'));
        await tester.pumpAndSettle();
        expect(ineligibleController.loadedEligibilityFor, 'delivery-1');

        final eligibleController = _SeededRefundReplacementController(
          RefundReplacementState(eligibility: eligibility()),
        );
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
          eligibleController,
        );
        expect(find.text('Submit request'), findsOneWidget);
        expect(find.text('Attach proof image'), findsOneWidget);
        expect(eligibleController.loadedEligibilityFor, 'delivery-1');
      },
    );

    testWidgets('validates the customer request form before submitting', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(eligibility: eligibility()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
        controller,
      );

      await tester.tap(find.text('Submit request'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(find.text('Enter a reason for your request.'), findsOneWidget);
      expect(controller.submittedReason, isNull);

      // Let the first snackbar expire so the next validation message is
      // actually visible instead of queued behind it. The snackbar's 4s
      // duration timer only starts once its entrance animation completes, so
      // advance past both the entrance and the duration before settling.
      await tester.pump(const Duration(seconds: 5));
      await tester.pump(const Duration(seconds: 5));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField).first, 'Milk was sour');
      await tester.pump();
      await tester.tap(find.text('Submit request'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(
        find.text('Attach a proof image before submitting.'),
        findsOneWidget,
      );
      expect(controller.submittedReason, isNull);
    });

    testWidgets('skips the proof requirement for the milk-test rejection flow', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(eligibility: eligibility()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          milkTestId: 'test-1',
        ),
        controller,
      );

      expect(
        find.text(
          'You rejected the doorstep milk test. You can raise a refund or replacement request for this delivery right away.',
        ),
        findsOneWidget,
      );
      expect(
        find.text('No proof image is required for this request.'),
        findsOneWidget,
      );
      expect(find.text('Attach proof image'), findsNothing);
      expect(controller.loadedEligibilityFor, 'delivery-1');
    });

    group('stage 2 conditional photo evidence', () {
      testWidgets(
        'failed-delivery case explains no photo is needed and submits without one',
        (tester) async {
          final controller = _SeededRefundReplacementController(
            RefundReplacementState(
              eligibility: eligibility(
                proofImageRequired: false,
                isFailedDeliveryWithoutProduct: true,
              ),
            ),
          );
          await _pumpWithRouter(
            tester,
            const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
            controller,
          );

          expect(
            find.textContaining(
              'Our records confirm this delivery failed',
              findRichText: true,
            ),
            findsOneWidget,
          );
          expect(find.text('Attach proof image'), findsNothing);

          await tester.enterText(
            find.byType(TextField).first,
            'Never received the milk',
          );
          await tester.pump();
          await tester.tap(find.text('Submit request'));
          await tester.pump();
          await tester.pump(const Duration(milliseconds: 100));
          expect(controller.submittedReason, 'Never received the milk');
          expect(find.text('Request submitted.'), findsOneWidget);
        },
      );

      testWidgets(
        'photo-required submit sends the image with the single submit call',
        (tester) async {
          final controller = _SeededRefundReplacementController(
            RefundReplacementState(eligibility: eligibility()),
          );
          await _pumpWithRouter(
            tester,
            CustomerRefundReplacementScreen(
              deliveryId: 'delivery-1',
              pickImage: ({required source}) async => XFile.fromData(
                Uint8List.fromList(const [0xff, 0xd8, 0xff]),
                name: 'proof.jpg',
                mimeType: 'image/jpeg',
              ),
            ),
            controller,
          );

          await tester.tap(find.text('Attach proof image'));
          await tester.pumpAndSettle();
          await tester.tap(find.text('Choose from gallery'));
          await tester.pumpAndSettle();
          await tester.tap(find.text('Use Photo'));
          await tester.pumpAndSettle();

          await tester.enterText(
            find.byType(TextField).first,
            'Milk was sour',
          );
          await tester.pump();
          await tester.tap(find.text('Submit request'));
          await tester.pump();
          await tester.pump(const Duration(milliseconds: 100));

          // Stage 2.1: evidence rides with the submission in one atomic call —
          // the bytes are attached to the submit itself, with no separate
          // post-submit upload.
          expect(controller.submittedReason, 'Milk was sour');
          expect(controller.submittedImageBytes, isNotNull);
          expect(controller.submittedImageFileName, endsWith('.jpg'));
          expect(controller.submittedImageContentType, 'image/jpeg');
          expect(find.text('Request submitted.'), findsOneWidget);
        },
      );

      testWidgets('a failed submit keeps user input for retry', (tester) async {
        final controller = _SeededRefundReplacementController(
          RefundReplacementState(
            eligibility: eligibility(
              proofImageRequired: false,
              isFailedDeliveryWithoutProduct: true,
            ),
          ),
        )..submitResult = false;
        await _pumpWithRouter(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
          controller,
        );

        await tester.enterText(find.byType(TextField).first, 'Milk was sour');
        await tester.pump();
        await tester.tap(find.text('Submit request'));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 100));
        // Still on the form with the reason intact: nothing was lost.
        expect(find.text('Submit request'), findsOneWidget);
        expect(
          tester.widget<TextField>(find.byType(TextField).first).controller?.text,
          'Milk was sour',
        );

        controller.submitResult = true;
        await tester.tap(find.text('Submit request'));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 100));
        expect(controller.submittedReason, 'Milk was sour');
        expect(find.text('Request submitted.'), findsOneWidget);
      });

      testWidgets(
        'delivered-product issue marks the photo mandatory and blocks submit without one',
        (tester) async {
          final controller = _SeededRefundReplacementController(
            RefundReplacementState(eligibility: eligibility()),
          );
          await _pump(
            tester,
            const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
            controller,
          );

          expect(find.text('Proof image (required)'), findsOneWidget);
          expect(
            find.textContaining(
              'A proof image is required for this request',
              findRichText: true,
            ),
            findsOneWidget,
          );
          expect(find.text('Attach proof image'), findsOneWidget);

          await tester.enterText(
            find.byType(TextField).first,
            'Milk was sour',
          );
          await tester.pump();
          await tester.tap(find.text('Submit request'));
          await tester.pump();
          await tester.pump(const Duration(milliseconds: 100));
          expect(
            find.text('Attach a proof image before submitting.'),
            findsOneWidget,
          );
          expect(controller.submittedReason, isNull);
        },
      );

      testWidgets('the displayed requirement follows the current delivery', (
        tester,
      ) async {
        final failedController = _SeededRefundReplacementController(
          RefundReplacementState(
            eligibility: eligibility(
              proofImageRequired: false,
              isFailedDeliveryWithoutProduct: true,
            ),
          ),
        );
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-failed'),
          failedController,
        );
        expect(failedController.loadedEligibilityFor, 'delivery-failed');
        expect(find.text('Attach proof image'), findsNothing);
        expect(
          find.textContaining(
            'Our records confirm this delivery failed',
            findRichText: true,
          ),
          findsOneWidget,
        );

        final deliveredController = _SeededRefundReplacementController(
          RefundReplacementState(eligibility: eligibility()),
        );
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-ok'),
          deliveredController,
        );
        expect(deliveredController.loadedEligibilityFor, 'delivery-ok');
        expect(find.text('Attach proof image'), findsOneWidget);
        expect(
          find.textContaining(
            'Our records confirm this delivery failed',
            findRichText: true,
          ),
          findsNothing,
        );
      });

      testWidgets(
        'backend validation errors are shown without losing user input',
        (tester) async {
          const serverError =
              'A proof image is required for this delivered-product issue.';
          final controller = _SeededRefundReplacementController(
            RefundReplacementState(
              eligibility: eligibility(),
              errorMessage: serverError,
            ),
          );
          await _pump(
            tester,
            const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
            controller,
          );

          await tester.enterText(
            find.byType(TextField).first,
            'Milk was sour',
          );
          await tester.pump();
          expect(find.text(serverError), findsOneWidget);
          final reasonField =
              tester.widget<TextField>(find.byType(TextField).first);
          expect(reasonField.controller?.text, 'Milk was sour');
        },
      );
    });

    testWidgets('renders the customer request list and detail', (
      tester,
    ) async {
      await _pump(
        tester,
        const CustomerRefundReplacementListScreen(),
        _SeededRefundReplacementController(const RefundReplacementState()),
      );
      expect(find.text('No requests yet'), findsOneWidget);
      expect(
        find.text(
          'Refund or replacement requests you raise for your deliveries will appear here.',
        ),
        findsOneWidget,
      );

      await _pump(
        tester,
        const CustomerRefundReplacementListScreen(),
        _SeededRefundReplacementController(
          RefundReplacementState(customerRequests: [request()]),
        ),
      );
      expect(find.text('RR-2026-0001'), findsOneWidget);
      expect(find.text('Refund · Requested after delivery'), findsOneWidget);
      expect(find.text('Order ORD-1001'), findsOneWidget);

      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(selectedCustomerRequest: request()),
        ),
      );
      expect(find.text('Request details'), findsOneWidget);
      expect(find.text('RR-2026-0001'), findsOneWidget);
      expect(find.text('Decision'), findsOneWidget);
      expect(
        find.text('Your request is awaiting a decision from the support team.'),
        findsOneWidget,
      );
    });

    testWidgets('loads protected proof images and falls back to a lock icon', (
      tester,
    ) async {
      final seeded = request(images: [image()]);
      final repository = _FakeRefundReplacementRepository();

      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(selectedCustomerRequest: seeded),
        ),
        repository: repository,
      );

      expect(repository.contentRequests, hasLength(1));
      expect(repository.contentRequests.single.token, 'refund-token');
      expect(repository.contentRequests.single.requestId, 'req-1');
      expect(repository.contentRequests.single.imageId, 'image-1');
      expect(find.text('Proof images'), findsOneWidget);
      expect(find.byType(Image), findsOneWidget);

      final failingRepository = _FakeRefundReplacementRepository()
        ..imageContent = (_, _, _) =>
            throw const ApiException(403, 'FORBIDDEN', 'no access');

      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(selectedCustomerRequest: seeded),
        ),
        repository: failingRepository,
      );
      expect(failingRepository.contentRequests, hasLength(1));
      expect(find.byIcon(Icons.lock_outline), findsOneWidget);
    });
  });

  group('staff refund/replacement screens', () {
    testWidgets('drives the staff list filter and decision lifecycle', (
      tester,
    ) async {
      final listController = _SeededRefundReplacementController(
        RefundReplacementState(staffPage: pageOf()),
      );
      await _pump(
        tester,
        const StaffRefundReplacementListScreen(branchId: 7),
        listController,
        session: _managerSession,
      );

      expect(find.text('Refund / replacement requests'), findsOneWidget);
      expect(find.text('RR-2026-0001'), findsOneWidget);
      expect(find.text('Order ORD-1001 · Asha Rao'), findsOneWidget);
      expect(find.text('Proof image missing'), findsOneWidget);

      // Status and date filters live behind the AppBar filter icon.
      expect(find.widgetWithText(ChoiceChip, 'Pending'), findsNothing);
      await tester.tap(find.byTooltip('Filters'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(ChoiceChip, 'Pending'));
      await tester.pumpAndSettle();
      expect(listController.staffBranchId, 7);
      expect(listController.staffStatusFilter, RefundReplacementStatus.pending);

      final detailController = _SeededRefundReplacementController(
        RefundReplacementState(
          selectedStaffRequest: request(
            status: RefundReplacementStatus.pending,
          ),
        ),
      );
      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        detailController,
        session: _managerSession,
      );

      expect(find.text('Request details'), findsOneWidget);
      expect(
        find.text('A proof image is required for this request.'),
        findsOneWidget,
      );
      // One-time order tickets are refund-only with an explicit deduction
      // decision — no Replacement action is offered.
      expect(
        find.widgetWithText(FilledButton, 'Approve refund decision'),
        findsOneWidget,
      );
      expect(find.text('Reject'), findsOneWidget);

      await tester.tap(
        find.widgetWithText(FilledButton, 'Approve refund decision'),
      );
      await tester.pumpAndSettle();
      expect(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(
            FilledButton,
            'Approve refund decision',
          ),
        ),
        findsOneWidget,
      );
      // No decision is preselected: support must consciously choose.
      expect(
        find.text(
          'Select a decision above to load the authoritative refund preview.',
        ),
        findsOneWidget,
      );

      await tester.tap(find.text('Apply deduction'));
      await tester.pumpAndSettle();
      expect(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.textContaining('180.00 INR'),
        ),
        findsOneWidget,
      );
      // No settlement method is preselected: support must consciously
      // choose one before the approval can be submitted.
      await tester.tap(find.text('Wallet refund'));
      await tester.pumpAndSettle();

      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(
            FilledButton,
            'Approve refund decision',
          ),
        ),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(detailController.decidedAction, 'approve');
      expect(detailController.decidedApplyDeduction, isTrue);
      expect(detailController.decidedSettlementMethod, 'Wallet');
      expect(
        find.textContaining('Refund decision approved.'),
        findsOneWidget,
      );

      // Let the snackbar expire before pumping the next screen so no timers
      // leak across the test's widget tree replacements.
      await tester.pump(const Duration(seconds: 5));
      await tester.pumpAndSettle();

      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(
            selectedStaffRequest: request(
              status: RefundReplacementStatus.approved,
              includeDecision: true,
            ),
          ),
        ),
        session: _managerSession,
      );
      expect(find.text('Complete and credit wallet'), findsOneWidget);

      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(
            selectedStaffRequest: request(
              status: RefundReplacementStatus.rejected,
              includeDecision: true,
            ),
          ),
        ),
        session: _managerSession,
      );
      expect(
        find.text('This request is final and no further action is available.'),
        findsOneWidget,
      );
    });

    testWidgets(
      'D1-D slice 7.1: staff distinguish cancellation resolution from failure',
      (tester) async {
        await _pump(
          tester,
          const StaffRefundReplacementListScreen(branchId: 7),
          _SeededRefundReplacementController(const RefundReplacementState()),
          session: _managerSession,
        );
        // Status chips live in the filter sheet behind the AppBar icon.
        await tester.tap(find.byTooltip('Filters'));
        await tester.pumpAndSettle();
        expect(find.widgetWithText(ChoiceChip, 'Cancelled (refunded)'), findsOneWidget);

        await _pump(
          tester,
          const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedStaffRequest: request(
                status: RefundReplacementStatus.cancelled,
                type: RefundReplacementType.replacement,
                source: RefundReplacementSource.deliveryFailed,
                includeDecision: true,
                completedAtUtc: DateTime.utc(2026, 8, 26, 10),
              ),
            ),
          ),
          session: _managerSession,
        );
        expect(find.text('Cancelled (refunded)'), findsOneWidget);
        expect(find.text('Closed'), findsOneWidget);
        expect(find.text('Completed'), findsNothing);
        expect(
          find.textContaining('This is not a replacement failure.'),
          findsOneWidget,
        );
        expect(
          find.text('This request is final and no further action is available.'),
          findsOneWidget,
        );
      },
    );

    testWidgets(
      'refund-only remedy hides Reject and explains the constraint',
      (tester) async {
        await _pump(
          tester,
          const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedStaffRequest: request(
                status: RefundReplacementStatus.pending,
                type: RefundReplacementType.refund,
                source: RefundReplacementSource.deliveryFailed,
                customerChoice: CustomerChoice.refund,
                isRefundOnly: true,
              ),
            ),
          ),
          session: _managerSession,
        );

        expect(find.text('Reject'), findsNothing);
        expect(find.widgetWithText(FilledButton, 'Approve'), findsOneWidget);
        expect(
          find.textContaining('Only the refund remedy is available'),
          findsOneWidget,
        );
      },
    );

    testWidgets('staff reject forwards the dialog remarks to the backend', (
      tester,
    ) async {
      // Regression: the confirm dialog collected remarks but the decision
      // handler dropped them, so support's explanation never reached the
      // customer ticket.
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(
          selectedStaffRequest: request(
            status: RefundReplacementStatus.pending,
            source: RefundReplacementSource.deliveryFailed,
            customerChoice: CustomerChoice.refund,
          ),
        ),
      );
      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        controller,
        session: _managerSession,
      );

      await tester.tap(find.widgetWithText(OutlinedButton, 'Reject'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.byType(TextField),
        ),
        'Customer already refunded for this delivery',
      );
      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(FilledButton, 'Reject'),
        ),
      );
      await tester.pump();

      expect(controller.decidedAction, 'reject');
      expect(
        controller.decidedRemarks,
        'Customer already refunded for this delivery',
      );
    });

    testWidgets('subscription ticket order row opens the linked delivery', (
      tester,
    ) async {
      // Subscription tickets have no order: the Order row must still open
      // the authoritative fulfillment record instead of a dead link.
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(
          selectedStaffRequest: request(
            status: RefundReplacementStatus.pending,
            source: RefundReplacementSource.deliveryFailed,
            customerChoice: CustomerChoice.refund,
            orderPublicId: null,
            orderNumber: null,
          ),
        ),
      );
      final router = GoRouter(
        initialLocation: '/',
        routes: [
          GoRoute(
            path: '/',
            builder: (context, state) =>
                const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
          ),
          GoRoute(
            path: '/staff/delivery/:deliveryId',
            builder: (context, state) => Text(
              'delivery-stub ${state.pathParameters['deliveryId']}',
            ),
          ),
        ],
      );
      addTearDown(router.dispose);
      await tester.binding.setSurfaceSize(const Size(800, 1200));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      await tester.pumpWidget(
        ProviderScope(
          key: UniqueKey(),
          overrides: [
            refundReplacementControllerProvider.overrideWith(() => controller),
            sessionControllerProvider.overrideWith(
              () => _SeededSessionController(session: _managerSession),
            ),
            refundReplacementRepositoryProvider.overrideWithValue(
              _FakeRefundReplacementRepository(),
            ),
          ],
          child: MaterialApp.router(
            theme: ThemeData(useMaterial3: true),
            routerConfig: router,
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Subscription delivery'), findsOneWidget);
      await tester.tap(find.text('Subscription delivery'));
      await tester.pumpAndSettle();
      expect(find.text('delivery-stub delivery-1'), findsOneWidget);
    });

    testWidgets(
      'approved one-time order distinguishes settlement from approval',
      (tester) async {
        // Stage 2: completing an approved order Refund credits the wallet —
        // approval alone moved nothing. The action and copy must name the
        // settlement explicitly so staff know when money actually moves.
        await _pump(
          tester,
          const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedStaffRequest: request(
                status: RefundReplacementStatus.approved,
                includeDecision: true,
              ),
            ),
          ),
          session: _managerSession,
        );

        expect(find.text('Complete and credit wallet'), findsOneWidget);
        expect(
          find.textContaining('moved no money'),
          findsOneWidget,
        );

        // Subscription tickets keep their existing completion copy without
        // the wallet-settlement notice.
        await _pump(
          tester,
          const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedStaffRequest: request(
                status: RefundReplacementStatus.approved,
                source: RefundReplacementSource.deliveryFailed,
                customerChoice: CustomerChoice.refund,
                includeDecision: true,
              ),
            ),
          ),
          session: _managerSession,
        );

        expect(find.text('Mark completed'), findsOneWidget);
        expect(
          find.textContaining('settled in Payments'),
          findsNothing,
        );
      },
    );
  });

  group('D1-D slice 7 customer re-delivery display', () {
    RefundReplacementRequest redeliveryTicket({
      String? replacementDeliveryNumber,
      DateTime? completedAtUtc,
    }) => request(
      status: RefundReplacementStatus.approved,
      type: RefundReplacementType.replacement,
      source: RefundReplacementSource.deliveryFailed,
      includeDecision: true,
      customerChoice: CustomerChoice.reDelivery,
      replacementDate: DateTime.utc(2026, 8, 25),
      replacementDeliveryNumber: replacementDeliveryNumber,
      completedAtUtc: completedAtUtc,
    );

    testWidgets(
      'customer detail keeps the persisted replacement date after reload ',
      (tester) async {
        // The date comes from a fresh read (list/detail GET), never from
        // the approval response: pumping the detail with read-shaped data
        // must still render it.
        await _pump(
          tester,
          const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedCustomerRequest: redeliveryTicket(),
            ),
          ),
        );
        expect(find.text('Replacement date'), findsOneWidget);
        expect(find.text('25/08/2026'), findsOneWidget);
        expect(find.text('You chose Re-delivery.'), findsOneWidget);
        // Unmaterialized: no delivery number, and no internal identifiers
        // leak as business references.
        expect(find.textContaining('Delivery DLV'), findsNothing);
        expect(find.textContaining('sub-delivery-1'), findsNothing);
        expect(find.textContaining('delivery-1'), findsNothing);
      },
    );

    testWidgets(
      'customer detail shows the real public delivery number once materialized',
      (tester) async {
        await _pump(
          tester,
          const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedCustomerRequest: redeliveryTicket(
                replacementDeliveryNumber: 'DLV/BR-07/000123',
              ),
            ),
          ),
        );
        expect(find.text('Replacement date'), findsOneWidget);
        expect(find.text('25/08/2026'), findsOneWidget);
        expect(find.text('Replacement delivery'), findsOneWidget);
        expect(find.text('DLV/BR-07/000123'), findsOneWidget);
        expect(find.textContaining('sub-delivery-1'), findsNothing);
      },
    );

    testWidgets('customer list cards surface the persisted replacement date', (
      tester,
    ) async {
      await _pump(
        tester,
        const CustomerRefundReplacementListScreen(),
        _SeededRefundReplacementController(
          RefundReplacementState(
            customerRequests: [
              redeliveryTicket(
                replacementDeliveryNumber: 'DLV/BR-07/000123',
              ),
            ],
          ),
        ),
      );
      expect(find.text('Re-delivery on 25/08/2026'), findsOneWidget);
      expect(find.text('Delivery DLV/BR-07/000123'), findsOneWidget);
    });

    testWidgets(
      'cancelled tickets resolve distinctly from failures and never read as delivered',
      (tester) async {
        await _pump(
          tester,
          const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedCustomerRequest: request(
                status: RefundReplacementStatus.cancelled,
                type: RefundReplacementType.replacement,
                source: RefundReplacementSource.deliveryFailed,
                includeDecision: true,
                customerChoice: CustomerChoice.reDelivery,
                completedAtUtc: DateTime.utc(2026, 8, 26, 10),
                completionRemarks:
                    'Subscription cancelled; pending replacement voided and '
                    '72.00 refunded to wallet.',
              ),
            ),
          ),
        );
        expect(find.text('Cancelled (refunded)'), findsOneWidget);
        expect(find.text('Closed'), findsOneWidget);
        expect(find.text('Completed'), findsNothing);
        expect(find.text('Closed without delivery'), findsNothing);
        expect(
          find.textContaining('was cancelled while this replacement was'),
          findsOneWidget,
        );
        expect(
          find.textContaining(
            'was cancelled while this replacement was still pending',
          ),
          findsOneWidget,
        );
        // The backend-authored money figures ride along untouched.
        expect(find.textContaining('72.00 refunded to wallet'), findsOneWidget);
        // Terminal: neither choice button is offered again.
        expect(find.widgetWithText(OutlinedButton, 'Refund'), findsNothing);
        expect(
          find.widgetWithText(OutlinedButton, 'Re-Delivery'),
          findsNothing,
        );
      },
    );

    testWidgets(
      'failed tickets close without delivery and offer no re-delivery',
      (tester) async {
        await _pump(
          tester,
          const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
          _SeededRefundReplacementController(
            RefundReplacementState(
              selectedCustomerRequest: request(
                status: RefundReplacementStatus.failed,
                type: RefundReplacementType.replacement,
                source: RefundReplacementSource.deliveryFailed,
                includeDecision: true,
                customerChoice: CustomerChoice.reDelivery,
                completedAtUtc: DateTime.utc(2026, 8, 26, 10),
                completionRemarks: 'Replacement failed twice; refund issued.',
              ),
            ),
          ),
        );
        expect(find.text('Closed without delivery'), findsOneWidget);
        expect(find.text('Closed'), findsOneWidget);
        expect(find.text('Completed'), findsNothing);
        expect(
          find.textContaining(
            'closed without delivery and the refund-only remedy has been '
            'resolved',
          ),
          findsOneWidget,
        );
        // Terminal: neither choice button is offered again.
        expect(find.widgetWithText(OutlinedButton, 'Refund'), findsNothing);
        expect(
          find.widgetWithText(OutlinedButton, 'Re-Delivery'),
          findsNothing,
        );
      },
    );
  });
}

Future<_FakeRefundReplacementRepository> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededRefundReplacementController controller, {
  _FakeRefundReplacementRepository? repository,
  AuthSession? session,
  bool settle = true,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final fakeRepository = repository ?? _FakeRefundReplacementRepository();
  await tester.pumpWidget(
    ProviderScope(
      // A fresh key forces a new ProviderContainer per pump so that re-seeding
      // a different state between pumps is actually applied.
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: session),
        ),
        refundReplacementRepositoryProvider.overrideWithValue(fakeRepository),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pump();
  if (settle) {
    await tester.pumpAndSettle();
  }
  return fakeRepository;
}

/// Variant of [_pump] hosted inside a [GoRouter] so the customer screen's
/// `context.pop()` after a successful submit resolves instead of throwing.
/// Popping the single root route is a no-op, leaving the success snackbar
/// visible for assertions.
Future<void> _pumpWithRouter(
  WidgetTester tester,
  Widget screen,
  _SeededRefundReplacementController controller, {
  AuthSession? session,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final router = GoRouter(
    initialLocation: '/',
    routes: [
      GoRoute(
        path: '/',
        builder: (context, state) => const Scaffold(body: Text('Home')),
      ),
      GoRoute(path: '/request', builder: (context, state) => screen),
    ],
  );
  addTearDown(router.dispose);
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: session),
        ),
        refundReplacementRepositoryProvider.overrideWithValue(
          _FakeRefundReplacementRepository(),
        ),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pumpAndSettle();
  // Push the screen over Home so the post-submit pop() lands back on Home
  // instead of throwing on an empty stack (go() would replace the stack).
  router.push('/request');
  await tester.pumpAndSettle();
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({this.session});

  final AuthSession? session;

  @override
  SessionState build() => SessionState.authenticated(session ?? _session);
}

/// Seeds the controller with a fixed state and records the load/decision calls
/// the screens make, so widget tests never hit the network.
class _SeededRefundReplacementController extends RefundReplacementController {
  _SeededRefundReplacementController(this.seed);

  final RefundReplacementState seed;

  String? loadedEligibilityFor;
  bool loadedCustomerList = false;
  String? loadedCustomerRequestId;
  int? staffBranchId;
  RefundReplacementStatus? staffStatusFilter;
  String? loadedStaffRequestId;
  RefundReplacementType? submittedType;
  String? submittedReason;
  Uint8List? submittedImageBytes;
  String? submittedImageFileName;
  String? submittedImageContentType;
  bool submitResult = true;
  String? decidedAction;
  String? decidedRemarks;
  bool? decidedApplyDeduction;
  String? decidedWaiverReason;
  String? decidedSettlementMethod;

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
    submittedImageFileName = imageFileName;
    submittedImageContentType = imageContentType;
    return submitResult;
  }

  @override
  Future<void> loadCustomerRequests() async {
    loadedCustomerList = true;
  }

  @override
  Future<void> loadCustomerRequest(String requestId) async {
    loadedCustomerRequestId = requestId;
  }

  @override
  Future<void> loadStaffPage({
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) async {
    staffBranchId = branchId;
    staffStatusFilter = status;
  }

  @override
  Future<void> loadStaffRequest(String requestId) async {
    loadedStaffRequestId = requestId;
  }

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
    decidedRemarks = remarks;
    decidedApplyDeduction = applyDeduction;
    decidedWaiverReason = waiverReason;
    decidedSettlementMethod = settlementMethod;
    return true;
  }

  @override
  Future<bool> reject(String requestId, {String? remarks}) async {
    decidedAction = 'reject';
    decidedRemarks = remarks;
    return true;
  }

  @override
  Future<bool> complete(String requestId, {String? remarks}) async {
    decidedAction = 'complete';
    decidedRemarks = remarks;
    return true;
  }
}

class _FakeRefundReplacementRepository extends RefundReplacementRepository {
  _FakeRefundReplacementRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  OrderRefundPreview? preview;
  Object? previewFailure;
  int previewCalls = 0;
  final List<bool> previewDecisions = [];

  @override
  Future<OrderRefundPreview> getOrderRefundPreview(
    String token,
    String requestId, {
    required bool applyDeduction,
  }) async {
    previewCalls++;
    previewDecisions.add(applyDeduction);
    final failure = previewFailure;
    if (failure != null) throw failure;
    return preview ??
        OrderRefundPreview(
          requestId: requestId,
          requestNumber: 'RR-2026-0001',
          orderPublicId: 'order-pub-1',
          orderNumber: 'ORD-1001',
          grossBasis: 200,
          appliedPercentage: applyDeduction ? 10 : 0,
          deductionAmount: applyDeduction ? 20 : 0,
          eligiblePaid: 200,
          proposedRefund: applyDeduction ? 180 : 200,
          currency: 'INR',
        );
  }

  ApiByteResponse Function(String token, String requestId, String imageId)?
  imageContent;
  final List<_ImageContentRequest> contentRequests = [];

  @override
  Future<ApiByteResponse> getImageContent(
    String token,
    String requestId,
    String imageId,
  ) async {
    contentRequests.add(
      _ImageContentRequest(
        token: token,
        requestId: requestId,
        imageId: imageId,
      ),
    );
    final handler = imageContent;
    if (handler == null) {
      return ApiByteResponse(
        bytes: Uint8List.fromList(const [
          0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
          0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
          0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
          0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        ]),
        contentType: 'image/png',
        fileName: 'proof.png',
      );
    }
    return handler(token, requestId, imageId);
  }
}

class _ImageContentRequest {
  const _ImageContentRequest({
    required this.token,
    required this.requestId,
    required this.imageId,
  });

  final String token;
  final String requestId;
  final String imageId;
}

RefundReplacementEligibility eligibility({
  bool isEligible = true,
  String? ineligibleReason,
  bool proofImageRequired = true,
  bool hasActiveRequest = false,
  bool isMilkTestRejectedFlow = false,
  bool isFailedDeliveryWithoutProduct = false,
}) => RefundReplacementEligibility(
  isEligible: isEligible,
  ineligibleReason: ineligibleReason,
  windowHours: 48,
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  hasActiveRequest: hasActiveRequest,
  proofImageRequired: proofImageRequired,
  isMilkTestRejectedFlow: isMilkTestRejectedFlow,
  isFailedDeliveryWithoutProduct: isFailedDeliveryWithoutProduct,
);

RefundReplacementImage image() => RefundReplacementImage(
  imageId: 'image-1',
  fileName: 'proof.jpg',
  contentType: 'image/jpeg',
  fileSize: 2048,
  uploadedAtUtc: DateTime.utc(2026, 8, 17, 9, 5),
);

RefundReplacementRequest request({
  RefundReplacementStatus status = RefundReplacementStatus.pending,
  RefundReplacementType type = RefundReplacementType.refund,
  RefundReplacementSource source = RefundReplacementSource.customerPostDelivery,
  String? orderPublicId = 'order-pub-1',
  String? orderNumber = 'ORD-1001',
  bool includeDecision = false,
  bool isRefundOnly = false,
  List<RefundReplacementImage> images = const [],
  CustomerChoice? customerChoice,
  DateTime? replacementDate,
  String? replacementDeliveryNumber,
  DateTime? completedAtUtc,
  String? completionRemarks,
}) => RefundReplacementRequest(
  requestId: 'req-1',
  requestNumber: 'RR-2026-0001',
  orderId: 101,
  orderPublicId: orderPublicId,
  orderNumber: orderNumber,
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
  type: type,
  source: source,
  status: status,
  reason: 'Milk was sour',
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
  proofImageRequired: true,
  images: images,
  isRefundOnly: isRefundOnly,
  subscriptionDeliveryPublicId: source == RefundReplacementSource.deliveryFailed
      ? 'sub-delivery-1'
      : null,
  customerChoice: customerChoice,
  customerChoiceAtUtc: customerChoice == null
      ? null
      : DateTime.utc(2026, 8, 17, 12),
  replacementDate: replacementDate,
  replacementDeliveryNumber: replacementDeliveryNumber,
);

RefundReplacementListItem listItem({
  RefundReplacementStatus status = RefundReplacementStatus.pending,
  bool proofImageRequired = true,
  int imageCount = 0,
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
  proofImageRequired: proofImageRequired,
  imageCount: imageCount,
);

RefundReplacementPage pageOf() => RefundReplacementPage(
  items: [listItem()],
  page: 1,
  pageSize: 20,
  totalCount: 1,
);

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
