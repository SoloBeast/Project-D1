import 'dart:async';

import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/branches/branch_controller.dart';
import 'package:doodh_direct_mobile/features/branches/branch_models.dart';
import 'package:doodh_direct_mobile/features/branches/branch_screens.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

void main() {
  group('branch list screen', () {
    testWidgets('shows loading state while fetching', (tester) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(const BranchState(isLoading: true)),
      );

      expect(find.bySemanticsLabel('Loading branches...'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows empty state with create action for managers', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(const BranchState()),
      );

      expect(find.text('No branches yet'), findsOneWidget);
      expect(find.text('Add branch'), findsOneWidget);
      expect(find.byTooltip('Add branch'), findsOneWidget);
    });

    testWidgets('hides create action without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(const BranchState()),
        permissions: const [kBranchesReadPermission],
      );

      expect(find.text('No branches yet'), findsOneWidget);
      expect(find.text('Add branch'), findsNothing);
      expect(find.byTooltip('Add branch'), findsNothing);
    });

    testWidgets('shows error state with retry', (tester) async {
      final controller = _SeededBranchController(
        const BranchState(errorMessage: 'Branch lookup failed.'),
      );
      await _pumpConfig(tester, const BranchListScreen(), controller);

      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.text('Branch lookup failed.'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);

      await tester.tap(find.text('Retry'));
      await tester.pumpAndSettle();

      // initState schedules one load() via Future.microtask and Retry fires a
      // second one.
      expect(controller.loadCount, 2);
    });

    testWidgets('renders branch cards sorted by name with code', (
      tester,
    ) async {
      await _pumpConfig(
        tester,
        const BranchListScreen(),
        _SeededBranchController(
          BranchState(
            branches: [
              _branch(
                publicId: 'b-zebra',
                name: 'Zebra Branch',
                code: 'ZEB',
              ),
              _branch(
                publicId: 'b-main',
                name: 'Main Branch',
                code: 'MAIN',
                isActive: false,
              ),
            ],
          ),
        ),
      );

      expect(find.text('Main Branch'), findsOneWidget);
      expect(find.text('Zebra Branch'), findsOneWidget);
      expect(find.text('Code MAIN · Mumbai'), findsOneWidget);
      expect(find.text('Code ZEB · Mumbai'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      expect(find.text('Inactive'), findsOneWidget);

      // Sorted alphabetically by name: Main Branch appears above Zebra Branch.
      final mainY = tester.getTopLeft(find.text('Main Branch')).dy;
      final zebraY = tester.getTopLeft(find.text('Zebra Branch')).dy;
      expect(mainY, lessThan(zebraY));
    });
  });

  group('branch form screen', () {
    testWidgets('blocks access without manage permission', (tester) async {
      await _pumpConfig(
        tester,
        const BranchFormScreen(),
        _SeededBranchController(const BranchState()),
        permissions: const [kBranchesReadPermission],
        routePath: '/admin/branches/new',
      );

      expect(find.text('Add branch'), findsNothing);
      expect(find.text('Access denied'), findsOneWidget);
    });

    testWidgets('validates required fields before saving', (tester) async {
      final controller = _SeededBranchController(const BranchState());
      await _pumpConfig(
        tester,
        const BranchFormScreen(),
        controller,
        routePath: '/admin/branches/new',
      );

      await tester.tap(find.text('Create branch'));
      await tester.pumpAndSettle();

      expect(controller.createCount, 0);
      expect(find.text('Enter a branch code.'), findsOneWidget);
      expect(find.text('Enter a branch name.'), findsOneWidget);
      expect(find.text('Enter a city.'), findsOneWidget);
      expect(find.text('Enter a state.'), findsOneWidget);
    });

    testWidgets('creates a branch and pops back to the list', (tester) async {
      final controller = _SeededBranchController(const BranchState());
      await _pumpConfig(
        tester,
        const BranchFormScreen(),
        controller,
        routePath: '/admin/branches/new',
      );

      await _enterField(tester, 'Branch code *', 'MAIN');
      await _enterField(tester, 'Branch name *', 'Main Branch');
      await _enterField(tester, 'City *', 'Mumbai');
      await _enterField(tester, 'State *', 'MH');
      await _enterField(tester, 'Service radius (km)', '5');

      await tester.tap(find.text('Create branch'));
      await tester.pumpAndSettle();

      // The map's initial center is only a visual fallback. A new branch must
      // not save until the administrator explicitly selects a location.
      expect(controller.createCount, 0);
      expect(controller.lastCreateRequest, isNull);
      expect(
        find.text('Select a valid location on the map.'),
        findsOneWidget,
      );
    });

    testWidgets('edits branch without a client-generated number', (tester) async {
      final branch = _branch();
      final controller = _SeededBranchController(BranchState(branches: [branch]));
      await _pumpConfig(
        tester,
        BranchFormScreen(branch: branch),
        controller,
        routePath: '/admin/branches/:id/edit',
        initialPath: '/admin/branches/b-main/edit',
      );

      expect(find.text('Edit branch'), findsOneWidget);
      // No branch-number input exists anywhere in the form.
      expect(find.text('Branch code *'), findsOneWidget);

      await tester.tap(find.text('Save changes'));
      await tester.pumpAndSettle();

      expect(controller.updateCount, 1);
      expect(controller.lastUpdateRequest?.code, 'MAIN');
      expect(find.text('Branch saved'), findsOneWidget);
    });

    testWidgets(
      'map selection shows a loading state, then auto-fills the address',
      (tester) async {
        final lookup = Completer<AddressLookup?>();
        final customer = _SeededCustomerController();
        customer.queueCompleter(lookup);
        final controller = _SeededBranchController(const BranchState());
        await _pumpConfig(
          tester,
          const BranchFormScreen(),
          controller,
          customerController: customer,
          routePath: '/admin/branches/new',
        );

        await _selectMapLocation(tester, const LatLng(28.4089, 77.3178));

        // Loading state is visible while the lookup is in flight.
        expect(
          find.text('Determining the address for the selected location...'),
          findsOneWidget,
        );
        // The reverse-geocode loading row is visible; the picker may also still
        // show its own map-loading spinner, so allow one or more.
        expect(find.byType(CircularProgressIndicator), findsWidgets);

        lookup.complete(
          _addressLookup(
            addressLine1: '12 MG Road',
            locality: 'Connaught Place',
            city: 'New Delhi',
            state: 'DL',
            pinCode: '110001',
            latitude: 28.4089,
            longitude: 77.3178,
          ),
        );
        await tester.pumpAndSettle();

        expect(customer.callCount, 1);
        expect(customer.latCalls, [28.4089]);
        expect(customer.lngCalls, [77.3178]);
        expect(find.text('Determining the address for the selected location...'),
            findsNothing);
        expect(_fieldText(tester, 'Address line 1'), '12 MG Road');
        expect(_fieldText(tester, 'Locality'), 'Connaught Place');
        expect(_fieldText(tester, 'City *'), 'New Delhi');
        expect(_fieldText(tester, 'State *'), 'DL');
        expect(_fieldText(tester, 'PIN code'), '110001');
        // The coordinates that drive the marker were updated.
        final picker = tester.widget<GoogleMapCoordinatePicker>(
          find.byType(GoogleMapCoordinatePicker),
        );
        expect(picker.initialLocation, const LatLng(28.4089, 77.3178));
      },
    );

    testWidgets(
      'selecting a second map location replaces the auto-filled address',
      (tester) async {
        final customer = _SeededCustomerController();
        customer.queue(
          _addressLookup(
            addressLine1: 'First Road',
            locality: 'Mumbai Central',
            city: 'Mumbai',
            state: 'MH',
            pinCode: '400001',
            latitude: 19.1136,
            longitude: 72.8697,
          ),
        );
        customer.queue(
          _addressLookup(
            addressLine1: 'Second Road',
            locality: 'Shivajinagar',
            city: 'Pune',
            state: 'MH',
            pinCode: '411005',
            latitude: 18.5204,
            longitude: 73.8567,
          ),
        );
        final controller = _SeededBranchController(const BranchState());
        await _pumpConfig(
          tester,
          const BranchFormScreen(),
          controller,
          customerController: customer,
          routePath: '/admin/branches/new',
        );

        await _selectMapLocation(tester, const LatLng(19.1136, 72.8697));
        await tester.pumpAndSettle();
        expect(_fieldText(tester, 'Address line 1'), 'First Road');
        expect(_fieldText(tester, 'City *'), 'Mumbai');

        await _selectMapLocation(tester, const LatLng(18.5204, 73.8567));
        await tester.pumpAndSettle();

        expect(customer.callCount, 2);
        expect(_fieldText(tester, 'Address line 1'), 'Second Road');
        expect(_fieldText(tester, 'Locality'), 'Shivajinagar');
        expect(_fieldText(tester, 'City *'), 'Pune');
        expect(_fieldText(tester, 'State *'), 'MH');
        expect(_fieldText(tester, 'PIN code'), '411005');
        final picker = tester.widget<GoogleMapCoordinatePicker>(
          find.byType(GoogleMapCoordinatePicker),
        );
        expect(picker.initialLocation, const LatLng(18.5204, 73.8567));
      },
    );

    testWidgets(
      'an older reverse-geocoding response never overwrites the newest selection',
      (tester) async {
        final first = Completer<AddressLookup?>();
        final second = Completer<AddressLookup?>();
        final customer = _SeededCustomerController();
        customer.queueCompleter(first);
        customer.queueCompleter(second);
        final controller = _SeededBranchController(const BranchState());
        await _pumpConfig(
          tester,
          const BranchFormScreen(),
          controller,
          customerController: customer,
          routePath: '/admin/branches/new',
        );

        // Select location A, then location B while A's lookup is still pending.
        await _selectMapLocation(tester, const LatLng(19.1136, 72.8697));
        await _selectMapLocation(tester, const LatLng(18.5204, 73.8567));
        expect(customer.callCount, 2);

        // The NEWEST response completes first.
        second.complete(
          _addressLookup(
            addressLine1: 'Second Road',
            locality: 'Shivajinagar',
            city: 'Pune',
            state: 'MH',
            pinCode: '411005',
            latitude: 18.5204,
            longitude: 73.8567,
          ),
        );
        await tester.pumpAndSettle();
        expect(_fieldText(tester, 'Address line 1'), 'Second Road');
        expect(find.text('Determining the address for the selected location...'),
            findsNothing);

        // The OLDER response completes late; it must be discarded.
        first.complete(
          _addressLookup(
            addressLine1: 'First Road',
            locality: 'Mumbai Central',
            city: 'Mumbai',
            state: 'MH',
            pinCode: '400001',
            latitude: 19.1136,
            longitude: 72.8697,
          ),
        );
        await tester.pumpAndSettle();
        expect(_fieldText(tester, 'Address line 1'), 'Second Road');
        expect(_fieldText(tester, 'City *'), 'Pune');
        expect(find.text('Determining the address for the selected location...'),
            findsNothing);
      },
    );

    testWidgets(
      'keeps the coordinates when a location cannot be converted to an address',
      (tester) async {
        final customer = _SeededCustomerController();
        customer.queue(null);
        final controller = _SeededBranchController(const BranchState());
        await _pumpConfig(
          tester,
          const BranchFormScreen(),
          controller,
          customerController: customer,
          routePath: '/admin/branches/new',
        );

        await _selectMapLocation(tester, const LatLng(28.4089, 77.3178));
        await tester.pumpAndSettle();

        expect(
          find.text(
            'The selected location could not be converted to an address. '
            'The map location is saved - enter the address manually.',
          ),
          findsOneWidget,
        );
        expect(find.text('Determining the address for the selected location...'),
            findsNothing);

        // The selected coordinates are still saved with the branch even though
        // reverse geocoding produced nothing.
        await _enterField(tester, 'Branch code *', 'DEL');
        await _enterField(tester, 'Branch name *', 'Delhi Branch');
        await _enterField(tester, 'City *', 'New Delhi');
        await _enterField(tester, 'State *', 'DL');

        await tester.tap(find.text('Create branch'));
        await tester.pumpAndSettle();

        expect(controller.createCount, 1);
        expect(controller.lastCreateRequest?.latitude, 28.4089);
        expect(controller.lastCreateRequest?.longitude, 77.3178);
        expect(controller.lastCreateRequest?.addressLine1, isNull);
      },
    );

    testWidgets(
      'does not clear an address the user typed when geocoding fails',
      (tester) async {
        final customer = _SeededCustomerController();
        customer.queueError(StateError('provider down'));
        final controller = _SeededBranchController(const BranchState());
        await _pumpConfig(
          tester,
          const BranchFormScreen(),
          controller,
          customerController: customer,
          routePath: '/admin/branches/new',
        );

        await _enterField(tester, 'Address line 1', '12 Main Road');
        await _enterField(tester, 'Locality', 'Andheri');

        await _selectMapLocation(tester, const LatLng(28.4089, 77.3178));
        await tester.pumpAndSettle();

        expect(
          find.text(
            'Address lookup is unavailable right now. The map location was '
            'updated and your existing address was preserved.',
          ),
          findsOneWidget,
        );
        // The user's typed address is untouched.
        expect(_fieldText(tester, 'Address line 1'), '12 Main Road');
        expect(_fieldText(tester, 'Locality'), 'Andheri');
        expect(find.text('Determining the address for the selected location...'),
            findsNothing);
      },
    );

    testWidgets(
      'preserves the existing address when reverse geocoding fails on edit',
      (tester) async {
        final branch = _branch();
        final customer = _SeededCustomerController();
        customer.queueError(StateError('provider down'));
        final controller = _SeededBranchController(BranchState(branches: [branch]));
        await _pumpConfig(
          tester,
          BranchFormScreen(branch: branch),
          controller,
          customerController: customer,
          routePath: '/admin/branches/:id/edit',
          initialPath: '/admin/branches/b-main/edit',
        );

        await _selectMapLocation(tester, const LatLng(19.1136, 72.8697));
        await tester.pumpAndSettle();

        expect(
          find.text(
            'Address lookup is unavailable right now. The map location was '
            'updated and your existing address was preserved.',
          ),
          findsOneWidget,
        );
        expect(_fieldText(tester, 'Address line 1'), '12 Main Road');
        expect(_fieldText(tester, 'Locality'), 'Andheri');
        expect(_fieldText(tester, 'City *'), 'Mumbai');
        expect(_fieldText(tester, 'State *'), 'MH');
        expect(find.text('Determining the address for the selected location...'),
            findsNothing);
      },
    );

    testWidgets(
      'editing a branch auto-fills the address from a new map selection',
      (tester) async {
        final branch = _branch();
        final customer = _SeededCustomerController();
        customer.queue(
          _addressLookup(
            addressLine1: 'Sector 15 Road',
            locality: 'Dwarka',
            city: 'New Delhi',
            state: 'DL',
            pinCode: '110075',
            latitude: 28.6519,
            longitude: 77.2216,
          ),
        );
        final controller = _SeededBranchController(BranchState(branches: [branch]));
        await _pumpConfig(
          tester,
          BranchFormScreen(branch: branch),
          controller,
          customerController: customer,
          routePath: '/admin/branches/:id/edit',
          initialPath: '/admin/branches/b-main/edit',
        );

        await _selectMapLocation(tester, const LatLng(28.6519, 77.2216));
        await tester.pumpAndSettle();

        expect(_fieldText(tester, 'Address line 1'), 'Sector 15 Road');
        expect(_fieldText(tester, 'Locality'), 'Dwarka');
        expect(_fieldText(tester, 'City *'), 'New Delhi');
        expect(_fieldText(tester, 'State *'), 'DL');
        expect(_fieldText(tester, 'PIN code'), '110075');

        await tester.tap(find.text('Save changes'));
        await tester.pumpAndSettle();

        expect(controller.updateCount, 1);
        expect(controller.lastUpdateRequest?.latitude, 28.6519);
        expect(controller.lastUpdateRequest?.longitude, 77.2216);
        expect(controller.lastUpdateRequest?.addressLine1, 'Sector 15 Road');
        expect(controller.lastUpdateRequest?.city, 'New Delhi');
      },
    );

    testWidgets(
      'creating a branch saves the reverse-geocoded address with the coordinates',
      (tester) async {
        final customer = _SeededCustomerController();
        customer.queue(
          _addressLookup(
            addressLine1: '12 MG Road',
            locality: 'Fort',
            city: 'Mumbai',
            state: 'MH',
            pinCode: '400001',
            latitude: 19.1136,
            longitude: 72.8697,
          ),
        );
        final controller = _SeededBranchController(const BranchState());
        await _pumpConfig(
          tester,
          const BranchFormScreen(),
          controller,
          customerController: customer,
          routePath: '/admin/branches/new',
        );

        await _enterField(tester, 'Branch code *', 'MUM');
        await _enterField(tester, 'Branch name *', 'Mumbai Branch');
        await _enterField(tester, 'Service radius (km)', '5');

        await _selectMapLocation(tester, const LatLng(19.1136, 72.8697));
        await tester.pumpAndSettle();

        expect(_fieldText(tester, 'Address line 1'), '12 MG Road');
        expect(_fieldText(tester, 'Locality'), 'Fort');
        expect(_fieldText(tester, 'City *'), 'Mumbai');
        expect(_fieldText(tester, 'State *'), 'MH');
        expect(_fieldText(tester, 'PIN code'), '400001');

        await tester.tap(find.text('Create branch'));
        await tester.pumpAndSettle();

        expect(controller.createCount, 1);
        expect(controller.lastCreateRequest?.code, 'MUM');
        expect(controller.lastCreateRequest?.name, 'Mumbai Branch');
        expect(controller.lastCreateRequest?.addressLine1, '12 MG Road');
        expect(controller.lastCreateRequest?.locality, 'Fort');
        expect(controller.lastCreateRequest?.city, 'Mumbai');
        expect(controller.lastCreateRequest?.state, 'MH');
        expect(controller.lastCreateRequest?.pinCode, '400001');
        expect(controller.lastCreateRequest?.latitude, 19.1136);
        expect(controller.lastCreateRequest?.longitude, 72.8697);
      },
    );
  });

  group('branch detail screen', () {
    testWidgets('shows loading state while fetching by id', (tester) async {
      await _pumpConfig(
        tester,
        const BranchDetailScreen(branchId: 'b-missing'),
        _SeededBranchController(const BranchState(isLoading: true)),
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-missing',
        settle: false,
      );

      expect(find.bySemanticsLabel('Loading branch...'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('renders branch details with system-allocated number', (
      tester,
    ) async {
      final branch = _branch();
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        _SeededBranchController(BranchState(branches: [branch])),
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      expect(find.text('Branch details'), findsOneWidget);
      expect(find.text('Main Branch'), findsOneWidget);
      expect(find.text('Code MAIN'), findsOneWidget);
      expect(
        find.text('12 Main Road, Andheri, Mumbai, MH, 400001'),
        findsOneWidget,
      );
      expect(find.byTooltip('Edit branch'), findsOneWidget);
      expect(find.text('Deactivate branch'), findsOneWidget);
    });

    testWidgets('deactivates a branch after confirmation', (tester) async {
      final branch = _branch();
      final controller = _SeededBranchController(
        BranchState(branches: [branch]),
      );
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        controller,
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      await tester.tap(find.text('Deactivate branch'));
      await tester.pumpAndSettle();

      expect(find.text('Deactivate branch?'), findsOneWidget);

      await tester.tap(find.widgetWithText(FilledButton, 'Deactivate'));
      await tester.pumpAndSettle();

      expect(controller.setActiveCount, 1);
      expect(controller.lastSetActiveBranchId, 'b-main');
      expect(controller.lastSetActiveValue, isFalse);
      expect(find.text('Branch deactivated'), findsOneWidget);
      // The branch is now inactive, so the button flips to Activate.
      expect(find.text('Activate branch'), findsOneWidget);
    });

    testWidgets('activates a branch after confirmation', (tester) async {
      final branch = _branch( isActive: false);
      final controller = _SeededBranchController(
        BranchState(branches: [branch]),
      );
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        controller,
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      expect(find.text('Activate branch'), findsOneWidget);

      await tester.tap(find.text('Activate branch'));
      await tester.pumpAndSettle();

      expect(find.text('Activate branch?'), findsOneWidget);

      await tester.tap(find.widgetWithText(FilledButton, 'Activate'));
      await tester.pumpAndSettle();

      expect(controller.setActiveCount, 1);
      expect(controller.lastSetActiveBranchId, 'b-main');
      expect(controller.lastSetActiveValue, isTrue);
      expect(find.text('Branch activated'), findsOneWidget);
      expect(find.text('Deactivate branch'), findsOneWidget);
    });

    testWidgets('hides manage actions without manage permission', (tester) async {
      final branch = _branch();
      await _pumpConfig(
        tester,
        BranchDetailScreen(branchId: 'b-main', branch: branch),
        _SeededBranchController(BranchState(branches: [branch])),
        permissions: const [kBranchesReadPermission],
        routePath: '/admin/branches/:id',
        initialPath: '/admin/branches/b-main',
      );

      expect(find.text('Main Branch'), findsOneWidget);
      expect(find.byTooltip('Edit branch'), findsNothing);
      expect(find.text('Deactivate branch'), findsNothing);
    });
  });
}

/// Pumps a branch screen inside a GoRouter shell ([routePath] renders [screen];
/// every other route renders a placeholder). The session carries the supplied
/// permissions.
Future<void> _pumpConfig(
  WidgetTester tester,
  Widget screen,
  _SeededBranchController controller, {
  _SeededCustomerController? customerController,
  List<String> permissions = const [
    kBranchesReadPermission,
    kBranchesManagePermission,
  ],
  String routePath = '/admin/branches',
  String? initialPath,
  bool settle = true,
}) async {
  await tester.binding.setSurfaceSize(const Size(900, 1400));
  addTearDown(() => tester.binding.setSurfaceSize(null));

  Widget placeholder(BuildContext context, GoRouterState state) =>
      const Scaffold(body: Center(child: Text('Placeholder')));

  final router = GoRouter(
    initialLocation: '/admin/branches',
    routes: [
      GoRoute(
        path: '/admin/branches',
        builder: routePath == '/admin/branches'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/branches/new',
        builder: routePath == '/admin/branches/new'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/branches/:id',
        builder: routePath == '/admin/branches/:id'
            ? (context, state) => screen
            : placeholder,
      ),
      GoRoute(
        path: '/admin/branches/:id/edit',
        builder: routePath == '/admin/branches/:id/edit'
            ? (context, state) => screen
            : placeholder,
      ),
    ],
  );

  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        branchControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(permissions: permissions),
        ),
        if (customerController != null)
          customerControllerProvider.overrideWith(
            () => customerController,
          ),
      ],
      child: MaterialApp.router(
        theme: ThemeData(useMaterial3: true),
        routerConfig: router,
      ),
    ),
  );
  await tester.pump();
  await tester.pump();

  final target = initialPath ?? routePath;
  if (target != '/admin/branches') {
    router.push(target);
    if (settle) {
      await tester.pumpAndSettle();
    } else {
      // A perpetual animation (e.g. a loading spinner) would make
      // pumpAndSettle hang, so pump the route transition manually instead.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
    }
  }
}

/// Enters text into a [TextFormField] identified by its label text.
Future<void> _enterField(WidgetTester tester, String label, String value) async {
  await tester.enterText(find.widgetWithText(TextFormField, label), value);
}

/// Reads the current text of a [TextFormField] identified by its label text.
String _fieldText(WidgetTester tester, String label) {
  final field = tester.widget<TextFormField>(
    find.widgetWithText(TextFormField, label),
  );
  return field.controller?.text ?? '';
}

/// Triggers a map location selection on the embedded
/// [GoogleMapCoordinatePicker] (the same callback the picker invokes when the
/// user taps a point on the map).
Future<void> _selectMapLocation(WidgetTester tester, LatLng location) async {
  final picker = tester.widget<GoogleMapCoordinatePicker>(
    find.byType(GoogleMapCoordinatePicker),
  );
  picker.onLocationSelected(location);
  await tester.pump();
}

class _SeededBranchController extends BranchController {
  _SeededBranchController(this.initialState);

  final BranchState initialState;

  int loadCount = 0;
  int loadByIdCount = 0;
  int createCount = 0;
  int updateCount = 0;
  int setActiveCount = 0;

  UpsertBranchRequest? lastCreateRequest;
  UpsertBranchRequest? lastUpdateRequest;
  String? lastSetActiveBranchId;
  bool? lastSetActiveValue;

  @override
  BranchState build() => initialState;

  @override
  Future<void> load() async {
    loadCount++;
  }

  @override
  Future<void> loadById(String branchId) async {
    loadByIdCount++;
  }

  @override
  Future<bool> create(UpsertBranchRequest request) async {
    createCount++;
    lastCreateRequest = request;
    state = state.copyWith(savedMessage: 'Branch saved');
    return true;
  }

  @override
  Future<bool> update(String branchId, UpsertBranchRequest request) async {
    updateCount++;
    lastUpdateRequest = request;
    state = state.copyWith(savedMessage: 'Branch saved');
    return true;
  }

  @override
  Future<bool> setActive(String branchId, bool isActive) async {
    setActiveCount++;
    lastSetActiveBranchId = branchId;
    lastSetActiveValue = isActive;
    final updated = _branch(
      publicId: branchId,
      isActive: isActive,
    );
    state = state.copyWith(
      branches: [updated],
      selectedBranch: updated,
      savedMessage: isActive ? 'Branch activated' : 'Branch deactivated',
    );
    return true;
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({required this.permissions});

  final List<String> permissions;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'branch-admin-user',
        displayName: 'Branch Administrator',
        email: null,
        mobile: '9999999999',
        roles: const ['OWNER'],
        permissions: permissions,
        branchIds: const [7],
      ),
      accessToken: 'branch-admin-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

Branch _branch({
  String publicId = 'b-main',
  String code = 'MAIN',
  String name = 'Main Branch',
  String city = 'Mumbai',
  String state = 'MH',
  double latitude = 19.07,
  double longitude = 72.87,
  double? serviceRadiusKm = 5,
  bool isActive = true,
}) => Branch(
  publicId: publicId,
  code: code,
  name: name,
  addressLine1: '12 Main Road',
  addressLine2: null,
  locality: 'Andheri',
  city: city,
  state: state,
  pinCode: '400001',
  latitude: latitude,
  longitude: longitude,
  serviceRadiusKm: serviceRadiusKm,
  isActive: isActive,
  createdAt: DateTime(2026, 8, 1, 10),
  updatedAt: DateTime(2026, 8, 2, 10),
);

AddressLookup _addressLookup({
  String? addressLine1,
  String? locality,
  String? city,
  String? state,
  String? pinCode,
  required double latitude,
  required double longitude,
}) => AddressLookup(
  addressLine1: addressLine1,
  locality: locality,
  city: city,
  state: state,
  pinCode: pinCode,
  latitude: latitude,
  longitude: longitude,
);

/// A [CustomerController] double whose [reverseLookup] returns queued results
/// so tests can control when (and whether) each response completes.
class _SeededCustomerController extends CustomerController {
  final _results = <Future<AddressLookup?>>[];
  final _errors = <Object>[];
  int callCount = 0;
  final latCalls = <double>[];
  final lngCalls = <double>[];

  /// Queues an immediate result (or a `null` result to simulate
  /// ZERO_RESULTS / provider failure).
  void queue(AddressLookup? result) {
    _results.add(Future.value(result));
  }

  /// Queues a manually-completed lookup so the test can control ordering.
  void queueCompleter(Completer<AddressLookup?> completer) {
    _results.add(completer.future);
  }

  /// Queues a lookup that fails, simulating a geocoding provider outage.
  void queueError(Object error) {
    _errors.add(error);
  }

  @override
  Future<AddressLookup?> reverseLookup(
    double latitude,
    double longitude,
  ) async {
    callCount++;
    latCalls.add(latitude);
    lngCalls.add(longitude);
    if (_errors.isNotEmpty) {
      throw _errors.removeAt(0);
    }
    if (_results.isEmpty) return null;
    return _results.removeAt(0);
  }
}
