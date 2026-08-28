import 'dart:typed_data';

import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_screens.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_controller.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_models.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_screens.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

/// Production-composition regression tests for the Milk Test routes.
///
/// These tests pump the ACTUAL production application (`DoodhDirectApp` with
/// the real `routerProvider`) and navigate via the production GoRouter route
/// table. They verify the full widget composition that a Delivery Boy reaches
/// through `Delivery → Perform milk test`:
///
///   * `/delivery/:deliveryId/milk-test` → `StaffMilkTestScreen` contains NO
///     map widget and NO "Google Maps API key is not configured" error text,
///     both before and after an image upload/replace cycle.
///   * `/customer/addresses/new` → `CustomerAddressEditScreen` still embeds
///     the `GoogleMapCoordinatePicker` (the ONLY map-bearing screen).
///   * `/deliveries/:deliveryId` → `CustomerDeliveryDetailScreen` (Delivery
///     Tracking) still shows its live-location card and no map regression.
///
/// The test deliberately does NOT seed `notificationControllerProvider`
/// because `DoodhDirectApp` and `RoleHomeScreen` watch it; the real
/// `NotificationController` degrades gracefully in the test environment
/// (Firebase push init returns false without a web configuration).
void main() {
  group('Milk test route composition (production app)', () {
    testWidgets(
      'DELIVERY_STAFF Perform Milk Test screen contains no map and no '
      'Google Maps error, before and after image upload', (tester) async {
        final milkController = _SeededMilkTestController(
          const MilkTestState(staffTest: null),
        );
        final container = await _pumpProductionApp(
          tester,
          session: _staffSession,
          milkController: milkController,
        );

        // Navigate exactly as the app does: home → delivery list → detail →
        // Perform milk test (StaffMilkTestScreen), via the production router.
        container.read(routerProvider).go('/delivery/delivery-1/milk-test');
        await tester.pumpAndSettle();

        expect(find.byType(StaffMilkTestScreen), findsOneWidget);
        expect(find.text('Perform milk test'), findsOneWidget);
        expect(find.byType(GoogleMap), findsNothing);
        expect(find.byType(GoogleMapCoordinatePicker), findsNothing);
        expect(
          find.textContaining('Google Maps API key is not configured'),
          findsNothing,
        );
        expect(tester.takeException(), isNull);

        // Simulate the post-upload rebuild that the UAT report observed: the
        // image list is populated and the composition is re-rendered.
        final uploaded = await container
            .read(milkTestControllerProvider.notifier)
            .uploadImage(
              'delivery-1',
              'test-1',
              bytes: Uint8List.fromList([1, 2, 3]),
              fileName: 'reading.jpg',
              contentType: 'image/jpeg',
            );
        expect(uploaded, isTrue);
        await tester.pumpAndSettle();

        // Image functionality is preserved (replace action available for a
        // requested test with an uploaded image)...
        expect(find.text('Replace Image'), findsOneWidget);
        expect(find.text('Test images'), findsOneWidget);
        // ...and the map STILL does not appear after the upload-triggered
        // rebuild. This directly addresses "why it appears after image
        // upload": the StaffMilkTestScreen rebuilds only its own body state,
        // which contains no map widget.
        expect(find.byType(GoogleMap), findsNothing);
        expect(find.byType(GoogleMapCoordinatePicker), findsNothing);
        expect(
          find.textContaining('Google Maps API key is not configured'),
          findsNothing,
        );
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'customer address picker still embeds GoogleMapsCoordinatePicker '
      '(map retained on its intended screen)', (tester) async {
        await _pumpProductionApp(
          tester,
          session: _customerSession,
          milkController: _SeededMilkTestController(
            const MilkTestState(),
          ),
        );

        // The address editor is the ONLY production screen that embeds the
        // GoogleMapCoordinatePicker.
        final container = _lastContainer!;
        container.read(routerProvider).go('/customer/addresses/new');
        await tester.pumpAndSettle();

        expect(find.byType(CustomerAddressEditScreen), findsOneWidget);
        expect(find.text('Add address'), findsOneWidget);
        expect(find.byType(GoogleMapCoordinatePicker), findsOneWidget);

        // In the test environment the web maps script stub throws the exact
        // runtime error string; the picker surfaces it in its _MapStatePanel.
        // This proves the error text can only originate from this screen and
        // is expected HERE, not on the milk-test screen.
        expect(
          find.textContaining('Google Maps API key is not configured'),
          findsOneWidget,
        );
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'delivery tracking screen keeps its live-location card and no map '
      'regression', (tester) async {
        final deliveryController = _SeededDeliveryController(
          const DeliveryState(selectedCustomerDelivery: null),
        );
        final container = await _pumpProductionApp(
          tester,
          session: _customerSession,
          deliveryController: deliveryController,
          milkController: _SeededMilkTestController(const MilkTestState()),
        );

        container.read(routerProvider).go('/deliveries/delivery-1');
        await tester.pumpAndSettle();

        expect(find.byType(CustomerDeliveryDetailScreen), findsOneWidget);
        expect(find.text('Delivery tracking'), findsOneWidget);

        // Seed the tracking-active delivery so the live-location card shows.
        deliveryController.state = DeliveryState(
          selectedCustomerDelivery: CustomerDelivery(
            deliveryId: 'delivery-1',
            sourceType: DeliverySourceType.oneTimeOrder,
            referenceNumber: 'ORD-1001',
            status: DeliveryStatus.outForDelivery,
            scheduledDate: DateTime(2026, 8, 16),
            destinationAddress: '1 Main Street, Pune',
            assignedEmployeeId: 'employee-1',
            assignedEmployeeName: 'Delivery Agent',
            isTrackingActive: true,
            latestLocation: DeliveryLocation(
              latitude: 18.5204,
              longitude: 73.8567,
              accuracyMetres: 5.5,
              recordedAt: DateTime.utc(2026, 8, 16, 10, 15),
            ),
            completedAt: null,
            failedAt: null,
            failureReason: null,
            activeOtp: '482913',
          ),
        );
        await tester.pumpAndSettle();

        await tester.drag(find.byType(ListView), const Offset(0, -400));
        await tester.pumpAndSettle();

        expect(find.text('Live tracking is active'), findsOneWidget);
        expect(
          find.text(
            'Your delivery partner is currently sharing an updated location.',
          ),
          findsOneWidget,
        );
        // Delivery Tracking is a text/icon card — not a Google Map — so no
        // GoogleMapCoordinatePicker appears here either.
        expect(find.byType(GoogleMapCoordinatePicker), findsNothing);
        expect(tester.takeException(), isNull);
      },
    );
  });
}

ProviderContainer? _lastContainer;

/// Pumps the ACTUAL production app (`DoodhDirectApp`) inside a
/// [ProviderContainer] whose repositories/controllers are replaced with
/// deterministic fakes, then settles until the session redirects to the role
/// home screen. Mirrors the proven app-level harness in `widget_test.dart`.
Future<ProviderContainer> _pumpProductionApp(
  WidgetTester tester, {
  required AuthSession session,
  _SeededMilkTestController? milkController,
  _SeededDeliveryController? deliveryController,
}) async {
  final auth = _SeededAuthRepository(session);
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(auth),
      orderRepositoryProvider.overrideWithValue(_FakeOrderRepository()),
      if (milkController != null)
        milkTestControllerProvider.overrideWith(() => milkController),
      if (deliveryController != null)
        deliveryControllerProvider.overrideWith(() => deliveryController),
      notificationControllerProvider.overrideWith(
        _SeededNotificationController.new,
      ),
    ],
  );
  addTearDown(container.dispose);
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const DoodhDirectApp(),
    ),
  );
  await tester.pumpAndSettle();

  _lastContainer = container;
  return container;
}

class _SeededAuthRepository extends AuthRepository {
  _SeededAuthRepository(this.session);

  final AuthSession session;

  @override
  Future<AuthSession?> restore() async => session;
}

class _SeededNotificationController extends NotificationController {
  @override
  NotificationState build() => const NotificationState();
}

class _SeededMilkTestController extends MilkTestController {
  _SeededMilkTestController(this.initialState);

  final MilkTestState initialState;

  @override
  MilkTestState build() => initialState;

  @override
  Future<void> loadForCustomer(String deliveryId) async {}

  @override
  Future<void> loadForStaff(String deliveryId) async {}

  @override
  Future<bool> uploadImage(
    String deliveryId,
    String milkTestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    state = state.copyWith(staffTest: _staffTest(images: [testImage]));
    return true;
  }
}

class _SeededDeliveryController extends DeliveryController {
  _SeededDeliveryController(this.initialState);

  final DeliveryState initialState;

  @override
  DeliveryState build() => initialState;

  @override
  Future<void> loadCustomerDeliveries() async {}

  @override
  Future<void> loadCustomerDelivery(String id) async {}

  @override
  Future<void> loadToday({DateTime? date}) async {}

  @override
  Future<void> loadStaffDelivery(String id) async {}

  @override
  Future<void> loadManagedDelivery(String id) async {}
}

class _FakeOrderRepository extends OrderRepository {
  _FakeOrderRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<OrderSummary>> getMine(String token) async => const [];
}

final _staffSession = AuthSession(
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

final _customerSession = AuthSession(
  user: const AuthUser(
    publicUserId: '00000000-0000-0000-0000-000000000001',
    displayName: 'Customer User',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-access-token',
  refreshToken: 'customer-refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
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
