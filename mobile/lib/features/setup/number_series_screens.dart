import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'number_series_controller.dart';
import 'number_series_models.dart';

const String kNumberSeriesReadPermission = 'SETUP.NUMBER_SERIES.READ';
const String kNumberSeriesManagePermission = 'SETUP.NUMBER_SERIES.MANAGE';

/// Setup → Number Series list. Requires `SETUP.NUMBER_SERIES.READ`.
class NumberSeriesListScreen extends ConsumerStatefulWidget {
  const NumberSeriesListScreen({super.key});

  @override
  ConsumerState<NumberSeriesListScreen> createState() =>
      _NumberSeriesListScreenState();
}

class _NumberSeriesListScreenState
    extends ConsumerState<NumberSeriesListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(numberSeriesControllerProvider.notifier).load(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(numberSeriesControllerProvider);
    final canManage =
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kNumberSeriesManagePermission,
            ) ??
            false;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Number series'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () => ref
                      .read(numberSeriesControllerProvider.notifier)
                      .load(),
            icon: const Icon(Icons.refresh),
          ),
          if (canManage)
            IconButton(
              tooltip: 'New series',
              onPressed: state.isSaving
                  ? null
                  : () => context.push('/admin/setup/number-series/new'),
              icon: const Icon(Icons.add),
            ),
        ],
      ),
      body: _body(context, state, canManage),
    );
  }

  Widget _body(BuildContext context, NumberSeriesState state, bool canManage) {
    if (state.isLoading && state.series.isEmpty) {
      return const LoadingStatePanel(message: 'Loading number series');
    }
    if (state.errorMessage != null && state.series.isEmpty) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () => ref.read(numberSeriesControllerProvider.notifier).load(),
      );
    }
    if (state.series.isEmpty) {
      return EmptyStatePanel(
        title: 'No number series',
        message:
            canManage
                ? 'Create a numbering series to start generating business numbers.'
                : 'Number series configured for this account are not available.',
        action: canManage
            ? FilledButton.icon(
                onPressed: () =>
                    context.push('/admin/setup/number-series/new'),
                icon: const Icon(Icons.add),
                label: const Text('New series'),
              )
            : null,
      );
    }

    final items = [...state.series]..sort(
      (a, b) => a.code.compareTo(b.code),
    );

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        if (state.savedMessage != null) ...[
          _SavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          _ErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        ...items.map(
          (series) => _SeriesCard(
            series: series,
            canManage: canManage,
            isBusy: state.isSaving,
          ),
        ),
      ],
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

class _SeriesCard extends ConsumerWidget {
  const _SeriesCard({
    required this.series,
    required this.canManage,
    required this.isBusy,
  });

  final NumberSeries series;
  final bool canManage;
  final bool isBusy;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    series.code,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                _ScopeBadge(scopeKey: series.scopeKey),
                const SizedBox(width: 8),
                _ActiveBadge(isActive: series.isActive),
              ],
            ),
            const SizedBox(height: 4),
            Text(series.description),
            const SizedBox(height: 12),
            Text('Template: ${series.template}'),
            const SizedBox(height: 4),
            Text(
              'Next: ${series.nextNumber ?? _nextHint(series)}  ·  '
              'Last used: ${series.lastUsedNumber}',
            ),
            const SizedBox(height: 4),
            Text(
              'Reset: ${series.resetPolicy.label}  ·  '
              'Step: ${series.incrementBy}',
            ),
            if (canManage) ...[
              const SizedBox(height: 8),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  if (series.isActive)
                    OutlinedButton.icon(
                      onPressed: isBusy
                          ? null
                          : () => ref
                                .read(numberSeriesControllerProvider.notifier)
                                .setActive(series.code, false, scope: series.scopeKey),
                      icon: const Icon(Icons.pause_outlined, size: 18),
                      label: const Text('Deactivate'),
                    )
                  else
                    FilledButton.tonalIcon(
                      onPressed: isBusy
                          ? null
                          : () => ref
                                .read(numberSeriesControllerProvider.notifier)
                                .setActive(series.code, true, scope: series.scopeKey),
                      icon: const Icon(Icons.play_arrow_outlined, size: 18),
                      label: const Text('Activate'),
                    ),
                  const SizedBox(width: 8),
                  IconButton(
                    tooltip: 'Configure ${series.code}',
                    onPressed: isBusy
                        ? null
                        : () => context.push(
                            '/admin/setup/number-series/${series.code}/edit',
                            extra: series,
                          ),
                    icon: const Icon(Icons.settings_outlined),
                  ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }

  String _nextHint(NumberSeries series) {
    if (series.isActive) {
      return '${series.lastUsedNumber + series.incrementBy}';
    }
    return '—';
  }
}

class _ScopeBadge extends StatelessWidget {
  const _ScopeBadge({required this.scopeKey});

  final String? scopeKey;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final scoped = scopeKey != null && scopeKey!.isNotEmpty;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: scoped
            ? scheme.secondaryContainer
            : scheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        scoped ? scopeKey! : 'Global',
        style: TextStyle(
          color: scoped
              ? scheme.onSecondaryContainer
              : scheme.onSurfaceVariant,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

class _ActiveBadge extends StatelessWidget {
  const _ActiveBadge({required this.isActive});

  final bool isActive;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: isActive ? DoodhColors.mint : scheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        isActive ? 'Active' : 'Inactive',
        style: TextStyle(
          color: isActive ? DoodhColors.tealDark : scheme.onSurfaceVariant,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

/// Configuration screen for a single series. Supports live preview (without
/// consuming) and safe edits.
class NumberSeriesConfigScreen extends ConsumerStatefulWidget {
  const NumberSeriesConfigScreen({
    super.key,
    required this.code,
    this.series,
  });

  final String code;
  final NumberSeries? series;

  @override
  ConsumerState<NumberSeriesConfigScreen> createState() =>
      _NumberSeriesConfigScreenState();
}

class _NumberSeriesConfigScreenState
    extends ConsumerState<NumberSeriesConfigScreen> {
  late final TextEditingController _description;
  late final TextEditingController _template;
  late final TextEditingController _scope;
  late final TextEditingController _startingNumber;
  late final TextEditingController _incrementBy;
  NumberSeriesResetPolicy _resetPolicy = NumberSeriesResetPolicy.never;

  @override
  void initState() {
    super.initState();
    final series = widget.series;
    _description = TextEditingController(text: series?.description ?? '');
    _template = TextEditingController(text: series?.template ?? '');
    _scope = TextEditingController(
      text: (series?.scopeKey ?? '').trim().isEmpty ? '' : series!.scopeKey!,
    );
    _startingNumber = TextEditingController(
      text: series?.startingNumber.toString() ?? '1',
    );
    _incrementBy = TextEditingController(
      text: series?.incrementBy.toString() ?? '1',
    );
    if (series != null) {
      _resetPolicy = series.resetPolicy;
    }
  }

  @override
  void dispose() {
    _description.dispose();
    _template.dispose();
    _scope.dispose();
    _startingNumber.dispose();
    _incrementBy.dispose();
    super.dispose();
  }

  /// The scope value to send for a draft series, or null when empty.
  String? get _draftScopeValue {
    final value = _scope.text.trim();
    return value.isEmpty ? null : value;
  }

  /// Scope is fixed once a series exists — it identifies the series instance
  /// and cannot be changed without breaking existing numbers.
  bool get _scopeLocked => widget.series != null;

  int? get _startingNumberValue => int.tryParse(_startingNumber.text.trim());

  int? get _incrementByValue => int.tryParse(_incrementBy.text.trim());

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(numberSeriesControllerProvider);
    final canManage =
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kNumberSeriesManagePermission,
            ) ??
            false;
    if (!canManage) {
      return Scaffold(
        appBar: AppBar(title: const Text('Configure series')),
        body: const UnauthorizedStatePanel(),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: Text(
          widget.series == null ? 'New series' : 'Configure ${widget.code}',
        ),
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (widget.series != null) ...[
            Text(
              'Code: ${widget.series!.code}',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 4),
            Text(
              'Last used: ${widget.series!.lastUsedNumber}  ·  '
              'Next live: ${widget.series!.nextNumber ?? widget.series!.lastUsedNumber + widget.series!.incrementBy}',
            ),
            const SizedBox(height: 16),
          ],
          TextField(
            controller: _description,
            enabled: !state.isSaving,
            onChanged: (_) => setState(() {}),
            textCapitalization: TextCapitalization.sentences,
            decoration: const InputDecoration(
              labelText: 'Description',
              hintText: 'e.g. Customer account numbers',
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _template,
            enabled: !state.isSaving,
            onChanged: (_) => setState(() {}),
            decoration: const InputDecoration(
              labelText: 'Template',
              hintText: 'e.g. CUST/{NUMBER:0000}',
              helperText:
                  'Tokens: {NUMBER:0000} (padded), {PREFIX}, {SCOPE}, {FY}, '
                  '{YEAR}, {YY}, {MONTH}, {DATE:yyyyMMdd}',
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _scope,
            enabled: !state.isSaving && !_scopeLocked,
            onChanged: (_) => setState(() {}),
            textCapitalization: TextCapitalization.characters,
            decoration: InputDecoration(
              labelText: 'Scope key',
              hintText: 'e.g. MAIN',
              helperText: _scopeLocked
                  ? 'Fixed for this series — identifies the branch or '
                        'division it belongs to.'
                  : 'Branch/division key. Required when the template '
                        'contains {SCOPE}.',
              errorText: _templateContainsScope && _draftScopeValue == null
                  ? 'Enter a scope key — the template uses {SCOPE}.'
                  : null,
            ),
          ),
          const SizedBox(height: 12),
          DropdownButtonFormField<NumberSeriesResetPolicy>(
            initialValue: _resetPolicy,
            decoration: const InputDecoration(
              labelText: 'Reset policy',
              helperText: 'When the counter restarts',
            ),
            items: NumberSeriesResetPolicy.values
                .map(
                  (policy) => DropdownMenuItem(
                    value: policy,
                    child: Text(policy.label),
                  ),
                )
                .toList(),
            onChanged: state.isSaving
                ? null
                : (value) => setState(() => _resetPolicy = value!),
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _startingNumber,
                  enabled: !state.isSaving,
                  onChanged: (_) => setState(() {}),
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(
                    labelText: 'Starting number',
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: TextField(
                  controller: _incrementBy,
                  enabled: !state.isSaving,
                  onChanged: (_) => setState(() {}),
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(labelText: 'Increment by'),
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          if (state.preview != null)
            Card(
              color: DoodhColors.mint,
              child: ListTile(
                leading: const Icon(
                  Icons.visibility_outlined,
                  color: DoodhColors.tealDark,
                ),
                title: const Text('Preview'),
                subtitle: Text(
                  '${state.preview!.formattedNumber}  '
                  '(next counter ${state.preview!.nextNumber})',
                ),
              ),
            ),
          if (state.preview == null && widget.series != null)
            Card(
              child: ListTile(
                leading: const Icon(Icons.numbers_outlined),
                title: const Text('Live next number'),
                subtitle: Text(
                  '${widget.series!.nextNumber ?? widget.series!.lastUsedNumber + widget.series!.incrementBy}',
                ),
              ),
            ),
          if (state.fieldErrors.isNotEmpty)
            ...state.fieldErrors.entries.map(
              (entry) => Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(
                  '${entry.key}: ${entry.value}',
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
              ),
            ),
          const SizedBox(height: 16),
          OutlinedButton.icon(
            onPressed: state.isPreviewing || state.isSaving
                ? null
                : () => ref
                      .read(numberSeriesControllerProvider.notifier)
                      .previewTemplate(
                        widget.series?.code ?? _draftCode(),
                        _template.text.trim(),
                        nextNumber: _startingNumberValue,
                        scope: _previewScope,
                      ),
            icon: state.isPreviewing
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.preview_outlined),
            label: const Text('Preview template'),
          ),
          const SizedBox(height: 8),
          FilledButton.icon(
            onPressed: state.isSaving
                ? null
                : _validate()
                    ? () => _save(context)
                    : null,
            icon: state.isSaving
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save_outlined),
            label: const Text('Save series'),
          ),
        ],
      ),
    );
  }

  String _draftCode() {
    final code = _description.text.trim().toUpperCase().split(' ').first;
    return code.isEmpty ? 'NEW' : code;
  }

  bool get _templateContainsScope =>
      _template.text.trim().contains('{SCOPE}');

  /// The scope used for preview: the locked scope on existing series, or the
  /// draft scope value on a new series.
  String? get _previewScope =>
      _scopeLocked ? widget.series!.scopeKey : _draftScopeValue;

  bool _validate() =>
      _description.text.trim().isNotEmpty &&
      _template.text.trim().isNotEmpty &&
      (!_templateContainsScope || _draftScopeValue != null) &&
      _startingNumberValue != null &&
      _startingNumberValue! >= 1 &&
      _incrementByValue != null &&
      _incrementByValue! >= 1;

  Future<void> _save(BuildContext context) async {
    final series = widget.series;
    if (series == null) {
      final created = await ref
          .read(numberSeriesControllerProvider.notifier)
          .create(
            CreateNumberSeriesRequest(
              code: _codeFromDescription(),
              description: _description.text.trim(),
              template: _template.text.trim(),
              startingNumber: _startingNumberValue!,
              incrementBy: _incrementByValue!,
              resetPolicy: _resetPolicy,
              scopeKey: _draftScopeValue,
            ),
          );
      if (created != null && context.mounted) {
        context.pop();
      }
      return;
    }
    final updated = await ref
        .read(numberSeriesControllerProvider.notifier)
        .update(
          series.code,
          UpdateNumberSeriesRequest(
            description: _description.text.trim(),
            template: _template.text.trim(),
            startingNumber: _startingNumberValue!,
            incrementBy: _incrementByValue!,
            resetPolicy: _resetPolicy,
          ),
          scope: series.scopeKey,
        );
    if (updated != null && context.mounted) {
      context.pop();
    }
  }

  String _codeFromDescription() {
    final words = _description.text.trim().toUpperCase().split(
      RegExp(r'[\s\-_]+'),
    );
    if (words.isEmpty || words.first.isEmpty) {
      return 'NEW';
    }
    final code = words.take(2).join('_');
    return code.length > 32 ? code.substring(0, 32) : code;
  }
}
