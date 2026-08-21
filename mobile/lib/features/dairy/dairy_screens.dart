import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'dairy_controller.dart';
import 'dairy_models.dart';

class DairyDashboardScreen extends ConsumerStatefulWidget {
  const DairyDashboardScreen({super.key, this.branchId});

  final int? branchId;

  @override
  ConsumerState<DairyDashboardScreen> createState() =>
      _DairyDashboardScreenState();
}

class _DairyDashboardScreenState extends ConsumerState<DairyDashboardScreen> {
  int? _branchId;

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  int? get _selectedBranch => widget.branchId ?? _branchId;

  Future<void> _load() async {
    final branches =
        ref.read(sessionControllerProvider).session?.user.branchIds ??
        const <int>[];
    final branch =
        _selectedBranch ?? (branches.isEmpty ? null : branches.first);
    if (branch == null) return;
    setState(() => _branchId = branch);
    await Future.wait([
      ref.read(dairyControllerProvider.notifier).loadDashboard(branch),
      ref.read(dairyControllerProvider.notifier).loadAvailability(branch),
    ]);
  }

  @override
  Widget build(BuildContext context) {
    final session = ref.watch(sessionControllerProvider).session;
    final branches = session?.user.branchIds ?? const <int>[];
    final state = ref.watch(dairyControllerProvider);
    final branch = _selectedBranch;

    if (branch == null) {
      return const Scaffold(
        appBar: _DairyAppBar(title: 'Dairy operations'),
        body: StatePanel(
          icon: Icons.location_off_outlined,
          title: 'No branch assigned',
          message: 'A branch assignment is required to use dairy operations.',
        ),
      );
    }

    return Scaffold(
      appBar: const _DairyAppBar(title: 'Dairy operations'),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.all(16),
          children: [
            if (branches.length > 1)
              DropdownButtonFormField<int>(
                initialValue: branch,
                decoration: const InputDecoration(labelText: 'Branch'),
                items: branches
                    .map(
                      (id) => DropdownMenuItem(
                        value: id,
                        child: Text('Branch $id'),
                      ),
                    )
                    .toList(growable: false),
                onChanged: (value) => setState(() => _branchId = value),
              ),
            if (branches.length > 1) const SizedBox(height: 16),
            if (state.isLoading && state.dashboard == null)
              const SizedBox(height: 240, child: LoadingStatePanel())
            else if (state.isUnauthorized)
              const SizedBox(height: 240, child: UnauthorizedStatePanel())
            else if (state.isOffline && state.dashboard == null)
              SizedBox(height: 240, child: OfflineStatePanel(onRetry: _load))
            else if (state.errorMessage != null && state.dashboard == null)
              SizedBox(
                height: 240,
                child: ErrorStatePanel(
                  message: state.errorMessage!,
                  onRetry: _load,
                ),
              )
            else ...[
              Text('Today', style: Theme.of(context).textTheme.headlineSmall),
              const SizedBox(height: 12),
              _MetricGrid(
                children: [
                  _MetricTile(
                    icon: Icons.water_drop_outlined,
                    label: 'Produced',
                    value: state.dashboard == null
                        ? '-'
                        : formatMilkQuantity(
                            state.dashboard!.quantityProduced,
                            state.dashboard!.unit,
                          ),
                  ),
                  _MetricTile(
                    icon: Icons.inventory_2_outlined,
                    label: 'Available',
                    value: state.dashboard == null
                        ? '-'
                        : formatMilkQuantity(
                            state.dashboard!.availableQuantity,
                            state.dashboard!.unit,
                          ),
                  ),
                  _MetricTile(
                    icon: Icons.fact_check_outlined,
                    label: 'Production entries',
                    value: '${state.dashboard?.productionEntryCount ?? 0}',
                  ),
                  _MetricTile(
                    icon: Icons.inventory_outlined,
                    label: 'Available batches',
                    value: '${state.dashboard?.availableBatchCount ?? 0}',
                  ),
                ],
              ),
              const SizedBox(height: 20),
              _ActionTile(
                icon: Icons.add_circle_outline,
                title: 'Record production',
                subtitle: 'Create today\'s production entry and batch',
                onTap: () =>
                    context.push('/dairy/branch/$branch/production/new'),
              ),
              _ActionTile(
                icon: Icons.inventory_2_outlined,
                title: 'Milk batches',
                subtitle: 'Inspect batch quantities and status',
                onTap: () => context.push('/dairy/branch/$branch/batches'),
              ),
              _ActionTile(
                icon: Icons.history_outlined,
                title: 'Production history',
                subtitle: 'Review recorded production entries',
                onTap: () => context.push('/dairy/branch/$branch/production'),
              ),
              _ActionTile(
                icon: Icons.local_drink_outlined,
                title: 'Usage and dispatch',
                subtitle: 'Append usage against available batches',
                onTap: () => context.push('/dairy/branch/$branch/usage'),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class DairyProductionEntryScreen extends ConsumerStatefulWidget {
  const DairyProductionEntryScreen({super.key, required this.branchId});
  final int branchId;

  @override
  ConsumerState<DairyProductionEntryScreen> createState() =>
      _DairyProductionEntryScreenState();
}

class _DairyProductionEntryScreenState
    extends ConsumerState<DairyProductionEntryScreen> {
  final _formKey = GlobalKey<FormState>();
  final _quantityController = TextEditingController();
  final _buffaloController = TextEditingController();
  final _remarksController = TextEditingController();
  String? _shift;
  DateTime _productionAt = indiaNow();

  @override
  void dispose() {
    _quantityController.dispose();
    _buffaloController.dispose();
    _remarksController.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    final saved = await ref
        .read(dairyControllerProvider.notifier)
        .recordProduction(
          widget.branchId,
          RecordMilkProductionRequest(
            productionAt: _productionAt,
            shift: _shift,
            buffaloCount: int.parse(_buffaloController.text.trim()),
            quantityProduced: double.parse(_quantityController.text.trim()),
            remarks: _remarksController.text.trim().isEmpty
                ? null
                : _remarksController.text.trim(),
          ),
        );
    if (!mounted) return;
    if (saved) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Production recorded and batch created.')),
      );
      context.pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(dairyControllerProvider);
    return Scaffold(
      appBar: const _DairyAppBar(title: 'Record production'),
      body: Form(
        key: _formKey,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(
              'Branch ${widget.branchId}',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 16),
            TextFormField(
              controller: _quantityController,
              decoration: const InputDecoration(
                labelText: 'Produced quantity (L)',
              ),
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              validator: _positiveNumber,
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _buffaloController,
              decoration: const InputDecoration(labelText: 'Buffalo count'),
              keyboardType: TextInputType.number,
              validator: _positiveInteger,
            ),
            const SizedBox(height: 12),
            DropdownButtonFormField<String>(
              initialValue: _shift,
              decoration: const InputDecoration(labelText: 'Shift'),
              items: const [
                DropdownMenuItem(value: 'Morning', child: Text('Morning')),
                DropdownMenuItem(value: 'Evening', child: Text('Evening')),
              ],
              onChanged: (value) => setState(() => _shift = value),
            ),
            const SizedBox(height: 12),
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Production time'),
              subtitle: Text(formatDairyDateTime(_productionAt)),
              trailing: const Icon(Icons.calendar_today_outlined),
              onTap: () async {
                final now = indiaNow();
                final date = await showDatePicker(
                  context: context,
                  firstDate: now.subtract(const Duration(days: 30)),
                  lastDate: now,
                  initialDate: _productionAt,
                );
                if (date != null) {
                  setState(
                    () => _productionAt = DateTime(
                      date.year,
                      date.month,
                      date.day,
                      _productionAt.hour,
                      _productionAt.minute,
                    ),
                  );
                }
              },
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _remarksController,
              decoration: const InputDecoration(
                labelText: 'Remarks (optional)',
              ),
              maxLines: 3,
              maxLength: 500,
            ),
            if (state.errorMessage != null) ...[
              const SizedBox(height: 12),
              Text(
                state.errorMessage!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ],
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: state.isSaving ? null : _submit,
              icon: state.isSaving
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.save_outlined),
              label: const Text('Save production'),
            ),
          ],
        ),
      ),
    );
  }
}

class DairyBatchListScreen extends ConsumerStatefulWidget {
  const DairyBatchListScreen({super.key, required this.branchId});
  final int branchId;

  @override
  ConsumerState<DairyBatchListScreen> createState() =>
      _DairyBatchListScreenState();
}

class _DairyBatchListScreenState extends ConsumerState<DairyBatchListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() =>
      ref.read(dairyControllerProvider.notifier).loadBatches(widget.branchId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(dairyControllerProvider);
    return Scaffold(
      appBar: const _DairyAppBar(title: 'Milk batches'),
      body: _DairyListState(
        isLoading: state.isLoading,
        isOffline: state.isOffline,
        isUnauthorized: state.isUnauthorized,
        errorMessage: state.errorMessage,
        isEmpty: state.batches.isEmpty,
        emptyTitle: 'No batches recorded',
        emptyMessage: 'Production entries will appear here as batches.',
        onRetry: _load,
        child: RefreshIndicator(
          onRefresh: _load,
          child: ListView.builder(
            padding: const EdgeInsets.all(12),
            itemCount: state.batches.length,
            itemBuilder: (context, index) {
              final batch = state.batches[index];
              return Card(
                child: ListTile(
                  leading: Icon(
                    batch.status == MilkBatchStatus.available
                        ? Icons.inventory_2_outlined
                        : Icons.inventory_outlined,
                  ),
                  title: Text(batch.batchNumber),
                  subtitle: Text(
                    '${formatMilkQuantity(batch.availableQuantity, batch.unit)} available\n'
                    '${formatDairyDateTime(batch.productionAt)}',
                  ),
                  isThreeLine: true,
                  trailing: Text(batch.status.label),
                  onTap: () => context.push('/dairy/batches/${batch.publicId}'),
                ),
              );
            },
          ),
        ),
      ),
    );
  }
}

class DairyBatchDetailScreen extends ConsumerStatefulWidget {
  const DairyBatchDetailScreen({super.key, required this.batchId});
  final String batchId;

  @override
  ConsumerState<DairyBatchDetailScreen> createState() =>
      _DairyBatchDetailScreenState();
}

class _DairyBatchDetailScreenState
    extends ConsumerState<DairyBatchDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () =>
          ref.read(dairyControllerProvider.notifier).loadBatch(widget.batchId),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(dairyControllerProvider);
    final batch = state.selectedBatch;
    return Scaffold(
      appBar: const _DairyAppBar(title: 'Batch details'),
      body: batch == null && state.isLoading
          ? const LoadingStatePanel()
          : batch == null && state.isUnauthorized
          ? const UnauthorizedStatePanel()
          : batch == null
          ? ErrorStatePanel(
              message: state.errorMessage ?? 'Batch could not be loaded.',
              onRetry: () => ref
                  .read(dairyControllerProvider.notifier)
                  .loadBatch(widget.batchId),
            )
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                Text(
                  batch.batchNumber,
                  style: Theme.of(context).textTheme.headlineSmall,
                ),
                const SizedBox(height: 16),
                _DetailRow(label: 'Status', value: batch.status.label),
                _DetailRow(
                  label: 'Produced',
                  value: formatMilkQuantity(batch.quantityProduced, batch.unit),
                ),
                _DetailRow(
                  label: 'Available',
                  value: formatMilkQuantity(
                    batch.availableQuantity,
                    batch.unit,
                  ),
                ),
                _DetailRow(
                  label: 'Production time',
                  value: formatDairyDateTime(batch.productionAt),
                ),
                _DetailRow(label: 'Branch', value: '${batch.branchId}'),
                const SizedBox(height: 24),
                FilledButton.icon(
                  onPressed: batch.status == MilkBatchStatus.available
                      ? () => context.push(
                          '/dairy/batches/${batch.publicId}/usage/new',
                        )
                      : null,
                  icon: const Icon(Icons.local_drink_outlined),
                  label: const Text('Record usage'),
                ),
              ],
            ),
    );
  }
}

class DairyAvailabilityScreen extends ConsumerStatefulWidget {
  const DairyAvailabilityScreen({super.key, required this.branchId});
  final int branchId;

  @override
  ConsumerState<DairyAvailabilityScreen> createState() =>
      _DairyAvailabilityScreenState();
}

class _DairyAvailabilityScreenState
    extends ConsumerState<DairyAvailabilityScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(dairyControllerProvider.notifier)
      .loadAvailability(widget.branchId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(dairyControllerProvider);
    final availability = state.availability;
    return Scaffold(
      appBar: const _DairyAppBar(title: 'Availability'),
      body: availability == null && state.isLoading
          ? const LoadingStatePanel()
          : availability == null && state.isUnauthorized
          ? const UnauthorizedStatePanel()
          : availability == null && state.isOffline
          ? OfflineStatePanel(onRetry: _load)
          : availability == null
          ? ErrorStatePanel(
              message:
                  state.errorMessage ?? 'Availability could not be loaded.',
              onRetry: _load,
            )
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.all(16),
                children: [
                  _MetricGrid(
                    children: [
                      _MetricTile(
                        icon: Icons.water_drop_outlined,
                        label: 'Produced',
                        value: formatMilkQuantity(
                          availability.quantityProduced,
                          availability.unit,
                        ),
                      ),
                      _MetricTile(
                        icon: Icons.local_drink_outlined,
                        label: 'Used',
                        value: formatMilkQuantity(
                          availability.quantityUsed,
                          availability.unit,
                        ),
                      ),
                      _MetricTile(
                        icon: Icons.inventory_2_outlined,
                        label: 'Available',
                        value: formatMilkQuantity(
                          availability.availableQuantity,
                          availability.unit,
                        ),
                      ),
                      _MetricTile(
                        icon: Icons.layers_outlined,
                        label: 'Batches',
                        value: '${availability.availableBatchCount}',
                      ),
                    ],
                  ),
                ],
              ),
            ),
    );
  }
}

class DairyProductionHistoryScreen extends ConsumerStatefulWidget {
  const DairyProductionHistoryScreen({super.key, required this.branchId});
  final int branchId;

  @override
  ConsumerState<DairyProductionHistoryScreen> createState() =>
      _DairyProductionHistoryScreenState();
}

class _DairyProductionHistoryScreenState
    extends ConsumerState<DairyProductionHistoryScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(dairyControllerProvider.notifier)
      .loadProduction(widget.branchId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(dairyControllerProvider);
    return Scaffold(
      appBar: const _DairyAppBar(title: 'Production history'),
      body: _DairyListState(
        isLoading: state.isLoading,
        isOffline: state.isOffline,
        isUnauthorized: state.isUnauthorized,
        errorMessage: state.errorMessage,
        isEmpty: state.production.isEmpty,
        emptyTitle: 'No production history',
        emptyMessage: 'Recorded production entries will appear here.',
        onRetry: _load,
        child: RefreshIndicator(
          onRefresh: _load,
          child: ListView.builder(
            padding: const EdgeInsets.all(12),
            itemCount: state.production.length,
            itemBuilder: (context, index) {
              final production = state.production[index];
              return Card(
                child: ListTile(
                  leading: const Icon(Icons.water_drop_outlined),
                  title: Text(
                    formatMilkQuantity(
                      production.quantityProduced,
                      production.unit,
                    ),
                  ),
                  subtitle: Text(
                    '${formatDairyDateTime(production.productionAt)}\n${production.batch.batchNumber}',
                  ),
                  isThreeLine: true,
                  trailing: Text('${production.buffaloCount} buffaloes'),
                ),
              );
            },
          ),
        ),
      ),
    );
  }
}

class DairyUsageScreen extends ConsumerStatefulWidget {
  const DairyUsageScreen({super.key, required this.branchId, this.batchId});
  final int branchId;
  final String? batchId;

  @override
  ConsumerState<DairyUsageScreen> createState() => _DairyUsageScreenState();
}

class _DairyUsageScreenState extends ConsumerState<DairyUsageScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() =>
      ref.read(dairyControllerProvider.notifier).loadUsage(widget.branchId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(dairyControllerProvider);
    return Scaffold(
      appBar: const _DairyAppBar(title: 'Usage and dispatch'),
      floatingActionButton: widget.batchId == null
          ? null
          : FloatingActionButton.extended(
              onPressed: () =>
                  context.push('/dairy/batches/${widget.batchId}/usage/new'),
              icon: const Icon(Icons.add),
              label: const Text('Record usage'),
            ),
      body: _DairyListState(
        isLoading: state.isLoading,
        isOffline: state.isOffline,
        isUnauthorized: state.isUnauthorized,
        errorMessage: state.errorMessage,
        isEmpty: state.usage.isEmpty,
        emptyTitle: 'No usage recorded',
        emptyMessage: 'Append-only batch usage will appear here.',
        onRetry: _load,
        child: RefreshIndicator(
          onRefresh: _load,
          child: ListView.builder(
            padding: const EdgeInsets.all(12),
            itemCount: state.usage.length,
            itemBuilder: (context, index) {
              final usage = state.usage[index];
              return Card(
                child: ListTile(
                  leading: const Icon(Icons.local_drink_outlined),
                  title: Text(
                    formatMilkQuantity(usage.quantityUsed, usage.unit),
                  ),
                  subtitle: Text(
                    '${usage.batchNumber}\n${usage.purpose} • ${formatDairyDateTime(usage.usedAt)}',
                  ),
                  isThreeLine: true,
                ),
              );
            },
          ),
        ),
      ),
    );
  }
}

class DairyUsageEntryScreen extends ConsumerStatefulWidget {
  const DairyUsageEntryScreen({super.key, required this.batchId});
  final String batchId;

  @override
  ConsumerState<DairyUsageEntryScreen> createState() =>
      _DairyUsageEntryScreenState();
}

class _DairyUsageEntryScreenState extends ConsumerState<DairyUsageEntryScreen> {
  final _formKey = GlobalKey<FormState>();
  final _quantityController = TextEditingController();
  final _purposeController = TextEditingController();
  final _remarksController = TextEditingController();
  DateTime _usedAt = indiaNow();

  @override
  void dispose() {
    _quantityController.dispose();
    _purposeController.dispose();
    _remarksController.dispose();
    super.dispose();
  }

  Future<void> _pickUsedAt() async {
    final now = indiaNow();
    final date = await showDatePicker(
      context: context,
      firstDate: now.subtract(const Duration(days: 30)),
      lastDate: now,
      initialDate: _usedAt,
    );
    if (date == null || !mounted) return;

    final time = await showTimePicker(
      context: context,
      initialTime: TimeOfDay.fromDateTime(_usedAt),
    );
    if (time == null || !mounted) return;

    final selected = DateTime(
      date.year,
      date.month,
      date.day,
      time.hour,
      time.minute,
    );
    if (selected.isAfter(indiaNow())) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Usage time cannot be in the future.')),
      );
      return;
    }
    setState(() => _usedAt = selected);
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    final saved = await ref
        .read(dairyControllerProvider.notifier)
        .recordUsage(
          widget.batchId,
          RecordMilkUsageRequest(
            usedAt: _usedAt,
            quantityUsed: double.parse(_quantityController.text.trim()),
            purpose: _purposeController.text.trim(),
            remarks: _remarksController.text.trim().isEmpty
                ? null
                : _remarksController.text.trim(),
          ),
        );
    if (!mounted) return;
    if (saved) context.pop();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(dairyControllerProvider);
    return Scaffold(
      appBar: const _DairyAppBar(title: 'Record usage'),
      body: Form(
        key: _formKey,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(
              'Batch ${widget.batchId}',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 16),
            TextFormField(
              controller: _quantityController,
              decoration: const InputDecoration(labelText: 'Quantity used (L)'),
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              validator: _positiveNumber,
            ),
            const SizedBox(height: 12),
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Usage time'),
              subtitle: Text(formatDairyDateTime(_usedAt)),
              trailing: const Icon(Icons.schedule_outlined),
              onTap: _pickUsedAt,
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _purposeController,
              decoration: const InputDecoration(labelText: 'Purpose'),
              maxLength: 100,
              validator: (value) => value == null || value.trim().isEmpty
                  ? 'Enter a purpose'
                  : null,
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _remarksController,
              decoration: const InputDecoration(
                labelText: 'Remarks (optional)',
              ),
              maxLines: 3,
              maxLength: 500,
            ),
            if (state.errorMessage != null) ...[
              const SizedBox(height: 12),
              Text(
                state.errorMessage!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ],
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: state.isSaving ? null : _submit,
              icon: state.isSaving
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.save_outlined),
              label: const Text('Save usage'),
            ),
          ],
        ),
      ),
    );
  }
}

class _DairyAppBar extends StatelessWidget implements PreferredSizeWidget {
  const _DairyAppBar({required this.title});
  final String title;

  @override
  Widget build(BuildContext context) => AppBar(title: Text(title));

  @override
  Size get preferredSize => const Size.fromHeight(kToolbarHeight);
}

class _MetricGrid extends StatelessWidget {
  const _MetricGrid({required this.children});
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final columns = constraints.maxWidth >= 600 ? 4 : 2;
      return GridView.count(
        crossAxisCount: columns,
        shrinkWrap: true,
        physics: const NeverScrollableScrollPhysics(),
        mainAxisSpacing: 8,
        crossAxisSpacing: 8,
        childAspectRatio: 1.35,
        children: children,
      );
    },
  );
}

class _MetricTile extends StatelessWidget {
  const _MetricTile({
    required this.icon,
    required this.label,
    required this.value,
  });
  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, color: Theme.of(context).colorScheme.primary),
          const Spacer(),
          Text(value, style: Theme.of(context).textTheme.titleLarge),
          Text(label, maxLines: 2, overflow: TextOverflow.ellipsis),
        ],
      ),
    ),
  );
}

class _ActionTile extends StatelessWidget {
  const _ActionTile({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
  });
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Card(
    child: ListTile(
      leading: Icon(icon),
      title: Text(title),
      subtitle: Text(subtitle),
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
    ),
  );
}

class _DetailRow extends StatelessWidget {
  const _DetailRow({required this.label, required this.value});
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 8),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: Text(
            label,
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        ),
        const SizedBox(width: 16),
        Expanded(child: Text(value, textAlign: TextAlign.end)),
      ],
    ),
  );
}

class _DairyListState extends StatelessWidget {
  const _DairyListState({
    required this.isLoading,
    required this.isOffline,
    required this.isUnauthorized,
    required this.errorMessage,
    required this.isEmpty,
    required this.emptyTitle,
    required this.emptyMessage,
    required this.onRetry,
    required this.child,
  });

  final bool isLoading;
  final bool isOffline;
  final bool isUnauthorized;
  final String? errorMessage;
  final bool isEmpty;
  final String emptyTitle;
  final String emptyMessage;
  final VoidCallback onRetry;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    if (isLoading && isEmpty) return const LoadingStatePanel();
    if (isUnauthorized) return const UnauthorizedStatePanel();
    if (isOffline && isEmpty) return OfflineStatePanel(onRetry: onRetry);
    if (errorMessage != null && isEmpty) {
      return ErrorStatePanel(message: errorMessage!, onRetry: onRetry);
    }
    if (isEmpty) {
      return EmptyStatePanel(title: emptyTitle, message: emptyMessage);
    }
    return child;
  }
}

String? _positiveNumber(String? value) {
  final parsed = double.tryParse(value?.trim() ?? '');
  return parsed == null || parsed <= 0 ? 'Enter a positive quantity' : null;
}

String? _positiveInteger(String? value) {
  final parsed = int.tryParse(value?.trim() ?? '');
  return parsed == null || parsed <= 0 ? 'Enter a positive count' : null;
}
