import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/admin_reports/admin_report_screens.dart';
import 'package:doodh_direct_mobile/features/auth/login_screen.dart';
import 'package:doodh_direct_mobile/features/auth/otp_onboarding_screen.dart';
import 'package:doodh_direct_mobile/features/auth/otp_screen.dart';
import 'package:doodh_direct_mobile/features/auth/security_screens.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_screens.dart';
import 'package:doodh_direct_mobile/features/customer/customer_screens.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_screens.dart';
import 'package:doodh_direct_mobile/features/branches/branch_models.dart';
import 'package:doodh_direct_mobile/features/branches/branch_screens.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_screens.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_screen.dart';
import 'package:doodh_direct_mobile/features/employees/employee_models.dart';
import 'package:doodh_direct_mobile/features/employees/employee_screens.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_screens.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_screens.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_screen.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_screens.dart';
import 'package:doodh_direct_mobile/features/payments/payment_screens.dart';
import 'package:doodh_direct_mobile/features/otp_config/otp_config_screen.dart';
import 'package:doodh_direct_mobile/features/integrations/integrations_screen.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_models.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_screens.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_screens.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Persists the sanitized destination the user should land on after signing
/// in. The GoRouter instance is rebuilt whenever the session changes, which
/// would otherwise discard the `/login?redirectTo=...` query parameter; this
/// provider survives that rebuild so the redirect can recover the intent.
final returnIntentProvider = NotifierProvider<ReturnIntentController, String?>(
  ReturnIntentController.new,
);

/// Holds a single pending return-intent. [take] consumes the value (used by
/// the redirect after authentication) so an intent is never applied twice.
class ReturnIntentController extends Notifier<String?> {
  @override
  String? build() => null;

  void set(String? value) => state = value;

  String? take() {
    final value = state;
    state = null;
    return value;
  }
}

final routerProvider = Provider<GoRouter>((ref) {
  final refresh = _RouterRefreshNotifier();
  ref.onDispose(refresh.dispose);
  late final GoRouter router;
  ref.listen(sessionControllerProvider, (previous, next) {
    refresh.notify();
    // Sign-out is an explicit boundary: do not silently turn the authenticated
    // home into guest home. Initial unauthenticated startup still follows the
    // normal public-home behavior through the redirect below.
    if (previous?.isAuthenticated == true && !next.isAuthenticated) {
      Future.microtask(() => router.go('/login'));
    }
  });

  router = GoRouter(
    // Let GoRouter read the browser's initial path. Session changes refresh the
    // existing router below instead of rebuilding it and losing that path.
    refreshListenable: refresh,
    redirect: (context, state) {
      final session = ref.read(sessionControllerProvider);
      // Use the query-stripped path for matching: state.matchedLocation keeps
      // query parameters (e.g. /login?redirectTo=/checkout) which would break
      // exact equality checks below.
      final path = state.uri.path;
      final isInvitationRoute = _isInvitationRoute(path);
      final isAuthRoute = _isAuthRoute(path);
      final isPublicStorefront = _isPublicStorefront(path);

      // Invitation links are credential-bearing deep links. They must bypass
      // restore, home, login, generic OTP, and return-intent routing for the
      // entire anonymous onboarding flow. The token remains in the route
      // parameter and is never copied into generic redirect state.
      if (isInvitationRoute) return null;

      // go_router runs this callback while the widget tree is building, so it
      // must stay pure: only reads are allowed here. Every provider write is
      // deferred to a microtask (see the _defer*/_take* helpers below).
      if (session.isLoading) {
        // The /restore placeholder normally holds the screen while the stored
        // session is loaded. But an in-flight auth operation (send-otp /
        // verify-otp) must keep its screen: the OTP screen uses the result of
        // verify-otp to route a new customer into onboarding, so tearing it
        // down mid-request (via this redirect) unmounts it before it can
        // navigate. Auth routes therefore stay put during the loading state.
        if (path == '/restore' || isAuthRoute) return null;
        return '/restore';
      }

      if (session.isAuthenticated) {
        if (path == '/restore') {
          // A session change rebuilds the GoRouter, so the login redirectTo
          // query is gone; recover the intent persisted by the auth flow.
          return _takePendingIntent(ref) ?? '/home';
        }
        if (isAuthRoute) {
          return _returnIntentFrom(state) ?? _takePendingIntent(ref) ?? '/home';
        }
        return null;
      }

      if (session.isGuest) {
        if (path == '/restore') {
          // A session change rebuilds the GoRouter; land back on the captured
          // deep link (set before enterAsGuest) instead of the guest home.
          return _takePendingIntent(ref) ?? '/home';
        }
        // A guest may browse the public storefront and open the auth flows
        // (sign in / register / OTP) freely.
        if (isAuthRoute || isPublicStorefront) {
          _syncReturnIntent(ref, state);
          return null;
        }
        // Any protected page is gated behind sign in; capture the intended
        // destination so the user returns there after authenticating.
        return _loginWithReturn(ref, state);
      }

      // Unauthenticated: allow the auth flows, auto-enter guest mode on the
      // public storefront so deep links browse seamlessly, and capture a
      // return-intent for every protected route.
      if (path == '/restore') return '/login';
      if (isAuthRoute) {
        _syncReturnIntent(ref, state);
        return null;
      }
      if (isPublicStorefront) {
        // A session change rebuilds the GoRouter; persist the destination so
        // the guest '/restore' branch can land back on this exact deep link
        // instead of the guest home. enterAsGuest is deferred: the resulting
        // session change rebuilds the router, whose '/restore' redirect then
        // consumes this intent.
        final intent = _sanitizeReturnIntent(path);
        if (intent != null) {
          _deferIntentSet(ref, intent);
        }
        _deferEnterAsGuest(ref);
        return null;
      }
      return _loginWithReturn(ref, state);
    },
    routes: [
      GoRoute(path: '/', redirect: (context, state) => '/home'),
      GoRoute(
        path: '/restore',
        builder: (context, state) => const _SessionRestoreScreen(),
      ),
      GoRoute(path: '/login', builder: (context, state) => const LoginScreen()),
      // Mobile OTP sign-in. A mobile entered on /login is carried here so the
      // field pre-fills and an OTP is requested without retyping.
      GoRoute(
        path: '/otp',
        builder: (context, state) => OtpScreen(
          initialMobile: state.uri.queryParameters['mobile'],
        ),
      ),
      // Customer onboarding for a mobile the OTP provider attested but that has
      // no account yet. Only reachable from a completed verify-otp handshake
      // that returned requiresOnboarding (carrying mobile + reqId).
      GoRoute(
        path: '/otp/onboarding',
        builder: (context, state) => const OtpOnboardingScreen(),
      ),
      GoRoute(
        path: '/forgot-password',
        builder: (context, state) => const ForgotPasswordScreen(),
      ),
      GoRoute(
        path: '/create-password',
        builder: (context, state) => const CreatePasswordScreen(),
      ),
      // Changes the password after verifying the current one. Authenticated-only
      // (like /create-password): deliberately NOT in _isAuthRoute so a guest
      // hitting it falls through to the login gate instead of being redirected
      // to /home, while an authenticated session opens it normally.
      GoRoute(
        path: '/change-password',
        builder: (context, state) => const ChangePasswordScreen(),
      ),
      GoRoute(
        path: '/customer/security',
        redirect: (context, state) => '/security',
      ),
      GoRoute(
        path: '/customer/security/email',
        redirect: (context, state) => '/security/email',
      ),
      GoRoute(
        path: '/security',
        builder: (context, state) => const LoginSecurityScreen(),
      ),
      GoRoute(
        path: '/security/email',
        builder: (context, state) => const EmailChangeScreen(),
      ),
      GoRoute(
        path: '/security/mobile',
        builder: (context, state) => const MobileChangeScreen(),
      ),
      GoRoute(
        path: '/home',
        builder: (context, state) => Consumer(
          builder: (context, ref, child) {
            final session = ref.watch(sessionControllerProvider);
            if (!session.isAuthenticated) return const GuestHomeScreen();
            return RoleHomeScreen(role: session.role!);
          },
        ),
      ),
      GoRoute(
        path: '/notifications',
        builder: (context, state) => const NotificationInboxScreen(),
      ),
      GoRoute(
        path: '/customer',
        redirect: (context, state) => '/customer/account',
      ),
      GoRoute(
        path: '/customer/account',
        builder: (context, state) => const CustomerOverviewScreen(),
      ),
      GoRoute(
        path: '/customer/profile/edit',
        builder: (context, state) => const CustomerProfileEditScreen(),
      ),
      GoRoute(
        path: '/customer/addresses/new',
        builder: (context, state) => const CustomerAddressEditScreen(),
      ),
      GoRoute(
        path: '/checkout/address/new',
        builder: (context, state) => const CustomerAddressEditScreen(
          checkoutMode: true,
        ),
      ),
      GoRoute(
        path: '/customer/addresses/:addressId/edit',
        builder: (context, state) => CustomerAddressEditScreen(
          addressId: state.pathParameters['addressId'],
        ),
      ),
      GoRoute(
        path: '/catalogue',
        builder: (context, state) => const ProductCatalogueScreen(),
      ),
      GoRoute(
        path: '/checkout',
        builder: (context, state) {
          final extra = state.extra;
          final payload = extra is Map<String, dynamic> ? extra : null;
          final product = payload?['product'];
          final quantity = payload?['quantity'];
          return CheckoutScreen(
            initialProduct: product is CatalogueProduct ? product : null,
            initialQuantity: quantity is num ? quantity.toDouble() : null,
          );
        },
      ),
      GoRoute(
        path: '/orders',
        builder: (context, state) => const OrderHistoryScreen(),
      ),
      GoRoute(
        path: '/orders/:orderId',
        builder: (context, state) {
          final orderId = _requiredPathParameter(state, 'orderId');
          return orderId == null
              ? const _RouteErrorScreen(resource: 'order')
              : OrderDetailScreen(orderId: orderId);
        },
      ),
      GoRoute(
        path: '/staff/orders/:orderId',
        builder: (context, state) {
          final orderId = _requiredPathParameter(state, 'orderId');
          return orderId == null
              ? const _RouteErrorScreen(resource: 'order')
              : StaffOrderInspectionScreen(orderId: orderId);
        },
      ),
      GoRoute(
        path: '/orders/:orderId/payment',
        builder: (context, state) {
          final orderId = _requiredPathParameter(state, 'orderId');
          if (orderId == null) {
            return const _RouteErrorScreen(resource: 'order');
          }
          final extra = state.extra;
          final initialOrder =
              extra is OrderSummary && extra.publicId == orderId ? extra : null;
          return PaymentMethodScreen(
            orderId: orderId,
            initialOrder: initialOrder,
          );
        },
      ),
      GoRoute(
        path: '/subscriptions',
        builder: (context, state) => const SubscriptionListScreen(),
      ),
      GoRoute(
        path: '/subscriptions/new',
        builder: (context, state) => const SubscriptionSetupScreen(),
      ),
      GoRoute(
        path: '/subscriptions/:subscriptionId',
        builder: (context, state) {
          final subscriptionId = _requiredPathParameter(
            state,
            'subscriptionId',
          );
          return subscriptionId == null
              ? const _RouteErrorScreen(resource: 'subscription')
              : SubscriptionDetailScreen(subscriptionId: subscriptionId);
        },
      ),
      GoRoute(
        path: '/subscriptions/:subscriptionId/calendar',
        builder: (context, state) {
          final subscriptionId = _requiredPathParameter(
            state,
            'subscriptionId',
          );
          return subscriptionId == null
              ? const _RouteErrorScreen(resource: 'subscription')
              : SubscriptionCalendarScreen(subscriptionId: subscriptionId);
        },
      ),
      GoRoute(
        path: '/payments/:paymentId/result',
        builder: (context, state) {
          final paymentId = _requiredPathParameter(state, 'paymentId');
          return paymentId == null
              ? const _RouteErrorScreen(resource: 'payment')
              : PaymentResultScreen(paymentId: paymentId);
        },
      ),
      GoRoute(
        path: '/wallet',
        builder: (context, state) => const WalletScreen(),
      ),
      GoRoute(
        path: '/catalogue/products/:productId',
        builder: (context, state) {
          final productId = _requiredPathParameter(state, 'productId');
          return productId == null
              ? const _RouteErrorScreen(resource: 'product')
              : ProductDetailScreen(productId: productId);
        },
      ),
      GoRoute(
        path: '/admin',
        builder: (context, state) => const AdminDashboardScreen(),
      ),
      GoRoute(
        path: '/admin/reports/:module',
        builder: (context, state) =>
            AdminReportScreen(moduleSlug: state.pathParameters['module'] ?? ''),
      ),
      GoRoute(
        path: '/admin/catalogue',
        builder: (context, state) => const AdminCatalogueScreen(),
      ),
      GoRoute(
        path: '/admin/cameras',
        builder: (context, state) => const AdminCameraListScreen(),
      ),
      GoRoute(
        path: '/admin/setup/otp-provider',
        builder: (context, state) => const OtpProviderConfigScreen(),
      ),
      GoRoute(
        path: '/admin/setup/integrations',
        builder: (context, state) => const IntegrationConfigurationScreen(),
      ),
      GoRoute(
        path: '/admin/setup/number-series',
        builder: (context, state) => const NumberSeriesListScreen(),
      ),
      GoRoute(
        path: '/admin/setup/number-series/new',
        builder: (context, state) =>
            const NumberSeriesConfigScreen(code: '', series: null),
      ),
      GoRoute(
        path: '/admin/setup/number-series/:code/edit',
        builder: (context, state) {
          final extra = state.extra;
          final series = extra is NumberSeries ? extra : null;
          return NumberSeriesConfigScreen(
            code: state.pathParameters['code'] ?? '',
            series: series,
          );
        },
      ),
      GoRoute(
        path: '/admin/setup/refund-replacement',
        builder: (context, state) => const RefundReplacementConfigScreen(),
      ),
      GoRoute(
        path: '/admin/employees',
        builder: (context, state) => const EmployeeListScreen(),
      ),
      GoRoute(
        path: '/admin/employees/new',
        builder: (context, state) => const CreateEmployeeScreen(),
      ),
      GoRoute(
        path: '/admin/employees/:id',
        builder: (context, state) {
          final id = int.tryParse(
            _requiredPathParameter(state, 'id') ?? '',
          );
          final extra = state.extra;
          final employee = extra is Employee ? extra : null;
          return id == null
              ? const _RouteErrorScreen(resource: 'employee')
              : EmployeeEditScreen(employeeId: id, employee: employee);
        },
      ),
      GoRoute(
        path: '/admin/branches',
        builder: (context, state) => const BranchListScreen(),
      ),
      GoRoute(
        path: '/admin/branches/new',
        builder: (context, state) => const BranchFormScreen(),
      ),
      GoRoute(
        path: '/admin/branches/:branchId',
        builder: (context, state) {
          final branchId = _requiredPathParameter(state, 'branchId');
          return branchId == null
              ? const _RouteErrorScreen(resource: 'branch')
              : BranchDetailScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/admin/branches/:branchId/edit',
        builder: (context, state) {
          final branchId = _requiredPathParameter(state, 'branchId');
          final extra = state.extra;
          final branch = extra is Branch ? extra : null;
          return branchId == null
              ? const _RouteErrorScreen(resource: 'branch')
              : BranchFormScreen(branch: branch);
        },
      ),
      GoRoute(
        path: '/invite/:token',
        builder: (context, state) => EmployeeInvitationScreen(
          token: state.pathParameters['token'] ?? '',
        ),
      ),
      GoRoute(
        path: '/deliveries',
        builder: (context, state) => const CustomerDeliveryListScreen(),
      ),
      GoRoute(
        path: '/deliveries/:deliveryId',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : CustomerDeliveryDetailScreen(deliveryId: deliveryId);
        },
      ),
      GoRoute(
        path: '/deliveries/:deliveryId/milk-test',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : CustomerMilkTestScreen(deliveryId: deliveryId);
        },
      ),
      GoRoute(
        path: '/deliveries/:deliveryId/refund-replacement',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          final milkTestId = state.uri.queryParameters['milkTestId'];
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : CustomerRefundReplacementScreen(
                  deliveryId: deliveryId,
                  milkTestId: milkTestId,
                );
        },
      ),
      GoRoute(
        path: '/refund-replacements',
        builder: (context, state) =>
            const CustomerRefundReplacementListScreen(),
      ),
      GoRoute(
        path: '/refund-replacements/:requestId',
        builder: (context, state) {
          final requestId = _requiredPathParameter(state, 'requestId');
          return requestId == null
              ? const _RouteErrorScreen(resource: 'refund/replacement request')
              : CustomerRefundReplacementDetailScreen(requestId: requestId);
        },
      ),
      GoRoute(
        path: '/staff/refund-replacements',
        builder: (context, state) {
          final branchId = int.tryParse(
            state.uri.queryParameters['branchId'] ?? '',
          );
          return StaffRefundReplacementListScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/staff/refund-replacements/:requestId',
        builder: (context, state) {
          final requestId = _requiredPathParameter(state, 'requestId');
          return requestId == null
              ? const _RouteErrorScreen(resource: 'refund/replacement request')
              : StaffRefundReplacementDetailScreen(requestId: requestId);
        },
      ),
      GoRoute(
        path: '/staff/delivery/:deliveryId',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : DeliveryInspectionScreen(deliveryId: deliveryId);
        },
      ),
      GoRoute(
        path: '/staff/delivery/:deliveryId/milk-test',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : BranchMilkTestInspectionScreen(deliveryId: deliveryId);
        },
      ),
      GoRoute(
        path: '/delivery',
        builder: (context, state) => const StaffDeliveryListScreen(),
      ),
      GoRoute(
        path: '/delivery/:deliveryId',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : StaffDeliveryDetailScreen(deliveryId: deliveryId);
        },
      ),
      GoRoute(
        path: '/delivery/:deliveryId/milk-test',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : StaffMilkTestScreen(deliveryId: deliveryId);
        },
      ),
      GoRoute(
        path: '/delivery-management/branch/:branchId',
        builder: (context, state) {
          final branchId = int.tryParse(
            _requiredPathParameter(state, 'branchId') ?? '',
          );
          return branchId == null
              ? const _RouteErrorScreen(resource: 'branch delivery')
              : DeliveryManagementScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/delivery-management/:deliveryId',
        builder: (context, state) {
          final deliveryId = _requiredPathParameter(state, 'deliveryId');
          return deliveryId == null
              ? const _RouteErrorScreen(resource: 'delivery')
              : DeliveryManagementDetailScreen(deliveryId: deliveryId);
        },
      ),
      GoRoute(
        path: '/cameras',
        builder: (context, state) => const LiveDairyCameraListScreen(),
      ),
      GoRoute(
        path: '/cameras/:cameraId',
        builder: (context, state) {
          final cameraId = _requiredPathParameter(state, 'cameraId');
          return cameraId == null
              ? const _RouteErrorScreen(resource: 'camera')
              : LiveDairyCameraViewerScreen(cameraId: cameraId);
        },
      ),
      GoRoute(path: '/dairy', redirect: (context, state) => '/dairy/dashboard'),
      GoRoute(
        path: '/dairy/dashboard',
        builder: (context, state) => const DairyDashboardScreen(),
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/production/new',
        builder: (context, state) {
          final branchId = int.tryParse(
            _requiredPathParameter(state, 'branchId') ?? '',
          );
          return branchId == null
              ? const _RouteErrorScreen(resource: 'dairy branch')
              : DairyProductionEntryScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/production',
        builder: (context, state) {
          final branchId = int.tryParse(
            _requiredPathParameter(state, 'branchId') ?? '',
          );
          return branchId == null
              ? const _RouteErrorScreen(resource: 'dairy branch')
              : DairyProductionHistoryScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/batches',
        builder: (context, state) {
          final branchId = int.tryParse(
            _requiredPathParameter(state, 'branchId') ?? '',
          );
          return branchId == null
              ? const _RouteErrorScreen(resource: 'dairy branch')
              : DairyBatchListScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/availability',
        builder: (context, state) {
          final branchId = int.tryParse(
            _requiredPathParameter(state, 'branchId') ?? '',
          );
          return branchId == null
              ? const _RouteErrorScreen(resource: 'dairy branch')
              : DairyAvailabilityScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/dairy/branch/:branchId/usage',
        builder: (context, state) {
          final branchId = int.tryParse(
            _requiredPathParameter(state, 'branchId') ?? '',
          );
          return branchId == null
              ? const _RouteErrorScreen(resource: 'dairy branch')
              : DairyUsageScreen(branchId: branchId);
        },
      ),
      GoRoute(
        path: '/dairy/batches/:batchId',
        builder: (context, state) {
          final batchId = _requiredPathParameter(state, 'batchId');
          return batchId == null
              ? const _RouteErrorScreen(resource: 'dairy batch')
              : DairyBatchDetailScreen(batchId: batchId);
        },
      ),
      GoRoute(
        path: '/dairy/batches/:batchId/usage/new',
        builder: (context, state) {
          final batchId = _requiredPathParameter(state, 'batchId');
          return batchId == null
              ? const _RouteErrorScreen(resource: 'dairy batch')
              : DairyUsageEntryScreen(batchId: batchId);
        },
      ),
    ],
  );
  return router;
});

String? _requiredPathParameter(GoRouterState state, String name) {
  final value = state.pathParameters[name]?.trim();
  return value == null || value.isEmpty ? null : value;
}

/// True only for a complete invitation route containing a token. Invitation
/// links are handled before all session/startup redirects so their token stays
/// in the route and never enters generic return-intent state.
bool _isInvitationRoute(String path) =>
    path.startsWith('/invite/') && path.length > '/invite/'.length;

/// True for the sign-in, OTP, onboarding, and password-reset flows that a
/// guest/unauthenticated user may open freely. Employee-invitation links are
/// handled earlier by [_isInvitationRoute]; this also covers a bare `/invite`
/// without a token so it is never mislabelled a protected page. Registration
/// is no longer a standalone route — new accounts are created through the OTP
/// onboarding flow (`/otp/onboarding`, matched by the `/otp` prefix).
///
/// `/create-password` is intentionally NOT in this set: it is an authenticated
/// flow reachable from Login & security for an OTP-created account that has no
/// password yet. Keeping it out of the auth set means an authenticated session
/// (even one whose user has `hasPassword == false`) is not redirected away
/// from it to `/home`, while a guest/unauthenticated user hitting the route
/// falls through to the login gate instead.
bool _isAuthRoute(String path) =>
    path == '/login' ||
    path.startsWith('/otp') ||
    path == '/forgot-password' ||
    path.startsWith('/invite');

/// The guest allowlist: everything a browsing-only visitor may open without an
/// account. Every other route is protected and requires sign in.
bool _isPublicStorefront(String path) =>
    path == '/' ||
    path == '/home' ||
    path == '/catalogue' ||
    path.startsWith('/catalogue/products/') ||
    path == '/checkout';

/// Internal route prefixes a return-intent may target. Only these are accepted
/// so the app can never act as an open redirect to an external location or to
/// an unknown path.
const _returnIntentAllowedPrefixes = <String>[
  '/home',
  '/catalogue',
  '/checkout',
  '/orders',
  '/subscriptions',
  '/wallet',
  '/customer',
  '/deliveries',
  '/refund-replacements',
  '/staff',
  '/notifications',
  '/payments',
  '/cameras',
  '/admin',
  '/delivery',
  '/delivery-management',
  '/dairy',
];

/// Reads and sanitizes the `redirectTo` query parameter carried on a login
/// route so the user returns to the exact protected page they intended.
String? _returnIntentFrom(GoRouterState state) =>
    _sanitizeReturnIntent(state.uri.queryParameters['redirectTo']);

/// Builds the login location for [state], capturing a validated return-intent
/// when the user was blocked on a protected route. The intent is also stored
/// in [returnIntentProvider] so it survives the router rebuild on sign in.
/// The write is deferred because go_router runs this redirect while the widget
/// tree is building and Riverpod forbids provider writes during build.
String _loginWithReturn(Ref ref, GoRouterState state) {
  final target = _sanitizeReturnIntent(state.uri.path);
  if (target != null) {
    _deferIntentSet(ref, target);
  }
  return target == null
      ? '/login'
      : '/login?redirectTo=${Uri.encodeQueryComponent(target)}';
}

/// Persists a valid `redirectTo` carried by an auth route into
/// [returnIntentProvider] so it survives the router rebuild triggered by a
/// session change, and clears a stale intent when an auth route carries none.
/// The write is deferred out of the widget-build phase.
void _syncReturnIntent(Ref ref, GoRouterState state) {
  final notifier = ref.read(returnIntentProvider.notifier);
  final intent = _sanitizeReturnIntent(state.uri.queryParameters['redirectTo']);
  if (intent != null) {
    Future.microtask(() => notifier.set(intent));
  } else if (state.uri.path == '/login') {
    Future.microtask(() => notifier.set(null));
  }
}

/// Reads the pending return-intent synchronously (reads are allowed during the
/// build phase) and schedules the consuming clear for after the build. The
/// redirect must stay pure during build: go_router runs it while the widget
/// tree is building and Riverpod rejects provider writes in that phase.
String? _takePendingIntent(Ref ref) {
  final value = ref.read(returnIntentProvider);
  if (value != null) {
    final notifier = ref.read(returnIntentProvider.notifier);
    Future.microtask(() => notifier.take());
  }
  return value;
}

/// Defers a write to [returnIntentProvider] out of the widget-build phase.
void _deferIntentSet(Ref ref, String intent) {
  final notifier = ref.read(returnIntentProvider.notifier);
  Future.microtask(() => notifier.set(intent));
}

/// Defers entering guest mode out of the widget-build phase. The resulting
/// session change rebuilds the router, which re-runs the redirect from
/// `/restore` and lands on the pending intent (or the guest home).
void _deferEnterAsGuest(Ref ref) {
  final notifier = ref.read(sessionControllerProvider.notifier);
  Future.microtask(() => notifier.enterAsGuest());
}

/// Validates a return-intent against the internal route allowlist, dropping
/// any query/fragment and rejecting external or unknown destinations.
String? _sanitizeReturnIntent(String? value) {
  if (value == null || value.isEmpty) return null;
  final path = value.split('?').first.split('#').first;
  if (path.isEmpty || path == '/') return null;
  for (final prefix in _returnIntentAllowedPrefixes) {
    if (path == prefix || path.startsWith('$prefix/')) return path;
  }
  return null;
}

class _RouteErrorScreen extends StatelessWidget {
  const _RouteErrorScreen({required this.resource});

  final String resource;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Invalid link')),
    body: StatePanel(
      icon: Icons.link_off_outlined,
      title:
          'Invalid ${resource[0].toUpperCase()}${resource.substring(1)} link',
      message: 'The required $resource identifier is missing.',
      action: FilledButton(
        onPressed: () => context.go('/home'),
        child: const Text('Return home'),
      ),
    ),
  );
}

class _SessionRestoreScreen extends StatelessWidget {
  const _SessionRestoreScreen();

  @override
  Widget build(BuildContext context) =>
      const Scaffold(body: Center(child: CircularProgressIndicator()));
}

class _RouterRefreshNotifier extends ChangeNotifier {
  void notify() => notifyListeners();
}

class DoodhDirectApp extends ConsumerWidget {
  const DoodhDirectApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    ref.watch(notificationControllerProvider);
    ref.listen<String?>(
      notificationControllerProvider.select((state) => state.pendingDeepLink),
      (previous, next) {
        if (next == null || next == previous) return;
        final link = ref
            .read(notificationControllerProvider.notifier)
            .takePendingDeepLink();
        if (link != null) ref.read(routerProvider).push(link);
      },
    );

    return MaterialApp.router(
      title: 'DoodhDirect',
      routerConfig: ref.watch(routerProvider),
      theme: buildDoodhTheme(),
    );
  }
}
