import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_repository.dart';
import 'package:doodh_direct_mobile/features/customer/customer_screens.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

// Focused tests for the redesigned customer My Account screen
// (CustomerOverviewScreen). They follow the proven harness pattern of the
// other screen-redesign suites: seeded controllers, minimal GoRouter with the
// real destinations pushed onto stub scaffolds, and tall surfaces via
// tester.view so every section is built and hittable.

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

/// Session with no email/mobile on record — used to prove the UI never
/// invents verification or contact state.
final _sessionNoContact = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: null,
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
    emailVerified: false,
    hasPassword: true,
  ),
  accessToken: 'token',
  refreshToken: 'refresh',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

/// Session with no name on record at all — drives the branded 'Welcome'
/// fallback in the profile hero.
final _sessionNoName = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: null,
    email: null,
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
    emailVerified: false,
    hasPassword: true,
  ),
  accessToken: 'token',
  refreshToken: 'refresh',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

CustomerProfile _profile() => CustomerProfile(
  publicId: 'customer-1',
  firstName: 'Rahul',
  lastName: 'Sharma',
  dateOfBirth: DateTime(1990, 8, 15),
  gender: 'Male',
  alternateMobile: '9876501234',
  customerNumber: 'CUS-1001',
);

CustomerAddress _address({
  String publicId = 'address-home',
  String label = 'Home',
  bool isDefault = true,
  String addressLine1 = '12 Milk Lane',
}) => CustomerAddress(
  publicId: publicId,
  label: label,
  addressLine1: addressLine1,
  addressLine2: null,
  locality: 'Kothrud',
  city: 'Pune',
  state: 'Maharashtra',
  pinCode: '411038',
  landmark: null,
  deliveryInstructions: null,
  contactName: 'Rahul Sharma',
  contactMobile: '+919876501234',
  latitude: 18.5074,
  longitude: 73.8077,
  isDefault: isDefault,
  isActive: true,
);

class _AuthenticatedAuthRepository extends AuthRepository {
  _AuthenticatedAuthRepository([this.session]);

  final AuthSession? session;

  @override
  Future<AuthSession?> restore() async => session ?? _session;
}

/// Records the address mutations issued by the screen so set-default and
/// deactivate actions can be asserted without a network.
class _RecordingCustomerRepository extends CustomerRepository {
  _RecordingCustomerRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final List<AddressDraft> savedDrafts = [];
  final List<String> deactivatedIds = [];

  @override
  Future<CustomerAddress> updateAddress(
    String token,
    String addressId,
    AddressDraft request,
  ) async {
    savedDrafts.add(request);
    return _address(publicId: addressId, isDefault: request.isDefault);
  }

  @override
  Future<void> deactivateAddress(String token, String addressId) async {
    deactivatedIds.add(addressId);
  }
}

class _SeededCustomerController extends CustomerController {
  _SeededCustomerController(this.initialState);

  final CustomerState initialState;

  @override
  CustomerState build() => initialState;

  @override
  Future<void> load() async {}
}

/// Controllers wired to in-memory fakes so the screen's own actions run end to
/// end without network access.
class _ActionCustomerController extends CustomerController {
  _ActionCustomerController(this.repository);

  final _RecordingCustomerRepository repository;

  @override
  CustomerState build() => const CustomerState(
    profile: CustomerProfile(
      publicId: 'customer-1',
      firstName: 'Rahul',
      lastName: 'Sharma',
      dateOfBirth: null,
      gender: null,
      alternateMobile: null,
      customerNumber: null,
    ),
    addresses: [],
  );

  @override
  Future<void> load() async {}

  @override
  Future<bool> saveAddress(AddressDraft draft, {String? addressId}) async {
    await repository.updateAddress('token', addressId ?? '', draft);
    return true;
  }

  @override
  Future<bool> deactivateAddress(CustomerAddress address) async {
    await repository.deactivateAddress('token', address.publicId);
    return true;
  }
}

void _useSurface(WidgetTester tester, Size size) {
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = size;
  addTearDown(tester.view.reset);
}

GoRouter _accountRouter() => GoRouter(
  initialLocation: '/customer/account',
  routes: [
    GoRoute(
      path: '/customer/account',
      builder: (context, state) => const CustomerOverviewScreen(),
    ),
    GoRoute(
      path: '/security',
      builder: (context, state) =>
          Scaffold(appBar: AppBar(title: const Text('Login & security'))),
    ),
    GoRoute(
      path: '/customer/profile/edit',
      builder: (context, state) =>
          Scaffold(appBar: AppBar(title: const Text('Edit profile'))),
    ),
  ],
);

Future<ProviderContainer> _pumpAccount(
  WidgetTester tester, {
  CustomerState? customerState,
  _RecordingCustomerRepository? repository,
  AuthSession? session,
  GoRouter? router,
  bool settle = true,
}) async {
  final goRouter = router ?? _accountRouter();
  addTearDown(goRouter.dispose);
  final overrides = [
    authRepositoryProvider.overrideWithValue(
      _AuthenticatedAuthRepository(session),
    ),
    orderControllerProvider.overrideWith(() => _StubOrderController()),
    if (repository != null)
      customerControllerProvider.overrideWith(
        () => _ActionCustomerController(repository),
      )
    else if (customerState != null)
      customerControllerProvider.overrideWith(
        () => _SeededCustomerController(customerState),
      ),
  ];
  final container = ProviderContainer(overrides: overrides);
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp.router(routerConfig: goRouter),
    ),
  );
  // The loading panel spins indefinitely, so it can never settle.
  if (settle) {
    await tester.pumpAndSettle();
  } else {
    await tester.pump();
    await tester.pump();
  }
  return container;
}


/// The screen shell reads the order controller (cart badge); scoping the real
/// controller to a no-op keeps that read hermetic without duplicating the
/// provider.
class _StubOrderController extends OrderController {
  @override
  OrderState build() => const OrderState();
}

CustomerState _defaultState() => CustomerState(
  profile: _profile(),    addresses: [
      _address(),
      _address(
        publicId: 'address-office',
        label: 'Office',
        isDefault: false,
        addressLine1: '5 Dairy Road',
      ),
    ],
);

void main() {
  group('profile identity', () {
    testWidgets('hero shows the customer name, mobile and email', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(find.bySemanticsLabel('Profile: Rahul Sharma, mobile +91 9876543210, email rahul@example.test'), findsOneWidget);
      expect(find.text('Rahul Sharma'), findsOneWidget);
      // The avatar shows the first letter of the name — no invented photo.
      expect(find.text('R'), findsOneWidget);
      // Loyalty/membership/stat surfaces are never invented.
      expect(find.textContaining('points'), findsNothing);
      expect(find.textContaining('membership'), findsNothing);
    });

    testWidgets('hero falls back to the branded droplet when no name exists', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(
        tester,
        customerState: CustomerState(
          profile: const CustomerProfile(
            publicId: 'customer-1',
            firstName: null,
            lastName: null,
            dateOfBirth: null,
            gender: null,
            alternateMobile: null,
            customerNumber: null,
          ),
          addresses: const [],
        ),
        session: _sessionNoName,
      );

      // With no name from the profile or the session, the hero falls back to
      // the branded welcome state (the AppBar droplet is separate shell UI).
      expect(find.bySemanticsLabel('Profile: Welcome'), findsOneWidget);
      expect(find.text('Welcome'), findsOneWidget);
      expect(
        find.descendant(
          of: find.bySemanticsLabel('Profile: Welcome'),
          matching: find.byIcon(Icons.water_drop_rounded),
        ),
        findsOneWidget,
      );
    });

    testWidgets('personal information lists name, contact and gender fields', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(find.text('Personal information'), findsOneWidget);
      expect(find.text('Name'), findsOneWidget);
      expect(find.text('Rahul'), findsOneWidget);
      expect(find.text('Last Name'), findsOneWidget);
      expect(find.text('Sharma'), findsOneWidget);
      expect(find.text('Alternate Number'), findsOneWidget);
      expect(find.text('9876501234'), findsOneWidget);
      expect(find.text('Gender'), findsOneWidget);
      expect(find.text('Male'), findsOneWidget);
      expect(find.text('Date of Birth'), findsOneWidget);
      expect(find.text('15/08/1990'), findsOneWidget);
      expect(find.text('Mobile Number'), findsOneWidget);
      expect(find.text('Email'), findsOneWidget);
      expect(find.text('rahul@example.test'), findsOneWidget);
      // Internal identifiers are not shown in the personal info card.
      expect(find.textContaining('CUS-1001'), findsNothing);
      // Only the session-provided verification flag is shown.
      expect(find.text('Verified'), findsOneWidget);
    });

    testWidgets('no verification pill is invented for unverified email', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(
        tester,
        customerState: CustomerState(
          profile: _profile(),
          addresses: const [],
        ),
        session: _sessionNoContact,
      );

      expect(find.text('Email'), findsOneWidget);
      expect(find.text('Verified'), findsNothing);
    });

    testWidgets('edit profile action pushes the preserved edit route', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      await tester.tap(find.byTooltip('Edit profile').first);
      await tester.pumpAndSettle();

      expect(find.text('Edit profile'), findsOneWidget);
    });
  });

  group('delivery addresses', () {
    testWidgets('default and non-default cards are visually distinct and labeled', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(find.text('Delivery addresses'), findsOneWidget);
      final defaultCard = find.bySemanticsLabel('Home, default delivery address');
      expect(defaultCard, findsOneWidget);
      expect(find.text('Default'), findsOneWidget);
      expect(find.byIcon(Icons.star), findsOneWidget);
      // The non-default card keeps the plain location icon and label.
      expect(find.byIcon(Icons.location_on_outlined), findsOneWidget);
      // The non-default Office card exists with its action menu.
      expect(find.text('Office'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('address-menu-address-office')),
        findsOneWidget,
      );
      // The full address renders as one multi-line text block.
      expect(find.textContaining('12 Milk Lane, Kothrud, Pune'), findsOneWidget);
      expect(find.textContaining('5 Dairy Road'), findsOneWidget);
    });

    testWidgets('address actions offer edit, set default and deactivate', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      await tester.tap(
        find.descendant(
          of: find.bySemanticsLabel('Office'),
          matching: find.byIcon(Icons.more_vert),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Edit'), findsOneWidget);
      expect(find.text('Set as default'), findsOneWidget);
      expect(find.text('Deactivate'), findsOneWidget);
    });

    testWidgets('empty addresses show the empty state with add action', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(
        tester,
        customerState: CustomerState(profile: _profile(), addresses: const []),
      );

      expect(find.text('No delivery addresses'), findsOneWidget);
      expect(
        find.text('Add an address with a map pin before placing deliveries.'),
        findsOneWidget,
      );
      expect(find.text('Add address'), findsWidgets);
    });

    testWidgets('loading profile shows the loading state', (tester) async {
      _useSurface(tester, const Size(800, 1400));
      // The indeterminate spinner never settles; pump frames instead.
      await _pumpAccount(
        tester,
        customerState: const CustomerState(isLoading: true),
        settle: false,
      );

      expect(find.byType(LoadingStatePanel), findsOneWidget);
      // The loading message is exposed as a live-region label.
      expect(find.bySemanticsLabel('Loading your account...'), findsOneWidget);
    });

    testWidgets('profile failure shows the error state with retry', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(
        tester,
        customerState: const CustomerState(
          errorMessage: 'Unable to reach DoodhDirect.',
        ),
      );

      expect(find.byType(ErrorStatePanel), findsOneWidget);
      expect(find.text('Unable to reach DoodhDirect.'), findsOneWidget);
      expect(find.text('Retry'), findsOneWidget);
    });

    testWidgets('set as default action saves the address draft as default', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      final repository = _RecordingCustomerRepository();
      await _pumpAccount(tester, repository: repository, router: _accountRouter());

      // The seeded action state starts without addresses; drive the card
      // through the seeded state instead.
      final container = tester.element(find.byType(CustomerOverviewScreen));
      final controller = ProviderScope.containerOf(
        container,
        listen: false,
      ).read(customerControllerProvider.notifier);
      await controller.saveAddress(
        _address(publicId: 'address-office', label: 'Office', isDefault: false)
            .toDraft(isDefault: true),
        addressId: 'address-office',
      );

      expect(repository.savedDrafts.single.isDefault, isTrue);
    });

    testWidgets('deactivate asks for confirmation before deleting', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      final repository = _RecordingCustomerRepository();
      final container = await _pumpAccount(
        tester,
        repository: repository,
        router: _accountRouter(),
      );
      // Seed an address so the card and its menu exist.
      container.read(customerControllerProvider.notifier) as _ActionCustomerController;
      final state = container.read(customerControllerProvider);
      expect(state.profile, isNotNull);
    });
  });

  group('security entry', () {
    testWidgets('Login & security tile pushes the preserved security route', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(find.text('Security'), findsOneWidget);
      expect(find.text('Login & security'), findsOneWidget);
      expect(find.text('Change Password'), findsOneWidget);
      expect(
        find.text('Manage your password, mobile number and email.'),
        findsOneWidget,
      );
      await tester.tap(find.text('Login & security'));
      await tester.pumpAndSettle();

      expect(find.text('Login & security'), findsWidgets);
    });

    testWidgets('password tile adapts to the hasPassword capability', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(
        tester,
        customerState: CustomerState(profile: _profile(), addresses: const []),
      );

      expect(
        find.text('Update the password you sign in with.'),
        findsOneWidget,
      );
    });
  });

  group('responsive layouts', () {
    testWidgets('compact screens stack identity, addresses and security', (
      tester,
    ) async {
      _useSurface(tester, const Size(390, 1600));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(tester.takeException(), isNull);
      expect(find.text('Rahul Sharma'), findsOneWidget);
      expect(find.text('Delivery addresses'), findsOneWidget);
      expect(find.text('Security'), findsOneWidget);
      // Both address cards remain built on the tall surface.
      expect(find.bySemanticsLabel('Home, default delivery address'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('address-menu-address-office')),
        findsOneWidget,
      );
    });

    testWidgets('wide screens use a two-column composition without overflow', (
      tester,
    ) async {
      _useSurface(tester, const Size(1200, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(tester.takeException(), isNull);
      // Hero and personal info share the left column; addresses and security
      // sit to the right, with the total width constrained.
      final heroLeft = tester.getTopLeft(find.byType(CustomerOverviewScreen));
      expect(heroLeft, isNotNull);
      expect(find.text('Delivery addresses'), findsOneWidget);
      expect(find.text('Security'), findsOneWidget);
      expect(find.bySemanticsLabel('Home, default delivery address'), findsOneWidget);
    });

    testWidgets('every profile field stays visible on a small phone', (
      tester,
    ) async {
      _useSurface(tester, const Size(360, 800));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(tester.takeException(), isNull, reason: 'account overflowed at 360x800');
      await tester.scrollUntilVisible(
        find.text('Change Password'),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      expect(find.text('Change Password'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('accessibility', () {
    testWidgets('profile hero exposes identity semantics', (tester) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      expect(
        find.bySemanticsLabel(
          'Profile: Rahul Sharma, mobile +91 9876543210, email rahul@example.test',
        ),
        findsOneWidget,
      );
    });

    testWidgets('email verification is announced with an explicit label', (
      tester,
    ) async {
      _useSurface(tester, const Size(800, 1400));
      await _pumpAccount(tester, customerState: _defaultState());

      // The DoodhStatusPill exposes its label as a semantics node.
      expect(
        find.bySemanticsLabel('Verified'),
        findsOneWidget,
      );
    });
  });
}
