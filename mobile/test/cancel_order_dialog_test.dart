import 'package:doodh_direct_mobile/features/orders/cancel_order_dialog.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

CancelOrderPreview preview({
  bool pickup = false,
  bool can = true,
  double percentage = 25,
}) => CancelOrderPreview(
  canCancel: can,
  blockReason: can ? null : 'Order has already been delivered.',
  deliveryStatus: pickup ? 'PickedUp' : 'Assigned',
  pickupOccurred: pickup,
  deductionPercentage: pickup ? percentage : 0,
  grossBasis: 200,
  deductionAmount: pickup ? 50 : 0,
  estimatedRefund: pickup ? 150 : 200,
  eligiblePaidAmount: 200,
  requiresConfirmation: pickup,
);

Future<void> openDialog(
  WidgetTester tester, {
  CancelOrderPreview? previewData,
  String? serverError,
  String? initialReasonCode,
  String? initialComment,
  void Function(Future<CancelOrderInput?> future)? capture,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      home: Builder(
        builder: (context) => TextButton(
          onPressed: () {
            final future = showCancelOrderDialog(
              context,
              preview: previewData,
              serverError: serverError,
              initialReasonCode: initialReasonCode,
              initialComment: initialComment,
            );
            capture?.call(future);
          },
          child: const Text('open'),
        ),
      ),
    ),
  );
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
}

Future<void> selectReason(WidgetTester tester, String reason) async {
  await tester.tap(find.text('Reason'));
  await tester.pumpAndSettle();
  await tester.tap(find.text(reason).last);
  await tester.pumpAndSettle();
}

void main() {
  group('cancel order dialog O2 (pre-pickup)', () {
    testWidgets('explains zero-deduction wallet refund', (tester) async {
      await openDialog(tester, previewData: preview());
      expect(
        find.textContaining('0% deduction', findRichText: true),
        findsOneWidget,
      );
      expect(find.textContaining('Wallet', findRichText: true), findsWidgets);
    });

    testWidgets('requires a reason or comment', (tester) async {
      await openDialog(tester, previewData: preview());
      await tester.tap(find.text('Cancel order'));
      await tester.pump();
      expect(
        find.text('Provide a reason or a comment to cancel.'),
        findsOneWidget,
      );
    });

    testWidgets('Other without comment is rejected, whitespace trimmed', (
      tester,
    ) async {
      CancelOrderInput? result;
      await openDialog(
        tester,
        previewData: preview(),
        initialReasonCode: 'Other',
        initialComment: '   ',
        capture: (future) => future.then((value) => result = value),
      );
      await tester.tap(find.text('Cancel order'));
      await tester.pump();
      expect(
        find.text('A comment is required when the reason is Other.'),
        findsOneWidget,
      );
      expect(result, isNull);
    });
  });

  group('cancel order dialog O3 (post-pickup)', () {
    testWidgets('warns with server percentage and amount', (tester) async {
      await openDialog(tester, previewData: preview(pickup: true));
      expect(
        find.textContaining('already left', findRichText: true),
        findsOneWidget,
      );
      expect(find.textContaining('25%', findRichText: true), findsWidgets);
      expect(find.textContaining('150', findRichText: true), findsWidgets);
      expect(find.text('Continue delivery'), findsOneWidget);
      expect(find.text('Confirm cancellation'), findsOneWidget);
    });

    testWidgets('explicit confirmation required before confirm', (tester) async {
      await openDialog(tester, previewData: preview(pickup: true));
      await selectReason(tester, 'ChangedMind');
      await tester.tap(find.text('Confirm cancellation'));
      await tester.pump();
      expect(
        find.textContaining('Confirm the deduction', findRichText: true),
        findsOneWidget,
      );
    });

    testWidgets('declining pops null (order stays active)', (tester) async {
      CancelOrderInput? result;
      var settled = false;
      await openDialog(
        tester,
        previewData: preview(pickup: true),
        capture: (future) => future.then((value) {
          result = value;
          settled = true;
        }),
      );
      await tester.tap(find.text('Continue delivery'));
      await tester.pumpAndSettle();
      expect(settled, isTrue);
      expect(result, isNull);
    });

    testWidgets('confirming returns confirmed input', (tester) async {
      CancelOrderInput? result;
      var settled = false;
      await openDialog(
        tester,
        previewData: preview(pickup: true),
        capture: (future) => future.then((value) {
          result = value;
          settled = true;
        }),
      );
      await selectReason(tester, 'ChangedMind');
      await tester.tap(find.byType(CheckboxListTile));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Confirm cancellation'));
      await tester.pumpAndSettle();
      expect(settled, isTrue);
      expect(result, isNotNull);
      expect(result!.reasonCode, 'ChangedMind');
      expect(result!.confirmed, isTrue);
    });
  });

  group('server errors and blocked orders', () {
    testWidgets('server error shown without losing typed comment', (
      tester,
    ) async {
      await openDialog(
        tester,
        previewData: preview(),
        serverError: 'Stale terms. Refresh and confirm again.',
        initialComment: 'keep my note',
      );
      expect(
        find.textContaining('Stale terms', findRichText: true),
        findsOneWidget,
      );
      expect(find.text('keep my note'), findsOneWidget);
    });

    testWidgets('blocked order shows reason with no confirm action', (
      tester,
    ) async {
      await openDialog(
        tester,
        previewData: preview(pickup: true, can: false),
      );
      expect(
        find.textContaining('already been delivered', findRichText: true),
        findsOneWidget,
      );
      expect(find.text('Confirm cancellation'), findsNothing);
      expect(find.text('Keep order'), findsOneWidget);
    });
  });
}
