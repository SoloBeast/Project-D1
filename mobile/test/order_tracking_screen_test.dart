import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

final _authenticatedSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2026, 8, 16, 6),
  refreshTokenExpiresAtUtc: DateTime.utc(2026, 9, 16),
);

class _AuthenticatedAuthRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _authenticatedSession;
}

class _SeededDeliveryController extends DeliveryController {
  _SeededDeliveryController(this.initialState);

  final DeliveryState initialState;

  @override
  DeliveryState build() => initialState;

  @override
  Future<void> loadCustomerDelivery(String id) async {
    // Keep the seeded snapshot; no network access in these tests.
  }
}

CustomerDelivery _delivery({
  DeliveryStatus status = DeliveryStatus.outForDelivery,
  bool tracking = false,
  String? activeOtp = '482913',
  String? employeeName = 'Delivery Agent',
  DateTime? completedAt,
  DateTime? failedAt,
  String? failureReason,
}) => CustomerDelivery(
  deliveryId: 'delivery-1',
  sourceType: DeliverySourceType.oneTimeOrder,
  referenceNumber: 'ORD-1001',
  status: status,
  scheduledDate: DateTime(2026, 8, 16),
  destinationAddress: '1 Main Street, Pune',
  assignedEmployeeId: 'employee-1',
  assignedEmployeeName: employeeName,
  isTrackingActive: tracking,
  latestLocation: tracking
      ? DeliveryLocation(
          latitude: 18.5204,
          longitude: 73.8567,
          accuracyMetres: 5.5,
          recordedAt: DateTime.utc(2026, 8, 16, 10, 15),
        )
      : null,
  completedAt: completedAt,
  failedAt: failedAt,
  failureReason: failureReason,
  activeOtp: activeOtp,
);

GoRouter _trackingRouter() => GoRouter(
  initialLocation: '/deliveries/delivery-1',
  routes: [
    GoRoute(
      path: '/deliveries/:deliveryId',
      builder: (context, state) => CustomerDeliveryDetailScreen(
        deliveryId: state.pathParameters['deliveryId']!,
      ),
    ),
    GoRoute(
      path: '/deliveries/:deliveryId/milk-test',
      builder: (context, state) =>
          const Scaffold(body: Text('Milk test target')),
    ),
    GoRoute(
      path: '/deliveries/:deliveryId/refund-replacement',
      builder: (context, state) =>
          const Scaffold(body: Text('Refund target')),
    ),
  ],
);

/// Tall surface so secondary sections and CTAs are built and hittable.
void _useSurface(WidgetTester tester, Size size) {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = size;
  addTearDown(tester.view.reset);
}

Future<void> _pumpTracking(
  WidgetTester tester, {
  required DeliveryState state,
  GoRouter? router,
  bool settle = true,
}) async {
  final goRouter = router ?? _trackingRouter();
  addTearDown(goRouter.dispose);
  await tester.pumpWidget(
    ProviderScope(
      // Fresh scope per pump: consecutive pumps in one test must not reuse
      // the previous scope's overridden provider state.
      key: UniqueKey(),
      overrides: [
        authRepositoryProvider.overrideWithValue(_AuthenticatedAuthRepository()),
        deliveryControllerProvider.overrideWith(
          () => _SeededDeliveryController(state),
        ),
      ],
      child: MaterialApp.router(routerConfig: goRouter),
    ),
  );
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    // Loading panels use an indeterminate spinner that never settles.
    await tester.pump();
    await tester.pump();
  }
}

void main() {
  testWidgets('tracking hero shows reference, source, schedule and status', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(
          status: DeliveryStatus.outForDelivery,
        ),
      ),
    );

    expect(find.text('Delivery tracking'), findsOneWidget);
    expect(find.text('ORD-1001'), findsOneWidget);
    expect(find.text('One-time'), findsOneWidget);
    expect(find.text('Scheduled for 16/08/2026'), findsOneWidget);
    // The current status appears in the hero AND as the highlighted timeline
    // stage — two intentional renderings of the same server truth.
    expect(find.text('Out for delivery'), findsNWidgets(2));
    expect(
      find.text('Your delivery is on the way to your address.'),
      findsOneWidget,
    );
    // Screen readers get the full status announcement.
    expect(
      find.bySemanticsLabel(
        'Delivery ORD-1001. Status: Out for delivery. '
        'Your delivery is on the way to your address.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('timeline marks completed, current and pending stages', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(
          status: DeliveryStatus.outForDelivery,
        ),
      ),
    );

    expect(find.text('Delivery progress'), findsOneWidget);
    expect(find.text('Assigned'), findsOneWidget);
    expect(find.text('Picked up'), findsOneWidget);
    // Hero status + highlighted timeline stage.
    expect(find.text('Out for delivery'), findsNWidgets(2));
    expect(find.text('Arrived'), findsOneWidget);
    expect(find.text('Delivered'), findsOneWidget);
    expect(find.bySemanticsLabel('Assigned: completed'), findsOneWidget);
    expect(find.bySemanticsLabel('Picked up: completed'), findsOneWidget);
    expect(
      find.bySemanticsLabel('Out for delivery: current step'),
      findsOneWidget,
    );
    expect(find.bySemanticsLabel('Arrived: pending'), findsOneWidget);
    expect(find.bySemanticsLabel('Delivered: pending'), findsOneWidget);
  });

  testWidgets('delivered state confirms completion with the server timestamp', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(
          status: DeliveryStatus.delivered,
          activeOtp: null,
          completedAt: DateTime(2026, 8, 16, 9, 30),
        ),
      ),
    );

    expect(find.text('Delivered'), findsNWidgets(2));
    expect(find.text('Your delivery has been completed.'), findsOneWidget);
    expect(find.text('Completed on 16/08/2026'), findsOneWidget);
    // The final stage is the reached state of the delivery.
    expect(find.bySemanticsLabel('Delivered: current step'), findsOneWidget);
    expect(find.text('Delivery needs attention'), findsNothing);
  });

  testWidgets('failed state explains the reason without inventing detail', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(
          status: DeliveryStatus.failed,
          activeOtp: null,
          failureReason: 'Customer not available',
          failedAt: DateTime(2026, 8, 16, 18, 0),
        ),
      ),
    );

    // Failed appears in the hero only; the interrupted stage is 'Arrived'.
    expect(find.text('Failed'), findsOneWidget);
    expect(find.text('This delivery attempt could not be completed.'),
        findsOneWidget);
    expect(find.text('Delivery failed'), findsOneWidget);
    expect(
      find.text('Customer not available · Attempted on 16/08/2026'),
      findsOneWidget,
    );
    expect(find.text('Delivery needs attention'), findsOneWidget);
    // The stage that was interrupted is explicit, not just colour-coded.
    expect(
      find.bySemanticsLabel('Arrived: not completed, delivery failed'),
      findsOneWidget,
    );
  });

  testWidgets('assignment-pending state stays neutral and factual', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(
          status: DeliveryStatus.readyForAssignment,
          employeeName: null,
          activeOtp: null,
        ),
      ),
    );

    // Ready for assignment appears in the hero; the timeline has no such stage.
    expect(find.text('Ready for assignment'), findsOneWidget);
    expect(
      find.text('Your delivery is confirmed and waiting to be assigned.'),
      findsOneWidget,
    );
    expect(find.text('Assignment pending'), findsOneWidget);
    // With no stage completed yet, the first stage is the current one.
    expect(find.bySemanticsLabel('Assigned: current step'), findsOneWidget);
  });

  testWidgets('destination and delivery details use the order snapshot', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(),
      ),
    );

    expect(find.text('Delivery address'), findsOneWidget);
    expect(find.text('1 Main Street, Pune'), findsOneWidget);
    expect(find.text('Delivery handled by'), findsOneWidget);
    expect(find.text('Delivery Agent'), findsOneWidget);
  });

  testWidgets('live tracking availability follows the server flags', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(tracking: true),
      ),
    );
    expect(find.text('Live tracking is active'), findsOneWidget);
    expect(
      find.text(
        'Your delivery partner is currently sharing an updated location.',
      ),
      findsOneWidget,
    );
    // Customer tracking never renders raw coordinates.
    expect(find.textContaining('18.5204'), findsNothing);

    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(tracking: false),
      ),
    );
    expect(
      find.text(
        'Location becomes available while your delivery is on the way.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('delivery OTP appears only while an active code exists', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: DeliveryState(selectedCustomerDelivery: _delivery()),
    );
    expect(find.text('Delivery OTP'), findsOneWidget);
    expect(find.text('482913'), findsOneWidget);
    expect(
      find.bySemanticsLabel('Delivery OTP code 482913'),
      findsOneWidget,
    );

    await _pumpTracking(
      tester,
      state: DeliveryState(
        selectedCustomerDelivery: _delivery(activeOtp: null),
      ),
    );
    expect(find.text('Delivery OTP'), findsNothing);
    expect(find.text('482913'), findsNothing);
  });

  testWidgets('milk test and refund/replacement actions stay available', (
    tester,
  ) async {
    _useSurface(tester, const Size(1024, 1600));
    await _pumpTracking(
      tester,
      state: DeliveryState(selectedCustomerDelivery: _delivery()),
    );

    // Both support entries render with their existing copy.
    expect(find.text('Doorstep milk test'), findsOneWidget);
    expect(find.text('Refund or replacement'), findsOneWidget);
    expect(
      find.text('Open the test details and customer actions'),
      findsOneWidget,
    );
    expect(
      find.text('Check backend-confirmed availability for this delivery'),
      findsOneWidget,
    );
  });

  testWidgets('milk-test and refund-replacement routes stay registered', (
    tester,
  ) async {
    final router = _trackingRouter();
    await _pumpTracking(
      tester,
      state: DeliveryState(selectedCustomerDelivery: _delivery()),
      router: router,
    );

    router.go('/deliveries/delivery-1/milk-test');
    await tester.pumpAndSettle();
    expect(
      router.routerDelegate.currentConfiguration.uri.path,
      '/deliveries/delivery-1/milk-test',
    );
    expect(find.text('Milk test target'), findsOneWidget);

    router.go('/deliveries/delivery-1/refund-replacement');
    await tester.pumpAndSettle();
    expect(
      router.routerDelegate.currentConfiguration.uri.path,
      '/deliveries/delivery-1/refund-replacement',
    );
    expect(find.text('Refund target'), findsOneWidget);
  });

  testWidgets('loading, error and offline states keep the shared panels', (
    tester,
  ) async {
    await _pumpTracking(
      tester,
      state: const DeliveryState(isLoading: true),
      settle: false,
    );
    expect(find.byType(LoadingStatePanel), findsOneWidget);

    await _pumpTracking(
      tester,
      state: const DeliveryState(errorMessage: 'Delivery could not be loaded.'),
    );
    expect(find.byType(ErrorStatePanel), findsOneWidget);
    expect(find.text('Delivery could not be loaded.'), findsOneWidget);

    await _pumpTracking(
      tester,
      state: const DeliveryState(
        isOffline: true,
        errorMessage:
            'Unable to reach DoodhDirect. Check your connection and try again.',
      ),
    );
    expect(find.textContaining('Unable to reach DoodhDirect'), findsOneWidget);
  });

  testWidgets('compact layout renders the full composition without overflow', (
    tester,
  ) async {
    _useSurface(tester, const Size(320, 640));
    await _pumpTracking(
      tester,
      state: DeliveryState(selectedCustomerDelivery: _delivery()),
    );

    expect(tester.takeException(), isNull);
    expect(find.text('ORD-1001'), findsOneWidget);
    expect(find.text('Doorstep milk test'), findsOneWidget);
  });

  testWidgets('wide layout splits status and details into balanced columns', (
    tester,
  ) async {
    _useSurface(tester, const Size(1440, 900));
    await _pumpTracking(
      tester,
      state: DeliveryState(selectedCustomerDelivery: _delivery()),
    );

    expect(tester.takeException(), isNull);
    expect(find.text('ORD-1001'), findsOneWidget);
    expect(find.text('Delivery progress'), findsOneWidget);
    // The hero stays constrained instead of stretching across the window.
    final heroWidth = tester.getSize(find.text('ORD-1001')).width;
    expect(heroWidth, lessThan(720));
  });
}
