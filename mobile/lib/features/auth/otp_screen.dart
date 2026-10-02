import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

class OtpScreen extends ConsumerStatefulWidget {
  const OtpScreen({super.key, this.initialMobile});

  /// Canonical `+91XXXXXXXXXX` mobile pre-filled from the login screen. When
  /// present the field is pre-populated and an OTP is requested automatically.
  final String? initialMobile;

  @override
  ConsumerState<OtpScreen> createState() => _OtpScreenState();
}

class _OtpScreenState extends ConsumerState<OtpScreen> {
  final _formKey = GlobalKey<FormState>();
  final _countryKey = GlobalKey<CountryCodeMobileFieldState>();
  final _mobileController = TextEditingController();
  final _codeController = TextEditingController();
  late CountryCode _initialCountry;
  bool _codeSent = false;
  bool _sending = false;
  String? _reqId;

  @override
  void initState() {
    super.initState();
    _initialCountry = CountryCodes.india;
    final initialMobile = widget.initialMobile;
    if (initialMobile == null || initialMobile.trim().isEmpty) return;
    final trimmed = initialMobile.trim();
    var didPrefill = false;
    if (trimmed.startsWith('+') || trimmed.startsWith('00')) {
      // International form: split into the matching country + national number.
      final parsed = parseMobileCountry(trimmed);
      if (parsed != null) {
        _initialCountry = parsed.$1;
        _mobileController.text = parsed.$2;
        didPrefill = true;
      }
    } else {
      // Plain national value: existing Indian normalization. This keeps
      // 10-digit numbers (e.g. stored `9876543210`) on India +91 instead of
      // being mis-read as another country sharing the leading digits.
      final canonical = canonicalizeIndianMobile(trimmed);
      if (canonical != null && canonical.length == 13) {
        _mobileController.text = canonical.substring(3);
        didPrefill = true;
      }
    }
    if (!didPrefill) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && !_codeSent && !_sending) _send();
    });
  }

  @override
  void dispose() {
    _mobileController.dispose();
    _codeController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final session = ref.watch(sessionControllerProvider);
    final busy = session.isLoading || _sending;

    return Scaffold(
      appBar: AppBar(title: const Text('Mobile OTP')),
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
                        'Verify your mobile number',
                        style: Theme.of(context).textTheme.headlineSmall,
                      ),
                      const SizedBox(height: DoodhSpacing.sm),
                      Text(
                        'A one-time code will be sent to your mobile number.',
                        style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: DoodhColors.muted,
                        ),
                      ),
                      if (session.errorMessage != null) ...[
                        const SizedBox(height: DoodhSpacing.md),
                        DoodhErrorBanner(message: session.errorMessage!),
                      ],
                      const SizedBox(height: DoodhSpacing.lg),
                      CountryCodeMobileField(
                        key: _countryKey,
                        controller: _mobileController,
                        initialCountry: _initialCountry,
                        enabled: !busy,
                      ),
                      if (_codeSent) ...[
                        const SizedBox(height: DoodhSpacing.md),
                        DoodhField(
                          label: '6-digit verification code',
                          controller: _codeController,
                          enabled: !busy,
                          keyboardType: TextInputType.number,
                          prefixIcon: const Icon(Icons.password_outlined),
                          inputFormatters: [
                            LengthLimitingTextInputFormatter(6),
                          ],
                          validator: (value) =>
                              value == null || value.trim().length != 6
                              ? 'Enter the 6-digit code.'
                              : null,
                        ),
                      ],
                      const SizedBox(height: DoodhSpacing.lg),
                      DoodhButton(
                        label: _codeSent ? 'Verify code' : 'Send code',
                        icon: _codeSent
                            ? Icons.verified_outlined
                            : Icons.sms_outlined,
                        busy: busy,
                        expand: true,
                        onPressed: _codeSent ? _verify : _send,
                      ),
                      if (_codeSent) ...[
                        const SizedBox(height: DoodhSpacing.xs),
                        TextButton.icon(
                          onPressed: busy ? null : _resend,
                          icon: const Icon(Icons.refresh),
                          label: const Text('Send a new code'),
                        ),
                      ],
                      TextButton(
                        onPressed: busy
                            ? null
                            : () => context.go(_withRedirect('/login')),
                        child: const Text('Back to password sign in'),
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

  Future<void> _send() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() => _sending = true);
    try {
      final mobile = _countryKey.currentState?.canonicalValue ?? '';
      final reqId = await ref
          .read(sessionControllerProvider.notifier)
          .sendOtp(mobile);
      if (mounted) {
        setState(() {
          _reqId = reqId;
          _codeSent = true;
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
      if (mounted) setState(() => _sending = false);
    }
  }

  Future<void> _resend() async {
    setState(() => _sending = true);
    try {
      final mobile = _countryKey.currentState?.canonicalValue ?? '';
      final reqId = await ref
          .read(sessionControllerProvider.notifier)
          .retryOtp(mobile);
      if (mounted) {
        setState(() => _reqId = reqId);
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('A new verification code is on its way.')),
        );
      }
    } on Object catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(error.toString())));
      }
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  Future<void> _verify() async {
    if (!_formKey.currentState!.validate()) return;
    final reqId = _reqId;
    if (reqId == null || reqId.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Request a verification code before verifying.'),
        ),
      );
      return;
    }
    final mobile = _countryKey.currentState?.canonicalValue ?? '';
    try {
      final result = await ref
          .read(sessionControllerProvider.notifier)
          .verifyOtp(mobile, _codeController.text, reqId);
      if (!mounted) return;
      // The server decided the outcome: a verified mobile with no account yet
      // continues into customer onboarding (create password); an existing user
      // of any role is already authenticated and the router lands on their home.
      if (result.requiresOnboarding) {
        final verifiedMobile = result.verifiedMobile ?? mobile;
        final onboardingReqId = result.reqId ?? reqId;
        final redirect =
            GoRouterState.of(context).uri.queryParameters['redirectTo'];
        context.go(
          '/otp/onboarding?mobile=${Uri.encodeQueryComponent(verifiedMobile)}'
          '&reqId=${Uri.encodeQueryComponent(onboardingReqId)}'
          '${redirect == null || redirect.isEmpty ? '' : '&redirectTo=${Uri.encodeQueryComponent(redirect)}'}',
        );
      }
    } on Object catch (error) {
      if (mounted) {
        // The session state carries the error message; surface it inline. The
        // controller rethrows so callers do not silently swallow failures.
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(error.toString())));
      }
    }
  }

  /// Carries the validated return-intent across the auth routes so the user
  /// returns to the exact page they wanted after signing in. The router
  /// validates the value before acting on it.
  String _withRedirect(String path) {
    final redirect = GoRouterState.of(context).uri.queryParameters['redirectTo'];
    return redirect == null || redirect.isEmpty
        ? path
        : '$path?redirectTo=${Uri.encodeQueryComponent(redirect)}';
  }
}
