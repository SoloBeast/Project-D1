import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
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
      body: DoodhPage(
        child: Form(
          key: _formKey,
          child: ListView(
            children: [
              Text(
                'Secure your account',
                style: Theme.of(context).textTheme.headlineSmall,
              ),
              const SizedBox(height: DoodhSpacing.sm),
              const Text(
                'Your account was created with a one-time code. Set a '
                'password so you can sign in with it in future.',
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
                  tooltip: _obscure ? 'Show password' : 'Hide password',
                  onPressed: () => setState(() => _obscure = !_obscure),
                  icon: Icon(
                    _obscure
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
                enabled: !_busy,
                obscureText: true,
                prefixIcon: const Icon(Icons.lock_reset_outlined),
                validator: (value) => value != _passwordController.text
                    ? 'Passwords do not match.'
                    : null,
              ),
              const SizedBox(height: DoodhSpacing.lg),
              DoodhButton(
                label: 'Set password',
                icon: Icons.lock_reset_outlined,
                busy: _busy,
                expand: true,
                onPressed: _submit,
              ),
            ],
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
      body: DoodhPage(
        child: Form(
          key: _formKey,
          child: ListView(
            children: [
              Text(
                'Update your password',
                style: Theme.of(context).textTheme.headlineSmall,
              ),
              const SizedBox(height: DoodhSpacing.sm),
              const Text(
                'Enter your current password and choose a new one to '
                'keep your account secure.',
              ),
              const SizedBox(height: DoodhSpacing.lg),
              DoodhField(
                label: 'Current Password',
                controller: _currentController,
                enabled: !_busy,
                obscureText: true,
                autofillHints: const [AutofillHints.password],
                prefixIcon: const Icon(Icons.lock_outline),
                validator: (value) => value == null || value.isEmpty
                    ? 'Enter your current password.'
                    : null,
              ),
              const SizedBox(height: DoodhSpacing.md),
              DoodhField(
                label: 'New Password',
                controller: _newController,
                enabled: !_busy,
                obscureText: _obscure,
                autofillHints: const [AutofillHints.newPassword],
                prefixIcon: const Icon(Icons.lock_reset_outlined),
                suffixIcon: IconButton(
                  tooltip: _obscure ? 'Show password' : 'Hide password',
                  onPressed: () => setState(() => _obscure = !_obscure),
                  icon: Icon(
                    _obscure
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
                label: 'Confirm New Password',
                controller: _confirmController,
                enabled: !_busy,
                obscureText: true,
                prefixIcon: const Icon(Icons.lock_reset_outlined),
                validator: (value) => value != _newController.text
                    ? 'Passwords do not match.'
                    : null,
              ),
              const SizedBox(height: DoodhSpacing.lg),
              DoodhButton(
                label: 'Change Password',
                icon: Icons.lock_reset_outlined,
                busy: _busy,
                expand: true,
                onPressed: _submit,
              ),
            ],
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
    final actionIcon = switch (_step) {
      0 => Icons.sms_outlined,
      1 => Icons.verified_outlined,
      _ => Icons.lock_reset_outlined,
    };
    return Scaffold(
      appBar: AppBar(title: const Text('Reset password')),
      body: DoodhPage(
        child: Form(
          key: _formKey,
          child: ListView(
            children: [
              Text(title, style: Theme.of(context).textTheme.headlineSmall),
              const SizedBox(height: DoodhSpacing.sm),
              Text(description),
              const SizedBox(height: DoodhSpacing.lg),
              if (_step == 0)
                CountryCodeMobileField(
                  key: _countryKey,
                  controller: _mobileController,
                  enabled: !_busy,
                ),
              if (_step == 1)
                DoodhField(
                  label: '6-digit verification code',
                  controller: _codeController,
                  enabled: !_busy,
                  keyboardType: TextInputType.number,
                  prefixIcon: const Icon(Icons.password_outlined),
                  inputFormatters: [LengthLimitingTextInputFormatter(6)],
                  validator: (value) =>
                      value == null || value.trim().length != 6
                      ? 'Enter the 6-digit code.'
                      : null,
                ),
              if (_otpVerified) ...[
                DoodhField(
                  label: 'New password',
                  controller: _passwordController,
                  enabled: !_busy,
                  obscureText: _obscurePassword,
                  autofillHints: const [AutofillHints.newPassword],
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
                  enabled: !_busy,
                  obscureText: _obscureConfirmPassword,
                  prefixIcon: const Icon(Icons.lock_reset_outlined),
                  suffixIcon: IconButton(
                    tooltip: _obscureConfirmPassword
                        ? 'Show password'
                        : 'Hide password',
                    onPressed: () => setState(
                      () => _obscureConfirmPassword = !_obscureConfirmPassword,
                    ),
                    icon: Icon(
                      _obscureConfirmPassword
                          ? Icons.visibility_outlined
                          : Icons.visibility_off_outlined,
                    ),
                  ),
                  validator: (value) => value != _passwordController.text
                      ? 'Passwords do not match.'
                      : null,
                ),
              ],
              const SizedBox(height: DoodhSpacing.lg),
              DoodhButton(
                label: _step == 0
                    ? 'Send OTP'
                    : _step == 1
                    ? 'Verify OTP'
                    : 'Reset password',
                icon: actionIcon,
                busy: _busy,
                expand: true,
                onPressed: _step == 0 ? _send : _step == 1 ? _verify : _reset,
              ),
              if (_step == 1)
                TextButton.icon(
                  onPressed: _busy ? null : _resend,
                  icon: const Icon(Icons.refresh),
                  label: const Text('Send a new code'),
                ),
              TextButton(
                onPressed: _busy
                    ? null
                    : () => context.go(_withRedirect('/login')),
                child: const Text('Cancel'),
              ),
            ],
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
      body: user == null
          ? const LoadingStatePanel(message: 'Loading your account security.')
          : DoodhPage(
              padding: false,
              child: ListView(
                padding: DoodhSpacing.pagePadding,
                children: [
                  const _SecurityIntroCard(),
                  const SizedBox(height: DoodhSpacing.lg),
                  DoodhSectionHeader(title: 'Mobile number'),
                  const SizedBox(height: DoodhSpacing.sm),
                  _MobileStatusCard(user: user),
                  if (user.pendingMobile != null) ...[
                    const SizedBox(height: DoodhSpacing.sm),
                    DoodhActionTile(
                      icon: Icons.sms_outlined,
                      title: 'Verify pending mobile',
                      subtitle:
                          'Enter the code sent to your new mobile number.',
                      onTap: () => context.push('/security/mobile'),
                    ),
                  ],
                  const SizedBox(height: DoodhSpacing.lg),
                  DoodhSectionHeader(title: 'Email'),
                  const SizedBox(height: DoodhSpacing.sm),
                  _EmailStatusCard(user: user),
                  if (user.pendingEmail != null) ...[
                    const SizedBox(height: DoodhSpacing.sm),
                    DoodhActionTile(
                      icon: Icons.sms_outlined,
                      title: 'Verify pending email',
                      subtitle:
                          'Enter the code sent to your new email address.',
                      onTap: () => context.push('/security/email'),
                    ),
                  ],
                  const SizedBox(height: DoodhSpacing.lg),
                  DoodhSectionHeader(title: 'Password'),
                  const SizedBox(height: DoodhSpacing.sm),
                  _PasswordStatusCard(
                    user: user,
                    onChangePassword: () =>
                        _showChangePasswordDialog(context),
                    onSetPassword: () => context.push('/create-password'),
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
                  DoodhField(
                    label: 'Current password',
                    controller: currentController,
                    enabled: !busy,
                    obscureText: true,
                    autofillHints: const [AutofillHints.password],
                    prefixIcon: const Icon(Icons.lock_outline),
                    validator: (value) => value == null || value.isEmpty
                        ? 'Enter your current password.'
                        : null,
                  ),
                  const SizedBox(height: DoodhSpacing.md),
                  DoodhField(
                    label: 'New password',
                    controller: newController,
                    enabled: !busy,
                    obscureText: obscure,
                    autofillHints: const [AutofillHints.newPassword],
                    prefixIcon: const Icon(Icons.lock_reset_outlined),
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
                    validator: (value) => value == null || value.length < 8
                        ? 'Use at least 8 characters.'
                        : null,
                  ),
                  const SizedBox(height: DoodhSpacing.md),
                  DoodhField(
                    label: 'Confirm new password',
                    controller: confirmController,
                    enabled: !busy,
                    obscureText: true,
                    prefixIcon: const Icon(Icons.lock_reset_outlined),
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
            DoodhButton(
              label: 'Change password',
              busy: busy,
              onPressed: () async {
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
                      const SnackBar(content: Text('Password changed.')),
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

/// Calm, factual framing for the security hub: it only describes what the
/// customer can manage here — no invented guarantees or certifications.
class _SecurityIntroCard extends StatelessWidget {
  const _SecurityIntroCard();

  @override
  Widget build(BuildContext context) => DoodhCard(
    color: DoodhColors.mint,
    child: Row(
      children: [
        const ExcludeSemantics(
          child: Icon(Icons.shield_outlined, color: DoodhColors.tealDark),
        ),
        const SizedBox(width: DoodhSpacing.md),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Your sign-in details',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              const SizedBox(height: DoodhSpacing.xs),
              Text(
                'Manage the mobile number, email address and password you use '
                'to sign in to DoodhDirect.',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: DoodhColors.muted,
                ),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

/// One sign-in-detail status row: icon tile, label/value, status pill and the
/// change affordance. Status is always carried by text + pill, never colour
/// alone.
class _SecurityStatusTile extends StatelessWidget {
  const _SecurityStatusTile({
    required this.icon,
    required this.value,
    required this.subtitle,
    required this.actionTooltip,
    required this.onAction,
    this.pill,
  });

  final IconData icon;
  final String value;
  final String subtitle;
  final String actionTooltip;
  final VoidCallback onAction;
  final DoodhStatusPill? pill;

  @override
  Widget build(BuildContext context) => Card(
    clipBehavior: Clip.antiAlias,
    child: InkWell(
      borderRadius: DoodhRadii.mdRadius,
      onTap: onAction,
      child: Padding(
        padding: const EdgeInsets.all(DoodhSpacing.md),
        child: Row(
          children: [
            ExcludeSemantics(
              child: Container(
                width: 44,
                height: 44,
                decoration: BoxDecoration(
                  color: DoodhColors.mint.withValues(alpha: .8),
                  borderRadius: DoodhRadii.sm,
                ),
                child: Icon(icon, color: DoodhColors.tealDark),
              ),
            ),
            const SizedBox(width: DoodhSpacing.md),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(value, style: Theme.of(context).textTheme.titleMedium),
                  const SizedBox(height: DoodhSpacing.xs),
                  Text(
                    subtitle,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: DoodhColors.muted,
                    ),
                  ),
                ],
              ),
            ),
            if (pill != null) ...[
              const SizedBox(width: DoodhSpacing.sm),
              pill!,
            ],
            const SizedBox(width: DoodhSpacing.sm),
            ExcludeSemantics(
              child: IconButton(
                tooltip: actionTooltip,
                onPressed: onAction,
                icon: const Icon(Icons.edit_outlined, size: 20),
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

class _MobileStatusCard extends StatelessWidget {
  const _MobileStatusCard({required this.user});

  final AuthUser user;

  @override
  Widget build(BuildContext context) {
    final pending = user.pendingMobile != null;
    final hasMobile = user.mobile != null;
    return _SecurityStatusTile(
      icon: Icons.phone_android_outlined,
      value: user.mobile ?? 'No mobile number',
      subtitle: pending
          ? 'Pending confirmation: ${user.pendingMobile}'
          : hasMobile
          ? 'Used for OTP sign-in.'
          : 'Add a mobile number for OTP sign-in.',
      pill: pending
          ? const DoodhStatusPill(label: 'Pending', tone: DoodhStatusTone.warning)
          : hasMobile
          ? const DoodhStatusPill(label: 'Active', tone: DoodhStatusTone.success)
          : const DoodhStatusPill(
              label: 'Not set',
              tone: DoodhStatusTone.neutral,
            ),
      actionTooltip: hasMobile ? 'Change mobile' : 'Add mobile',
      onAction: () => context.push('/security/mobile'),
    );
  }
}

class _EmailStatusCard extends StatelessWidget {
  const _EmailStatusCard({required this.user});

  final AuthUser user;

  @override
  Widget build(BuildContext context) {
    final pending = user.pendingEmail != null;
    final hasEmail = user.email != null;
    final verified = user.emailVerified;
    return _SecurityStatusTile(
      icon: hasEmail
          ? (verified ? Icons.mark_email_read_outlined : Icons.mail_outline)
          : Icons.mail_outline,
      value: user.email ?? 'No email address',
      subtitle: pending
          ? 'Pending confirmation: ${user.pendingEmail}'
          : hasEmail
          ? (verified
                ? 'Used for account notifications and sign-in.'
                : 'Not yet verified — verify to secure your account.')
          : 'Add an email to receive account notifications.',
      pill: pending
          ? const DoodhStatusPill(label: 'Pending', tone: DoodhStatusTone.warning)
          : !hasEmail
          ? const DoodhStatusPill(
              label: 'Not set',
              tone: DoodhStatusTone.neutral,
            )
          : verified
          ? const DoodhStatusPill(label: 'Verified', tone: DoodhStatusTone.success)
          : const DoodhStatusPill(label: 'Unverified', tone: DoodhStatusTone.warning),
      actionTooltip: hasEmail ? 'Change email' : 'Add email',
      onAction: () => context.push('/security/email'),
    );
  }
}

class _PasswordStatusCard extends StatelessWidget {
  const _PasswordStatusCard({
    required this.user,
    required this.onChangePassword,
    required this.onSetPassword,
  });

  final AuthUser user;
  final VoidCallback onChangePassword;
  final VoidCallback onSetPassword;

  @override
  Widget build(BuildContext context) => _SecurityStatusTile(
    icon: user.hasPassword
        ? Icons.password_outlined
        : Icons.lock_open_outlined,
    value: user.hasPassword ? 'Configure Password' : 'No password set',
    subtitle: user.hasPassword
        ? 'Change it any time to keep your account secure.'
        : 'Set a password to sign in with it instead of an OTP.',
    pill: user.hasPassword
        ? const DoodhStatusPill(label: 'Active', tone: DoodhStatusTone.success)
        : const DoodhStatusPill(label: 'Not set', tone: DoodhStatusTone.neutral),
    actionTooltip: user.hasPassword ? 'Change password' : 'Set password',
    // Preserve the original branching: an existing password opens the
    // change dialog; without one the dedicated create-password flow opens.
    onAction: user.hasPassword ? onChangePassword : onSetPassword,
  );
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
    body: DoodhPage(
      child: Form(
        key: _formKey,
        child: ListView(
          children: _verifying ? _verificationFields() : _addressFields(),
        ),
      ),
    ),
  );

  List<Widget> _addressFields() => [
    Text(
      'Add a mobile number',
      style: Theme.of(context).textTheme.headlineSmall,
    ),
    const SizedBox(height: DoodhSpacing.sm),
    const Text('We will send a one-time code by SMS to confirm this number.'),
    const SizedBox(height: DoodhSpacing.lg),
    DoodhCard(
      color: DoodhColors.mint,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          CountryCodeMobileField(
            key: _countryKey,
            controller: _mobileController,
            initialCountry: _initialCountry,
            enabled: !_busy,
          ),
        ],
      ),
    ),
    const SizedBox(height: DoodhSpacing.lg),
    DoodhButton(
      label: 'Send verification code',
      icon: Icons.sms_outlined,
      busy: _busy,
      expand: true,
      onPressed: _requestCode,
    ),
  ];

  List<Widget> _verificationFields() => [
    Text(
      'Verify your mobile',
      style: Theme.of(context).textTheme.headlineSmall,
    ),
    const SizedBox(height: DoodhSpacing.sm),
    DoodhInfoBanner(
      message: 'Enter the 6-digit code sent to $_sentTo.',
    ),
    const SizedBox(height: DoodhSpacing.lg),
    DoodhCard(
      color: DoodhColors.mint,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          DoodhField(
            label: '6-digit verification code',
            controller: _codeController,
            enabled: !_busy,
            keyboardType: TextInputType.number,
            prefixIcon: const Icon(Icons.password_outlined),
            inputFormatters: [LengthLimitingTextInputFormatter(6)],
            validator: (value) => value == null || value.trim().length != 6
                ? 'Enter the 6-digit code.'
                : null,
          ),
        ],
      ),
    ),
    const SizedBox(height: DoodhSpacing.lg),
    DoodhButton(
      label: 'Verify mobile',
      icon: Icons.verified_outlined,
      busy: _busy,
      expand: true,
      onPressed: _verify,
    ),
    TextButton.icon(
      onPressed: _busy ? null : _resendCode,
      icon: const Icon(Icons.refresh),
      label: const Text('Send a new code'),
    ),
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
      body: DoodhPage(
        child: Form(
          key: _formKey,
          child: ListView(
            children: _verifying
                ? _verificationFields(context)
                : _addressFields(context),
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
      const SizedBox(height: DoodhSpacing.sm),
      const Text(
        'We will send a one-time code to confirm this address. It will be '
        'used for account notifications and as a sign-in identifier.',
      ),
      const SizedBox(height: DoodhSpacing.lg),
      DoodhField(
        label: 'Email address',
        controller: _emailController,
        enabled: !_busy,
        keyboardType: TextInputType.emailAddress,
        autofillHints: const [AutofillHints.email],
        prefixIcon: const Icon(Icons.mail_outline),
        validator: _validateEmail,
      ),
      const SizedBox(height: DoodhSpacing.lg),
      DoodhButton(
        label: 'Send verification code',
        icon: Icons.sms_outlined,
        busy: _busy,
        expand: true,
        onPressed: _requestCode,
      ),
    ];
  }

  List<Widget> _verificationFields(BuildContext context) {
    return [
      Text(
        'Verify your email',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: DoodhSpacing.sm),
      Text(
        'Enter the 6-digit code sent to $_sentTo.',
      ),
      const SizedBox(height: DoodhSpacing.lg),
      DoodhField(
        label: '6-digit verification code',
        controller: _codeController,
        enabled: !_busy,
        keyboardType: TextInputType.number,
        prefixIcon: const Icon(Icons.password_outlined),
        inputFormatters: [LengthLimitingTextInputFormatter(6)],
        validator: (value) => value == null || value.trim().length != 6
            ? 'Enter the 6-digit code.'
            : null,
      ),
      const SizedBox(height: DoodhSpacing.lg),
      DoodhButton(
        label: 'Verify email',
        icon: Icons.verified_outlined,
        busy: _busy,
        expand: true,
        onPressed: _verify,
      ),
      const SizedBox(height: DoodhSpacing.xs),
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