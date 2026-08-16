import 'package:doodh_direct_mobile/features/auth/login_screen.dart';
import 'package:doodh_direct_mobile/features/auth/otp_screen.dart';
import 'package:doodh_direct_mobile/features/auth/register_screen.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_screens.dart';
import 'package:doodh_direct_mobile/features/customer/customer_screens.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_screens.dart';
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
          return CheckoutScreen(
            initialProduct: payload?['product'] as CatalogueProduct?,
            initialQuantity: payload?['quantity'] as double?,
          );
        },
      ),
      GoRoute(
        path: '/orders',
        builder: (context, state) => const OrderHistoryScreen(),
      ),
      GoRoute(
        path: '/orders/:orderId',
        builder: (context, state) =>
            OrderDetailScreen(orderId: state.pathParameters['orderId']!),
      ),
      GoRoute(
        path: '/orders/:orderId/confirmation',
        builder: (context, state) =>
            OrderConfirmationScreen(order: state.extra! as OrderSummary),
      ),
      GoRoute(
        path: '/catalogue/products/:productId',
        builder: (context, state) =>
            ProductDetailScreen(productId: state.pathParameters['productId']!),
      ),
      GoRoute(
        path: '/admin/catalogue',
        builder: (context, state) => const AdminCatalogueScreen(),
      ),
    ],
  );
});

class _SessionRestoreScreen extends StatelessWidget {
  const _SessionRestoreScreen();

  @override
  Widget build(BuildContext context) =>
      const Scaffold(body: Center(child: CircularProgressIndicator()));
}

class DoodhDirectApp extends ConsumerWidget {
  const DoodhDirectApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) => MaterialApp.router(
    title: 'DoodhDirect',
    routerConfig: ref.watch(routerProvider),
    theme: ThemeData(
      colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF087F8C)),
      useMaterial3: true,
    ),
  );
}
