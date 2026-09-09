import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Sign-in entry screen.
///
/// Mobile + OTP is the primary flow: a mobile number and a [Send OTP] button
/// that forwards to the OTP screen carrying the canonical mobile. Password
/// sign-in (email-or-mobile + password) remains available behind the
/// "Use password instead" toggle so existing credentials keep working.
/// Registration is no longer a separate tab/entry — a mobile that the OTP
/// server attests as having no account continues into customer onboarding
/// directly from the OTP flow.
class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _otpFormKey = GlobalKey<FormState>();
  final _passwordFormKey = GlobalKey<FormState>();
  final _otpCountryKey = GlobalKey<CountryCodeMobileFieldState>();
  final _otpMobileController = TextEditingController();
  final _loginController = TextEditingController();
  final _passwordController = TextEditingController();
  bool _showPasswordSignIn = false;
  bool _obscurePassword = true;

  @override
  void dispose() {
    _otpMobileController.dispose();
    _loginController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final session = ref.watch(sessionControllerProvider);
    final busy = session.isLoading;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 440),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Icon(
                    Icons.local_drink,
                    size: 56,
                    color: Theme.of(context).colorScheme.primary,
                  ),
                  const SizedBox(height: 20),
                  Text(
                    'DoodhDirect',
                    style: Theme.of(context).textTheme.headlineMedium,
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 8),
                  Text(
                    'Sign in to your account',
                    style: Theme.of(context).textTheme.titleMedium,
                    textAlign: TextAlign.center,
                  ),
                  if (session.errorMessage != null) ...[
                    const SizedBox(height: 20),
                    MaterialBanner(
                      content: Text(session.errorMessage!),
                      actions: [
                        TextButton(
                          onPressed: () => ref
                              .read(sessionControllerProvider.notifier)
                              .clearError(),
                          child: const Text('Dismiss'),
                        ),
                      ],
                    ),
                  ],
                  const SizedBox(height: 24),
                  if (!_showPasswordSignIn) ...[
                    // Primary: mobile OTP first.
                    Form(
                      key: _otpFormKey,
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          CountryCodeMobileField(
                            key: _otpCountryKey,
                            controller: _otpMobileController,
                            enabled: !busy,
                            autofocus: true,
                            onSubmitted: busy ? null : (_) => _sendOtp(),
                          ),
                          const SizedBox(height: 20),
                          FilledButton.icon(
                            onPressed: busy ? null : _sendOtp,
                            icon: busy
                                ? const SizedBox.square(
                                    dimension: 18,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  )
                                : const Icon(Icons.sms_outlined),
                            label: const Text('Send OTP'),
                          ),
                          const SizedBox(height: 12),
                          TextButton(
                            onPressed: busy
                                ? null
                                : () => setState(
                                    () => _showPasswordSignIn = true,
                                  ),
                            child: const Text('Use password instead'),
                          ),
                        ],
                      ),
                    ),
                  ] else ...[
                    // Secondary: password sign-in (email-or-mobile + password).
                    Form(
                      key: _passwordFormKey,
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          TextFormField(
                            controller: _loginController,
                            enabled: !busy,
                            autofillHints: const [
                              AutofillHints.username,
                              AutofillHints.email,
                              AutofillHints.telephoneNumber,
                            ],
                            decoration: const InputDecoration(
                              labelText: 'Email or mobile',
                              prefixIcon: Icon(Icons.person_outline),
                              border: OutlineInputBorder(),
                            ),
                            validator: (value) =>
                                value == null || value.trim().isEmpty
                                ? 'Enter your email or mobile number.'
                                : null,
                          ),
                          const SizedBox(height: 16),
                          TextFormField(
                            controller: _passwordController,
                            enabled: !busy,
                            obscureText: _obscurePassword,
                            autofillHints: const [AutofillHints.password],
                            decoration: InputDecoration(
                              labelText: 'Password',
                              prefixIcon: const Icon(Icons.lock_outline),
                              border: const OutlineInputBorder(),
                              suffixIcon: IconButton(
                                tooltip: _obscurePassword
                                    ? 'Show password'
                                    : 'Hide password',
                                onPressed: () => setState(
                                  () => _obscurePassword = !_obscurePassword,
                                ),
                                icon: Icon(
                                  _obscurePassword
                                      ? Icons.visibility_outlined
                                      : Icons.visibility_off_outlined,
                                ),
                              ),
                            ),
                            validator: (value) =>
                                value == null || value.isEmpty
                                ? 'Enter your password.'
                                : null,
                            onFieldSubmitted: busy ? null : (_) => _submit(),
                          ),
                          const SizedBox(height: 20),
                          FilledButton.icon(
                            onPressed: busy ? null : _submit,
                            icon: busy
                                ? const SizedBox.square(
                                    dimension: 18,
                                    child: CircularProgressIndicator(
                                      strokeWidth: 2,
                                    ),
                                  )
                                : const Icon(Icons.login),
                            label: const Text('Sign in'),
                          ),
                          const SizedBox(height: 12),
                          TextButton.icon(
                            onPressed: busy
                                ? null
                                : () => context.go(
                                    _withRedirect('/forgot-password'),
                                  ),
                            icon: const Icon(Icons.lock_reset_outlined),
                            label: const Text('Forgot password?'),
                          ),
                          TextButton(
                            onPressed: busy
                                ? null
                                : () => setState(
                                    () => _showPasswordSignIn = false,
                                  ),
                            child: const Text('Sign in with mobile OTP'),
                          ),
                        ],
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  /// Validates the mobile and forwards to the OTP screen, carrying the
  /// canonical mobile so the user does not retype it. The redirect target is
  /// threaded through so the user returns to their intended page after the
  /// whole OTP (or onboarding) flow completes.
  Future<void> _sendOtp() async {
    if (!_otpFormKey.currentState!.validate()) return;
    final mobile = _otpCountryKey.currentState?.canonicalValue ?? '';
    final redirect =
        GoRouterState.of(context).uri.queryParameters['redirectTo'];
    final target = Uri(
      path: '/otp',
      queryParameters: {
        if (mobile.isNotEmpty) 'mobile': mobile,
        if (redirect != null && redirect.isNotEmpty)
          'redirectTo': redirect,
      },
    ).toString();
    context.go(target);
  }

  Future<void> _submit() async {
    if (!_passwordFormKey.currentState!.validate()) return;
    // A 10-digit mobile entry is canonicalized to +91 so it matches the
    // stored canonical identity; email addresses pass through unchanged.
    final login =
        canonicalizeIndianMobile(_loginController.text) ??
        _loginController.text.trim();
    await ref
        .read(sessionControllerProvider.notifier)
        .login(login, _passwordController.text);
  }

  /// Carries the validated return-intent (e.g. `/login?redirectTo=/checkout`)
  /// across the auth routes so the user returns to the exact page they wanted
  /// after signing in. The router validates the value before acting on it.
  String _withRedirect(String path) {
    final redirect =
        GoRouterState.of(context).uri.queryParameters['redirectTo'];
    return redirect == null || redirect.isEmpty
        ? path
        : '$path?redirectTo=${Uri.encodeQueryComponent(redirect)}';
  }
}
