import 'dart:typed_data';

import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';

/// Minimal valid 1x1 PNG so Image.memory can lay out in tests.
Uint8List get _pngBytes => Uint8List.fromList(const [
  137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1,
  0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137, 0, 0, 0, 10, 73, 68, 65, 84,
  120, 156, 99, 0, 1, 0, 0, 5, 0, 1, 13, 10, 45, 180, 0, 0, 0, 0, 73, 69,
  78, 68, 174, 66, 96, 130,
]);

Future<void> openDialog(
  WidgetTester tester, {
  required bool requiresProof,
  DeliveryFailImagePicker? pickImage,
  void Function(Future<DeliveryFailInput?> future)? capture,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      home: Builder(
        builder: (context) => TextButton(
          onPressed: () {
            final future = showDeliveryFailDialog(
              context,
              requiresProof: requiresProof,
              pickImage: pickImage,
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

void main() {
  group('resolveDeliveryFailImageContentType', () {
    test('accepts the server-supported JPEG, PNG, and WebP types', () {
      expect(resolveDeliveryFailImageContentType('a.jpg', null), 'image/jpeg');
      expect(
        resolveDeliveryFailImageContentType('a.jpeg', 'image/jpeg'),
        'image/jpeg',
      );
      expect(resolveDeliveryFailImageContentType('a.png', null), 'image/png');
      expect(
        resolveDeliveryFailImageContentType('a.webp', null),
        'image/webp',
      );
      expect(
        resolveDeliveryFailImageContentType('a.webp', 'image/webp'),
        'image/webp',
      );
    });

    test('rejects anything the server would reject', () {
      expect(resolveDeliveryFailImageContentType('a.gif', null), isNull);
      expect(resolveDeliveryFailImageContentType('a.pdf', null), isNull);
      expect(
        resolveDeliveryFailImageContentType('a.jpg', 'image/gif'),
        isNull,
      );
    });
  });

  group('fail dialog without proof (one-time orders)', () {
    testWidgets('has no photo UI and confirms with reason only', (
      tester,
    ) async {
      DeliveryFailInput? result;
      var settled = false;
      await openDialog(
        tester,
        requiresProof: false,
        capture: (future) => future.then((value) {
          result = value;
          settled = true;
        }),
      );
      expect(find.text('Take photo'), findsNothing);
      expect(find.text('Gallery'), findsNothing);
      await tester.tap(find.text('Mark failed'));
      await tester.pumpAndSettle();
      expect(settled, isTrue);
      expect(result, isNotNull);
      expect(
        result!.reason,
        DeliveryFailureReasons.customerNotAvailable,
      );
      expect(result!.proof, isNull);
    });

    testWidgets('cancel returns null', (tester) async {
      DeliveryFailInput? result = const DeliveryFailInput(reason: 'x');
      var settled = false;
      await openDialog(
        tester,
        requiresProof: false,
        capture: (future) => future.then((value) {
          result = value;
          settled = true;
        }),
      );
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(settled, isTrue);
      expect(result, isNull);
    });
  });

  group('fail dialog with proof (subscription deliveries)', () {
    testWidgets('explains the requirement and blocks photo-less confirm', (
      tester,
    ) async {
      await openDialog(tester, requiresProof: true);
      expect(
        find.textContaining('proof image is required', findRichText: true),
        findsOneWidget,
      );
      expect(find.text('Take photo'), findsOneWidget);
      expect(find.text('Gallery'), findsOneWidget);
      await tester.tap(find.text('Mark failed'));
      await tester.pump();
      expect(
        find.text('Attach a proof image before marking failed.'),
        findsOneWidget,
      );
    });

    testWidgets('attached photo unblocks confirm and returns proof', (
      tester,
    ) async {
      DeliveryFailInput? result;
      var settled = false;
      Future<XFile?> picker({required ImageSource source}) async =>
          XFile.fromData(_pngBytes, name: 'proof.png', mimeType: 'image/png');
      await openDialog(
        tester,
        requiresProof: true,
        pickImage: picker,
        capture: (future) => future.then((value) {
          result = value;
          settled = true;
        }),
      );
      await tester.tap(find.text('Gallery'));
      await tester.pumpAndSettle();
      // Attached photo previews in the dialog.
      expect(find.byType(Image), findsOneWidget);
      await tester.tap(find.text('Mark failed'));
      await tester.pumpAndSettle();
      expect(settled, isTrue);
      expect(result, isNotNull);
      expect(result!.proof, isNotNull);
      expect(result!.proof!.bytes, _pngBytes);
      expect(result!.proof!.fileName, 'proof.png');
      expect(result!.proof!.contentType, 'image/png');
    });

    testWidgets('unsupported photo type is rejected with guidance', (
      tester,
    ) async {
      Future<XFile?> picker({required ImageSource source}) async =>
          XFile.fromData(
            Uint8List.fromList(const [1, 2, 3]),
            name: 'proof.gif',
            mimeType: 'image/gif',
          );
      await openDialog(
        tester,
        requiresProof: true,
        pickImage: picker,
      );
      await tester.tap(find.text('Gallery'));
      await tester.pumpAndSettle();
      expect(
        find.text('Photo must be a JPEG, PNG, or WebP image.'),
        findsOneWidget,
      );
      // Still blocked: the rejected file must not satisfy the requirement.
      await tester.tap(find.text('Mark failed'));
      await tester.pump();
      expect(
        find.text('Attach a proof image before marking failed.'),
        findsOneWidget,
      );
    });

    testWidgets('attached photo can be removed before confirming', (
      tester,
    ) async {
      Future<XFile?> picker({required ImageSource source}) async =>
          XFile.fromData(_pngBytes, name: 'proof.png', mimeType: 'image/png');
      await openDialog(
        tester,
        requiresProof: true,
        pickImage: picker,
      );
      await tester.tap(find.text('Gallery'));
      await tester.pumpAndSettle();
      expect(find.byType(Image), findsOneWidget);
      await tester.tap(find.byTooltip('Remove photo'));
      await tester.pumpAndSettle();
      expect(find.byType(Image), findsNothing);
      expect(find.text('Take photo'), findsOneWidget);
    });
  });
}
