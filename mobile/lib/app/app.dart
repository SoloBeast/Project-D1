import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/login_screen.dart';
import 'package:doodh_direct_mobile/features/auth/otp_screen.dart';
import 'package:doodh_direct_mobile/features/auth/register_screen.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_screens.dart';
import 'package:doodh_direct_mobile/features/customer/customer_screens.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_screens.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_screens.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:doodh_direct_mobile/features/dairy/dairy_screens.dart';
import 'package:doodh_direct_mobile/features/milk_testing/milk_test_screens.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_screen.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_screens.dart';
import 'package:doodh_direct_mobile/features/payments/payment_screens.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_screens.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

final routerProvider = Provider<GoRouter>((ref) {
  final session = ref.watch(sessionControllerProvider);
  return GoRouter(
    initialLocation: '/restore',
    redirect: (context, state) {
      final location = state.matchedLocation;
      final isAuthRoute =
          location == '/login' || location == '/register' || location == '/otp';

      if (session.isLoading) return location == '/restore' ? null : '/restore';
      if (!session.isAuthenticated) return isAuthRoute ? null : '/login';
      if (isAuthRoute || location == '/restore') return '/home';
      return null;
    },
    routes: [
      GoRoute(
        path: '/restore',
        builder: (context, state) => const _SessionRestoreScreen(),
      ),
      GoRoute(path: '/login', builder: (context, state) => const LoginScreen()),
      GoRoute(
        path: '/register',
        builder: (context, state) => const RegisterScreen(),
      ),
      GoRoute(path: '/otp', builder: (context, state) => const OtpScreen()),
      GoRoute(
        path: '/home',
        builder: (context, state) => RoleHomeScreen(role: session.role!),
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
        path: '/admin/catalogue',
        builder: (context, state) => const AdminCatalogueScreen(),
      ),
      GoRoute(
        path: '/admin/cameras',
        builder: (context, state) => const AdminCameraListScreen(),
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
});

String? _requiredPathParameter(GoRouterState state, String name) {
  final value = state.pathParameters[name]?.trim();
  return value == null || value.isEmpty ? null : value;
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
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF087F8C)),
        useMaterial3: true,
      ),
    );
  }
}
