import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_controller.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_models.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_repository.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';

void main() {
  group('customer milk-test screen', () {
    testWidgets('shows request state and requests the current delivery', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(const MilkTestState());
      await _pump(
        tester,
        const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(find.text('No test requested'), findsOneWidget);
      expect(find.text('Request test'), findsOneWidget);

      await tester.tap(find.text('Request test'));
      await tester.pumpAndSettle();

      expect(controller.requestedDeliveryId, 'delivery-1');
      expect(
        find.text(
          'The assigned delivery employee will perform the test at your doorstep.',
        ),
        findsOneWidget,
      );
      expect(find.text('Confirm'), findsNothing);
      expect(find.text('Reject'), findsNothing);
    });

    testWidgets('shows an existing pending request without a request action', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(
          customerTest: _customerTest(status: MilkTestStatus.requested),
        ),
      );
      await _pump(
        tester,
        const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(find.text('Requested'), findsNWidgets(2));
      expect(
        find.text(
          'The assigned delivery employee will perform the test at your doorstep.',
        ),
        findsOneWidget,
      );
      expect(find.text('Request test'), findsNothing);
      expect(controller.requestedDeliveryId, isNull);
    });

    testWidgets('shows explicit notice for an unsupported business state', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(
          customerTest: _customerTest(status: MilkTestStatus.unknown),
        ),
      );
      await _pump(
        tester,
        const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(
        find.text(
          'This milk test has a status the app does not yet support. Refresh later or contact support.',
        ),
        findsOneWidget,
      );
      expect(find.text('Request test'), findsNothing);
      expect(controller.requestedDeliveryId, isNull);
    });

    testWidgets('shows completed images and decision controls, then confirms', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(customerTest: _customerTest()),
      );
      await _pump(
        tester,
        const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(find.text('Completed'), findsNWidgets(2));
      expect(find.text('Test images'), findsOneWidget);
      expect(find.bySemanticsLabel('Milk test image 1'), findsOneWidget);
      expect(find.text('Confirm'), findsOneWidget);
      expect(find.text('Reject'), findsOneWidget);
      expect(find.text('FAT'), findsNothing);
      expect(find.text('6.5'), findsNothing);

      await tester.tap(find.text('Confirm'));
      await tester.pumpAndSettle();
      expect(find.text('Confirm milk test?'), findsOneWidget);

      await tester.enterText(
        find.widgetWithText(TextField, 'Remarks (optional)'),
        'Accepted at doorstep',
      );
      await tester.tap(find.text('Confirm').last);
      await tester.pumpAndSettle();

      expect(controller.confirmedMilkTestId, 'test-1');
      expect(controller.confirmRemarks, 'Accepted at doorstep');
    });

    testWidgets('hides decision controls after a terminal decision', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(
          customerTest: _customerTest(
            decision: MilkTestCustomerDecision.rejected,
          ),
        ),
      );
      await _pump(
        tester,
        const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(find.text('Rejected'), findsNWidgets(2));
      expect(find.text('Reject'), findsNothing);
      expect(find.text('Confirm'), findsNothing);
    });
  });

  group('staff milk-test screen', () {
    testWidgets('disables completion until an image is uploaded', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: const [])),
      );
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      final completeButton = find.widgetWithText(FilledButton, 'Complete test');
      expect(completeButton, findsOneWidget);
      expect(tester.widget<FilledButton>(completeButton).onPressed, isNull);
      expect(
        find.text('Upload at least one test image before completion.'),
        findsOneWidget,
      );
    });

    testWidgets('opens camera selection and cancels without uploading', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: const [])),
      );
      ImageSource? selectedSource;
      await _pump(
        tester,
        StaffMilkTestScreen(
          deliveryId: 'delivery-1',
          pickImage: ({required source}) async {
            selectedSource = source;
            return XFile.fromData(
              Uint8List.fromList([0x89, 0x50, 0x4e, 0x47]),
              name: 'reading.png',
              mimeType: 'image/png',
            );
          },
        ),
        controller,
      );

      await tester.tap(find.text('Add Test Image'));
      await tester.pumpAndSettle();
      expect(find.text('Take photo'), findsOneWidget);
      await tester.tap(find.text('Take photo'));
      await tester.pumpAndSettle();

      expect(selectedSource, ImageSource.camera);
      expect(find.text('Use this test image?'), findsOneWidget);
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(controller.uploadedBytes, isNull);
      expect(find.text('Add Test Image'), findsOneWidget);
    });

    testWidgets('forwards gallery selection and uploads bytes and MIME', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: const [])),
      );
      ImageSource? selectedSource;
      await _pump(
        tester,
        StaffMilkTestScreen(
          deliveryId: 'delivery-1',
          pickImage: ({required source}) async {
            selectedSource = source;
            return XFile.fromData(
              Uint8List.fromList([0x89, 0x50, 0x4e, 0x47]),
              name: 'reading.png',
              mimeType: 'image/png',
            );
          },
        ),
        controller,
      );

      await tester.tap(find.text('Add Test Image'));
      await tester.pumpAndSettle();
      expect(find.text('Choose from gallery'), findsOneWidget);
      await tester.tap(find.text('Choose from gallery'));
      await tester.pumpAndSettle();

      expect(selectedSource, ImageSource.gallery);
      expect(find.text('Use this test image?'), findsOneWidget);
      await tester.tap(find.text('Use Photo'));
      await tester.pumpAndSettle();
      expect(controller.uploadedFileName, 'milk-test-image.png');
      expect(controller.uploadedContentType, 'image/png');
      expect(controller.uploadedBytes, [0x89, 0x50, 0x4e, 0x47]);
      expect(find.text('Test image uploaded.'), findsOneWidget);
      expect(find.text('Camera'), findsNothing);
      expect(find.text('Gallery'), findsNothing);
    });

    testWidgets('labels a newly selected image as replaceable', (tester) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: const [])),
      );
      await _pump(
        tester,
        StaffMilkTestScreen(
          deliveryId: 'delivery-1',
          pickImage: ({required source}) async => XFile.fromData(
            Uint8List.fromList([0x89, 0x50, 0x4e, 0x47]),
            name: 'reading.png',
            mimeType: 'image/png',
          ),
        ),
        controller,
      );

      await tester.tap(find.text('Add Test Image'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Choose from gallery'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use Photo'));
      await tester.pumpAndSettle();

      expect(find.text('Replace Image'), findsOneWidget);
      expect(find.byType(Image), findsOneWidget);
    });

    testWidgets('validates reading fields before completion', (tester) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      );
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      await tester.ensureVisible(find.text('Complete test'));
      await tester.tap(find.text('Complete test'));
      await tester.pump();

      expect(find.text('Enter a number'), findsNWidgets(2));
      expect(controller.completedMilkTestId, isNull);
    });

    testWidgets('submits configured readings and remarks after validation', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      );
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      final fields = find.byType(TextFormField);
      await tester.enterText(fields.at(2), '6.5');
      await tester.enterText(fields.at(6), '8.7');
      await tester.enterText(
        find.widgetWithText(TextFormField, 'Staff remarks (optional)'),
        'Doorstep reading',
      );
      await tester.ensureVisible(find.text('Complete test'));
      await tester.tap(find.text('Complete test'));
      await tester.pumpAndSettle();

      expect(controller.completedMilkTestId, 'test-1');
      expect(controller.completedParameters, hasLength(2));
      expect(controller.completedParameters.first.value, 6.5);
      expect(controller.completedRemarks, 'Doorstep reading');
      expect(find.text('Milk test completed.'), findsOneWidget);
    });
  });

  group('milk-test image lifecycle', () {
    testWidgets('shows uploaded images with filenames while editable', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      );
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(find.bySemanticsLabel('Milk test image 1'), findsOneWidget);
      expect(find.text('reading.jpg'), findsOneWidget);
    });

    testWidgets('shows Delete action for each image before confirmation', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      );
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(find.text('Delete'), findsOneWidget);
      expect(find.text('Replace'), findsOneWidget);
    });

    testWidgets('deletes an image only after confirmation', (tester) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      );
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      expect(find.text('Delete this test image?'), findsOneWidget);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(controller.deletedImageId, isNull);
      expect(find.bySemanticsLabel('Milk test image 1'), findsOneWidget);

      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
      await tester.pumpAndSettle();

      expect(controller.deletedImageId, 'image-1');
      expect(find.text('Test image deleted.'), findsOneWidget);
      expect(
        find.text('No test images have been uploaded.'),
        findsOneWidget,
      );
    });

    testWidgets('adds another image while the test is editable', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: const [])),
      );
      await _pump(
        tester,
        StaffMilkTestScreen(
          deliveryId: 'delivery-1',
          pickImage: ({required source}) async => XFile.fromData(
            Uint8List.fromList([0x89, 0x50, 0x4e, 0x47]),
            name: 'second.png',
            mimeType: 'image/png',
          ),
        ),
        controller,
      );

      await tester.tap(find.text('Add Test Image'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Choose from gallery'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use Photo'));
      await tester.pumpAndSettle();

      expect(controller.uploadedBytes, [0x89, 0x50, 0x4e, 0x47]);
      expect(find.bySemanticsLabel('Milk test image 1'), findsOneWidget);
    });

    testWidgets('replaces the selected image instead of appending', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      );
      await _pump(
        tester,
        StaffMilkTestScreen(
          deliveryId: 'delivery-1',
          pickImage: ({required source}) async => XFile.fromData(
            Uint8List.fromList([0x89, 0x50, 0x4e, 0x47]),
            name: 'replacement.png',
            mimeType: 'image/png',
          ),
        ),
        controller,
      );

      await tester.tap(find.text('Replace'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Choose from gallery'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use Photo'));
      await tester.pumpAndSettle();

      expect(controller.replacedAsStaffImageId, 'image-1');
      expect(find.text('Test image replaced.'), findsOneWidget);
    });

    testWidgets('removes the old image after a successful replacement', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      );
      await _pump(
        tester,
        StaffMilkTestScreen(
          deliveryId: 'delivery-1',
          pickImage: ({required source}) async => XFile.fromData(
            Uint8List.fromList([0x89, 0x50, 0x4e, 0x47]),
            name: 'replacement.png',
            mimeType: 'image/png',
          ),
        ),
        controller,
      );

      await tester.tap(find.text('Replace'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Choose from gallery'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use Photo'));
      await tester.pumpAndSettle();

      expect(find.text('replacement.jpg'), findsOneWidget);
      expect(find.text('reading.jpg'), findsNothing);
      expect(find.bySemanticsLabel('Milk test image 1'), findsOneWidget);
    });

    testWidgets('keeps the original image when replacement fails', (
      tester,
    ) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      )..failReplaces = true;
      await _pump(
        tester,
        StaffMilkTestScreen(
          deliveryId: 'delivery-1',
          pickImage: ({required source}) async => XFile.fromData(
            Uint8List.fromList([0x89, 0x50, 0x4e, 0x47]),
            name: 'replacement.png',
            mimeType: 'image/png',
          ),
        ),
        controller,
      );

      await tester.tap(find.text('Replace'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Choose from gallery'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use Photo'));
      await tester.pumpAndSettle();

      expect(controller.replacedAsStaffImageId, 'image-1');
      expect(find.text('reading.jpg'), findsOneWidget);
      expect(find.text('Test image replaced.'), findsNothing);
    });

    testWidgets('is read-only after the customer confirms', (tester) async {
      final customerController = _SeededMilkTestController(
        MilkTestState(
          customerTest: _customerTest(
            status: MilkTestStatus.completed,
            decision: MilkTestCustomerDecision.confirmed,
          ),
        ),
      );
      await _pump(
        tester,
        const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
        customerController,
      );

      expect(find.bySemanticsLabel('Milk test image 1'), findsOneWidget);
      expect(find.text('Replace'), findsNothing);
      expect(find.text('Delete'), findsNothing);
      expect(find.text('Confirm'), findsNothing);

      final staffController = _SeededMilkTestController(
        MilkTestState(
          staffTest: _staffTest(
            images: [testImage],
            status: MilkTestStatus.completed,
          ),
        ),
      );
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        staffController,
      );

      expect(find.text('Delete'), findsNothing);
      expect(find.text('Replace'), findsNothing);
    });

    testWidgets('renders no Google Map on milk-test screens', (tester) async {
      final controller = _SeededMilkTestController(
        MilkTestState(customerTest: _customerTest()),
      );
      await _pump(
        tester,
        const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      expect(
        find.byWidgetPredicate(
          (widget) => widget.runtimeType.toString().contains('Map'),
        ),
        findsNothing,
      );
      expect(find.textContaining('Map'), findsNothing);
    });

    testWidgets(
      'loads protected image content via the authenticated client and '
      'renders the bytes as a memory image',
      (tester) async {
        final repository = _FakeMilkTestRepository()
          ..imageContent = (token, milkTestId, imageId) => ApiByteResponse(
            bytes: Uint8List.fromList(
              const [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
              ],
            ),
            contentType: 'image/png',
            fileName: 'reading.png',
          );
        final controller = _SeededMilkTestController(
          MilkTestState(staffTest: _staffTest(images: [testImage])),
        );
        await _pump(
          tester,
          const StaffMilkTestScreen(deliveryId: 'delivery-1'),
          controller,
          repository: repository,
        );

        final image = tester.widget<Image>(
          find.byWidgetPredicate(
            (widget) => widget is Image && widget.image is MemoryImage,
          ),
        );
        final memoryImage = image.image as MemoryImage;
        expect(memoryImage.bytes.length, greaterThan(8));

        expect(repository.contentRequests, hasLength(1));
        final request = repository.contentRequests.single;
        expect(request.token, 'milk-token');
        expect(request.milkTestId, 'test-1');
        expect(request.imageId, 'image-1');
      },
    );

    testWidgets(
      'shows a controlled no-access state when image content returns 403',
      (tester) async {
        final repository = _FakeMilkTestRepository()
          ..imageContent = (_, _, _) =>
              throw const ApiException(403, 'FORBIDDEN', 'Forbidden');
        final controller = _SeededMilkTestController(
          MilkTestState(staffTest: _staffTest(images: [testImage])),
        );
        await _pump(
          tester,
          const StaffMilkTestScreen(deliveryId: 'delivery-1'),
          controller,
          repository: repository,
        );

        expect(
          find.bySemanticsLabel(RegExp('^Milk test image 1')),
          findsOneWidget,
        );
        expect(find.byIcon(Icons.lock_outline), findsOneWidget);
        expect(
          find.text('You do not have access to this image.'),
          findsOneWidget,
        );
      },
    );

    testWidgets(
      'never requests protected content without an authenticated session',
      (tester) async {
        final repository = _FakeMilkTestRepository();
        final controller = _SeededMilkTestController(
          MilkTestState(customerTest: _customerTest()),
        );
        await _pump(
          tester,
          const CustomerMilkTestScreen(deliveryId: 'delivery-1'),
          controller,
          repository: repository,
          session: const SessionState.unauthenticated(),
        );

        expect(repository.contentRequests, isEmpty);
        expect(
          find.bySemanticsLabel(RegExp('^Milk test image 1')),
          findsOneWidget,
        );
        expect(
          find.text('You do not have access to this image.'),
          findsOneWidget,
        );
      },
    );

    testWidgets('keeps the image when deletion fails', (tester) async {
      final controller = _SeededMilkTestController(
        MilkTestState(staffTest: _staffTest(images: [testImage])),
      )..failDeletes = true;
      await _pump(
        tester,
        const StaffMilkTestScreen(deliveryId: 'delivery-1'),
        controller,
      );

      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
      await tester.pumpAndSettle();

      expect(controller.deletedImageId, isNull);
      expect(find.bySemanticsLabel('Milk test image 1'), findsOneWidget);
      expect(find.text('reading.jpg'), findsOneWidget);
      expect(
        find.text('No test images have been uploaded.'),
        findsNothing,
      );
    });
  });

  test('milk-test helpers normalize supported image types', () {
    expect(
      resolveMilkTestImageContentType('photo.bin', ' IMAGE/JPEG '),
      'image/jpeg',
    );
    expect(resolveMilkTestImageContentType('photo.JPG', null), 'image/jpeg');
    expect(
      resolveMilkTestImageContentType('photo.png', 'application/octet-stream'),
      'image/png',
    );
    expect(resolveMilkTestImageContentType('photo.gif', null), isNull);
    expect(
      formatMilkTestDateTime(DateTime(2026, 8, 17, 9, 5)),
      '17/08/2026 09:05',
    );
  });
}

Future<_FakeMilkTestRepository> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededMilkTestController controller, {
  _FakeMilkTestRepository? repository,
  SessionState? session,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final fakeRepository = repository ?? _FakeMilkTestRepository();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        milkTestControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: session),
        ),
        milkTestRepositoryProvider.overrideWithValue(fakeRepository),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pumpAndSettle();
  return fakeRepository;
}

class _SeededMilkTestController extends MilkTestController {
  _SeededMilkTestController(this.initialState);

  final MilkTestState initialState;
  String? requestedDeliveryId;
  String? uploadedFileName;
  String? uploadedContentType;
  List<int>? uploadedBytes;
  String? completedMilkTestId;
  String? completedRemarks;
  List<MilkTestParameter> completedParameters = const [];
  String? confirmedMilkTestId;
  String? confirmRemarks;
  String? deletedImageId;
  String? replacedAsStaffImageId;
  String? replacedAsCustomerImageId;
  bool failDeletes = false;
  bool failReplaces = false;

  @override
  MilkTestState build() => initialState;

  @override
  Future<void> loadForCustomer(String deliveryId) async {}

  @override
  Future<void> loadForStaff(String deliveryId) async {}

  @override
  Future<bool> request(String deliveryId) async {
    requestedDeliveryId = deliveryId;
    state = state.copyWith(
      customerTest: _customerTest(status: MilkTestStatus.requested),
    );
    return true;
  }

  @override
  Future<bool> uploadImage(
    String deliveryId,
    String milkTestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    uploadedBytes = bytes;
    uploadedFileName = fileName;
    uploadedContentType = contentType;
    state = state.copyWith(staffTest: _staffTest(images: [testImage]));
    return true;
  }

  @override
  Future<bool> complete(
    String milkTestId, {
    required List<MilkTestParameter> parameters,
    String? remarks,
  }) async {
    completedMilkTestId = milkTestId;
    completedParameters = parameters;
    completedRemarks = remarks;
    return true;
  }

  @override
  Future<bool> confirm(String milkTestId, {String? remarks}) async {
    confirmedMilkTestId = milkTestId;
    confirmRemarks = remarks;
    return true;
  }

  @override
  Future<bool> deleteImage(
    String deliveryId,
    String milkTestId,
    String imageId,
  ) async {
    if (failDeletes) return false;
    deletedImageId = imageId;
    final test = state.staffTest!;
    state = state.copyWith(
      staffTest: _staffTest(
        images: test.images
            .where((image) => image.imageId != imageId)
            .toList(growable: false),
      ),
    );
    return true;
  }

  @override
  Future<bool> replaceImageAsStaff(
    String deliveryId,
    String milkTestId,
    String imageId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    replacedAsStaffImageId = imageId;
    if (failReplaces) return false;
    final test = state.staffTest!;
    state = state.copyWith(
      staffTest: _staffTest(
        images: [
          for (final image in test.images)
            if (image.imageId == imageId)
              MilkTestImage(
                imageId: 'image-2',
                fileName: 'replacement.jpg',
                contentType: contentType,
                fileSize: bytes.length,
                uploadedAtUtc: DateTime.utc(2026, 8, 17, 9, 6),
                contentPath:
                    '/api/v1/milk-tests/test-1/images/image-2/content',
              )
            else
              image,
        ],
      ),
    );
    return true;
  }

  @override
  Future<bool> replaceImageAsCustomer(
    String deliveryId,
    String milkTestId,
    String imageId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    replacedAsCustomerImageId = imageId;
    if (failReplaces) return false;
    final test = state.customerTest!;
    state = state.copyWith(
      customerTest: CustomerMilkTest(
        milkTestId: test.milkTestId,
        deliveryId: test.deliveryId,
        status: test.status,
        customerDecision: test.customerDecision,
        requestedAtUtc: test.requestedAtUtc,
        completedAtUtc: test.completedAtUtc,
        confirmedAtUtc: test.confirmedAtUtc,
        rejectedAtUtc: test.rejectedAtUtc,
        customerRemarks: test.customerRemarks,
        images: [
          for (final image in test.images)
            if (image.imageId == imageId)
              MilkTestImage(
                imageId: 'image-2',
                fileName: 'replacement.jpg',
                contentType: contentType,
                fileSize: bytes.length,
                uploadedAtUtc: DateTime.utc(2026, 8, 17, 9, 6),
                contentPath:
                    '/api/v1/milk-tests/test-1/images/image-2/content',
              )
            else
              image,
        ],
      ),
    );
    return true;
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({this.session});

  final SessionState? session;

  @override
  SessionState build() => session ?? SessionState.authenticated(_session);
}

class _FakeMilkTestRepository extends MilkTestRepository {
  _FakeMilkTestRepository()
      : super(api: ApiClient(baseUrl: 'http://localhost.test'));

  ApiByteResponse Function(String token, String milkTestId, String imageId)?
      imageContent;

  final List<_ImageContentRequest> contentRequests = [];

  @override
  Future<ApiByteResponse> getImageContent(
    String token,
    String milkTestId,
    String imageId,
  ) async {
    contentRequests.add(
      _ImageContentRequest(
        token: token,
        milkTestId: milkTestId,
        imageId: imageId,
      ),
    );
    final handler = imageContent;
    if (handler == null) {
      return ApiByteResponse(
        bytes: Uint8List.fromList(
          const [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
          ],
        ),
        contentType: 'image/png',
        fileName: 'reading.png',
      );
    }
    return handler(token, milkTestId, imageId);
  }
}

class _ImageContentRequest {
  const _ImageContentRequest({
    required this.token,
    required this.milkTestId,
    required this.imageId,
  });

  final String token;
  final String milkTestId;
  final String imageId;
}

CustomerMilkTest _customerTest({
  MilkTestStatus status = MilkTestStatus.completed,
  MilkTestCustomerDecision decision = MilkTestCustomerDecision.pending,
}) => CustomerMilkTest(
  milkTestId: 'test-1',
  deliveryId: 'delivery-1',
  status: status,
  customerDecision: decision,
  requestedAtUtc: DateTime.utc(2026, 8, 17, 9),
  completedAtUtc: status == MilkTestStatus.completed
      ? DateTime.utc(2026, 8, 17, 9, 10)
      : null,
  confirmedAtUtc: null,
  rejectedAtUtc: decision == MilkTestCustomerDecision.rejected
      ? DateTime.utc(2026, 8, 17, 9, 12)
      : null,
  customerRemarks: decision.isTerminal ? 'Customer decision' : null,
  images: status == MilkTestStatus.completed ? [testImage] : const [],
);

StaffMilkTest _staffTest({
  required List<MilkTestImage> images,
  MilkTestStatus status = MilkTestStatus.requested,
  MilkTestCustomerDecision decision = MilkTestCustomerDecision.pending,
}) =>
    StaffMilkTest(
      milkTestId: 'test-1',
      deliveryId: 'delivery-1',
      status: status,
      customerDecision: decision,
      requestedAtUtc: DateTime.utc(2026, 8, 17, 9),
      completedAtUtc: status == MilkTestStatus.completed
          ? DateTime.utc(2026, 8, 17, 9, 10)
          : null,
      staffRemarks: null,
      confirmedAtUtc: null,
      rejectedAtUtc: null,
      customerRemarks: null,
      parameters: const [],
      images: images,
    );

final testImage = MilkTestImage(
  imageId: 'image-1',
  fileName: 'reading.jpg',
  contentType: 'image/jpeg',
  fileSize: 2048,
  uploadedAtUtc: DateTime.utc(2026, 8, 17, 9, 5),
  contentPath: '/api/v1/milk-tests/test-1/images/image-1/content',
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Delivery Staff',
    email: null,
    mobile: '9999999999',
    roles: ['DELIVERY_STAFF'],
    permissions: ['DELIVERY.READ'],
    branchIds: [7],
  ),
  accessToken: 'milk-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
