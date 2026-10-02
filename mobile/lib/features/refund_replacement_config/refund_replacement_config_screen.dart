import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'refund_replacement_config_controller.dart';
import 'refund_replacement_config_models.dart';

const String kRefundReplacementConfigReadPermission =
    'SETUP.REFUND_REPLACEMENT.READ';
const String kRefundReplacementConfigManagePermission =
    'SETUP.REFUND_REPLACEMENT.MANAGE';

/// Setup → Refund / Replacement request window configuration.
///
/// Requires `SETUP.REFUND_REPLACEMENT.READ` to view and
/// `SETUP.REFUND_REPLACEMENT.MANAGE` to edit. Both permissions are granted
/// only to OWNER and SYSTEM_ADMIN, matching the backend authorization policy.
///
/// The window is server-authoritative: it is expressed in whole hours and the
/// backend computes a normal request's deadline as
/// `Delivery.CompletedAt + WindowHours`. Values outside
/// [kRefundReplacementMinimumWindowHours]..[kRefundReplacementMaximumWindowHours]
/// are rejected by the backend (field `windowHours`).
///
/// Form state is hydrated from the server configuration exactly once (when the
/// first configuration arrives, after a manual refresh, or after a successful
/// save) so user edits survive rebuilds caused by typing.
class RefundReplacementConfigScreen extends ConsumerStatefulWidget {
  const RefundReplacementConfigScreen({super.key});

  @override
  ConsumerState<RefundReplacementConfigScreen> createState() =>
      _RefundReplacementConfigScreenState();
}

class _RefundReplacementConfigScreenState
    extends ConsumerState<RefundReplacementConfigScreen> {
  final TextEditingController _windowHours = TextEditingController();

  /// Whether the local form has been hydrated from the server configuration.
  bool _hydrated = false;

  /// Snapshot of the last hydrated server value, used to decide whether the
  /// admin made a change worth saving.
  String _originalWindowHours = '';

  /// Client-side validation message for the hours field, shown under the input.
  String? _validationError;

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(refundReplacementConfigControllerProvider.notifier)
          .load(),
    );
  }

  @override
  void dispose() {
    _windowHours.dispose();
    super.dispose();
  }

  void _hydrateFrom(RefundReplacementConfiguration configuration) {
    _windowHours.text = configuration.windowHours.toString();
    _originalWindowHours = configuration.windowHours.toString();
    _validationError = null;
    _hydrated = true;
  }

  void _resetHydration() {
    _hydrated = false;
  }

  /// True when the local form differs from the last hydrated server value.
  bool _hasChanges() => _windowHours.text.trim() != _originalWindowHours;

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(refundReplacementConfigControllerProvider);
    final configuration = state.configuration;

    // Hydrate exactly once per loaded configuration. Never while a load is in
    // flight and never while the user is editing.
    if (configuration != null && !state.isLoading && !_hydrated) {
      _hydrateFrom(configuration);
    }

    final session = ref.watch(sessionControllerProvider).session;
    final permissions = session?.user.permissions ?? const <String>[];
    final canRead = permissions.contains(
      kRefundReplacementConfigReadPermission,
    );
    final canManage = permissions.contains(
      kRefundReplacementConfigManagePermission,
    );

    if (!canRead) {
      return Scaffold(
        appBar: AppBar(title: const Text('Refund / Replacement Window')),
        body: const UnauthorizedStatePanel(),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text('Refund / Replacement Window'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () {
                    setState(_resetHydration);
                    ref
                        .read(
                          refundReplacementConfigControllerProvider.notifier,
                        )
                        .load();
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
    RefundReplacementConfigState state,
    bool canManage,
  ) {
    final configuration = state.configuration;

    if (state.isLoading && configuration == null) {
      return const LoadingStatePanel(
        message: 'Loading refund/replacement window settings',
      );
    }
    if (state.errorMessage != null && configuration == null) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () => ref
            .read(refundReplacementConfigControllerProvider.notifier)
            .load(),
      );
    }
    if (configuration == null) {
      return EmptyStatePanel(
        title: 'No refund/replacement settings',
        message: canManage
            ? 'Set the request window to control how long customers can raise '
                  'a normal refund or replacement request after delivery.'
            : 'Refund/replacement window settings are not available for this '
                  'account.',
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
        if (state.errorMessage != null) ...[
          DoodhErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        TextField(
          controller: _windowHours,
          enabled: canEdit,
          keyboardType: TextInputType.number,
          inputFormatters: [FilteringTextInputFormatter.digitsOnly],
          onChanged: (_) => setState(() => _validationError = null),
          decoration: InputDecoration(
            labelText: 'Refund/Replacement Request Window (Hours)',
            hintText: 'e.g. 5',
            helperText:
                'Customers can raise a normal Refund/Replacement Request only '
                'within this many hours after delivery.',
            border: const OutlineInputBorder(),
            errorText: _validationError ?? state.fieldErrors['windowHours'],
          ),
        ),
        if (canManage) ...[
          const SizedBox(height: 16),
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
    final hours = int.tryParse(_windowHours.text.trim());
    if (hours == null) {
      setState(() => _validationError = 'Enter the window in whole hours.');
      return;
    }
    final rangeError = RefundReplacementConfiguration.validateWindowHours(hours);
    if (rangeError != null) {
      setState(() => _validationError = rangeError);
      return;
    }

    final saved = await ref
        .read(refundReplacementConfigControllerProvider.notifier)
        .save(UpdateRefundReplacementConfigurationRequest(windowHours: hours));
    if (saved && mounted) {
      // Re-hydrate from the configuration returned by the successful PUT so
      // the form reflects the authoritative saved state.
      setState(_resetHydration);
    }
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
                  'Request window',
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: scheme.onSurfaceVariant,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            const Text(
              'The window is server-authoritative. The backend computes a '
              'normal refund/replacement request deadline as the delivery '
              'completion time plus this many hours.',
            ),
            const SizedBox(height: 4),
            const Text(
              'Requests linked to a rejected doorstep milk test follow a '
              'separate flow and are not limited by this window.',
            ),
          ],
        ),
      ),
    );
  }
}

class _StatusCard extends StatelessWidget {
  const _StatusCard({required this.configuration});

  final RefundReplacementConfiguration configuration;

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
          '${configuration.windowHours} h',
          style: TextStyle(
            color: configured ? DoodhColors.tealDark : scheme.onErrorContainer,
            fontWeight: FontWeight.w600,
          ),
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
          'You can view the refund/replacement window but do not have '
          'permission to change it.',
        ),
      ),
    );
  }
}

