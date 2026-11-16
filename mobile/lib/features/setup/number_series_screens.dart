import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
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
        ref
            .watch(sessionControllerProvider)
            .session
            ?.user
            .permissions
            .contains(kNumberSeriesManagePermission) ??
        false;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Number series'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () =>
                      ref.read(numberSeriesControllerProvider.notifier).load(),
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
        message: canManage
            ? 'Create a numbering series to start generating business numbers.'
            : 'Number series configured for this account are not available.',
        action: canManage
            ? FilledButton.icon(
                onPressed: () => context.push('/admin/setup/number-series/new'),
                icon: const Icon(Icons.add),
                label: const Text('New series'),
              )
            : null,
      );
    }

    final items = [...state.series]..sort((a, b) => a.code.compareTo(b.code));

    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        _NumberSeriesHero(count: items.length),
        const SizedBox(height: 12),
        if (state.savedMessage != null) ...[
          DoodhSavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          DoodhErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        // Cards open the configure screen directly; read-only users and
        // mid-save taps stay non-tappable.
        ...items.map(
          (series) => _TappableSeriesCard(
            onTap: canManage && !state.isSaving
                ? () => context.push(
                    '/admin/setup/number-series/${series.code}/edit',
                    extra: series,
                  )
                : null,
            child: _SeriesCard(
              series: series,
              canManage: canManage,
              isBusy: state.isSaving,
            ),
          ),
        ),
      ],
    );
  }
}

/// Teal gradient hero heading the number series list, in the shared
/// Admin Centre language.
class _NumberSeriesHero extends StatelessWidget {
  const _NumberSeriesHero({required this.count});

  final int count;

  @override
  Widget build(BuildContext context) => Container(
    width: double.infinity,
    decoration: BoxDecoration(
      gradient: const LinearGradient(
        begin: Alignment.topLeft,
        end: Alignment.bottomRight,
        colors: [DoodhColors.teal, DoodhColors.tealDark],
      ),
      borderRadius: DoodhRadii.mdRadius,
    ),
    child: Stack(
      children: [
        Positioned(
          right: -30,
          top: -36,
          child: ExcludeSemantics(
            child: Container(
              width: 104,
              height: 104,
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                color: Colors.white.withValues(alpha: 0.07),
              ),
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
          child: Row(
            children: [
              Container(
                width: 44,
                height: 44,
                decoration: BoxDecoration(
                  color: Colors.white.withValues(alpha: 0.16),
                  borderRadius: DoodhRadii.sm,
                  border: Border.all(
                    color: Colors.white.withValues(alpha: 0.35),
                  ),
                ),
                child: const Icon(
                  Icons.numbers_outlined,
                  color: Colors.white,
                  size: 24,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Text(
                      'Number series',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontSize: 20,
                        fontWeight: FontWeight.w800,
                        color: Colors.white,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      count == 1
                          ? '1 template · business numbers'
                          : '$count templates · business numbers',
                      style: const TextStyle(
                        color: Colors.white70,
                        height: 1.35,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ],
    ),
  );
}

/// First letter for the card avatar.
String _seriesInitial(String code) {
  final trimmed = code.trim();
  if (trimmed.isEmpty) return '?';
  return trimmed[0].toUpperCase();
}

/// Icon-led detail line. The text stays a single string so assistive
/// tech announces it whole.
class _SeriesDetailRow extends StatelessWidget {
  const _SeriesDetailRow({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 4),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 16, color: DoodhColors.tealDark),
        const SizedBox(width: 6),
        Expanded(child: Text(text)),
      ],
    ),
  );
}

/// Card wrapper that opens the configure screen on tap. Renders the plain
/// card when there is nothing to open. The InkWell announces the card as
/// tappable to assistive tech.
class _TappableSeriesCard extends StatelessWidget {
  const _TappableSeriesCard({required this.onTap, required this.child});

  final VoidCallback? onTap;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final onTap = this.onTap;
    if (onTap == null) return child;
    return InkWell(
      borderRadius: DoodhRadii.md,
      onTap: onTap,
      child: child,
    );
  }
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
        padding: DoodhSpacing.cardPadding,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                ExcludeSemantics(
                  child: CircleAvatar(
                    radius: 20,
                    backgroundColor: DoodhColors.mint,
                    child: Text(
                      _seriesInitial(series.code),
                      style: Theme.of(context).textTheme.titleMedium?.copyWith(
                        color: DoodhColors.tealDark,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    series.code,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                DoodhChip(
                  label:
                      (series.scopeKey != null && series.scopeKey!.isNotEmpty)
                      ? series.scopeKey!
                      : 'Global',
                  tone: (series.scopeKey != null && series.scopeKey!.isNotEmpty)
                      ? DoodhTone.info
                      : DoodhTone.neutral,
                ),
                const SizedBox(width: 8),
                DoodhChip(
                  label: series.isActive ? 'Active' : 'Inactive',
                  tone: series.isActive ? DoodhTone.success : DoodhTone.neutral,
                ),
              ],
            ),
            const SizedBox(height: 4),
            Text(series.description),
            const SizedBox(height: 12),
            _SeriesDetailRow(
              icon: Icons.tag_outlined,
              text: 'Template: ${series.template}',
            ),
            _SeriesDetailRow(
              icon: Icons.numbers_outlined,
              text:
                  'Next: ${series.nextNumber ?? _nextHint(series)}  ·  '
                  'Last used: ${series.lastUsedNumber}',
            ),
            _SeriesDetailRow(
              icon: Icons.restart_alt_outlined,
              text:
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
                                .setActive(
                                  series.code,
                                  false,
                                  scope: series.scopeKey,
                                ),
                      icon: const Icon(Icons.pause_outlined, size: 18),
                      label: const Text('Deactivate'),
                    )
                  else
                    FilledButton.tonalIcon(
                      onPressed: isBusy
                          ? null
                          : () => ref
                                .read(numberSeriesControllerProvider.notifier)
                                .setActive(
                                  series.code,
                                  true,
                                  scope: series.scopeKey,
                                ),
                      icon: const Icon(Icons.play_arrow_outlined, size: 18),
                      label: const Text('Activate'),
                    ),
                  const SizedBox(width: 8),
                  IconButton(
                    tooltip: 'Delete ${series.code}',
                    onPressed: isBusy
                        ? null
                        : () => _confirmDelete(context, ref),
                    icon: const Icon(Icons.delete_outline),
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

  Future<void> _confirmDelete(BuildContext context, WidgetRef ref) async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete series?'),
        content: Text(
          '"${series.code}" will be permanently deleted, active or inactive. '
          'Numbers already issued stay on historical records — only the series '
          'configuration and its counter are removed. This cannot be undone.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirm != true || !context.mounted) return;
    final success = await ref
        .read(numberSeriesControllerProvider.notifier)
        .delete(series.code, scope: series.scopeKey);
    if (!context.mounted) return;
    final controller = ref.read(numberSeriesControllerProvider);
    final message = success
        ? (controller.savedMessage ?? 'Series ${series.code} deleted.')
        : (controller.errorMessage ?? 'Unable to delete the series.');
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }
}

/// Configuration screen for a single series. Supports live preview (without
/// consuming) and safe edits.
class NumberSeriesConfigScreen extends ConsumerStatefulWidget {
  const NumberSeriesConfigScreen({super.key, required this.code, this.series});

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

  int? get _startingNumberValue => int.tryParse(_startingNumber.text.trim());

  int? get _incrementByValue => int.tryParse(_incrementBy.text.trim());

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(numberSeriesControllerProvider);
    final canManage =
        ref
            .watch(sessionControllerProvider)
            .session
            ?.user
            .permissions
            .contains(kNumberSeriesManagePermission) ??
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
        padding: DoodhSpacing.pagePadding,
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
            enabled: !state.isSaving,
            onChanged: (_) => setState(() {}),
            textCapitalization: TextCapitalization.characters,
            decoration: InputDecoration(
              labelText: 'Scope key',
              hintText: 'e.g. MAIN',
              helperText:
                  'Branch/division key. Required when the template '
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
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
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

  bool get _templateContainsScope => _template.text.trim().contains('{SCOPE}');

  /// The scope used for preview — always the current draft scope value.
  String? get _previewScope => _draftScopeValue;

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
            scopeKey: _draftScopeValue,
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
