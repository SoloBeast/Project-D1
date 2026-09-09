import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'integrations_controller.dart';
import 'integrations_models.dart';

const String kIntegrationsReadPermission = 'SETUP.INTEGRATIONS.READ';
const String kIntegrationsManagePermission = 'SETUP.INTEGRATIONS.MANAGE';

/// Setup → System Setup → INTEGRATIONS configuration.
///
/// This screen owns every external-service credential the backend needs at
/// runtime:
///
/// * Email (SMTP) — the mail ID DoodhDirect uses to send employee invitation
///   links and other transactional email. Includes host, port, user name,
///   password, SSL flag, and an invitation link base URL so generated
///   invitation links are absolute.
/// * Razorpay — runtime Key ID, Key Secret, and webhook secret used when
///   collecting payments.
/// * Google Maps — the server-side API key (used for address lookups on the
///   backend) AND the web client key (used by the web client/browser to load
///   Google Maps), plus the base URL.
///
/// Requires `SETUP.INTEGRATIONS.READ` to view and
/// `SETUP.INTEGRATIONS.MANAGE` to edit. Secrets (SMTP password, Razorpay key
/// secret/webhook secret, Google Maps SERVER-side API key) are write-only: the
/// backend never returns them, so each secret field starts blank and only
/// changes the stored secret when a new value is typed. The Google Maps WEB
/// CLIENT key is client-visible by design: the backend returns it so the field
/// shows the exact value the browser will load at runtime.
///
/// Values saved here become DATABASE overrides. A non-empty value overrides the
/// appsettings/environment fallback at call time; clearing a field removes the
/// override so the fallback applies again. The backend resolves the effective
/// value from the database first and falls back to configuration only when the
/// database has no value.
///
/// Form state is hydrated from the server configuration exactly once (when the
/// first configuration arrives, after a manual refresh, or after a successful
/// save). Rebuilds caused by typing or toggling never overwrite what the user
/// is editing.
class IntegrationConfigurationScreen extends ConsumerStatefulWidget {
  const IntegrationConfigurationScreen({super.key});

  @override
  ConsumerState<IntegrationConfigurationScreen> createState() =>
      _IntegrationConfigurationScreenState();
}

class _IntegrationConfigurationScreenState
    extends ConsumerState<IntegrationConfigurationScreen> {
  final TextEditingController _emailFromAddress = TextEditingController();
  final TextEditingController _emailFromName = TextEditingController();
  final TextEditingController _emailHost = TextEditingController();
  final TextEditingController _emailPort = TextEditingController();
  final TextEditingController _emailUserName = TextEditingController();
  final TextEditingController _emailPassword = TextEditingController();
  final TextEditingController _inviteUrlBase = TextEditingController();
  final TextEditingController _razorpayKeyId = TextEditingController();
  final TextEditingController _razorpayKeySecret = TextEditingController();
  final TextEditingController _razorpayWebhookSecret = TextEditingController();
  final TextEditingController _googleMapsApiKey = TextEditingController();
  final TextEditingController _googleMapsWebClientKey =
      TextEditingController();
  final TextEditingController _googleMapsBaseUrl = TextEditingController();
  bool _emailUseSsl = true;

  /// Whether the local form has been hydrated from the server configuration.
  /// While `true`, `build()` never touches the controllers or the switch, so
  /// user edits survive rebuilds. Set to `false` (and re-hydrated) only on a
  /// manual refresh or after a successful save.
  bool _hydrated = false;

  /// Snapshot of the server (effective) values used to decide whether the
  /// admin made a change worth saving. Secret values are never part of this
  /// snapshot — a typed secret alone counts as a change.
  String _originalFromAddress = '';
  String _originalFromName = '';
  String _originalHost = '';
  String _originalPort = '587';
  String _originalUserName = '';
  bool _originalUseSsl = true;
  String _originalInviteUrlBase = '';
  String _originalRazorpayKeyId = '';
  String _originalGoogleMapsWebClientKey = '';
  String _originalGoogleMapsBaseUrl = '';

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(integrationsControllerProvider.notifier).load(),
    );
  }

  @override
  void dispose() {
    _emailFromAddress.dispose();
    _emailFromName.dispose();
    _emailHost.dispose();
    _emailPort.dispose();
    _emailUserName.dispose();
    _emailPassword.dispose();
    _inviteUrlBase.dispose();
    _razorpayKeyId.dispose();
    _razorpayKeySecret.dispose();
    _razorpayWebhookSecret.dispose();
    _googleMapsApiKey.dispose();
    _googleMapsWebClientKey.dispose();
    _googleMapsBaseUrl.dispose();
    super.dispose();
  }

  void _hydrateFrom(IntegrationConfiguration configuration) {
    _emailFromAddress.text = configuration.emailFromAddress ?? '';
    _emailFromName.text = configuration.emailFromName ?? '';
    _emailHost.text = configuration.emailHost ?? '';
    _emailPort.text = configuration.emailPort.toString();
    _emailUserName.text = configuration.emailUserName ?? '';
    _emailUseSsl = configuration.emailUseSsl;
    _inviteUrlBase.text = configuration.inviteUrlBase ?? '';
    _razorpayKeyId.text = configuration.razorpayKeyId ?? '';
    // The web client key is client-visible, so it hydrates into the field like
    // any other visible value (unlike the write-only server-side key above).
    _googleMapsWebClientKey.text = configuration.googleMapsWebClientKey ?? '';
    _googleMapsBaseUrl.text = configuration.googleMapsBaseUrl ?? '';

    _originalFromAddress = _emailFromAddress.text.trim();
    _originalFromName = _emailFromName.text.trim();
    _originalHost = _emailHost.text.trim();
    _originalPort = _emailPort.text.trim();
    _originalUserName = _emailUserName.text.trim();
    _originalUseSsl = _emailUseSsl;
    _originalInviteUrlBase = _inviteUrlBase.text.trim();
    _originalRazorpayKeyId = _razorpayKeyId.text.trim();
    _originalGoogleMapsWebClientKey = _googleMapsWebClientKey.text.trim();
    _originalGoogleMapsBaseUrl = _googleMapsBaseUrl.text.trim();

    _clearSecrets();
    _hydrated = true;
  }

  /// Clears every write-only secret field. Called after hydration (they never
  /// arrive from the server) and after a successful save so an old typed
  /// secret is never re-submitted accidentally.
  void _clearSecrets() {
    _emailPassword.clear();
    _razorpayKeySecret.clear();
    _razorpayWebhookSecret.clear();
    _googleMapsApiKey.clear();
  }

  void _resetHydration() {
    _hydrated = false;
    _clearSecrets();
  }

  int? get _parsedPort {
    final value = int.tryParse(_emailPort.text.trim());
    if (value == null || value < 1 || value > 65535) return null;
    return value;
  }

  String? get _portError {
    final text = _emailPort.text.trim();
    if (text.isEmpty) return 'Enter a port between 1 and 65535.';
    if (_parsedPort == null) {
      return 'Enter a valid port between 1 and 65535.';
    }
    return null;
  }

  /// True when the local form differs from the last hydrated server state or
  /// the admin typed a new secret. The Save button is only enabled then.
  bool _hasChanges() {
    return _emailFromAddress.text.trim() != _originalFromAddress ||
        _emailFromName.text.trim() != _originalFromName ||
        _emailHost.text.trim() != _originalHost ||
        _emailPort.text.trim() != _originalPort ||
        _emailUserName.text.trim() != _originalUserName ||
        _emailPassword.text.trim().isNotEmpty ||
        _emailUseSsl != _originalUseSsl ||
        _inviteUrlBase.text.trim() != _originalInviteUrlBase ||
        _razorpayKeyId.text.trim() != _originalRazorpayKeyId ||
        _razorpayKeySecret.text.trim().isNotEmpty ||
        _razorpayWebhookSecret.text.trim().isNotEmpty ||
        _googleMapsApiKey.text.trim().isNotEmpty ||
        _googleMapsWebClientKey.text.trim() !=
            _originalGoogleMapsWebClientKey ||
        _googleMapsBaseUrl.text.trim() != _originalGoogleMapsBaseUrl;
  }

  /// Returns the trimmed text when it differs from the hydrated original,
  /// otherwise null (leave the backend override untouched). A differing empty
  /// string is returned as `''` so the request clears the database override.
  String? _changedText(String text, String original) {
    final trimmed = text.trim();
    return trimmed == original ? null : trimmed;
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(integrationsControllerProvider);
    final configuration = state.configuration;

    // Hydrate exactly once per loaded configuration. Never while a load is in
    // flight (otherwise a manual refresh would re-apply stale values) and never
    // while the user is editing.
    if (configuration != null && !state.isLoading && !_hydrated) {
      _hydrateFrom(configuration);
    }

    final session = ref.watch(sessionControllerProvider).session;
    final permissions = session?.user.permissions ?? const <String>[];
    final canRead = permissions.contains(kIntegrationsReadPermission);
    final canManage = permissions.contains(kIntegrationsManagePermission);

    if (!canRead) {
      return Scaffold(
        appBar: AppBar(title: const Text('Integrations')),
        body: const UnauthorizedStatePanel(),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text('Integrations'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () {
                    setState(_resetHydration);
                    ref.read(integrationsControllerProvider.notifier).load();
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
    IntegrationsState state,
    bool canManage,
  ) {
    final configuration = state.configuration;

    if (state.isLoading && configuration == null) {
      return const LoadingStatePanel(
        message: 'Loading integration settings',
      );
    }
    if (state.errorMessage != null && configuration == null) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () => ref.read(integrationsControllerProvider.notifier).load(),
      );
    }
    if (configuration == null) {
      return EmptyStatePanel(
        title: 'No integration settings',
        message: canManage
            ? 'Configure SMTP email delivery, Razorpay credentials, and the '
                  'Google Maps server key used by the backend.'
            : 'Integration settings are not available for this account.',
      );
    }

    final canEdit = canManage && !state.isSaving;
    final hasChanges = _hasChanges();
    final portError = _portError;
    final scheme = Theme.of(context).colorScheme;

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        const _ScopeInfoCard(),
        const SizedBox(height: 12),
        if (!canManage) ...[
          const _ReadOnlyBanner(),
          const SizedBox(height: 12),
        ],
        if (state.savedMessage != null) ...[
          _SavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.testMessage != null) ...[
          _TestResultBanner(message: state.testMessage!),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          _ErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        _SectionCard(
          icon: Icons.mail_outlined,
          title: 'Email (SMTP) delivery',
          subtitle:
              'The mail ID DoodhDirect uses to send employee invitation links '
              'and transactional email.',
          configured: configuration.isEmailConfigured,
          children: [
            TextField(
              controller: _emailFromAddress,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              textCapitalization: TextCapitalization.none,
              keyboardType: TextInputType.emailAddress,
              decoration: const InputDecoration(
                labelText: 'From address',
                hintText: 'no-reply@example.com',
                helperText:
                    'The sender email address shown on outgoing mail.',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _emailFromName,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              decoration: const InputDecoration(
                labelText: 'From name',
                hintText: 'DoodhDirect',
                helperText: 'Optional sender display name.',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _emailHost,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              textCapitalization: TextCapitalization.none,
              decoration: const InputDecoration(
                labelText: 'SMTP host',
                hintText: 'smtp.example.com',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _emailPort,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              keyboardType: TextInputType.number,
              inputFormatters: [FilteringTextInputFormatter.digitsOnly],
              decoration: InputDecoration(
                labelText: 'SMTP port',
                helperText:
                    portError ?? 'Usually 587 (TLS) or 465 (SSL).',
                errorText: portError,
                border: const OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _emailUserName,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              textCapitalization: TextCapitalization.none,
              decoration: const InputDecoration(
                labelText: 'SMTP user name',
                helperText:
                    'Optional. Leave blank when the relay accepts the mail '
                    'without authentication.',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _emailPassword,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              obscureText: true,
              autocorrect: false,
              enableSuggestions: false,
              decoration: const InputDecoration(
                labelText: 'SMTP password',
                hintText: '••••••••••••',
                helperText:
                    'Write-only. Leave blank to keep the existing password.',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Use SSL/TLS'),
              subtitle: const Text(
                'Requires a secure connection to the SMTP server.',
              ),
              value: _emailUseSsl,
              onChanged: canEdit
                  ? (value) => setState(() => _emailUseSsl = value)
                  : null,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _inviteUrlBase,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              textCapitalization: TextCapitalization.none,
              decoration: const InputDecoration(
                labelText: 'Invitation link base URL',
                hintText: 'https://app.example.com',
                helperText:
                    'Base URL used to build the absolute invitation links sent '
                    'in onboarding emails.',
                border: OutlineInputBorder(),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        _SectionCard(
          icon: Icons.payments_outlined,
          title: 'Razorpay',
          subtitle: 'Runtime payment credentials used to collect payments.',
          configured: configuration.isRazorpayConfigured,
          children: [
            TextField(
              controller: _razorpayKeyId,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              textCapitalization: TextCapitalization.none,
              decoration: const InputDecoration(
                labelText: 'Key ID',
                hintText: 'rzp_test_…',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _razorpayKeySecret,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              obscureText: true,
              autocorrect: false,
              enableSuggestions: false,
              decoration: const InputDecoration(
                labelText: 'Key secret',
                hintText: '••••••••••••',
                helperText:
                    'Write-only. Leave blank to keep the existing secret.',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _razorpayWebhookSecret,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              obscureText: true,
              autocorrect: false,
              enableSuggestions: false,
              decoration: const InputDecoration(
                labelText: 'Webhook secret',
                hintText: '••••••••••••',
                helperText:
                    'Write-only. Used to verify Razorpay webhook signatures.',
                border: OutlineInputBorder(),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        _SectionCard(
          icon: Icons.map_outlined,
          title: 'Google Maps',
          subtitle: 'Backend/server key and web client key used to load maps.',
          configured: configuration.isGoogleMapsConfigured,
          children: [
            TextField(
              controller: _googleMapsApiKey,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              obscureText: true,
              autocorrect: false,
              enableSuggestions: false,
              decoration: const InputDecoration(
                labelText: 'Google Maps Server-Side Key',
                hintText: '••••••••••••',
                helperText:
                    'Used by backend/server integrations. Write-only — leave '
                    'blank to keep the existing key.',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _googleMapsWebClientKey,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              textCapitalization: TextCapitalization.none,
              decoration: const InputDecoration(
                labelText: 'Google Maps Web Client Key',
                hintText: 'AIza…',
                helperText:
                    'Used by the web client/browser for Google Maps. The web '
                    'app fetches this key at runtime — changing it takes '
                    'effect after a client refresh, no rebuild needed.',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _googleMapsBaseUrl,
              enabled: canEdit,
              onChanged: (_) => setState(() {}),
              textCapitalization: TextCapitalization.none,
              decoration: const InputDecoration(
                labelText: 'Base URL',
                hintText: 'https://maps.googleapis.com/maps/api',
                border: OutlineInputBorder(),
              ),
            ),
          ],
        ),
        if (state.fieldErrors.isNotEmpty)
          ...state.fieldErrors.entries.map(
            (entry) => Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                '${entry.key}: ${entry.value}',
                style: TextStyle(color: scheme.error),
              ),
            ),
          ),
        if (canManage) ...[
          const SizedBox(height: 16),
          OutlinedButton.icon(
            onPressed: state.isTesting || state.isSaving
                ? null
                : () => ref.read(integrationsControllerProvider.notifier).test(),
            icon: state.isTesting
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.send_outlined),
            label: const Text('Send test email'),
          ),
          const SizedBox(height: 8),
          FilledButton.icon(
            onPressed:
                canEdit && hasChanges && portError == null ? _save : null,
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

  Future<void> _save() async {
    final password = _emailPassword.text.trim();
    final keySecret = _razorpayKeySecret.text.trim();
    final webhookSecret = _razorpayWebhookSecret.text.trim();
    final mapsApiKey = _googleMapsApiKey.text.trim();

    final request = UpdateIntegrationConfigurationRequest(
      emailFromAddress: _changedText(
        _emailFromAddress.text,
        _originalFromAddress,
      ),
      emailFromName: _changedText(_emailFromName.text, _originalFromName),
      emailHost: _changedText(_emailHost.text, _originalHost),
      emailPort: _emailPort.text.trim() != _originalPort &&
              _parsedPort != null
          ? _parsedPort
          : null,
      emailUserName: _changedText(_emailUserName.text, _originalUserName),
      emailPassword: password.isEmpty ? null : password,
      emailUseSsl: _emailUseSsl != _originalUseSsl ? _emailUseSsl : null,
      inviteUrlBase: _changedText(
        _inviteUrlBase.text,
        _originalInviteUrlBase,
      ),
      razorpayKeyId: _changedText(
        _razorpayKeyId.text,
        _originalRazorpayKeyId,
      ),
      razorpayKeySecret: keySecret.isEmpty ? null : keySecret,
      razorpayWebhookSecret: webhookSecret.isEmpty ? null : webhookSecret,
      googleMapsApiKey: mapsApiKey.isEmpty ? null : mapsApiKey,
      // The web client key is client-visible (not write-only): an edited value
      // is saved, and clearing the field sends '' so the backend clears the
      // database override — exactly like the base URL below.
      googleMapsWebClientKey: _changedText(
        _googleMapsWebClientKey.text,
        _originalGoogleMapsWebClientKey,
      ),
      googleMapsBaseUrl: _changedText(
        _googleMapsBaseUrl.text,
        _originalGoogleMapsBaseUrl,
      ),
    );

    final saved = await ref
        .read(integrationsControllerProvider.notifier)
        .save(request);
    if (saved && mounted) {
      // Re-hydrate from the configuration returned by the successful PUT so
      // the form reflects the authoritative saved state and every secret field
      // is cleared (they stay write-only).
      setState(_resetHydration);
    }
  }
}

/// A rounded chip showing whether an integration section is configured.
class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.configured});

  final bool configured;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final color = configured ? DoodhColors.tealDark : scheme.error;
    final background = configured ? DoodhColors.mint : scheme.errorContainer;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            configured ? Icons.check_circle_outline : Icons.error_outline,
            size: 14,
            color: color,
          ),
          const SizedBox(width: 4),
          Text(
            configured ? 'Configured' : 'Not configured',
            style: Theme.of(
              context,
            ).textTheme.labelSmall?.copyWith(color: color),
          ),
        ],
      ),
    );
  }
}

/// Card grouping one integration section: header row (icon, title, subtitle,
/// optional status chip) followed by the child fields.
class _SectionCard extends StatelessWidget {
  const _SectionCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.children,
    this.configured,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final List<Widget> children;
  final bool? configured;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    return Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.all(16),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(icon, color: scheme.primary),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: theme.textTheme.titleMedium),
                      const SizedBox(height: 4),
                      Text(
                        subtitle,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: scheme.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
                if (configured != null) ...[
                  const SizedBox(width: 8),
                  Padding(
                    padding: const EdgeInsets.only(top: 2),
                    child: _StatusChip(configured: configured!),
                  ),
                ],
              ],
            ),
          ),
          const Divider(height: 1),
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: children,
            ),
          ),
        ],
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
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(Icons.info_outline, color: scheme.primary),
                const SizedBox(width: 8),
                Text(
                  'What this screen controls',
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: scheme.onSurfaceVariant,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            const Text(
              'SMTP mail settings are used to email employee onboarding '
              'invitation links. Razorpay credentials are used at payment '
              'time. The Google Maps server key is used by the backend for '
              'address lookups and the web client key is used by the web app '
              'to load Google Maps in the browser.',
            ),
            const SizedBox(height: 4),
            const Text(
              'Saved values override environment/appsettings at runtime. '
              'Clearing a field restores the fallback value.',
            ),
            const SizedBox(height: 4),
            const Text(
              'Passwords and secrets are write-only: they are never shown '
              'again after saving.',
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
          'You can view the integration settings but do not have permission '
          'to change them.',
        ),
      ),
    );
  }
}

class _SavedBanner extends StatelessWidget {
  const _SavedBanner({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Card(
    color: DoodhColors.mint,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Row(
        children: [
          const Icon(Icons.check_circle_outline, color: DoodhColors.tealDark),
          const SizedBox(width: 8),
          Expanded(child: Text(message)),
        ],
      ),
    ),
  );
}

class _TestResultBanner extends StatelessWidget {
  const _TestResultBanner({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Card(
    color: DoodhColors.mint,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Row(
        children: [
          const Icon(Icons.send_outlined, color: DoodhColors.tealDark),
          const SizedBox(width: 8),
          Expanded(child: Text(message)),
        ],
      ),
    ),
  );
}

class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Card(
    color: Theme.of(context).colorScheme.errorContainer,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Row(
        children: [
          Icon(
            Icons.error_outline,
            color: Theme.of(context).colorScheme.error,
          ),
          const SizedBox(width: 8),
          Expanded(child: Text(message)),
        ],
      ),
    ),
  );
}
