import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'charge_controller.dart';
import 'charge_models.dart';

const String kTaxChargesReadPermission = 'SETUP.TAX_CHARGES.READ';
const String kTaxChargesManagePermission = 'SETUP.TAX_CHARGES.MANAGE';

/// Setup → Tax & Charges list. Requires `SETUP.TAX_CHARGES.READ`.
class ChargeListScreen extends ConsumerStatefulWidget {
  const ChargeListScreen({super.key});

  @override
  ConsumerState<ChargeListScreen> createState() => _ChargeListScreenState();
}

/// Web-style management dashboard matching the Catalogue list page: brand
/// app bar, page header, white management card with header row, filter bar,
/// data table and pagination footer. Behaviour (controller calls, button
/// labels, tooltips, banners) is unchanged — only the presentation matches
/// the catalogue dashboard.
class _ChargeListScreenState extends ConsumerState<ChargeListScreen> {
  static const _panelGreen = Color(0xFF198754);
  static const _pageGrey = Color(0xFFF8F9FA);
  static const _tableMinWidth = 980.0;
  static const _pageSize = 10;

  final _search = TextEditingController();
  String? _typeFilter;
  String _statusFilter = 'All';
  int _page = 0;

  /// When true, a blank editable draft row is pinned above the table so the
  /// admin creates the charge directly in the grid (no separate page).
  bool _drafting = false;

  void _startDraft() {
    if (_drafting) return;
    setState(() {
      _drafting = true;
      _page = 0;
    });
  }

  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(chargeControllerProvider.notifier).load());
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(chargeControllerProvider);
    final canManage =
        ref
            .watch(sessionControllerProvider)
            .session
            ?.user
            .permissions
            .contains(kTaxChargesManagePermission) ??
        false;

    return Scaffold(
      backgroundColor: _pageGrey,
      appBar: AppBar(
        backgroundColor: _pageGrey,
        title: const Text('Tax & Charges'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () => ref.read(chargeControllerProvider.notifier).load(),
            icon: const Icon(Icons.refresh),
          ),
          if (canManage)
            IconButton(
              tooltip: 'New charge',
              onPressed: state.isSaving || _drafting ? null : _startDraft,
              icon: const Icon(Icons.add),
            ),
        ],
      ),
      body: _body(context, state, canManage),
    );
  }

  Widget _body(BuildContext context, ChargeState state, bool canManage) {
    if (state.isLoading && state.charges.isEmpty) {
      return const LoadingStatePanel(message: 'Loading Tax & Charges');
    }
    if (state.errorMessage != null && state.charges.isEmpty) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () => ref.read(chargeControllerProvider.notifier).load(),
      );
    }

    final items = [...state.charges]
      ..sort((a, b) {
        final byType = a.chargeType.compareTo(b.chargeType);
        return byType != 0 ? byType : a.chargeCode.compareTo(b.chargeCode);
      });
    final types = <String>{
      for (final charge in items) charge.chargeType,
    }.toList(growable: false)..sort();
    final filtered = items.where((charge) {
      if (_typeFilter != null && charge.chargeType != _typeFilter) {
        return false;
      }
      if (_statusFilter == 'Active' && !charge.isActive) return false;
      if (_statusFilter == 'Inactive' && charge.isActive) return false;
      final query = _search.text.trim().toLowerCase();
      if (query.isEmpty) return true;
      return charge.chargeCode.toLowerCase().contains(query) ||
          charge.chargeType.toLowerCase().contains(query) ||
          (charge.description?.toLowerCase().contains(query) ?? false);
    }).toList(growable: false);
    final total = filtered.length;
    final pageCount = total == 0 ? 1 : ((total - 1) ~/ _pageSize) + 1;
    final page = _page.clamp(0, pageCount - 1);
    final start = page * _pageSize;
    final end = (start + _pageSize).clamp(0, total);
    final visible = total == 0
        ? const <Charge>[]
        : filtered.sublist(start, end);

    return RefreshIndicator(
      onRefresh: () => ref.read(chargeControllerProvider.notifier).load(),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: DoodhContentMax.wide),
          child: ListView(
            padding: DoodhSpacing.pagePadding,
            children: [
              Text(
                'Tax & Charges',
                style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                  fontSize: 24,
                  fontWeight: FontWeight.w800,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                'Configure taxes and fees applied at checkout',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: DoodhColors.muted,
                  fontSize: 14,
                ),
              ),
              const SizedBox(height: DoodhSpacing.md),
              if (state.savedMessage != null) ...[
                DoodhSavedBanner(message: state.savedMessage!),
                const SizedBox(height: 12),
              ],
              if (state.errorMessage != null) ...[
                DoodhErrorBanner(message: state.errorMessage!),
                const SizedBox(height: 12),
              ],
              Card(
                elevation: 1,
                color: Colors.white,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(12),
                  side: const BorderSide(color: Color(0xFFE9ECEF)),
                ),
                child: Padding(
                  padding: const EdgeInsets.all(DoodhSpacing.md),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      // Wrap (not Row+Spacer): the themed button padding makes
                      // the action ~200px wide, which overflowed narrow phone
                      // cards. Wrap keeps title+button on one row when they
                      // fit and drops the button below when they don't.
                      Wrap(
                        spacing: 12,
                        runSpacing: 8,
                        alignment: WrapAlignment.spaceBetween,
                        crossAxisAlignment: WrapCrossAlignment.center,
                        children: [
                          Text(
                            'Charges ($total)',
                            style: const TextStyle(
                              fontWeight: FontWeight.w800,
                              fontSize: 16,
                            ),
                          ),
                          if (canManage)
                            FilledButton.icon(
                              onPressed: state.isSaving || _drafting
                                  ? null
                                  : _startDraft,
                              style: FilledButton.styleFrom(
                                backgroundColor: _panelGreen,
                                shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(8),
                                ),
                                minimumSize: const Size(0, 40),
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 12,
                                ),
                              ),
                              icon: const Icon(Icons.add_rounded, size: 18),
                              label: const Text('New charge'),
                            ),
                        ],
                      ),
                      const SizedBox(height: DoodhSpacing.md),
                      _filterBar(types),
                      const SizedBox(height: DoodhSpacing.md),
                      // The draft renders even with zero charges so creation
                      // never strands the admin on the empty panel.
                      if (visible.isEmpty && !_drafting)
                        EmptyStatePanel(
                          title: 'No charges configured',
                          message: canManage
                              ? 'Create a charge to apply taxes or fees at checkout.'
                              : 'Charges configured for this business are not available.',
                        )
                      else ...[
                        SingleChildScrollView(
                          scrollDirection: Axis.horizontal,
                          child: ConstrainedBox(
                            constraints: const BoxConstraints(
                              minWidth: _tableMinWidth,
                            ),
                            child: SizedBox(
                              width: _tableMinWidth,
                              child: Column(
                                crossAxisAlignment:
                                    CrossAxisAlignment.stretch,
                                children: [
                                  _headerRow(),
                                  const Divider(height: 1),
                                  // Blank draft row for in-grid creation.
                                  if (_drafting && canManage)
                                    _ChargeRow(
                                      key: const ValueKey('charge-draft'),
                                      charge: null,
                                      canManage: canManage,
                                      isBusy: state.isSaving,
                                      onCreate: _createFromRow,
                                      onCancelDraft: () => setState(
                                        () => _drafting = false,
                                      ),
                                    ),
                                  for (final charge in visible)
                                    _ChargeRow(
                                      key: ValueKey(
                                        'charge-${charge.publicId}',
                                      ),
                                      charge: charge,
                                      canManage: canManage,
                                      isBusy: state.isSaving,
                                      onUpdate: _updateFromRow,
                                      onDelete: () =>
                                          _confirmDelete(context, charge),
                                      onToggleActive: () => ref
                                          .read(
                                            chargeControllerProvider.notifier,
                                          )
                                          .setActive(
                                            charge.publicId,
                                            !charge.isActive,
                                          ),
                                      onToggleApplicability: (value) =>
                                          _toggleApplicability(
                                            context,
                                            charge.publicId,
                                            value,
                                          ),
                                    ),
                                ],
                              ),
                            ),
                          ),
                        ),
                        const Divider(height: 1),
                        _paginationFooter(
                          start: total == 0 ? 0 : start + 1,
                          end: end,
                          total: total,
                          page: page,
                          pageCount: pageCount,
                        ),
                      ],
                    ],
                  ),
                ),
              ),
              const SizedBox(height: DoodhSpacing.xxxl),
            ],
          ),
        ),
      ),
    );
  }

  Widget _filterBar(List<String> types) => LayoutBuilder(
    builder: (context, constraints) {
      final narrow = constraints.maxWidth < 640;
      final search = TextField(
        controller: _search,
        decoration: const InputDecoration(
          hintText: 'Search charges by code, type or description...',
          prefixIcon: Icon(Icons.search_rounded),
          contentPadding: EdgeInsets.symmetric(vertical: 10),
        ),
        onChanged: (_) => setState(() => _page = 0),
      );
      final typeDropdown = DropdownButtonFormField<String>(
        isExpanded: true,
        initialValue: _typeFilter,
        decoration: const InputDecoration(
          contentPadding: EdgeInsets.symmetric(
            horizontal: 12,
            vertical: 10,
          ),
        ),
        hint: const Text('All types'),
        items: [
          const DropdownMenuItem<String>(
            value: null,
            child: Text('All types'),
          ),
          for (final type in types)
            DropdownMenuItem<String>(value: type, child: Text(type)),
        ],
        onChanged: (value) => setState(() {
          _typeFilter = value;
          _page = 0;
        }),
      );
      final statusDropdown = DropdownButtonFormField<String>(
        isExpanded: true,
        initialValue: _statusFilter,
        decoration: const InputDecoration(
          contentPadding: EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        ),
        items: const ['All', 'Active', 'Inactive']
            .map(
              (s) => DropdownMenuItem<String>(
                value: s,
                child: Text(s == 'All' ? 'Status: All' : s),
              ),
            )
            .toList(),
        onChanged: (value) => setState(() {
          _statusFilter = value ?? 'All';
          _page = 0;
        }),
      );
      if (narrow) {
        return Column(
          children: [
            search,
            const SizedBox(height: 8),
            typeDropdown,
            const SizedBox(height: 8),
            statusDropdown,
          ],
        );
      }
      return Row(
        children: [
          Expanded(flex: 5, child: search),
          const SizedBox(width: 8),
          Expanded(flex: 2, child: typeDropdown),
          const SizedBox(width: 8),
          Expanded(flex: 2, child: statusDropdown),
        ],
      );
    },
  );

  Widget _headerCell(String label) => Padding(
    // Horizontal 4 matches the row cells' outer padding so header text
    // aligns exactly with the in-grid field content below.
    padding: const EdgeInsets.symmetric(horizontal: 4),
    child: Text(
      label,
      style: const TextStyle(
        color: DoodhColors.muted,
        fontSize: 12,
        fontWeight: FontWeight.w700,
      ),
    ),
  );

  /// Description lives in its own column (previously stacked under the
  /// code) so long text never shifts the horizontal rhythm of the row.
  Widget _headerRow() => Padding(
    padding: const EdgeInsets.symmetric(vertical: 10),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        Expanded(flex: 2, child: _headerCell('Code')),
        Expanded(flex: 3, child: _headerCell('Description')),
        Expanded(flex: 2, child: _headerCell('Type')),
        Expanded(flex: 1, child: _headerCell('Percentage')),
        Expanded(flex: 1, child: _headerCell('Applicable on All')),
        Expanded(flex: 1, child: _headerCell('Status')),
        const SizedBox(width: 120, child: _HeaderActionsCell()),
      ],
    ),
  );

  /// Creates the charge from the in-grid draft row. Returns true when the
  /// server accepted it so the row can close itself.
  Future<bool> _createFromRow(CreateChargeRequest request) async {
    final created = await ref
        .read(chargeControllerProvider.notifier)
        .create(request);
    if (created == null || !mounted) return false;
    setState(() => _drafting = false);
    return true;
  }

  /// Saves in-grid edits through the generic update endpoint (type only when
  /// the charge was never used — the server rejects it otherwise — plus
  /// description and percentage). Returns true on success.
  Future<bool> _updateFromRow(
    String publicId,
    UpdateChargeRequest request,
  ) async {
    final updated = await ref
        .read(chargeControllerProvider.notifier)
        .update(publicId, request);
    return updated != null;
  }

  Widget _paginationFooter({
    required int start,
    required int end,
    required int total,
    required int page,
    required int pageCount,
  }) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 8),
    // The single-row layout overflows narrow phone cards (~296px content),
    // so small widths stack the range row above a compact pager.
    child: LayoutBuilder(
      builder: (context, constraints) {
        final pager = Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            _pageButton(
              tooltip: 'First page',
              icon: Icons.first_page_rounded,
              onPressed: page > 0 ? () => setState(() => _page = 0) : null,
            ),
            _pageButton(
              tooltip: 'Previous page',
              icon: Icons.chevron_left_rounded,
              onPressed: page > 0
                  ? () => setState(() => _page = page - 1)
                  : null,
            ),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
              decoration: BoxDecoration(
                border: Border.all(color: DoodhColors.line),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Text('${page + 1}'),
            ),
            _pageButton(
              tooltip: 'Next page',
              icon: Icons.chevron_right_rounded,
              onPressed: page < pageCount - 1
                  ? () => setState(() => _page = page + 1)
                  : null,
            ),
            _pageButton(
              tooltip: 'Last page',
              icon: Icons.last_page_rounded,
              onPressed: page < pageCount - 1
                  ? () => setState(() => _page = pageCount - 1)
                  : null,
            ),
          ],
        );
        final rangeRow = Row(
          children: [
            const Flexible(
              child: Text('Rows per page:', overflow: TextOverflow.ellipsis),
            ),
            const SizedBox(width: 6),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              decoration: BoxDecoration(
                border: Border.all(color: DoodhColors.line),
                borderRadius: BorderRadius.circular(6),
              ),
              child: const Text('10'),
            ),
            const Spacer(),
            Text('$start-$end of $total'),
          ],
        );
        if (constraints.maxWidth < 520) {
          return Column(
            children: [rangeRow, const SizedBox(height: 4), pager],
          );
        }
        return Row(children: [Expanded(child: rangeRow), pager]);
      },
    ),
  );

  Widget _pageButton({
    required String tooltip,
    required IconData icon,
    required VoidCallback? onPressed,
  }) => IconButton(
    tooltip: tooltip,
    onPressed: onPressed,
    style: IconButton.styleFrom(
      minimumSize: const Size(36, 36),
      padding: EdgeInsets.zero,
      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
    ),
    icon: Icon(icon, size: 20),
  );

  Future<void> _toggleApplicability(
    BuildContext context,
    String publicId,
    bool applicableOnAll,
  ) async {
    await ref
        .read(chargeControllerProvider.notifier)
        .setApplicableOnAll(publicId, applicableOnAll);
    if (!context.mounted) return;
    final state = ref.read(chargeControllerProvider);
    // The list refresh inside the controller already restores the server
    // state; only the failure message needs surfacing here.
    if (state.errorMessage != null) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(state.errorMessage!)));
    }
  }

  Future<void> _confirmDelete(BuildContext context, Charge charge) async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete charge?'),
        content: Text(
          '"${charge.chargeCode}" will be permanently deleted. Charges already '
          'applied to orders cannot be deleted — they must be deactivated '
          'instead. Historical orders keep their frozen amounts either way. '
          'This cannot be undone.',
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
        .read(chargeControllerProvider.notifier)
        .delete(charge.publicId);
    if (!context.mounted) return;
    final state = ref.read(chargeControllerProvider);
    final message = success
        ? (state.savedMessage ?? 'Charge ${charge.chargeCode} deleted.')
        : (state.errorMessage ??
              'Unable to delete the charge. If it has been applied to orders, '
                  'deactivate it instead.');
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text(message)));
  }
}

/// One editable grid row. `charge == null` renders the blank draft pinned
/// above the table for in-grid creation; otherwise the row edits the charge
/// inline (type unless already used, description, percentage) with
/// save/discard actions next to the delete-only action. The mode and status
/// switches keep calling their dedicated endpoints exactly as before — the
/// update endpoint never carries code, mode or active.
class _ChargeRow extends StatefulWidget {
  const _ChargeRow({
    super.key,
    this.charge,
    required this.canManage,
    required this.isBusy,
    this.onCreate,
    this.onUpdate,
    this.onCancelDraft,
    this.onDelete,
    this.onToggleActive,
    this.onToggleApplicability,
  });

  final Charge? charge;
  final bool canManage;
  final bool isBusy;
  final Future<bool> Function(CreateChargeRequest request)? onCreate;
  final Future<bool> Function(String publicId, UpdateChargeRequest request)?
      onUpdate;
  final VoidCallback? onCancelDraft;
  final VoidCallback? onDelete;
  final VoidCallback? onToggleActive;
  final ValueChanged<bool>? onToggleApplicability;

  @override
  State<_ChargeRow> createState() => _ChargeRowState();
}

class _ChargeRowState extends State<_ChargeRow> {
  late final TextEditingController _code = TextEditingController(
    text: widget.charge?.chargeCode ?? '',
  );
  late final TextEditingController _type = TextEditingController(
    text: widget.charge?.chargeType ?? '',
  );
  late final TextEditingController _description = TextEditingController(
    text: widget.charge?.description ?? '',
  );
  late final TextEditingController _percentage = TextEditingController(
    text: widget.charge == null ? '' : _format(widget.charge!.percentage),
  );
  late bool _draftApplicableOnAll = true;
  bool _saving = false;

  Charge? get _charge => widget.charge;

  bool get _isDraft => _charge == null;

  /// ChargeType freezes once the charge has been applied to orders (the
  /// server rejects the change); the code never travels on update at all.
  bool get _typeEditable => _isDraft || !(_charge!.isUsed);

  @override
  void initState() {
    super.initState();
    for (final controller in [_code, _type, _description, _percentage]) {
      controller.addListener(_onChanged);
    }
  }

  @override
  void didUpdateWidget(_ChargeRow oldWidget) {
    super.didUpdateWidget(oldWidget);
    // The controller refreshes the list after every mutation: adopt the
    // server values, but never clobber in-progress edits.
    if (!_isDraft && !_dirty) _resync();
  }

  @override
  void dispose() {
    _code.dispose();
    _type.dispose();
    _description.dispose();
    _percentage.dispose();
    super.dispose();
  }

  void _onChanged() {
    if (mounted) setState(() {});
  }

  void _resync() {
    final charge = _charge;
    if (charge == null) return;
    _type.text = charge.chargeType;
    _description.text = charge.description ?? '';
    _percentage.text = _format(charge.percentage);
  }

  bool get _dirty {
    final charge = _charge;
    if (charge == null) {
      return _code.text.trim().isNotEmpty ||
          _type.text.trim().isNotEmpty ||
          _description.text.trim().isNotEmpty ||
          _percentage.text.trim().isNotEmpty;
    }
    return _type.text.trim() != charge.chargeType ||
        _description.text.trim() != (charge.description?.trim() ?? '') ||
        _percentage.text.trim() != _format(charge.percentage);
  }

  double? get _percentageValue {
    final parsed = double.tryParse(_percentage.text.trim());
    if (parsed == null || parsed <= 0 || parsed > 100) return null;
    // The backend accepts at most two decimal places — mirror that exactly.
    if (double.parse(parsed.toStringAsFixed(2)) != parsed) return null;
    return parsed;
  }

  bool get _valid =>
      _type.text.trim().isNotEmpty &&
      _type.text.trim().length <= 40 &&
      _description.text.trim().length <= 200 &&
      _percentageValue != null &&
      (_isDraft
          ? _code.text.trim().isNotEmpty && _code.text.trim().length <= 20
          : true);

  static String _format(double value) => value == value.roundToDouble()
      ? value.round().toString()
      : value.toStringAsFixed(2);

  static String? _optional(String value) {
    final normalized = value.trim();
    return normalized.isEmpty ? null : normalized;
  }

  Future<void> _save() async {
    if (!_valid || _saving) return;
    setState(() => _saving = true);
    try {
      if (_isDraft) {
        await widget.onCreate!(CreateChargeRequest(
          chargeType: _type.text.trim(),
          chargeCode: _code.text.trim(),
          description: _optional(_description.text),
          percentage: _percentageValue!,
          applicableOnAll: _draftApplicableOnAll,
        ));
        // On success the screen closes the draft; on failure the list error
        // banner carries the server message.
      } else {
        await widget.onUpdate!(
          _charge!.publicId,
          UpdateChargeRequest(
            chargeType: _type.text.trim(),
            description: _optional(_description.text),
            percentage: _percentageValue!,
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _cancel() {
    if (_isDraft) {
      widget.onCancelDraft!();
      return;
    }
    setState(_resync);
  }

  @override
  Widget build(BuildContext context) {
    final charge = _charge;
    final editable = widget.canManage && !widget.isBusy && !_saving;
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              Expanded(flex: 2, child: _padded(_codeCell(charge, editable))),
              Expanded(
                flex: 3,
                child: _padded(
                  _cellField(
                    controller: _description,
                    hint: 'Description',
                    enabled: editable,
                    maxLength: 200,
                    capitalization: TextCapitalization.sentences,
                  ),
                ),
              ),
              Expanded(flex: 2, child: _padded(_typeCell(charge, editable))),
              Expanded(
                flex: 1,
                child: _padded(
                  _cellField(
                    controller: _percentage,
                    hint: '%',
                    enabled: editable,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                  ),
                ),
              ),
              Expanded(flex: 1, child: _padded(_modeSwitch(charge, editable))),
              Expanded(
                flex: 1,
                child: _padded(_statusSwitch(charge, editable)),
              ),
              SizedBox(width: 120, child: _actions(charge, editable)),
            ],
          ),
        ),
        const Divider(height: 1),
      ],
    );
  }

  /// Outer 4px gutter shared with the header so columns line up exactly.
  Widget _padded(Widget child) => Padding(
    padding: const EdgeInsets.symmetric(horizontal: 4),
    child: child,
  );

  Widget _codeCell(Charge? charge, bool editable) {
    if (_isDraft) {
      return _cellField(
        controller: _code,
        hint: 'Code e.g. GST-5',
        enabled: editable,
        maxLength: 20,
        capitalization: TextCapitalization.characters,
      );
    }
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
      child: Text(
        charge!.chargeCode,
        style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
    );
  }

  Widget _typeCell(Charge? charge, bool editable) {
    if (_typeEditable) {
      return _cellField(
        controller: _type,
        hint: 'Type e.g. GST',
        enabled: editable,
        maxLength: 40,
        capitalization: TextCapitalization.characters,
      );
    }
    return Align(
      alignment: Alignment.centerLeft,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
        decoration: BoxDecoration(
          color: _ChargeListColors.pillBlue,
          borderRadius: BorderRadius.circular(6),
        ),
        child: Text(
          charge!.chargeType.toUpperCase(),
          style: const TextStyle(
            color: _ChargeListColors.pillBlueText,
            fontSize: 11,
            fontWeight: FontWeight.w800,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
      ),
    );
  }

  Widget _cellField({
    required TextEditingController controller,
    required String hint,
    required bool enabled,
    int? maxLength,
    TextInputType? keyboardType,
    TextCapitalization capitalization = TextCapitalization.none,
  }) => TextField(
    controller: controller,
    enabled: enabled,
    maxLength: maxLength,
    keyboardType: keyboardType,
    textCapitalization: capitalization,
    style: const TextStyle(fontSize: 13),
    decoration: InputDecoration(
      hintText: hint,
      isDense: true,
      contentPadding: const EdgeInsets.symmetric(
        horizontal: 8,
        vertical: 8,
      ),
      counterText: '',
    ),
  );

  /// Bare mode toggle (no text — the column header carries the meaning).
  /// Existing rows show the server state; the draft keeps local state that
  /// is sent on creation. Enabling the global mode is refused server-side
  /// while any product assignment exists, so an assigned item-level charge
  /// renders a locked switch explaining what to do instead of a toggle
  /// whose tap could only fail.
  Widget _modeSwitch(Charge? charge, bool editable) {
    final assigned = charge?.productCount ?? 0;
    final locked = charge != null && !charge.applicableOnAll && assigned > 0;
    return Tooltip(
      message: locked
          ? 'Assigned to $assigned product${assigned == 1 ? '' : 's'} — '
                'remove ${assigned == 1 ? 'it' : 'them'} from those products first'
          : 'Applicable on All',
      child: Switch(
        value: charge?.applicableOnAll ?? _draftApplicableOnAll,
        onChanged: !editable || locked
            ? null
            : (value) {
                if (charge == null) {
                  setState(() => _draftApplicableOnAll = value);
                } else {
                  widget.onToggleApplicability!(value);
                }
              },
      ),
    );
  }

  /// Bare active toggle (no text). Draft rows start active on the server, so
  /// the draft shows a disabled ON switch.
  Widget _statusSwitch(Charge? charge, bool editable) {
    if (charge == null) {
      return const Tooltip(
        message: 'New charges start active',
        child: Switch(value: true, onChanged: null),
      );
    }
    return Tooltip(
      message: charge.isActive
          ? 'Deactivate ${charge.chargeCode}'
          : 'Activate ${charge.chargeCode}',
      child: Switch(
        value: charge.isActive,
        onChanged: editable ? (_) => widget.onToggleActive!() : null,
      ),
    );
  }

  /// Delete-only actions (plus save/discard while the row is dirty — the
  /// draft always offers them, disabled until it validates). There is no
  /// Edit button — editing happens directly in the row.
  Widget _actions(Charge? charge, bool editable) {
    final label = charge?.chargeCode ?? 'new charge';
    final showSave = widget.canManage && (_isDraft || _dirty);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (showSave) ...[
          IconButton(
            tooltip: 'Save $label',
            style: IconButton.styleFrom(
              minimumSize: const Size(36, 36),
              padding: const EdgeInsets.all(6),
              tapTargetSize: MaterialTapTargetSize.shrinkWrap,
            ),
            onPressed: !_valid || !editable ? null : _save,
            icon: const Icon(Icons.check_rounded, size: 20),
          ),
          IconButton(
            tooltip: 'Discard changes',
            style: IconButton.styleFrom(
              minimumSize: const Size(36, 36),
              padding: const EdgeInsets.all(6),
              tapTargetSize: MaterialTapTargetSize.shrinkWrap,
            ),
            onPressed: !editable ? null : _cancel,
            icon: const Icon(Icons.close_rounded, size: 20),
          ),
        ],
        if (charge != null && widget.canManage)
          IconButton(
            tooltip: 'Delete ${charge.chargeCode}',
            style: IconButton.styleFrom(
              minimumSize: const Size(36, 36),
              padding: const EdgeInsets.all(6),
              tapTargetSize: MaterialTapTargetSize.shrinkWrap,
            ),
            onPressed: !editable ? null : widget.onDelete,
            icon: const Icon(Icons.delete_outline, size: 20),
          ),
      ],
    );
  }
}

/// Fixed-width header label for the actions column (save/discard + delete
/// need ~108px; the row uses the same width so they line up).
class _HeaderActionsCell extends StatelessWidget {
  const _HeaderActionsCell();
  @override
  Widget build(BuildContext context) => const Padding(
    padding: EdgeInsets.symmetric(horizontal: 4),
    child: Text(
      'Actions',
      style: TextStyle(
        color: DoodhColors.muted,
        fontSize: 12,
        fontWeight: FontWeight.w700,
      ),
    ),
  );
}

/// Shared row colours (the list state class keeps its own copies for the
/// header, filter bar and pagination footer).
class _ChargeListColors {
  static const pillBlue = Color(0xFFE7F1FF);
  static const pillBlueText = Color(0xFF0D6EFD);
}


