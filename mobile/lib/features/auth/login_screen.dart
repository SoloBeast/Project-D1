import 'package:doodh_direct_mobile/core/config/app_config.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

const _developmentCustomerEmail = 'customer@doodhdirect.local';
const _developmentCustomerPassword = 'DoodhDirect@123';

class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _loginController = TextEditingController();
  final _passwordController = TextEditingController();
  bool _obscurePassword = true;

  @override
  void dispose() {
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
              child: Form(
                key: _formKey,
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
                      validator: (value) => value == null || value.isEmpty
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
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.login),
                      label: const Text('Sign in'),
                    ),
                    if (devToolsEnabled) ...[
                      const SizedBox(height: 12),
                      OutlinedButton.icon(
                        onPressed: busy ? null : _signInAsDevelopmentCustomer,
                        icon: const Icon(Icons.science_outlined),
                        label: const Text('Sign in as development customer'),
                      ),
                    ],
                    const SizedBox(height: 12),
                    OutlinedButton.icon(
                      onPressed: busy ? null : () => context.go('/otp'),
                      icon: const Icon(Icons.sms_outlined),
                      label: const Text('Sign in with mobile OTP'),
                    ),
                    TextButton(
                      onPressed: busy ? null : () => context.go('/register'),
                      child: const Text('Create a customer account'),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    await ref
        .read(sessionControllerProvider.notifier)
        .login(_loginController.text, _passwordController.text);
  }

  Future<void> _signInAsDevelopmentCustomer() async {
    _loginController.text = _developmentCustomerEmail;
    _passwordController.text = _developmentCustomerPassword;
    await ref
        .read(sessionControllerProvider.notifier)
        .login(_developmentCustomerEmail, _developmentCustomerPassword);
  }
}
