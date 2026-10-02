import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Completes customer onboarding for a mobile number that the OTP server
/// attested as verified but that maps to no account yet.
///
/// This runs BEFORE a session exists, so it cannot reuse the authenticated
/// `setPassword` flow ([/create-password]). It calls
/// [SessionController.completeOtpRegistration], which asks the backend to
/// create the Customer from the already-verified mobile, set the password, and
/// return a fresh session. Once the session lands, the router's auth-route
/// redirect delivers the user to their intended destination (or Customer home).
class OtpOnboardingScreen extends ConsumerStatefulWidget {
  const OtpOnboardingScreen({super.key});

  @override
  ConsumerState<OtpOnboardingScreen> createState() =>
      _OtpOnboardingScreenState();
}

class _OtpOnboardingScreenState extends ConsumerState<OtpOnboardingScreen> {
  final _formKey = GlobalKey<FormState>();
  final _passwordController = TextEditingController();
  final _confirmController = TextEditingController();
  bool _obscure = true;
  bool _busy = false;

  @override
  void dispose() {
    _passwordController.dispose();
    _confirmController.dispose();
    super.dispose();
  }

  /// The canonical `+91XXXXXXXXXX` mobile attested by the OTP provider.
  String? get _mobile => _param('mobile');

  /// The consumed MSG91 challenge request id from the verify step.
  String? get _reqId => _param('reqId');

  String? _param(String name) {
    final value = GoRouterState.of(context).uri.queryParameters[name];
    return (value == null || value.trim().isEmpty) ? null : value;
  }

  @override
  Widget build(BuildContext context) {
    final mobile = _mobile;
    final reqId = _reqId;
    final missingContext = mobile == null || reqId == null;

    return Scaffold(
      appBar: AppBar(title: const Text('Create password')),
      body: DoodhPage(
        child: missingContext
            ? Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(
                    maxWidth: DoodhContentMax.narrow,
                  ),
                  child: const _MissingContextNotice(),
                ),
              )
            : Form(
                key: _formKey,
                child: ListView(
                  children: [
                    Center(
                      child: ConstrainedBox(
                        constraints: const BoxConstraints(
                          maxWidth: DoodhContentMax.narrow,
                        ),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            const SizedBox(height: DoodhSpacing.sm),
                            Text(
                              'You are almost in',
                              style: Theme.of(context).textTheme.headlineSmall,
                            ),
                            const SizedBox(height: DoodhSpacing.sm),
                            Text(
                              'Your mobile ${_displayMobile(mobile)} is verified. '
                              'Set a password to create your customer account.',
                            ),
                            const SizedBox(height: DoodhSpacing.lg),
                            DoodhField(
                              label: 'New password',
                              controller: _passwordController,
                              enabled: !_busy,
                              obscureText: _obscure,
                              autofillHints: const [AutofillHints.newPassword],
                              prefixIcon: const Icon(Icons.lock_outline),
                              suffixIcon: IconButton(
                                tooltip: _obscure
                                    ? 'Show password'
                                    : 'Hide password',
                                onPressed: () =>
                                    setState(() => _obscure = !_obscure),
                                icon: Icon(
                                  _obscure
                                      ? Icons.visibility_outlined
                                      : Icons.visibility_off_outlined,
                                ),
                              ),
                              validator: (value) =>
                                  value == null || value.length < 8
                                  ? 'Use at least 8 characters.'
                                  : null,
                            ),
                            const SizedBox(height: DoodhSpacing.md),
                            DoodhField(
                              label: 'Confirm password',
                              controller: _confirmController,
                              enabled: !_busy,
                              obscureText: true,
                              prefixIcon: const Icon(Icons.lock_reset_outlined),
                              validator: (value) =>
                                  value != _passwordController.text
                                  ? 'Passwords do not match.'
                                  : null,
                            ),
                            const SizedBox(height: DoodhSpacing.lg),
                            DoodhButton(
                              label: 'Create account',
                              icon: Icons.person_add_alt,
                              busy: _busy,
                              expand: true,
                              onPressed: _submit,
                            ),
                            const SizedBox(height: DoodhSpacing.xl),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
              ),
      ),
    );
  }

  /// Renders the national number (`98XXX XXXXX`) for a canonical mobile.
  static String _displayMobile(String canonical) {
    final national = canonical.replaceFirst('+91', '');
    if (national.length == 10) {
      return '+91 ${national.substring(0, 5)} ${national.substring(5)}';
    }
    return canonical;
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    final mobile = _mobile;
    final reqId = _reqId;
    if (mobile == null || reqId == null) return;
    setState(() => _busy = true);
    try {
      // On success the controller moves the state to authenticated. The
      // router's auth-route redirect then lands the user on their intended
      // destination (the return-intent carried from the OTP flow) or home —
      // no manual navigation is needed here.
      await ref
          .read(sessionControllerProvider.notifier)
          .completeOtpRegistration(
            mobile: mobile,
            reqId: reqId,
            newPassword: _passwordController.text,
          );
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('Account created. Welcome to DoodhDirect!'),
          ),
        );
      }
    } on Object catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(error.toString())));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }
}

/// Shown when the route is opened without the verified mobile + reqId that
/// onboarding requires — e.g. a stale/deep link. There is nothing safe to
/// complete without server-verified context, so the user returns to sign in.
class _MissingContextNotice extends StatelessWidget {
  const _MissingContextNotice();

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Text(
        'Sign in required',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: DoodhSpacing.sm),
      const Text(
        'Your verification details are missing or have expired. Sign in again '
        'to continue.',
      ),
      const SizedBox(height: DoodhSpacing.md),
      DoodhButton(
        label: 'Sign in',
        icon: Icons.login,
        expand: true,
        onPressed: () => context.go('/login'),
      ),
    ],
  );
}
