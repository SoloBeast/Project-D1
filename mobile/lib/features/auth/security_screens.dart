import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Sets a password on an account that currently has none — e.g. an account
/// created through mobile OTP login. The backend returns a refreshed session
/// reflecting the new `hasPassword` capability, which replaces the current one.
class CreatePasswordScreen extends ConsumerStatefulWidget {
  const CreatePasswordScreen({super.key});

  @override
  ConsumerState<CreatePasswordScreen> createState() =>
      _CreatePasswordScreenState();
}

class _CreatePasswordScreenState extends ConsumerState<CreatePasswordScreen> {
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

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Create password')),
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
                    Text(
                      'Secure your account',
                      style: Theme.of(context).textTheme.headlineSmall,
                    ),
                    const SizedBox(height: 8),
                    const Text(
                      'Your account was created with a one-time code. Set a '
                      'password so you can sign in with it in future.',
                    ),
                    const SizedBox(height: 24),
                    TextFormField(
                      controller: _passwordController,
                      enabled: !_busy,
                      obscureText: _obscure,
                      autofillHints: const [AutofillHints.newPassword],
                      decoration: InputDecoration(
                        labelText: 'New password',
                        prefixIcon: const Icon(Icons.lock_outline),
                        border: const OutlineInputBorder(),
                        suffixIcon: IconButton(
                          tooltip: _obscure ? 'Show password' : 'Hide password',
                          onPressed: () =>
                              setState(() => _obscure = !_obscure),
                          icon: Icon(
                            _obscure
                                ? Icons.visibility_outlined
                                : Icons.visibility_off_outlined,
                          ),
                        ),
                      ),
                      validator: (value) => value == null || value.length < 8
                          ? 'Use at least 8 characters.'
                          : null,
                    ),
                    const SizedBox(height: 16),
                    TextFormField(
                      controller: _confirmController,
                      enabled: !_busy,
                      obscureText: true,
                      decoration: const InputDecoration(
                        labelText: 'Confirm password',
                        prefixIcon: Icon(Icons.lock_reset_outlined),
                        border: OutlineInputBorder(),
                      ),
                      validator: (value) => value != _passwordController.text
                          ? 'Passwords do not match.'
                          : null,
                    ),
                    const SizedBox(height: 20),
                    FilledButton.icon(
                      onPressed: _busy ? null : _submit,
                      icon: _busy
                          ? const SizedBox.square(
                              dimension: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.lock_reset_outlined),
                      label: const Text('Set password'),
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
    setState(() => _busy = true);
    try {
      await ref
          .read(sessionControllerProvider.notifier)
          .setPassword(_passwordController.text);
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('Password set. You can sign in with it.'),
          ),
        );
        // Return to the Login & security screen that opened this flow. The
        // refreshed session (hasPassword = true) updates the password card to
        // show "Password set" / Change password. The snackbar is shown via the
        // root ScaffoldMessenger so it stays visible over the previous screen.
        context.pop();
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

/// Changes the account password after verifying the current one. Contains only
/// password fields — profile details (name, mobile, email, etc.) stay on the
/// My Account screen. The session stays active; errors (e.g. a wrong current
/// password) surface inline via a snackbar.
class ChangePasswordScreen extends ConsumerStatefulWidget {
  const ChangePasswordScreen({super.key});

  @override
  ConsumerState<ChangePasswordScreen> createState() =>
      _ChangePasswordScreenState();
}

class _ChangePasswordScreenState extends ConsumerState<ChangePasswordScreen> {
  final _formKey = GlobalKey<FormState>();
  final _currentController = TextEditingController();
  final _newController = TextEditingController();
  final _confirmController = TextEditingController();
  bool _obscure = true;
  bool _busy = false;

  @override
  void dispose() {
    _currentController.dispose();
    _newController.dispose();
    _confirmController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Change Password')),
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
                    Text(
                      'Update your password',
                      style: Theme.of(context).textTheme.headlineSmall,
                    ),
                    const SizedBox(height: 8),
                    const Text(
                      'Enter your current password and choose a new one to '
                      'keep your account secure.',
                    ),
                    const SizedBox(height: 24),
                    TextFormField(
                      controller: _currentController,
                      enabled: !_busy,
                      obscureText: true,
                      autofillHints: const [AutofillHints.password],
                      decoration: const InputDecoration(
                        labelText: 'Current Password',
                        prefixIcon: Icon(Icons.lock_outline),
                        border: OutlineInputBorder(),
                      ),
                      validator: (value) => value == null || value.isEmpty
                          ? 'Enter your current password.'
                          : null,
                    ),
                    const SizedBox(height: 16),
                    TextFormField(
                      controller: _newController,
                      enabled: !_busy,
                      obscureText: _obscure,
                      autofillHints: const [AutofillHints.newPassword],
                      decoration: InputDecoration(
                        labelText: 'New Password',
                        prefixIcon: const Icon(Icons.lock_reset_outlined),
                        border: const OutlineInputBorder(),
                        suffixIcon: IconButton(
                          tooltip:
                              _obscure ? 'Show password' : 'Hide password',
                          onPressed: () =>
                              setState(() => _obscure = !_obscure),
                          icon: Icon(
                            _obscure
                                ? Icons.visibility_outlined
                                : Icons.visibility_off_outlined,
                          ),
                        ),
                      ),
                      validator: (value) => value == null || value.length < 8
                          ? 'Use at least 8 characters.'
                          : null,
                    ),
                    const SizedBox(height: 16),
                    TextFormField(
                      controller: _confirmController,
                      enabled: !_busy,
                      obscureText: true,
                      decoration: const InputDecoration(
                        labelText: 'Confirm New Password',
                        prefixIcon: Icon(Icons.lock_reset_outlined),
                        border: OutlineInputBorder(),
                      ),
                      validator: (value) => value != _newController.text
                          ? 'Passwords do not match.'
                          : null,
                    ),
                    const SizedBox(height: 20),
                    FilledButton.icon(
                      onPressed: _busy ? null : _submit,
                      icon: _busy
                          ? const SizedBox.square(
                              dimension: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.lock_reset_outlined),
                      label: const Text('Change Password'),
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
    setState(() => _busy = true);
    try {
      await ref
          .read(sessionControllerProvider.notifier)
          .changePassword(
            currentPassword: _currentController.text,
            newPassword: _newController.text,
          );
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Password changed.')),
        );
        context.pop();
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

/// Requests a password-reset OTP, verifies it, and lets the user set a new
/// password. The backend creates a fresh session on reset, so the user lands
/// authenticated in their workspace.
class ForgotPasswordScreen extends ConsumerStatefulWidget {
  const ForgotPasswordScreen({super.key});

  @override
  ConsumerState<ForgotPasswordScreen> createState() =>
      _ForgotPasswordScreenState();
}

class _ForgotPasswordScreenState extends ConsumerState<ForgotPasswordScreen> {
  final _formKey = GlobalKey<FormState>();
  final _mobileController = TextEditingController();
  final _countryKey = GlobalKey<CountryCodeMobileFieldState>();
  String _canonicalMobile = '';
  final _codeController = TextEditingController();
  final _passwordController = TextEditingController();
  final _confirmController = TextEditingController();
  bool _obscurePassword = true;
  bool _obscureConfirmPassword = true;
  int _step = 0; // 0: mobile, 1: OTP, 2: new password
  bool _busy = false;
  String? _reqId;

  bool get _otpVerified => _step == 2;

  @override
  void dispose() {
    _mobileController.dispose();
    _codeController.dispose();
    _passwordController.dispose();
    _confirmController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final title = switch (_step) {
      0 => 'Reset your password',
      1 => 'Verify your mobile',
      _ => 'Choose a new password',
    };
    final description = switch (_step) {
      0 => 'Enter the mobile number on your account. We will send a one-time code through SMS.',
      1 => 'Enter the 6-digit code sent to your mobile. Password fields appear after verification.',
      _ => 'Your mobile is verified. Choose a new password for your account.',
    };
    return Scaffold(
      appBar: AppBar(title: const Text('Reset password')),
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
                    Text(title, style: Theme.of(context).textTheme.headlineSmall),
                    const SizedBox(height: 8),
                    Text(description),
                    const SizedBox(height: 24),
                    if (_step == 0)
                      CountryCodeMobileField(
                        key: _countryKey,
                        controller: _mobileController,
                        enabled: !_busy,
                      ),
                    if (_step == 1)
                      TextFormField(
                        controller: _codeController,
                        enabled: !_busy,
                        keyboardType: TextInputType.number,
                        maxLength: 6,
                        decoration: const InputDecoration(
                          labelText: '6-digit verification code',
                          prefixIcon: Icon(Icons.password_outlined),
                          border: OutlineInputBorder(),
                          counterText: '',
                        ),
                        validator: (value) => value == null || value.trim().length != 6
                            ? 'Enter the 6-digit code.'
                            : null,
                      ),
                    if (_otpVerified) ...[
                      TextFormField(
                        controller: _passwordController,
                        enabled: !_busy,
                        obscureText: _obscurePassword,
                        autofillHints: const [AutofillHints.newPassword],
                        decoration: InputDecoration(
                          labelText: 'New password',
                          prefixIcon: const Icon(Icons.lock_outline),
                          border: const OutlineInputBorder(),
                          suffixIcon: IconButton(
                            tooltip: _obscurePassword ? 'Show password' : 'Hide password',
                            onPressed: () => setState(() => _obscurePassword = !_obscurePassword),
                            icon: Icon(_obscurePassword ? Icons.visibility_outlined : Icons.visibility_off_outlined),
                          ),
                        ),
                        validator: (value) => value == null || value.length < 8
                            ? 'Use at least 8 characters.'
                            : null,
                      ),
                      const SizedBox(height: 16),
                      TextFormField(
                        controller: _confirmController,
                        enabled: !_busy,
                        obscureText: _obscureConfirmPassword,
                        decoration: InputDecoration(
                          labelText: 'Confirm password',
                          prefixIcon: const Icon(Icons.lock_reset_outlined),
                          border: const OutlineInputBorder(),
                          suffixIcon: IconButton(
                            tooltip: _obscureConfirmPassword ? 'Show password' : 'Hide password',
                            onPressed: () => setState(() => _obscureConfirmPassword = !_obscureConfirmPassword),
                            icon: Icon(_obscureConfirmPassword ? Icons.visibility_outlined : Icons.visibility_off_outlined),
                          ),
                        ),
                        validator: (value) => value != _passwordController.text
                            ? 'Passwords do not match.'
                            : null,
                      ),
                    ],
                    const SizedBox(height: 20),
                    FilledButton.icon(
                      onPressed: _busy ? null : (_step == 0 ? _send : _step == 1 ? _verify : _reset),
                      icon: _busy
                          ? const SizedBox.square(dimension: 18, child: CircularProgressIndicator(strokeWidth: 2))
                          : Icon(_step == 0 ? Icons.sms_outlined : _step == 1 ? Icons.verified_outlined : Icons.lock_reset_outlined),
                      label: Text(_step == 0 ? 'Send OTP' : _step == 1 ? 'Verify OTP' : 'Reset password'),
                    ),
                    if (_step == 1)
                      TextButton.icon(
                        onPressed: _busy ? null : _resend,
                        icon: const Icon(Icons.refresh),
                        label: const Text('Send a new code'),
                      ),
                    TextButton(
                      onPressed: _busy ? null : () => context.go(_withRedirect('/login')),
                      child: const Text('Cancel'),
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

  Future<void> _send() async {
    if (!_formKey.currentState!.validate()) return;
    _canonicalMobile = _countryKey.currentState?.canonicalValue ?? '';
    setState(() => _busy = true);
    try {
      final reqId = await ref
          .read(sessionControllerProvider.notifier)
          .forgotPassword(_canonicalMobile);
      if (mounted) {
        setState(() {
          _reqId = reqId;
          _step = 1;
        });
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Verification code request accepted.')),
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

  Future<void> _resend() async {
    setState(() => _busy = true);
    try {
      final reqId = await ref
          .read(sessionControllerProvider.notifier)
          .forgotPassword(_canonicalMobile);
      if (mounted) {
        setState(() => _reqId = reqId);
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('A new verification code is on its way.'),
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

  Future<void> _verify() async {
    if (!_formKey.currentState!.validate()) return;
    final reqId = _reqId;
    if (reqId == null || reqId.isEmpty) return;
    setState(() => _busy = true);
    try {
      await ref.read(sessionControllerProvider.notifier).verifyResetOtp(
        destination: _canonicalMobile,
        code: _codeController.text,
        reqId: reqId,
      );
      if (mounted) {
        setState(() => _step = 2);
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Mobile verified. Choose a new password.')),
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

  Future<void> _reset() async {
    if (!_formKey.currentState!.validate()) return;
    final reqId = _reqId;
    if (reqId == null || reqId.isEmpty) return;
    setState(() => _busy = true);
    try {
      await ref.read(sessionControllerProvider.notifier).resetPassword(
        mobile: _canonicalMobile,
        reqId: reqId,
        newPassword: _passwordController.text,
      );
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Password reset. You are signed in.')),
        );
        context.go('/home');
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


  /// Carries a sanitized return-intent across the auth routes so the post-auth
  /// landing is preserved after the fresh session created by the reset.
  String _withRedirect(String path) {
    final redirect =
        GoRouterState.of(context).uri.queryParameters['redirectTo'];
    return redirect == null || redirect.isEmpty
        ? path
        : '$path?redirectTo=${Uri.encodeQueryComponent(redirect)}';
  }
}

/// Login & Security: shows the account's email (verified or pending) and
/// password status, with actions to add/verify/change the email and to set or
/// change the password.
class LoginSecurityScreen extends ConsumerStatefulWidget {
  const LoginSecurityScreen({super.key});

  @override
  ConsumerState<LoginSecurityScreen> createState() =>
      _LoginSecurityScreenState();
}

class _LoginSecurityScreenState extends ConsumerState<LoginSecurityScreen> {
  final _dialogFormKey = GlobalKey<FormState>();

  @override
  Widget build(BuildContext context) {
    final session = ref.watch(sessionControllerProvider);
    final user = session.session?.user;

    return Scaffold(
      appBar: AppBar(title: const Text('Login & security')),
      body: SafeArea(
        child: user == null
            ? const Center(child: CircularProgressIndicator())
            : ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  DoodhSectionHeader(title: 'Mobile number'),
                  const SizedBox(height: 8),
                  Card(
                    child: ListTile(
                      leading: Icon(
                        user.mobile == null || user.pendingMobile != null
                            ? Icons.phone_android_outlined
                            : Icons.verified_outlined,
                        color: user.mobile == null || user.pendingMobile != null
                            ? Theme.of(context).colorScheme.error
                            : DoodhColors.teal,
                      ),
                      title: Text(user.mobile ?? 'No mobile number'),
                      subtitle: Text(
                        user.pendingMobile != null
                            ? 'Pending confirmation: ${user.pendingMobile}'
                            : user.mobile == null
                            ? 'Add a mobile number for OTP sign-in.'
                            : 'Active',
                      ),
                      isThreeLine: true,
                      trailing: IconButton(
                        tooltip: user.mobile == null ? 'Add mobile' : 'Change mobile',
                        onPressed: () => context.push('/security/mobile'),
                        icon: const Icon(Icons.edit_outlined),
                      ),
                    ),
                  ),
                  if (user.pendingMobile != null) ...[
                    const SizedBox(height: 8),
                    Card(
                      child: ListTile(
                        leading: Icon(Icons.sms_outlined, color: Theme.of(context).colorScheme.primary),
                        title: const Text('Verify pending mobile'),
                        subtitle: const Text('Enter the code sent to your new mobile number.'),
                        trailing: const Icon(Icons.arrow_forward_ios_rounded, size: 16),
                        onTap: () => context.push('/security/mobile'),
                      ),
                    ),
                  ],
                  const SizedBox(height: 24),
                  DoodhSectionHeader(title: 'Email'),
                  const SizedBox(height: 8),
                  Card(
                    child: ListTile(
                      leading: Icon(
                        user.email == null
                            ? Icons.mark_email_read_outlined
                            : user.emailVerified
                            ? Icons.verified_outlined
                            : Icons.mark_email_unread_outlined,
                        color: user.email == null || !user.emailVerified
                            ? Theme.of(context).colorScheme.error
                            : DoodhColors.teal,
                      ),
                      title: Text(user.email ?? 'No email address'),
                      subtitle: Text(
                        user.pendingEmail != null
                            ? 'Pending confirmation: ${user.pendingEmail}'
                            : user.email == null
                            ? 'Add an email to receive account notifications.'
                            : user.emailVerified
                            ? 'Verified'
                            : 'Not yet verified — verify to secure your account.',
                      ),
                      isThreeLine: true,
                      trailing: IconButton(
                        tooltip:
                            user.email == null ? 'Add email' : 'Change email',
                        onPressed: () =>
                            context.push('/security/email'),
                        icon: const Icon(Icons.edit_outlined),
                      ),
                    ),
                  ),
                  if (user.pendingEmail != null) ...[
                    const SizedBox(height: 8),
                    Card(
                      child: ListTile(
                        leading: Icon(
                          Icons.sms_outlined,
                          color: Theme.of(context).colorScheme.primary,
                        ),
                        title: const Text('Verify pending email'),
                        subtitle: const Text(
                          'Enter the code sent to your new email address.',
                        ),
                        trailing: const Icon(
                          Icons.arrow_forward_ios_rounded,
                          size: 16,
                        ),
                        onTap: () => context.push('/security/email'),
                      ),
                    ),
                  ],
                  const SizedBox(height: 24),
                  DoodhSectionHeader(title: 'Password'),
                  const SizedBox(height: 8),
                  Card(
                    child: ListTile(
                      leading: Icon(
                        user.hasPassword
                            ? Icons.password_outlined
                            : Icons.lock_open_outlined,
                        color: user.hasPassword
                            ? DoodhColors.teal
                            : Theme.of(context).colorScheme.error,
                      ),
                      title: Text(
                        user.hasPassword
                            ? 'Configure Password'
                            : 'No password set',
                      ),
                      subtitle: Text(
                        user.hasPassword
                            ? 'Change it any time to keep your account secure.'
                            : 'Set a password to sign in with it instead of an OTP.',
                      ),
                      isThreeLine: true,
                      trailing: const Icon(
                        Icons.arrow_forward_ios_rounded,
                        size: 16,
                      ),
                      onTap: () {
                        if (user.hasPassword) {
                          _showChangePasswordDialog(context);
                        } else {
                          context.push('/create-password');
                        }
                      },
                    ),
                  ),
                ],
              ),
      ),
    );
  }

  Future<void> _showChangePasswordDialog(BuildContext context) async {
    final controller = ProviderScope.containerOf(context, listen: false);
    final currentController = TextEditingController();
    final newController = TextEditingController();
    final confirmController = TextEditingController();
    var obscure = true;
    var busy = false;

    await showDialog<void>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (dialogContext, setDialogState) => AlertDialog(
          title: const Text('Change password'),
          content: SingleChildScrollView(
            child: Form(
              key: _dialogFormKey,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  TextFormField(
                    controller: currentController,
                    enabled: !busy,
                    obscureText: true,
                    autofillHints: const [AutofillHints.password],
                    decoration: const InputDecoration(
                      labelText: 'Current password',
                      prefixIcon: Icon(Icons.lock_outline),
                      border: OutlineInputBorder(),
                    ),
                    validator: (value) =>
                        value == null || value.isEmpty
                        ? 'Enter your current password.'
                        : null,
                  ),
                  const SizedBox(height: 16),
                  TextFormField(
                    controller: newController,
                    enabled: !busy,
                    obscureText: obscure,
                    autofillHints: const [AutofillHints.newPassword],
                    decoration: InputDecoration(
                      labelText: 'New password',
                      prefixIcon: const Icon(Icons.lock_reset_outlined),
                      border: const OutlineInputBorder(),
                      suffixIcon: IconButton(
                        tooltip: obscure ? 'Show password' : 'Hide password',
                        onPressed: () =>
                            setDialogState(() => obscure = !obscure),
                        icon: Icon(
                          obscure
                              ? Icons.visibility_outlined
                              : Icons.visibility_off_outlined,
                        ),
                      ),
                    ),
                    validator: (value) =>
                        value == null || value.length < 8
                        ? 'Use at least 8 characters.'
                        : null,
                  ),
                  const SizedBox(height: 16),
                  TextFormField(
                    controller: confirmController,
                    enabled: !busy,
                    obscureText: true,
                    decoration: const InputDecoration(
                      labelText: 'Confirm new password',
                      prefixIcon: Icon(Icons.lock_reset_outlined),
                      border: OutlineInputBorder(),
                    ),
                    validator: (value) => value != newController.text
                        ? 'Passwords do not match.'
                        : null,
                  ),
                ],
              ),
            ),
          ),
          actions: [
            TextButton(
              onPressed: busy
                  ? null
                  : () => Navigator.of(dialogContext).pop(),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: busy
                  ? null
                  : () async {
                      if (!_dialogFormKey.currentState!.validate()) return;
                      setDialogState(() => busy = true);
                      try {
                        await controller
                            .read(sessionControllerProvider.notifier)
                            .changePassword(
                              currentPassword: currentController.text,
                              newPassword: newController.text,
                            );
                        if (dialogContext.mounted) {
                          Navigator.of(dialogContext).pop();
                          ScaffoldMessenger.of(context).showSnackBar(
                            const SnackBar(
                              content: Text('Password changed.'),
                            ),
                          );
                        }
                      } on Object catch (error) {
                        if (dialogContext.mounted) {
                          ScaffoldMessenger.of(context).showSnackBar(
                            SnackBar(content: Text(error.toString())),
                          );
                        }
                      } finally {
                        if (dialogContext.mounted) {
                          setDialogState(() => busy = false);
                        }
                      }
                    },
              child: const Text('Change password'),
            ),
          ],
        ),
      ),
    );
    currentController.dispose();
    newController.dispose();
    confirmController.dispose();
  }
}

/// Adds, changes, or verifies the account mobile number.
class MobileChangeScreen extends ConsumerStatefulWidget {
  const MobileChangeScreen({super.key});

  @override
  ConsumerState<MobileChangeScreen> createState() => _MobileChangeScreenState();
}

class _MobileChangeScreenState extends ConsumerState<MobileChangeScreen> {
  final _formKey = GlobalKey<FormState>();
  final _mobileController = TextEditingController();
  final _codeController = TextEditingController();
  final _countryKey = GlobalKey<CountryCodeMobileFieldState>();
  late CountryCode _initialCountry;
  late bool _verifying;
  late String _sentTo;
  bool _busy = false;
  String? _reqId;

  @override
  void initState() {
    super.initState();
    final user = ref.read(sessionControllerProvider).session?.user;
    final pending = user?.pendingMobile;
    _verifying = pending != null;
    _sentTo = pending ?? user?.mobile ?? '';
    _initialCountry = CountryCodes.india;
    final trimmed = _sentTo.trim();
    if (trimmed.isEmpty) return;
    if (trimmed.startsWith('+') || trimmed.startsWith('00')) {
      final parsed = parseMobileCountry(trimmed);
      if (parsed != null) {
        _initialCountry = parsed.$1;
        _mobileController.text = parsed.$2;
        return;
      }
    } else {
      final canonical = canonicalizeIndianMobile(trimmed);
      if (canonical != null && canonical.length == 13) {
        _mobileController.text = canonical.substring(3);
        return;
      }
    }
    _mobileController.text = _sentTo;
  }

  @override
  void dispose() {
    _mobileController.dispose();
    _codeController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: Text(_verifying ? 'Verify mobile' : 'Change mobile')),
    body: SafeArea(child: Center(child: SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 440),
        child: Form(key: _formKey, child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: _verifying ? _verificationFields() : _addressFields(),
        )),
      ),
    ))),
  );

  List<Widget> _addressFields() => [
    Text('Add a mobile number', style: Theme.of(context).textTheme.headlineSmall),
    const SizedBox(height: 8),
    const Text('We will send a one-time code by SMS to confirm this number.'),
    const SizedBox(height: 24),
    CountryCodeMobileField(
      key: _countryKey,
      controller: _mobileController,
      initialCountry: _initialCountry,
      enabled: !_busy,
    ),
    const SizedBox(height: 20),
    FilledButton.icon(onPressed: _busy ? null : _requestCode, icon: const Icon(Icons.sms_outlined), label: const Text('Send verification code')),
  ];

  List<Widget> _verificationFields() => [
    Text('Verify your mobile', style: Theme.of(context).textTheme.headlineSmall),
    const SizedBox(height: 8),
    Text('Enter the 6-digit code sent to $_sentTo.'),
    const SizedBox(height: 24),
    TextFormField(controller: _codeController, enabled: !_busy, keyboardType: TextInputType.number, maxLength: 6, decoration: const InputDecoration(labelText: '6-digit verification code', prefixIcon: Icon(Icons.password_outlined), border: OutlineInputBorder(), counterText: ''), validator: (value) => value == null || value.trim().length != 6 ? 'Enter the 6-digit code.' : null),
    const SizedBox(height: 20),
    FilledButton.icon(onPressed: _busy ? null : _verify, icon: const Icon(Icons.verified_outlined), label: const Text('Verify mobile')),
    TextButton.icon(onPressed: _busy ? null : _resendCode, icon: const Icon(Icons.refresh), label: const Text('Send a new code')),
  ];

  Future<void> _requestCode() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() => _busy = true);
    try {
      final requested = await ref.read(sessionControllerProvider.notifier).requestMobileChange(_countryKey.currentState?.canonicalValue ?? '');
      if (mounted) setState(() { _sentTo = requested.pendingMobile; _reqId = requested.reqId; _verifying = true; });
    } on Object catch (error) { if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(error.toString()))); }
    finally { if (mounted) setState(() => _busy = false); }
  }

  Future<void> _resendCode() async {
    setState(() => _busy = true);
    try {
      final requested = await ref.read(sessionControllerProvider.notifier).requestMobileChange(_sentTo);
      if (mounted) setState(() => _reqId = requested.reqId);
    } on Object catch (error) { if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(error.toString()))); }
    finally { if (mounted) setState(() => _busy = false); }
  }

  Future<void> _verify() async {
    if (!_formKey.currentState!.validate()) return;
    final reqId = _reqId;
    if (reqId == null || reqId.isEmpty) { await _resendCode(); return; }
    setState(() => _busy = true);
    try {
      await ref.read(sessionControllerProvider.notifier).verifyMobileChange(_sentTo, _codeController.text, reqId);
      if (mounted) { ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Mobile verified.'))); context.pop(); }
    } on Object catch (error) { if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(error.toString()))); }
    finally { if (mounted) setState(() => _busy = false); }
  }
}

/// Adds, changes, or verifies the account email.
///
/// - No email yet (or a verified one): enter a new address and request a code.
/// - A pending (staged) email exists — e.g. an earlier request still awaiting
///   its OTP — or the current email is unverified: show the verification form
///   for that address directly. Because the reqId is held only in memory, the
///   user taps "Send a new code" to obtain a fresh one before verifying.
class EmailChangeScreen extends ConsumerStatefulWidget {
  const EmailChangeScreen({super.key});

  @override
  ConsumerState<EmailChangeScreen> createState() => _EmailChangeScreenState();
}

class _EmailChangeScreenState extends ConsumerState<EmailChangeScreen> {
  final _formKey = GlobalKey<FormState>();
  final _emailController = TextEditingController();
  final _codeController = TextEditingController();
  late bool _verifying;
  late String _sentTo;
  bool _busy = false;
  String? _reqId;

  @override
  void initState() {
    super.initState();
    final user = ref.read(sessionControllerProvider).session?.user;
    final pending = user?.pendingEmail;
    final hasUnverified = user?.email != null && !(user?.emailVerified ?? true);
    _verifying = pending != null || hasUnverified;
    _sentTo = pending ?? user?.email ?? '';
    _emailController.text = _sentTo;
  }

  @override
  void dispose() {
    _emailController.dispose();
    _codeController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(_verifying ? 'Verify email' : 'Change email'),
      ),
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
                  children: _verifying
                      ? _verificationFields(context)
                      : _addressFields(context),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  List<Widget> _addressFields(BuildContext context) {
    return [
      Text(
        'Add an email address',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: 8),
      const Text(
        'We will send a one-time code to confirm this address. It will be '
        'used for account notifications and as a sign-in identifier.',
      ),
      const SizedBox(height: 24),
      TextFormField(
        controller: _emailController,
        enabled: !_busy,
        keyboardType: TextInputType.emailAddress,
        autofillHints: const [AutofillHints.email],
        decoration: const InputDecoration(
          labelText: 'Email address',
          prefixIcon: Icon(Icons.mail_outline),
          border: OutlineInputBorder(),
        ),
        validator: _validateEmail,
      ),
      const SizedBox(height: 20),
      FilledButton.icon(
        onPressed: _busy ? null : _requestCode,
        icon: _busy
            ? const SizedBox.square(
                dimension: 18,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : const Icon(Icons.sms_outlined),
        label: const Text('Send verification code'),
      ),
    ];
  }

  List<Widget> _verificationFields(BuildContext context) {
    return [
      Text(
        'Verify your email',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: 8),
      Text(
        'Enter the 6-digit code sent to $_sentTo.',
      ),
      const SizedBox(height: 24),
      TextFormField(
        controller: _codeController,
        enabled: !_busy,
        keyboardType: TextInputType.number,
        maxLength: 6,
        decoration: const InputDecoration(
          labelText: '6-digit verification code',
          prefixIcon: Icon(Icons.password_outlined),
          border: OutlineInputBorder(),
          counterText: '',
        ),
        validator: (value) => value == null || value.trim().length != 6
            ? 'Enter the 6-digit code.'
            : null,
      ),
      const SizedBox(height: 20),
      FilledButton.icon(
        onPressed: _busy ? null : _verify,
        icon: _busy
            ? const SizedBox.square(
                dimension: 18,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : const Icon(Icons.verified_outlined),
        label: const Text('Verify email'),
      ),
      const SizedBox(height: 4),
      TextButton.icon(
        onPressed: _busy ? null : _resendCode,
        icon: const Icon(Icons.refresh),
        label: const Text('Send a new code'),
      ),
    ];
  }

  Future<void> _requestCode() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() => _busy = true);
    try {
      final requested = await ref
          .read(sessionControllerProvider.notifier)
          .requestEmailChange(_emailController.text.trim());
      if (mounted) {
        setState(() {
          _sentTo = requested.pendingEmail;
          _reqId = requested.reqId;
          _verifying = true;
        });
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('Verification code sent. Check your email.'),
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

  Future<void> _resendCode() async {
    setState(() => _busy = true);
    try {
      final requested = await ref
          .read(sessionControllerProvider.notifier)
          .requestEmailChange(_sentTo);
      if (mounted) {
        setState(() => _reqId = requested.reqId);
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('A new verification code is on its way.'),
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

  Future<void> _verify() async {
    if (!_formKey.currentState!.validate()) return;
    final reqId = _reqId;
    if (reqId == null || reqId.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Send a verification code before verifying.'),
        ),
      );
      return;
    }
    setState(() => _busy = true);
    try {
      await ref
          .read(sessionControllerProvider.notifier)
          .verifyEmailChange(_codeController.text, reqId);
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Email verified.')),
        );
        context.pop();
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

  String? _validateEmail(String? value) {
    final email = value?.trim() ?? '';
    if (email.isEmpty) return 'Enter your email address.';
    if (!RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$').hasMatch(email)) {
      return 'Enter a valid email address.';
    }
    return null;
  }
}