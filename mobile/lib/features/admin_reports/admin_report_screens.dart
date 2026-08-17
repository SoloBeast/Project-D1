import 'dart:async';

import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'admin_report_controller.dart';
import 'admin_report_models.dart';
import 'report_export_saver.dart';

class AdminDashboardScreen extends ConsumerStatefulWidget {
  const AdminDashboardScreen({super.key});

  @override
  ConsumerState<AdminDashboardScreen> createState() =>
      _AdminDashboardScreenState();
}

class _AdminDashboardScreenState extends ConsumerState<AdminDashboardScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      if (_visibleModules(ref).isNotEmpty) {
        ref.read(adminReportControllerProvider.notifier).loadDashboard();
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final modules = _visibleModules(ref);
    final state = ref.watch(adminReportControllerProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Administration'),
        actions: [
          IconButton(
            tooltip: 'Refresh dashboard',
            onPressed: state.isDashboardLoading || modules.isEmpty
                ? null
                : () => ref
                      .read(adminReportControllerProvider.notifier)
                      .loadDashboard(),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: modules.isEmpty
          ? const UnauthorizedStatePanel()
          : _dashboardBody(context, state, modules),
    );
  }

  Widget _dashboardBody(
    BuildContext context,
    AdminReportState state,
    List<ReportModuleDescriptor> modules,
  ) {
    if (state.isDashboardLoading && state.dashboard == null) {
      return const LoadingStatePanel(message: 'Loading administration data');
    }
    if (state.isUnauthorized && state.dashboard == null) {
      return const UnauthorizedStatePanel();
    }
    if (state.isOffline && state.dashboard == null) {
      return OfflineStatePanel(
        onRetry: () =>
            ref.read(adminReportControllerProvider.notifier).loadDashboard(),
      );
    }
    if (state.errorMessage != null && state.dashboard == null) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () =>
            ref.read(adminReportControllerProvider.notifier).loadDashboard(),
      );
    }

    return RefreshIndicator(
      onRefresh: () =>
          ref.read(adminReportControllerProvider.notifier).loadDashboard(),
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(16),
        children: [
          if (state.dashboard case final dashboard?) ...[
            Text('Overview', style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 12),
            _MetricGrid(metrics: _dashboardMetrics(dashboard)),
            const SizedBox(height: 28),
          ],
          Text('Reports', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 12),
          _ModuleGrid(modules: modules),
        ],
      ),
    );
  }
}

class AdminReportScreen extends ConsumerStatefulWidget {
  const AdminReportScreen({super.key, required this.moduleSlug});

  final String moduleSlug;

  @override
  ConsumerState<AdminReportScreen> createState() => _AdminReportScreenState();
}

class _AdminReportScreenState extends ConsumerState<AdminReportScreen> {
  final _searchController = TextEditingController();
  final _statusController = TextEditingController();
  Timer? _searchTimer;

  ReportModuleDescriptor? get _module => reportModuleBySlug(widget.moduleSlug);

  @override
  void initState() {
    super.initState();
    Future.microtask(_loadInitialReport);
  }

  @override
  void didUpdateWidget(covariant AdminReportScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.moduleSlug != widget.moduleSlug) {
      _searchController.clear();
      _statusController.clear();
      Future.microtask(_loadInitialReport);
    }
  }

  @override
  void dispose() {
    _searchTimer?.cancel();
    _searchController.dispose();
    _statusController.dispose();
    super.dispose();
  }

  void _loadInitialReport() {
    final module = _module;
    if (module != null && _hasPermission(ref, module.permission)) {
      ref.read(adminReportControllerProvider.notifier).loadReport(module);
    }
  }

  @override
  Widget build(BuildContext context) {
    final module = _module;
    if (module == null) {
      return Scaffold(
        appBar: AppBar(title: const Text('Report unavailable')),
        body: const StatePanel(
          icon: Icons.search_off_outlined,
          title: 'Report not found',
          message: 'The requested report module does not exist.',
        ),
      );
    }

    final canRead = _hasPermission(ref, module.permission);
    final canExport = _hasPermission(ref, 'REPORTS.EXPORT');
    final state = ref.watch(adminReportControllerProvider);
    return Scaffold(
      appBar: AppBar(
        title: Text('${module.label} report'),
        actions: [
          IconButton(
            tooltip: 'Refresh report',
            onPressed: !canRead || state.isReportLoading
                ? null
                : () => ref
                      .read(adminReportControllerProvider.notifier)
                      .loadReport(module),
            icon: const Icon(Icons.refresh),
          ),
          if (canExport)
            PopupMenuButton<String>(
              tooltip: 'Export report',
              enabled: canRead && !state.isExporting,
              icon: state.isExporting
                  ? const SizedBox.square(
                      dimension: 20,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.download_outlined),
              onSelected: _export,
              itemBuilder: (context) => const [
                PopupMenuItem(value: 'Csv', child: Text('Export CSV')),
                PopupMenuItem(value: 'Xlsx', child: Text('Export Excel')),
              ],
            ),
        ],
      ),
      body: !canRead
          ? const UnauthorizedStatePanel()
          : _reportBody(module, state),
    );
  }

  Widget _reportBody(ReportModuleDescriptor module, AdminReportState state) {
    final currentModuleState = state.module?.slug == module.slug;
    final report = currentModuleState ? state.report : null;
    final filter = currentModuleState
        ? state.filter
        : ReportFilter(sortBy: module.sorts.firstOrNull?.value);

    return Column(
      children: [
        _ReportFilters(
          module: module,
          filter: filter,
          searchController: _searchController,
          statusController: _statusController,
          enabled: !state.isReportLoading,
          onSearchChanged: _onSearchChanged,
          onStatusSubmitted: _applyStatus,
          onFilterChanged: _updateFilter,
        ),
        if (state.isReportLoading && report != null)
          const LinearProgressIndicator(minHeight: 2),
        Expanded(child: _reportResult(module, state, report)),
      ],
    );
  }

  Widget _reportResult(
    ReportModuleDescriptor module,
    AdminReportState state,
    ReportPageData? report,
  ) {
    if (state.isReportLoading && report == null) {
      return const LoadingStatePanel(message: 'Loading report');
    }
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isOffline && report == null) {
      return OfflineStatePanel(
        onRetry: () =>
            ref.read(adminReportControllerProvider.notifier).loadReport(module),
      );
    }
    if (state.errorMessage != null && report == null) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () =>
            ref.read(adminReportControllerProvider.notifier).loadReport(module),
      );
    }
    if (report == null || report.items.isEmpty) {
      return const EmptyStatePanel(
        title: 'No results',
        message: 'No records match the current report filters.',
      );
    }

    return Column(
      children: [
        if (state.errorMessage != null)
          MaterialBanner(
            content: Text(state.errorMessage!),
            actions: [
              TextButton(
                onPressed: () => ref
                    .read(adminReportControllerProvider.notifier)
                    .loadReport(module),
                child: const Text('Retry'),
              ),
            ],
          ),
        Expanded(
          child: LayoutBuilder(
            builder: (context, constraints) => constraints.maxWidth >= 720
                ? _ReportTable(module: module, items: report.items)
                : _ReportList(module: module, items: report.items),
          ),
        ),
        _PaginationBar(
          report: report,
          isLoading: state.isReportLoading,
          onPrevious: () =>
              ref.read(adminReportControllerProvider.notifier).previousPage(),
          onNext: () =>
              ref.read(adminReportControllerProvider.notifier).nextPage(),
        ),
      ],
    );
  }

  void _onSearchChanged(String value) {
    _searchTimer?.cancel();
    _searchTimer = Timer(const Duration(milliseconds: 450), () {
      if (!mounted) return;
      final filter = ref.read(adminReportControllerProvider).filter;
      _updateFilter(
        filter.copyWith(
          search: value.trim().isEmpty ? null : value.trim(),
          clearSearch: value.trim().isEmpty,
        ),
      );
    });
  }

  void _applyStatus(String value) {
    final statuses = value
        .split(',')
        .map((item) => item.trim())
        .where((item) => item.isNotEmpty)
        .toList(growable: false);
    _updateFilter(
      ref
          .read(adminReportControllerProvider)
          .filter
          .copyWith(statuses: statuses),
    );
  }

  void _updateFilter(ReportFilter filter) {
    ref.read(adminReportControllerProvider.notifier).updateFilter(filter);
  }

  Future<void> _export(String format) async {
    final controller = ref.read(adminReportControllerProvider.notifier);
    final file = await controller.export(format);
    if (!mounted || file == null) return;

    try {
      final destination = await ref.read(reportExportSaverProvider).save(file);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('${file.fileName} saved to $destination.')),
      );
    } on Object {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('The report was generated but could not be saved.'),
        ),
      );
    } finally {
      controller.clearExport();
    }
  }
}

class _ReportFilters extends StatelessWidget {
  const _ReportFilters({
    required this.module,
    required this.filter,
    required this.searchController,
    required this.statusController,
    required this.enabled,
    required this.onSearchChanged,
    required this.onStatusSubmitted,
    required this.onFilterChanged,
  });

  final ReportModuleDescriptor module;
  final ReportFilter filter;
  final TextEditingController searchController;
  final TextEditingController statusController;
  final bool enabled;
  final ValueChanged<String> onSearchChanged;
  final ValueChanged<String> onStatusSubmitted;
  final ValueChanged<ReportFilter> onFilterChanged;

  @override
  Widget build(BuildContext context) => Material(
    color: Theme.of(context).colorScheme.surfaceContainerLow,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Wrap(
        spacing: 12,
        runSpacing: 12,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          SizedBox(
            width: 260,
            child: TextField(
              controller: searchController,
              enabled: enabled,
              onChanged: onSearchChanged,
              decoration: const InputDecoration(
                labelText: 'Search',
                prefixIcon: Icon(Icons.search),
                border: OutlineInputBorder(),
                isDense: true,
              ),
            ),
          ),
          SizedBox(
            width: 220,
            child: TextField(
              controller: statusController,
              enabled: enabled,
              onSubmitted: onStatusSubmitted,
              decoration: InputDecoration(
                labelText: 'Statuses',
                hintText: 'Comma-separated',
                prefixIcon: const Icon(Icons.filter_alt_outlined),
                suffixIcon: IconButton(
                  tooltip: 'Apply status filter',
                  onPressed: enabled
                      ? () => onStatusSubmitted(statusController.text)
                      : null,
                  icon: const Icon(Icons.check),
                ),
                border: const OutlineInputBorder(),
                isDense: true,
              ),
            ),
          ),
          OutlinedButton.icon(
            onPressed: enabled ? () => _pickDateRange(context) : null,
            icon: const Icon(Icons.date_range_outlined),
            label: Text(_dateLabel()),
          ),
          SizedBox(
            width: 190,
            child: DropdownButtonFormField<String>(
              isExpanded: true,
              initialValue: filter.sortBy,
              decoration: const InputDecoration(
                labelText: 'Sort by',
                border: OutlineInputBorder(),
                isDense: true,
              ),
              items: module.sorts
                  .map(
                    (sort) => DropdownMenuItem(
                      value: sort.value,
                      child: Text(sort.label),
                    ),
                  )
                  .toList(growable: false),
              onChanged: enabled
                  ? (value) {
                      if (value != null) {
                        onFilterChanged(filter.copyWith(sortBy: value));
                      }
                    }
                  : null,
            ),
          ),
          SegmentedButton<bool>(
            segments: const [
              ButtonSegment(
                value: false,
                icon: Icon(Icons.arrow_upward),
                label: Text('Ascending'),
              ),
              ButtonSegment(
                value: true,
                icon: Icon(Icons.arrow_downward),
                label: Text('Descending'),
              ),
            ],
            selected: {filter.descending},
            onSelectionChanged: enabled
                ? (selection) => onFilterChanged(
                    filter.copyWith(descending: selection.first),
                  )
                : null,
          ),
          SizedBox(
            width: 120,
            child: DropdownButtonFormField<int>(
              initialValue: filter.pageSize,
              decoration: const InputDecoration(
                labelText: 'Rows',
                border: OutlineInputBorder(),
                isDense: true,
              ),
              items: const [10, 25, 50, 100]
                  .map(
                    (size) =>
                        DropdownMenuItem(value: size, child: Text('$size')),
                  )
                  .toList(growable: false),
              onChanged: enabled
                  ? (value) {
                      if (value != null) {
                        onFilterChanged(filter.copyWith(pageSize: value));
                      }
                    }
                  : null,
            ),
          ),
        ],
      ),
    ),
  );

  String _dateLabel() {
    if (filter.fromUtc == null && filter.toUtc == null) return 'Date range';
    final from = filter.fromUtc == null ? 'Any' : _shortDate(filter.fromUtc!);
    final to = filter.toUtc == null ? 'Any' : _shortDate(filter.toUtc!);
    return '$from - $to';
  }

  Future<void> _pickDateRange(BuildContext context) async {
    final now = DateTime.now();
    final result = await showDateRangePicker(
      context: context,
      firstDate: DateTime(2020),
      lastDate: DateTime(now.year + 1, 12, 31),
      initialDateRange: filter.fromUtc != null && filter.toUtc != null
          ? DateTimeRange(start: filter.fromUtc!, end: filter.toUtc!)
          : null,
    );
    if (result == null) return;
    onFilterChanged(
      filter.copyWith(
        fromUtc: DateTime(
          result.start.year,
          result.start.month,
          result.start.day,
        ).toUtc(),
        toUtc: DateTime(
          result.end.year,
          result.end.month,
          result.end.day,
          23,
          59,
          59,
          999,
        ).toUtc(),
      ),
    );
  }
}

class _ReportTable extends StatelessWidget {
  const _ReportTable({required this.module, required this.items});

  final ReportModuleDescriptor module;
  final List<Map<String, dynamic>> items;

  @override
  Widget build(BuildContext context) => Scrollbar(
    thumbVisibility: true,
    child: SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
      scrollDirection: Axis.horizontal,
      child: SingleChildScrollView(
        child: DataTable(
          columns: module.columns
              .map((column) => DataColumn(label: Text(column.label)))
              .toList(growable: false),
          rows: items
              .map(
                (item) => DataRow(
                  cells: module.columns
                      .map(
                        (column) => DataCell(
                          ConstrainedBox(
                            constraints: const BoxConstraints(maxWidth: 260),
                            child: Text(
                              displayReportValue(item[column.key]),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                        ),
                      )
                      .toList(growable: false),
                ),
              )
              .toList(growable: false),
        ),
      ),
    ),
  );
}

class _ReportList extends StatelessWidget {
  const _ReportList({required this.module, required this.items});

  final ReportModuleDescriptor module;
  final List<Map<String, dynamic>> items;

  @override
  Widget build(BuildContext context) => ListView.separated(
    padding: const EdgeInsets.all(12),
    itemCount: items.length,
    separatorBuilder: (context, index) => const Divider(height: 1),
    itemBuilder: (context, index) {
      final item = items[index];
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: module.columns
              .map(
                (column) => Padding(
                  padding: const EdgeInsets.symmetric(vertical: 3),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      SizedBox(
                        width: 108,
                        child: Text(
                          column.label,
                          style: Theme.of(context).textTheme.labelMedium,
                        ),
                      ),
                      Expanded(
                        child: Text(displayReportValue(item[column.key])),
                      ),
                    ],
                  ),
                ),
              )
              .toList(growable: false),
        ),
      );
    },
  );
}

class _PaginationBar extends StatelessWidget {
  const _PaginationBar({
    required this.report,
    required this.isLoading,
    required this.onPrevious,
    required this.onNext,
  });

  final ReportPageData report;
  final bool isLoading;
  final VoidCallback onPrevious;
  final VoidCallback onNext;

  @override
  Widget build(BuildContext context) {
    final first = report.totalCount == 0
        ? 0
        : ((report.page - 1) * report.pageSize) + 1;
    final last = (report.page * report.pageSize).clamp(0, report.totalCount);
    return Material(
      color: Theme.of(context).colorScheme.surfaceContainerLow,
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
          child: Row(
            children: [
              Expanded(child: Text('$first-$last of ${report.totalCount}')),
              IconButton(
                tooltip: 'Previous page',
                onPressed: isLoading || report.page <= 1 ? null : onPrevious,
                icon: const Icon(Icons.chevron_left),
              ),
              Text('Page ${report.page}'),
              IconButton(
                tooltip: 'Next page',
                onPressed: isLoading || !report.hasNextPage ? null : onNext,
                icon: const Icon(Icons.chevron_right),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _MetricGrid extends StatelessWidget {
  const _MetricGrid({required this.metrics});

  final List<_Metric> metrics;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final columns = constraints.maxWidth >= 1100
          ? 4
          : constraints.maxWidth >= 680
          ? 3
          : 2;
      final width = (constraints.maxWidth - (12 * (columns - 1))) / columns;
      return Wrap(
        spacing: 12,
        runSpacing: 12,
        children: metrics
            .map(
              (metric) => SizedBox(
                width: width,
                child: Card(
                  child: Padding(
                    padding: const EdgeInsets.all(14),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Icon(metric.icon),
                        const SizedBox(height: 14),
                        Text(
                          metric.value,
                          style: Theme.of(context).textTheme.titleLarge,
                        ),
                        const SizedBox(height: 2),
                        Text(metric.label),
                      ],
                    ),
                  ),
                ),
              ),
            )
            .toList(growable: false),
      );
    },
  );
}

class _ModuleGrid extends StatelessWidget {
  const _ModuleGrid({required this.modules});

  final List<ReportModuleDescriptor> modules;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final columns = constraints.maxWidth >= 1000
          ? 4
          : constraints.maxWidth >= 640
          ? 3
          : 1;
      final width = (constraints.maxWidth - (12 * (columns - 1))) / columns;
      return Wrap(
        spacing: 12,
        runSpacing: 12,
        children: modules
            .map(
              (module) => SizedBox(
                width: width,
                child: Card(
                  child: ListTile(
                    leading: Icon(module.icon),
                    title: Text(module.label),
                    trailing: const Icon(Icons.chevron_right),
                    onTap: () => context.push('/admin/reports/${module.slug}'),
                  ),
                ),
              ),
            )
            .toList(growable: false),
      );
    },
  );
}

class _Metric {
  const _Metric(this.label, this.value, this.icon);

  final String label;
  final String value;
  final IconData icon;
}

List<_Metric> _dashboardMetrics(DashboardMetrics value) => [
  _Metric('Customers', '${value.customers}', Icons.people_outline),
  _Metric('Active customers', '${value.activeCustomers}', Icons.person_outline),
  _Metric('Employees', '${value.employees}', Icons.badge_outlined),
  _Metric('Orders', '${value.orders}', Icons.receipt_long_outlined),
  _Metric(
    'Order revenue',
    _money(value.oneTimeOrderRevenue),
    Icons.currency_rupee,
  ),
  _Metric(
    'Active subscriptions',
    '${value.activeSubscriptions}',
    Icons.event_repeat_outlined,
  ),
  _Metric(
    'Successful payments',
    _money(value.successfulPayments),
    Icons.payments_outlined,
  ),
  _Metric(
    'Pending payments',
    _money(value.pendingPayments),
    Icons.pending_actions_outlined,
  ),
  _Metric('Refunds', _money(value.refunds), Icons.undo_outlined),
  _Metric(
    'Wallet balances',
    _money(value.walletBalances),
    Icons.account_balance_wallet_outlined,
  ),
  _Metric('Deliveries', '${value.deliveries}', Icons.local_shipping_outlined),
  _Metric(
    'Successful deliveries',
    '${value.successfulDeliveries}',
    Icons.task_alt_outlined,
  ),
  _Metric(
    'Failed deliveries',
    '${value.failedDeliveries}',
    Icons.report_problem_outlined,
  ),
  _Metric('Milk produced', '${value.milkProduced}', Icons.water_drop_outlined),
  _Metric('Milk used', '${value.milkUsed}', Icons.science_outlined),
  _Metric(
    'Pending milk tests',
    '${value.pendingMilkTests}',
    Icons.biotech_outlined,
  ),
  _Metric(
    'Available cameras',
    '${value.availableCameras}',
    Icons.videocam_outlined,
  ),
  _Metric(
    'Notification failures',
    '${value.notificationFailures}',
    Icons.notifications_off_outlined,
  ),
];

List<ReportModuleDescriptor> _visibleModules(WidgetRef ref) {
  final permissions =
      ref.watch(sessionControllerProvider).session?.user.permissions ??
      const <String>[];
  return reportModules
      .where((module) => permissions.contains(module.permission))
      .toList(growable: false);
}

bool _hasPermission(WidgetRef ref, String permission) {
  final permissions =
      ref.watch(sessionControllerProvider).session?.user.permissions ??
      const <String>[];
  return permissions.contains(permission);
}

String _shortDate(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-${value.month.toString().padLeft(2, '0')}-${value.day.toString().padLeft(2, '0')}';
String _money(double value) => 'INR ${value.toStringAsFixed(2)}';
