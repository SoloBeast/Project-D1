import 'dart:io';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/customer/current_location_provider.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_screens.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
import 'package:doodh_direct_mobile/features/customer/location_permission.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

/// Feature-triggered location permission: no app-launch request, a
/// permission gate in front of the interactive map, manual address entry
/// that works without permission, and saved coordinates that survive
/// denial.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('LocationPermissionService over Geolocator', () {
    const channel = MethodChannel('flutter.baseflow.com/geolocator');

    /// Geolocator status indexes: denied=0, deniedForever=1, whileInUse=2,
    /// always=3, unableToDetermine=4.
    void mockGeolocator({
      int? checkStatus,
      int? requestStatus,
      bool serviceEnabled = true,
      bool throwOnCheck = false,
    }) {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, (call) async {
            if (call.method == 'isLocationServiceEnabled') {
              return serviceEnabled;
            }
            if (call.method == 'checkPermission') {
              if (throwOnCheck) throw PlatformException(code: 'UNAVAILABLE');
              return checkStatus;
            }
            if (call.method == 'requestPermission') return requestStatus;
            if (call.method == 'openAppSettings') return true;
            return null;
          });
      addTearDown(
        () => TestDefaultBinaryMessengerBinding
            .instance
            .defaultBinaryMessenger
            .setMockMethodCallHandler(channel, null),
      );
    }

    test('checkStatus maps every OS value', () async {
      const service = GeolocatorLocationPermissionService();
      final cases = {
        0: LocationGateStatus.denied,
        1: LocationGateStatus.permanentlyDenied,
        2: LocationGateStatus.granted,
        3: LocationGateStatus.granted,
      };
      for (final entry in cases.entries) {
        mockGeolocator(checkStatus: entry.key);
        expect(
          await service.checkStatus(),
          entry.value,
          reason: 'status index ${entry.key}',
        );
      }
    });

    test('requestAccess maps the OS result without extra prompts', () async {
      const service = GeolocatorLocationPermissionService();
      mockGeolocator(
        checkStatus: 0,
        requestStatus: 2,
        serviceEnabled: true,
      );
      expect(
        await service.requestAccess(),
        LocationGateStatus.granted,
      );
    });

    test('unreadable OS state degrades to unavailable', () async {
      const service = GeolocatorLocationPermissionService();
      mockGeolocator(throwOnCheck: true);
      expect(
        await service.checkStatus(),
        LocationGateStatus.unavailable,
      );
    });

    test('serviceEnabled reflects the device location switch', () async {
      const service = GeolocatorLocationPermissionService();
      mockGeolocator(checkStatus: 2, serviceEnabled: false);
      expect(await service.isServiceEnabled(), isFalse);
      mockGeolocator(checkStatus: 2, serviceEnabled: true);
      expect(await service.isServiceEnabled(), isTrue);
    });
  });

  group('getCurrentLocation never requests permission', () {
    const channel = MethodChannel('flutter.baseflow.com/geolocator');

    Future<List<String>> mockCalls({
      required bool serviceEnabled,
      required int checkStatus,
    }) async {
      final calls = <String>[];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, (call) async {
            calls.add(call.method);
            if (call.method == 'isLocationServiceEnabled') {
              return serviceEnabled;
            }
            if (call.method == 'checkPermission') return checkStatus;
            return null;
          });
      addTearDown(
        () => TestDefaultBinaryMessengerBinding
            .instance
            .defaultBinaryMessenger
            .setMockMethodCallHandler(channel, null),
      );
      return calls;
    }

    test('denied status throws without a request call', () async {
      final calls = await mockCalls(serviceEnabled: true, checkStatus: 0);
      await expectLater(
        getCurrentLocation(),
        throwsA(
          isA<CurrentLocationException>().having(
            (e) => e.failure,
            'failure',
            CurrentLocationFailure.permissionDenied,
          ),
        ),
      );
      expect(calls, contains('checkPermission'));
      expect(calls, isNot(contains('requestPermission')));
    });

    test('permanently denied throws without a request call', () async {
      final calls = await mockCalls(serviceEnabled: true, checkStatus: 1);
      await expectLater(
        getCurrentLocation(),
        throwsA(
          isA<CurrentLocationException>().having(
            (e) => e.failure,
            'failure',
            CurrentLocationFailure.permissionDenied,
          ),
        ),
      );
      expect(calls, isNot(contains('requestPermission')));
    });

    test('disabled services report unavailable separately', () async {
      final calls = await mockCalls(serviceEnabled: false, checkStatus: 2);
      await expectLater(
        getCurrentLocation(),
        throwsA(
          isA<CurrentLocationException>().having(
            (e) => e.failure,
            'failure',
            CurrentLocationFailure.unavailable,
          ),
        ),
      );
      expect(calls, isNot(contains('requestPermission')));
    });
  });

  group('map permission gate', () {
    testWidgets('app launch requests nothing: check only, no prompt', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.denied,
      );
      var mapBuilds = 0;
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) {
              mapBuilds++;
              return const SizedBox.expand();
            },
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(permissions.checkCalls, greaterThanOrEqualTo(1));
      expect(permissions.requestCalls, 0);
      expect(mapBuilds, 0);
      expect(find.text('Location access needed'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('granted permission shows the interactive map directly', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.granted,
      );
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(permissions.requestCalls, 0);
      expect(find.text('Location access needed'), findsNothing);
      expect(find.text('Use my current location'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('denied gate blocks map interaction until allowed', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.denied,
      );
      var selectedCalls = 0;
      var mapBuilds = 0;
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) => selectedCalls++,
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) {
              mapBuilds++;
              return const SizedBox.expand();
            },
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(mapBuilds, 0);
      expect(selectedCalls, 0);
      expect(find.text('Use my current location'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('explicit retry after denial requests exactly once more', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.denied,
      );
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Allow location access'));
      await tester.pumpAndSettle();
      expect(permissions.requestCalls, 1);
      // Denied again: non-blocking explanation plus retry, no auto prompt.
      expect(
        find.text('Map unavailable without location access'),
        findsOneWidget,
      );

      await tester.tap(find.text('Try again'));
      await tester.pumpAndSettle();
      expect(permissions.requestCalls, 2);
      expect(
        find.text('Map unavailable without location access'),
        findsOneWidget,
      );
      expect(tester.takeException(), isNull);
    });

    testWidgets('permanently denied offers settings and rechecks', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.permanentlyDenied,
      );
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Location is turned off'), findsOneWidget);
      expect(permissions.requestCalls, 0);
      await tester.tap(find.text('Open settings'));
      await tester.pumpAndSettle();
      expect(permissions.openSettingsCalls, 1);
      expect(permissions.requestCalls, 0);
      expect(tester.takeException(), isNull);
    });

    testWidgets('grant after returning from settings opens the map', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.permanentlyDenied,
      );
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Location is turned off'), findsOneWidget);

      // The user enables location in Settings; resuming rechecks the OS.
      permissions.status = LocationGateStatus.granted;
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pumpAndSettle();

      expect(find.text('Location is turned off'), findsNothing);
      expect(find.text('Use my current location'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('unavailable location degrades gracefully with recheck', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.unavailable,
      );
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Location unavailable'), findsOneWidget);
      expect(permissions.requestCalls, 0);
      final checks = permissions.checkCalls;
      permissions.status = LocationGateStatus.granted;
      await tester.tap(find.text('Check again'));
      await tester.pumpAndSettle();
      expect(permissions.checkCalls, greaterThan(checks));
      expect(find.text('Use my current location'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('current location rechecks a mid-use revocation', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.granted,
      );
      var providerCalls = 0;
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            currentLocationProvider: () async {
              providerCalls++;
              return const LatLng(28.6, 77.4);
            },
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use my current location'));
      await tester.pumpAndSettle();
      expect(providerCalls, 1);

      // Revoked while the map is open: the next tap gates the map instead
      // of locating.
      permissions.status = LocationGateStatus.denied;
      await tester.tap(find.text('Use my current location'));
      await tester.pumpAndSettle();
      expect(providerCalls, 1);
      expect(find.text('Location access needed'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('disabled services explain separately from denial', (
      tester,
    ) async {
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.granted,
        serviceEnabled: false,
      );
      var providerCalls = 0;
      await tester.pumpWidget(
        MaterialApp(
          home: GoogleMapCoordinatePicker(
            onLocationSelected: (_) {},
            currentLocationProvider: () async {
              providerCalls++;
              return const LatLng(28.6, 77.4);
            },
            locationPermissionService: permissions,
            mapsLoader: () async {},
            mapBuilder: (_, _, _) => const SizedBox.expand(),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use my current location'));
      await tester.pumpAndSettle();

      expect(providerCalls, 0);
      expect(find.textContaining('Location services are turned off'), findsOneWidget);
      // The map itself stays available: only the action is explained.
      expect(find.text('Use my current location'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('no app-launch permission request (static source guards)', () {
    String readLib(String relativePath) =>
        File('lib/$relativePath').readAsStringSync();

    test('app shell never touches location permission', () {
      for (final path in [
        'app/app.dart',
        'features/auth/login_screen.dart',
        'features/auth/session_controller.dart',
        'features/home/role_home_screen.dart',
      ]) {
        final source = readLib(path);
        expect(source, isNot(contains('Geolocator')), reason: path);
        expect(source, isNot(contains('requestPermission')), reason: path);
      }
    });

    test('address form owns fields, never the OS prompt', () {
      final source = readLib('features/customer/customer_screens.dart');
      // The only location reference is the injected gate default; the form
      // itself makes no Geolocator calls and never requests access.
      expect(source, isNot(contains('Geolocator.')));
      expect(source, isNot(contains('requestAccess(')));
      expect(source, contains('GoogleMapCoordinatePicker'));
    });

    test('only foreground permissions are declared, no background access', () {
      final manifest =
          File('android/app/src/main/AndroidManifest.xml').readAsStringSync();
      expect(manifest, contains('ACCESS_FINE_LOCATION'));
      expect(manifest, isNot(contains('BACKGROUND_LOCATION')));
      expect(manifest, isNot(contains('ACCESS_BACKGROUND_LOCATION')));

      final plist = File('ios/Runner/Info.plist').readAsStringSync();
      expect(plist, contains('NSLocationWhenInUseUsageDescription'));
      expect(plist, isNot(contains('NSLocationAlwaysAndWhenInUseUsageDescription')));
      expect(plist, isNot(contains('NSLocationAlwaysUsageDescription')));

      final provider =
          readLib('features/customer/current_location_provider.dart');
      expect(provider, isNot(contains('requestPermission')));
    });
  });

  group('manual address entry without permission', () {
    testWidgets('fields stay editable and saved coordinates are preserved', (
      tester,
    ) async {
      const savedLatitude = 18.5074;
      const savedLongitude = 73.8077;
      final controller = _RecordingCustomerController(
        CustomerState(
          addresses: [
            CustomerAddress(
              publicId: 'address-home',
              label: 'Home',
              addressLine1: '12 Market Road',
              addressLine2: null,
              locality: 'Kothrud',
              city: 'Pune',
              state: 'Maharashtra',
              pinCode: '411038',
              landmark: null,
              deliveryInstructions: null,
              contactName: 'Rahul Sharma',
              contactMobile: '+919876501234',
              latitude: savedLatitude,
              longitude: savedLongitude,
              isDefault: true,
              isActive: true,
            ),
          ],
        ),
      );
      final permissions = _FakeLocationPermissionService(
        status: LocationGateStatus.denied,
      );
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(
            _SeededAuthRepository(_session),
          ),
          customerControllerProvider.overrideWith(() => controller),
          // No web key: the picker shows its maps panel while the gate
          // stays denied, so no map interaction can alter coordinates.
          clientConfigurationRepositoryProvider.overrideWithValue(
            _FakeClientConfigurationRepository(),
          ),
        ],
      );
      addTearDown(container.dispose);

      tester.view.physicalSize = const Size(800, 2400);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);

      final router = GoRouter(
        initialLocation: '/',
        routes: [
          GoRoute(path: '/', builder: (context, state) => const SizedBox()),
          GoRoute(
            path: '/edit',
            builder: (context, state) => CustomerAddressEditScreen(
              addressId: 'address-home',
              locationPermissionService: permissions,
            ),
          ),
        ],
      );
      addTearDown(router.dispose);

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp.router(routerConfig: router),
        ),
      );
      await tester.pumpAndSettle();
      router.push('/edit');
      await tester.pumpAndSettle();

      // Manual fields are usable with no location access granted: the map
      // area shows its maps-configuration panel (test stub has no web key)
      // while every text field stays interactive.
      expect(find.byType(GoogleMapCoordinatePicker), findsOneWidget);
      await tester.enterText(
        find.widgetWithText(TextFormField, 'City'),
        'Mumbai',
      );
      await tester.pump();
      expect(find.widgetWithText(TextFormField, 'Mumbai'), findsOneWidget);

      await tester.tap(find.text('Save address'));
      await tester.pumpAndSettle();

      // The save carries the original saved coordinates: denial (or an
      // unavailable map) never replaces them with fallback coordinates,
      // and a failed reverse lookup never overwrites them either.
      expect(controller.savedDraft, isNotNull);
      expect(controller.savedAddressId, 'address-home');
      expect(controller.savedDraft!.latitude, savedLatitude);
      expect(controller.savedDraft!.longitude, savedLongitude);
      expect(controller.savedDraft!.city, 'Mumbai');
      expect(tester.takeException(), isNull);
    });
  });
}

class _FakeLocationPermissionService implements LocationPermissionService {
  _FakeLocationPermissionService({
    this.status = LocationGateStatus.granted,
    this.serviceEnabled = true,
  });

  LocationGateStatus status;
  bool serviceEnabled;
  int checkCalls = 0;
  int requestCalls = 0;
  int openSettingsCalls = 0;

  @override
  Future<LocationGateStatus> checkStatus() async {
    checkCalls++;
    return status;
  }

  @override
  Future<LocationGateStatus> requestAccess() async {
    requestCalls++;
    return status;
  }

  @override
  Future<bool> isServiceEnabled() async => serviceEnabled;

  @override
  Future<void> openSettings() async {
    openSettingsCalls++;
  }
}

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: 'rahul@example.test',
    mobile: '+919876543210',
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
    emailVerified: true,
    hasPassword: true,
  ),
  accessToken: 'token',
  refreshToken: 'refresh',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

class _FakeClientConfigurationRepository
    extends ClientConfigurationRepository {
  _FakeClientConfigurationRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<ClientConfiguration> get(String token) async =>
      const ClientConfiguration();
}

class _SeededAuthRepository extends AuthRepository {
  _SeededAuthRepository(this.session);

  final AuthSession session;

  @override
  Future<AuthSession?> restore() async => session;
}

class _RecordingCustomerController extends CustomerController {
  _RecordingCustomerController(this.initialState);

  final CustomerState initialState;
  AddressDraft? savedDraft;
  String? savedAddressId;

  @override
  CustomerState build() => initialState;

  @override
  Future<void> load() async {}

  @override
  Future<AddressLookup?> reverseLookup(
    double latitude,
    double longitude,
  ) async => null;

  @override
  Future<bool> saveAddress(AddressDraft draft, {String? addressId}) async {
    savedDraft = draft;
    savedAddressId = addressId;
    return true;
  }
}
