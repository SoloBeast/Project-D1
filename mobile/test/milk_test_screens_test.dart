import 'dart:typed_data';

import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_controller.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_models.dart';
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
          pickImage: (source) async {
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

      await tester.tap(find.text('Gallery'));
      await tester.pumpAndSettle();

      expect(selectedSource, ImageSource.gallery);
      expect(controller.uploadedFileName, 'milk-test-image.png');
      expect(controller.uploadedContentType, 'image/png');
      expect(controller.uploadedBytes, [0x89, 0x50, 0x4e, 0x47]);
      expect(find.text('Test image uploaded.'), findsOneWidget);
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

  test('milk-test helpers normalize supported image types and paths', () {
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
      milkTestImageUrl('/api/v1/milk-tests/test/images/1'),
      'http://localhost:5209/api/v1/milk-tests/test/images/1',
    );
    expect(
      milkTestImageUrl('https://cdn.example.test/image.jpg'),
      'https://cdn.example.test/image.jpg',
    );
    expect(
      formatMilkTestDateTime(DateTime(2026, 8, 17, 9, 5)),
      '17/08/2026 09:05',
    );
  });
}

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededMilkTestController controller,
) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        milkTestControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(_SeededSessionController.new),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pumpAndSettle();
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
}

class _SeededSessionController extends SessionController {
  @override
  SessionState build() => SessionState.authenticated(_session);
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

StaffMilkTest _staffTest({required List<MilkTestImage> images}) =>
    StaffMilkTest(
      milkTestId: 'test-1',
      deliveryId: 'delivery-1',
      status: MilkTestStatus.requested,
      customerDecision: MilkTestCustomerDecision.pending,
      requestedAtUtc: DateTime.utc(2026, 8, 17, 9),
      completedAtUtc: null,
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
