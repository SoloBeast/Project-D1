import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'otp_config_controller.dart';
import 'otp_config_models.dart';

const String kOtpProviderReadPermission = 'SETUP.OTP_PROVIDER.READ';
const String kOtpProviderManagePermission = 'SETUP.OTP_PROVIDER.MANAGE';

/// Setup → Identity / Onboarding OTP Provider (MSG91) configuration.
///
/// MSG91 is used ONLY for customer and employee identity/onboarding OTP
/// (customer login OTP, customer registration OTP, employee invitation OTP,
/// employee login OTP). Delivery verification OTPs are generated and verified
/// in-app by DoodhDirect and are never sent through MSG91.
///
/// Requires `SETUP.OTP_PROVIDER.READ` to view and
/// `SETUP.OTP_PROVIDER.MANAGE` to edit. The MSG91 AuthKey is write-only: it is
/// never returned by the backend, so the field always starts blank and only
/// changes the stored key when a new value is typed.
///
/// Form state is hydrated from the server configuration exactly once (when the
/// first configuration arrives, after a manual refresh, or after a successful
/// save). Rebuilds caused by typing or toggling never overwrite what the user
/// is editing — the previous implementation re-applied the server values on
/// every `build()` call, which reverted every keystroke immediately.
class OtpProviderConfigScreen extends ConsumerStatefulWidget {
  const OtpProviderConfigScreen({super.key});

  @override
  ConsumerState<OtpProviderConfigScreen> createState() =>
      _OtpProviderConfigScreenState();
}

class _OtpProviderConfigScreenState
    extends ConsumerState<OtpProviderConfigScreen> {
  final TextEditingController _widgetId = TextEditingController();
  final TextEditingController _authKey = TextEditingController();
  bool _enabled = false;
  OtpProviderEnvironment? _environment;

  /// Whether the local form has been hydrated from the server configuration.
  ///
  /// While `true`, `build()` never touches the controllers or the switch, so
  /// user edits survive rebuilds. Set to `false` (and re-hydrated) only on a
  /// manual refresh or after a successful save.
  bool _hydrated = false;

  /// Incremented on every hydration so the environment dropdown (which holds
  /// its selection in internal [FormFieldState] state keyed off `initialValue`)
  /// is recreated with the freshly hydrated value.
  int _hydrationEpoch = 0;

  /// Snapshot of the server values used to decide whether the admin made a
  /// change worth saving.
  String? _originalWidgetId;
  bool _originalEnabled = false;
  OtpProviderEnvironment? _originalEnvironment;

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(otpConfigControllerProvider.notifier).load(),
    );
  }

  @override
  void dispose() {
    _widgetId.dispose();
    _authKey.dispose();
    super.dispose();
  }

  void _hydrateFrom(OtpProviderConfiguration configuration) {
    _widgetId.text = configuration.widgetId ?? '';
    _enabled = configuration.enabled;
    _environment = configuration.environment;
    _originalWidgetId = configuration.widgetId ?? '';
    _originalEnabled = configuration.enabled;
    _originalEnvironment = configuration.environment;
    _hydrated = true;
    _hydrationEpoch++;
  }

  void _resetHydration() {
    _hydrated = false;
    _authKey.clear();
  }

  /// True when the local form differs from the last hydrated server state or
  /// the admin typed a new AuthKey. The Save button is only enabled then.
  bool _hasChanges() {
    final widgetId = _widgetId.text.trim();
    final hasNewAuthKey = _authKey.text.trim().isNotEmpty;
    return widgetId != (_originalWidgetId ?? '') ||
        _enabled != _originalEnabled ||
        _environment != _originalEnvironment ||
        hasNewAuthKey;
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(otpConfigControllerProvider);
    final configuration = state.configuration;

    // Hydrate exactly once per loaded configuration. Never while a load is in
    // flight (otherwise a manual refresh would re-apply stale values) and never
    // while the user is editing (that was the root cause of the bug).
    if (configuration != null && !state.isLoading && !_hydrated) {
      _hydrateFrom(configuration);
    }

    final session = ref.watch(sessionControllerProvider).session;
    final permissions = session?.user.permissions ?? const <String>[];
    final canRead = permissions.contains(kOtpProviderReadPermission);
    final canManage = permissions.contains(kOtpProviderManagePermission);

    if (!canRead) {
      return Scaffold(
        appBar: AppBar(title: const Text('Identity / Onboarding OTP')),
        body: const UnauthorizedStatePanel(),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text('Identity / Onboarding OTP'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () {
                    setState(_resetHydration);
                    ref.read(otpConfigControllerProvider.notifier).load();
                  },
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: _body(context, state, canManage),
    );
  }

  Widget _body(
    BuildContext context,
    OtpConfigState state,
    bool canManage,
  ) {
    final configuration = state.configuration;

    if (state.isLoading && configuration == null) {
      return const LoadingStatePanel(
        message: 'Loading identity/onboarding OTP provider settings',
      );
    }
    if (state.errorMessage != null && configuration == null) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () => ref.read(otpConfigControllerProvider.notifier).load(),
      );
    }
    if (configuration == null) {
      return EmptyStatePanel(
        title: 'No OTP provider settings',
        message: canManage
            ? 'Configure the MSG91 widget to start sending identity and '
                  'onboarding OTPs through it. Delivery OTPs are always '
                  'in-app and do not use MSG91.'
            : 'Identity/onboarding OTP provider settings are not available '
                  'for this account.',
      );
    }

    final canEdit = canManage && !state.isSaving;
    final hasChanges = _hasChanges();

    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        const _ScopeInfoCard(),
        const SizedBox(height: 12),
        _StatusCard(configuration: configuration),
        if (!canManage) ...[
          const SizedBox(height: 12),
          const _ReadOnlyBanner(),
        ],
        const SizedBox(height: 12),
        if (state.savedMessage != null) ...[
          DoodhSavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.testMessage != null) ...[
          DoodhInfoBanner(
            tone: DoodhTone.success,
            icon: Icons.send_outlined,
            message: state.testMessage!,
          ),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          DoodhErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        Card(
          child: SwitchListTile(
            title: const Text('Identity / Onboarding OTP enabled'),
            subtitle: const Text(
              'When enabled, customer login, customer registration, and '
              'employee onboarding OTPs are sent through MSG91. Delivery '
              'verification OTPs remain in-app and are not sent by SMS.',
            ),
            value: _enabled,
            onChanged: canEdit
                ? (value) => setState(() => _enabled = value)
                : null,
          ),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _widgetId,
          enabled: canEdit,
          onChanged: (_) => setState(() {}),
          textCapitalization: TextCapitalization.none,
          decoration: const InputDecoration(
            labelText: 'MSG91 Widget ID',
            hintText: 'e.g. 6f2c4a1b9e3d',
            helperText: 'The MSG91 OTP widget used for customer and employee '
                'identity/onboarding OTP.',
            border: OutlineInputBorder(),
          ),
        ),
        const SizedBox(height: 12),
        DropdownButtonFormField<OtpProviderEnvironment>(
          key: ValueKey<int>(_hydrationEpoch),
          initialValue: _environment,
          decoration: const InputDecoration(
            labelText: 'Environment',
            helperText: 'Test widgets use a static code and do not send real '
                'SMS.',
            border: OutlineInputBorder(),
          ),
          items: OtpProviderEnvironment.values
              .map(
                (environment) => DropdownMenuItem(
                  value: environment,
                  child: Text(environment.label),
                ),
              )
              .toList(),
          onChanged: canEdit
              ? (value) => setState(() => _environment = value)
              : null,
        ),
        const SizedBox(height: 12),
        TextField(
          controller: _authKey,
          enabled: canEdit,
          onChanged: (_) => setState(() {}),
          obscureText: true,
          autocorrect: false,
          enableSuggestions: false,
          decoration: const InputDecoration(
            labelText: 'MSG91 AuthKey',
            hintText: '••••••••••••',
            helperText:
                'Write-only. Leave blank to keep the existing key. Never '
                'shared outside this screen.',
            border: OutlineInputBorder(),
          ),
        ),
        if (state.fieldErrors.isNotEmpty)
          ...state.fieldErrors.entries.map(
            (entry) => Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                '${entry.key}: ${entry.value}',
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ),
          ),
        if (canManage) ...[
          const SizedBox(height: 16),
          OutlinedButton.icon(
            onPressed: state.isTesting || state.isSaving
                ? null
                : () => ref.read(otpConfigControllerProvider.notifier).test(),
            icon: state.isTesting
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.send_outlined),
            label: const Text('Send test OTP'),
          ),
          const SizedBox(height: 8),
          FilledButton.icon(
            onPressed: canEdit && hasChanges ? () => _save(context) : null,
            icon: state.isSaving
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save_outlined),
            label: const Text('Save settings'),
          ),
        ],
      ],
    );
  }

  Future<void> _save(BuildContext context) async {
    final authKey = _authKey.text.trim();
    final saved = await ref.read(otpConfigControllerProvider.notifier).save(
      UpdateOtpProviderConfigurationRequest(
        enabled: _enabled,
        widgetId: _widgetId.text.trim(),
        authKey: authKey.isEmpty ? null : authKey,
        environment: _environment,
      ),
    );
    if (saved && mounted) {
      // Re-hydrate from the configuration returned by the successful PUT so
      // the form reflects the authoritative saved state and the AuthKey field
      // is cleared (it stays write-only).
      setState(_resetHydration);
    }
  }
}

class _StatusCard extends StatelessWidget {
  const _StatusCard({required this.configuration});

  final OtpProviderConfiguration configuration;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final configured = configuration.configured;
    return Card(
      color: configured ? DoodhColors.mint : scheme.errorContainer,
      child: ListTile(
        leading: Icon(
          configured ? Icons.check_circle_outline : Icons.error_outline,
          color: configured ? DoodhColors.tealDark : scheme.error,
        ),
        title: Text(configured ? 'Configured' : 'Not configured'),
        subtitle: Text(configuration.status),
        trailing: Text(
          configuration.provider,
          style: TextStyle(
            color: configured ? DoodhColors.tealDark : scheme.onErrorContainer,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
    );
  }
}

class _ScopeInfoCard extends StatelessWidget {
  const _ScopeInfoCard();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    return Card(
      color: scheme.surfaceContainerHighest,
      child: Padding(
        padding: DoodhSpacing.cardPadding,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(Icons.info_outline, color: scheme.primary),
                const SizedBox(width: 8),
                Text(
                  'OTP scope',
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: scheme.onSurfaceVariant,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            const Text(
              'MSG91 is used only for customer and employee identity/onboarding '
              'OTP (customer login, customer registration, employee invitation, '
              'employee login).',
            ),
            const SizedBox(height: 4),
            const Text(
              'Delivery verification OTPs are generated and verified in-app by '
              'DoodhDirect and are never sent by SMS.',
            ),
          ],
        ),
      ),
    );
  }
}

class _ReadOnlyBanner extends StatelessWidget {
  const _ReadOnlyBanner();

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Card(
      color: scheme.surfaceContainerHighest,
      child: ListTile(
        leading: Icon(Icons.lock_outline, color: scheme.onSurfaceVariant),
        title: const Text('Read-only view'),
        subtitle: const Text(
          'You can view the OTP provider settings but do not have permission '
          'to change them.',
        ),
      ),
    );
  }
}

