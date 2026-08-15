import 'package:doodh_direct_mobile/features/auth/login_screen.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/home/role_home_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

final routerProvider = Provider<GoRouter>((ref) {
  final session = ref.watch(sessionControllerProvider);
  return GoRouter(
    initialLocation: session.isAuthenticated ? '/home' : '/login',
    redirect: (context, state) {
      final isLogin = state.matchedLocation == '/login';
      if (!session.isAuthenticated && !isLogin) return '/login';
      if (session.isAuthenticated && isLogin) return '/home';
      return null;
    },
    routes: [
      GoRoute(path: '/login', builder: (context, state) => const LoginScreen()),
      GoRoute(
        path: '/home',
        builder: (context, state) => RoleHomeScreen(role: session.role!),
      ),
    ],
  );
});

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
