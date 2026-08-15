import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

class OtpScreen extends ConsumerStatefulWidget {
  const OtpScreen({super.key});

  @override
  ConsumerState<OtpScreen> createState() => _OtpScreenState();
}

class _OtpScreenState extends ConsumerState<OtpScreen> {
  final _formKey = GlobalKey<FormState>();
  final _mobileController = TextEditingController();
  final _codeController = TextEditingController();
  bool _registration = false;
  bool _codeSent = false;
  bool _sending = false;

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
                      'Verify your mobile number',
                      style: Theme.of(context).textTheme.headlineSmall,
                    ),
                    const SizedBox(height: 8),
                    const Text('A one-time code will be sent to your mobile number.'),
                    const SizedBox(height: 24),
                    SegmentedButton<bool>(
                      segments: const [
                        ButtonSegment<bool>(
                          value: false,
                          label: Text('Sign in'),
                          icon: Icon(Icons.login),
                        ),
                        ButtonSegment<bool>(
                          value: true,
                          label: Text('Register'),
                          icon: Icon(Icons.person_add_outlined),
                        ),
                      ],
                      selected: {_registration},
                      onSelectionChanged: busy
                          ? null
                          : (selection) => setState(() {
                                _registration = selection.first;
                                _codeSent = false;
                              }),
                    ),
                    if (session.errorMessage != null) ...[
                      const SizedBox(height: 16),
                      Text(
                        session.errorMessage!,
                        style: TextStyle(color: Theme.of(context).colorScheme.error),
                      ),
                    ],
                    const SizedBox(height: 24),
                    TextFormField(
                      controller: _mobileController,
                      enabled: !busy,
                      keyboardType: TextInputType.phone,
                      decoration: const InputDecoration(
                        labelText: 'Mobile number',
                        prefixIcon: Icon(Icons.phone_outlined),
                        border: OutlineInputBorder(),
                      ),
                      validator: (value) => value == null || value.trim().length < 8
                          ? 'Enter a valid mobile number.'
                          : null,
                    ),
                    if (_codeSent) ...[
                      const SizedBox(height: 16),
                      TextFormField(
                        controller: _codeController,
                        enabled: !busy,
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
                    ],
                    const SizedBox(height: 20),
                    FilledButton.icon(
                      onPressed: busy ? null : (_codeSent ? _verify : _send),
                      icon: busy
                          ? const SizedBox.square(
                              dimension: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : Icon(_codeSent ? Icons.verified_outlined : Icons.sms_outlined),
                      label: Text(_codeSent ? 'Verify code' : 'Send code'),
                    ),
                    if (_codeSent)
                      TextButton.icon(
                        onPressed: busy ? null : _send,
                        icon: const Icon(Icons.refresh),
                        label: const Text('Send a new code'),
                      ),
                    TextButton(
                      onPressed: busy ? null : () => context.go('/login'),
                      child: const Text('Back to password sign in'),
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
    setState(() => _sending = true);
    try {
      await ref.read(sessionControllerProvider.notifier).sendOtp(
            _mobileController.text,
            registration: _registration,
          );
      if (mounted) {
        setState(() => _codeSent = true);
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Verification code request accepted.')),
        );
      }
    } on Object catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(error.toString())),
        );
      }
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  Future<void> _verify() async {
    if (!_formKey.currentState!.validate()) return;
    await ref.read(sessionControllerProvider.notifier).verifyOtp(
          _mobileController.text,
          _codeController.text,
          registration: _registration,
        );
  }
}
