import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

class RegisterScreen extends ConsumerStatefulWidget {
  const RegisterScreen({super.key});

  @override
  ConsumerState<RegisterScreen> createState() => _RegisterScreenState();
}

class _RegisterScreenState extends ConsumerState<RegisterScreen> {
  final _formKey = GlobalKey<FormState>();
  final _nameController = TextEditingController();
  final _emailController = TextEditingController();
  final _countryKey = GlobalKey<CountryCodeMobileFieldState>();
  final _mobileController = TextEditingController();
  final _passwordController = TextEditingController();
  final _confirmController = TextEditingController();
  bool _obscurePassword = true;

  @override
  void dispose() {
    _nameController.dispose();
    _emailController.dispose();
    _mobileController.dispose();
    _passwordController.dispose();
    _confirmController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final session = ref.watch(sessionControllerProvider);
    final busy = session.isLoading;

    return Scaffold(
      appBar: AppBar(title: const Text('Create account')),
      body: DoodhPage(
        child: Form(
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
                        'Customer registration',
                        style: Theme.of(context).textTheme.headlineSmall,
                      ),
                      const SizedBox(height: DoodhSpacing.sm),
                      Text(
                        'Use an email address or mobile number to sign in later.',
                        style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: DoodhColors.muted,
                        ),
                      ),
                      if (session.errorMessage != null) ...[
                        const SizedBox(height: DoodhSpacing.md),
                        DoodhErrorBanner(message: session.errorMessage!),
                      ],
                      const SizedBox(height: DoodhSpacing.lg),
                      TextFormField(
                        controller: _nameController,
                        enabled: !busy,
                        textCapitalization: TextCapitalization.words,
                        decoration: const InputDecoration(
                          labelText: 'Full name',
                          prefixIcon: Icon(Icons.badge_outlined),
                        ),
                        validator: (value) =>
                            value == null || value.trim().length < 2
                            ? 'Enter your name.'
                            : null,
                      ),
                      const SizedBox(height: DoodhSpacing.md),
                      DoodhField(
                        label: 'Email address (optional)',
                        controller: _emailController,
                        enabled: !busy,
                        keyboardType: TextInputType.emailAddress,
                        prefixIcon: const Icon(Icons.email_outlined),
                        validator: (value) {
                          if (value == null || value.trim().isEmpty) {
                            return null;
                          }
                          return RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$')
                                  .hasMatch(value.trim())
                              ? null
                              : 'Enter a valid email address.';
                        },
                      ),
                      const SizedBox(height: DoodhSpacing.md),
                      CountryCodeMobileField(
                        key: _countryKey,
                        controller: _mobileController,
                        enabled: !busy,
                        optional: true,
                        label: 'Mobile number (optional)',
                        validator: (canonical) {
                          if (canonical == null &&
                              _emailController.text.trim().isEmpty) {
                            return 'Enter an email address or mobile number.';
                          }
                          return null;
                        },
                      ),
                      const SizedBox(height: DoodhSpacing.md),
                      DoodhField(
                        label: 'Password',
                        controller: _passwordController,
                        enabled: !busy,
                        obscureText: _obscurePassword,
                        prefixIcon: const Icon(Icons.lock_outline),
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
                        validator: (value) => value == null || value.length < 8
                            ? 'Use at least 8 characters.'
                            : null,
                      ),
                      const SizedBox(height: DoodhSpacing.md),
                      DoodhField(
                        label: 'Confirm password',
                        controller: _confirmController,
                        enabled: !busy,
                        obscureText: true,
                        prefixIcon: const Icon(Icons.lock_reset_outlined),
                        validator: (value) => value != _passwordController.text
                            ? 'Passwords do not match.'
                            : null,
                      ),
                      const SizedBox(height: DoodhSpacing.lg),
                      DoodhButton(
                        label: 'Create account',
                        icon: Icons.person_add_outlined,
                        busy: busy,
                        expand: true,
                        onPressed: _submit,
                      ),
                      const SizedBox(height: DoodhSpacing.xs),
                      TextButton(
                        onPressed: busy
                            ? null
                            : () => context.go(_withRedirect('/login')),
                        child: const Text('Back to sign in'),
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

  /// Threads a sanitized [redirectTo] return-intent (e.g. `/login?redirectTo=/checkout`)
  /// across the auth screens so the post-auth landing is preserved.
  String _withRedirect(String path) {
    final redirect = GoRouterState.of(context).uri.queryParameters['redirectTo'];
    return redirect == null || redirect.isEmpty
        ? path
        : '$path?redirectTo=${Uri.encodeQueryComponent(redirect)}';
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    final mobile = _countryKey.currentState?.canonicalValue;
    await ref
        .read(sessionControllerProvider.notifier)
        .register(
          displayName: _nameController.text,
          email: _emailController.text,
          mobile: mobile,
          password: _passwordController.text,
        );
  }
}
