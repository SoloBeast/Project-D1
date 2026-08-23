import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'delivery_controller.dart';
import 'delivery_models.dart';
import 'delivery_navigation.dart';

class CustomerDeliveryListScreen extends ConsumerStatefulWidget {
  const CustomerDeliveryListScreen({super.key});
  @override
  ConsumerState<CustomerDeliveryListScreen> createState() =>
      _CustomerDeliveryListScreenState();
}

class _CustomerDeliveryListScreenState
    extends ConsumerState<CustomerDeliveryListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(deliveryControllerProvider.notifier)
          .loadCustomerDeliveries(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    return Scaffold(
      appBar: AppBar(title: const Text('My deliveries')),
      body: _listBody<CustomerDelivery>(
        state: state,
        items: state.customerDeliveries,
        emptyTitle: 'No deliveries yet',
        emptyMessage:
            'Confirmed order and subscription deliveries will appear here.',
        reload: () => ref
            .read(deliveryControllerProvider.notifier)
            .loadCustomerDeliveries(),
        itemBuilder: (delivery) => DeliveryListTile(
          reference: delivery.referenceNumber,
          status: delivery.status,
          date: delivery.scheduledDate,
          subtitle: delivery.destinationAddress,
          tracking: delivery.isTrackingActive,
          onTap: () => context.push('/deliveries/${delivery.deliveryId}'),
        ),
      ),
    );
  }
}

class CustomerDeliveryDetailScreen extends ConsumerStatefulWidget {
  const CustomerDeliveryDetailScreen({super.key, required this.deliveryId});
  final String deliveryId;
  @override
  ConsumerState<CustomerDeliveryDetailScreen> createState() =>
      _CustomerDeliveryDetailScreenState();
}

class _CustomerDeliveryDetailScreenState
    extends ConsumerState<CustomerDeliveryDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(deliveryControllerProvider.notifier)
          .loadCustomerDelivery(widget.deliveryId),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    final delivery =
        state.selectedCustomerDelivery?.deliveryId == widget.deliveryId
        ? state.selectedCustomerDelivery
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Delivery tracking')),
      body: delivery == null
          ? _missingBody(
              state,
              () => ref
                  .read(deliveryControllerProvider.notifier)
                  .loadCustomerDelivery(widget.deliveryId),
            )
          : RefreshIndicator(
              onRefresh: () => ref
                  .read(deliveryControllerProvider.notifier)
                  .loadCustomerDelivery(widget.deliveryId),
              child: ListView(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
                children: [
                  _DeliveryHeader(
                    reference: delivery.referenceNumber,
                    source: delivery.sourceType,
                    status: delivery.status,
                    date: delivery.scheduledDate,
                  ),
                  const SizedBox(height: 16),
                  _DeliveryProgress(status: delivery.status),
                  const SizedBox(height: 16),
                  _InfoTile(
                    icon: Icons.location_on_outlined,
                    title: 'Delivery address',
                    text: delivery.destinationAddress,
                  ),
                  if (delivery.activeOtp != null)
                    _CustomerOtpCard(code: delivery.activeOtp!),
                  _InfoTile(
                    icon: Icons.badge_outlined,
                    title: 'Assigned to',
                    text: delivery.assignedEmployeeName ?? 'Assignment pending',
                  ),
                  if (delivery.isTrackingActive &&
                      delivery.latestLocation != null)
                    const _LiveLocationCard()
                  else
                    const _InfoTile(
                      icon: Icons.location_searching_outlined,
                      title: 'Live location',
                      text: 'Location becomes available while your delivery is on the way.',
                    ),
                  if (delivery.failureReason != null)
                    _InfoTile(
                      icon: Icons.error_outline,
                      title: 'Failure reason',
                      text: delivery.failureReason!,
                    ),
                  const SizedBox(height: 16),
                  OutlinedButton.icon(
                    icon: const Icon(Icons.science_outlined),
                    label: const Text('Doorstep milk test'),
                    onPressed: () => context.push(
                      '/deliveries/${widget.deliveryId}/milk-test',
                    ),
                  ),
                ],
              ),
            ),
    );
  }
}

class StaffDeliveryListScreen extends ConsumerStatefulWidget {
  const StaffDeliveryListScreen({super.key});
  @override
  ConsumerState<StaffDeliveryListScreen> createState() =>
      _StaffDeliveryListScreenState();
}

class _StaffDeliveryListScreenState
    extends ConsumerState<StaffDeliveryListScreen> {
  static const _filterStatuses = [
    DeliveryStatus.assigned,
    DeliveryStatus.pickedUp,
    DeliveryStatus.outForDelivery,
    DeliveryStatus.arrived,
    DeliveryStatus.delivered,
  ];

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(deliveryControllerProvider.notifier).loadToday(),
    );
  }

  Future<void> _selectStatus(DeliveryStatus? status) =>
      ref.read(deliveryControllerProvider.notifier).selectStaffStatus(status);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    return Scaffold(
      appBar: AppBar(title: const Text("Today's deliveries")),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
            child: DropdownButtonFormField<DeliveryStatus?>(
              initialValue: state.staffStatus,
              decoration: const InputDecoration(
                labelText: 'Delivery status',
                border: OutlineInputBorder(),
              ),
              items: [
                const DropdownMenuItem<DeliveryStatus?>(
                  value: null,
                  child: Text('All'),
                ),
                ..._filterStatuses.map(
                  (status) => DropdownMenuItem<DeliveryStatus?>(
                    value: status,
                    child: Text(status.label),
                  ),
                ),
              ],
              onChanged: state.isLoading ? null : _selectStatus,
            ),
          ),
          Expanded(
            child: _listBody<DeliveryDetails>(
              state: state,
              items: state.staffDeliveries,
              emptyTitle: state.staffStatus == null
                  ? 'No deliveries today'
                  : 'No ${state.staffStatus!.label.toLowerCase()} deliveries',
              emptyMessage: 'Assigned deliveries for today will appear here.',
              reload: () =>
                  ref.read(deliveryControllerProvider.notifier).loadToday(),
              itemBuilder: (delivery) => DeliveryListTile(
                reference: delivery.referenceNumber,
                status: delivery.status,
                date: delivery.scheduledDate,
                subtitle:
                    '${delivery.customerName} · ${delivery.destinationAddress}',
                tracking: delivery.isTrackingActive,
                onTap: () => context.push('/delivery/${delivery.deliveryId}'),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class StaffDeliveryDetailScreen extends ConsumerStatefulWidget {
  const StaffDeliveryDetailScreen({super.key, required this.deliveryId});
  final String deliveryId;
  @override
  ConsumerState<StaffDeliveryDetailScreen> createState() =>
      _StaffDeliveryDetailScreenState();
}

class _StaffDeliveryDetailScreenState
    extends ConsumerState<StaffDeliveryDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(deliveryControllerProvider.notifier)
          .loadStaffDelivery(widget.deliveryId),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    final delivery = state.selectedDelivery?.deliveryId == widget.deliveryId
        ? state.selectedDelivery
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Delivery details')),
      body: delivery == null
          ? _missingBody(
              state,
              () => ref
                  .read(deliveryControllerProvider.notifier)
                  .loadStaffDelivery(widget.deliveryId),
            )
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                _DeliveryHeader(
                  reference: delivery.referenceNumber,
                  source: delivery.sourceType,
                  status: delivery.status,
                  date: delivery.scheduledDate,
                ),
                const SizedBox(height: 12),
                _InfoTile(
                  icon: Icons.person_outline,
                  title: delivery.customerName,
                  text: delivery.customerMobile,
                ),
                _InfoTile(
                  icon: Icons.location_on_outlined,
                  title: 'Destination',
                  text: delivery.destinationAddress,
                ),
                _NavigateButton(delivery: delivery),
                if (delivery.deliveryInstructions != null)
                  _InfoTile(
                    icon: Icons.notes_outlined,
                    title: 'Instructions',
                    text: delivery.deliveryInstructions!,
                  ),
                if (state.errorMessage != null) _ErrorText(state.errorMessage!),
                const SizedBox(height: 12),
                if (delivery.status == DeliveryStatus.arrived)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 8),
                    child: OutlinedButton.icon(
                      icon: const Icon(Icons.science_outlined),
                      label: const Text('Perform milk test'),
                      onPressed: () => context.push(
                        '/delivery/${widget.deliveryId}/milk-test',
                      ),
                    ),
                  ),
                _StaffActions(delivery: delivery, saving: state.isSaving),
              ],
            ),
    );
  }
}

class DeliveryManagementScreen extends ConsumerStatefulWidget {
  const DeliveryManagementScreen({super.key, required this.branchId});
  final int branchId;
  @override
  ConsumerState<DeliveryManagementScreen> createState() =>
      _DeliveryManagementScreenState();
}

class _DeliveryManagementScreenState
    extends ConsumerState<DeliveryManagementScreen> {
  DateTime _date = indiaNow();
  DeliveryStatus? _status;
  DeliverySourceType? _sourceType;
  SubscriptionDeliverySlot? _slot;

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(deliveryControllerProvider.notifier)
      .loadBranch(
        widget.branchId,
        date: _date,
        status: _status,
        sourceType: _sourceType,
        slot: _slot,
      );

  Future<void> _chooseDate() async {
    final selected = await showDatePicker(
      context: context,
      initialDate: _date,
      firstDate: DateTime(2020),
      lastDate: DateTime(2100),
    );
    if (selected == null) return;
    setState(() => _date = selected);
    await _load();
  }

  Future<void> _assignSelected(DeliveryState state) async {
    final choice = await _showBulkAssignmentDialog(context, state.employees);
    if (choice == null) return;
    final assigned = await ref
        .read(deliveryControllerProvider.notifier)
        .bulkAssign(choice.employeeId, reason: choice.reason);
    if (!mounted) return;
    if (assigned) {
      await _load();
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Selected deliveries assigned.')),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    final readyCount = state.managedDeliveries
        .where(
          (delivery) => delivery.status == DeliveryStatus.readyForAssignment,
        )
        .length;
    final selectedCount = state.selectedManagedDeliveryIds.length;
    final canFilterSlot =
        _sourceType == null ||
        _sourceType == DeliverySourceType.subscriptionOccurrence;
    return Scaffold(
      appBar: AppBar(
        title: Text('Branch ${widget.branchId} deliveries'),
        actions: [
          IconButton(
            tooltip: 'Choose date',
            icon: const Icon(Icons.calendar_today_outlined),
            onPressed: state.isSaving ? null : _chooseDate,
          ),
          IconButton(
            tooltip: 'Generate Subscription Deliveries',
            icon: const Icon(Icons.sync),
            onPressed: state.isSaving
                ? null
                : () async {
                    await ref
                        .read(deliveryControllerProvider.notifier)
                        .materialize(_date);
                    await _load();
                  },
          ),
        ],
      ),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
            child: Column(
              children: [
                Align(
                  alignment: Alignment.centerLeft,
                  child: Text(
                    'Deliveries for ${formatDeliveryDate(_date)}',
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                const SizedBox(height: 8),
                DropdownButtonFormField<DeliveryStatus?>(
                  initialValue: _status,
                  decoration: const InputDecoration(
                    labelText: 'Status',
                    border: OutlineInputBorder(),
                  ),
                  items: [
                    const DropdownMenuItem(
                      value: null,
                      child: Text('All statuses'),
                    ),
                    ...DeliveryStatus.values
                        .where((value) => value != DeliveryStatus.unknown)
                        .map(
                          (value) => DropdownMenuItem(
                            value: value,
                            child: Text(value.label),
                          ),
                        ),
                  ],
                  onChanged: (value) {
                    setState(() => _status = value);
                    _load();
                  },
                ),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: DropdownButtonFormField<DeliverySourceType?>(
                        initialValue: _sourceType,
                        decoration: const InputDecoration(
                          labelText: 'Delivery type',
                          border: OutlineInputBorder(),
                        ),
                        items: const [
                          DropdownMenuItem(value: null, child: Text('All')),
                          DropdownMenuItem(
                            value: DeliverySourceType.oneTimeOrder,
                            child: Text('One-time'),
                          ),
                          DropdownMenuItem(
                            value: DeliverySourceType.subscriptionOccurrence,
                            child: Text('Subscription'),
                          ),
                        ],
                        onChanged: (value) {
                          setState(() {
                            _sourceType = value;
                            if (_sourceType ==
                                DeliverySourceType.oneTimeOrder) {
                              _slot = null;
                            }
                          });
                          _load();
                        },
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: DropdownButtonFormField<SubscriptionDeliverySlot?>(
                        initialValue: canFilterSlot ? _slot : null,
                        decoration: const InputDecoration(
                          labelText: 'Subscription slot',
                          border: OutlineInputBorder(),
                        ),
                        items: const [
                          DropdownMenuItem(
                            value: null,
                            child: Text('All slots'),
                          ),
                          DropdownMenuItem(
                            value: SubscriptionDeliverySlot.morning,
                            child: Text('Morning'),
                          ),
                          DropdownMenuItem(
                            value: SubscriptionDeliverySlot.evening,
                            child: Text('Evening'),
                          ),
                        ],
                        onChanged: canFilterSlot
                            ? (value) {
                                setState(() => _slot = value);
                                _load();
                              }
                            : null,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          if (readyCount > 0)
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
              child: Row(
                children: [
                  Text('$selectedCount selected'),
                  const Spacer(),
                  TextButton(
                    onPressed: selectedCount == readyCount
                        ? null
                        : () => ref
                              .read(deliveryControllerProvider.notifier)
                              .selectAllManagedDeliveries(),
                    child: const Text('Select All'),
                  ),
                  TextButton(
                    onPressed: selectedCount == 0
                        ? null
                        : () => ref
                              .read(deliveryControllerProvider.notifier)
                              .clearManagedSelection(),
                    child: const Text('Clear Selection'),
                  ),
                  FilledButton.icon(
                    icon: const Icon(Icons.assignment_ind_outlined),
                    label: const Text('Assign Selected'),
                    onPressed: selectedCount == 0 || state.isSaving
                        ? null
                        : () => _assignSelected(state),
                  ),
                ],
              ),
            ),
          Expanded(
            child: _listBody<DeliveryDetails>(
              state: state,
              items: state.managedDeliveries,
              emptyTitle: 'No branch deliveries',
              emptyMessage: 'No deliveries match the selected filters.',
              reload: _load,
              itemBuilder: (delivery) => _ManagedDeliveryTile(
                delivery: delivery,
                selected: state.selectedManagedDeliveryIds.contains(
                  delivery.deliveryId,
                ),
                onSelected: delivery.status == DeliveryStatus.readyForAssignment
                    ? () => ref
                          .read(deliveryControllerProvider.notifier)
                          .toggleManagedDelivery(delivery.deliveryId)
                    : null,
                onTap: () =>
                    context.push('/delivery-management/${delivery.deliveryId}'),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class DeliveryManagementDetailScreen extends ConsumerStatefulWidget {
  const DeliveryManagementDetailScreen({super.key, required this.deliveryId});
  final String deliveryId;
  @override
  ConsumerState<DeliveryManagementDetailScreen> createState() =>
      _DeliveryManagementDetailScreenState();
}

class _DeliveryManagementDetailScreenState
    extends ConsumerState<DeliveryManagementDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(deliveryControllerProvider.notifier)
          .loadManagedDelivery(widget.deliveryId),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    final delivery = state.selectedDelivery?.deliveryId == widget.deliveryId
        ? state.selectedDelivery
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Manage delivery')),
      body: delivery == null
          ? _missingBody(
              state,
              () => ref
                  .read(deliveryControllerProvider.notifier)
                  .loadManagedDelivery(widget.deliveryId),
            )
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                _DeliveryHeader(
                  reference: delivery.referenceNumber,
                  source: delivery.sourceType,
                  status: delivery.status,
                  date: delivery.scheduledDate,
                ),
                const SizedBox(height: 12),
                _InfoTile(
                  icon: Icons.person_outline,
                  title: delivery.customerName,
                  text: delivery.destinationAddress,
                ),
                _InfoTile(
                  icon: Icons.badge_outlined,
                  title: 'Assigned employee',
                  text: delivery.assignedEmployeeName ?? 'Unassigned',
                ),
                if (state.errorMessage != null) _ErrorText(state.errorMessage!),
                const SizedBox(height: 16),
                FilledButton.icon(
                  icon: const Icon(Icons.assignment_ind_outlined),
                  label: Text(
                    delivery.assignedEmployeeId == null
                        ? 'Assign employee'
                        : 'Reassign employee',
                  ),
                  onPressed: state.isSaving
                      ? null
                      : () => _showAssignmentDialog(
                          context,
                          ref,
                          delivery,
                          state.employees,
                        ),
                ),
                if (delivery.assignments.isNotEmpty) ...[
                  const SizedBox(height: 20),
                  Text(
                    'Assignment history',
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                  ...delivery.assignments.reversed.map(
                    (item) => ListTile(
                      contentPadding: EdgeInsets.zero,
                      leading: const Icon(Icons.history),
                      title: Text(item.employeeName ?? item.employeeId),
                      subtitle: Text(item.reason ?? 'Assigned'),
                    ),
                  ),
                ],
              ],
            ),
    );
  }
}

class _StaffActions extends ConsumerWidget {
  const _StaffActions({required this.delivery, required this.saving});
  final DeliveryDetails delivery;
  final bool saving;
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final controller = ref.read(deliveryControllerProvider.notifier);
    final actions = <Widget>[];
    if (delivery.status == DeliveryStatus.assigned) {
      actions.add(
        FilledButton.icon(
          icon: const Icon(Icons.inventory_2_outlined),
          label: const Text('Pick up'),
          onPressed: saving
              ? null
              : () => controller.pickup(delivery.deliveryId),
        ),
      );
    }
    if (delivery.status == DeliveryStatus.pickedUp) {
      actions.add(
        FilledButton.icon(
          icon: const Icon(Icons.route_outlined),
          label: const Text('Start delivery'),
          onPressed: saving
              ? null
              : () => controller.start(delivery.deliveryId),
        ),
      );
    }
    if (delivery.status == DeliveryStatus.outForDelivery) {
      actions.add(
        FilledButton.icon(
          icon: const Icon(Icons.location_on_outlined),
          label: const Text('Mark arrived'),
          onPressed: saving
              ? null
              : () => controller.arrive(delivery.deliveryId),
        ),
      );
    }
    if (delivery.status == DeliveryStatus.arrived) {
      actions.add(
        FilledButton.icon(
          icon: const Icon(Icons.password_outlined),
          label: const Text('Verify delivery OTP'),
          onPressed: saving
              ? null
              : () => _showOtpDialog(context, ref, delivery.deliveryId),
        ),
      );
    }
    if (![
      DeliveryStatus.delivered,
      DeliveryStatus.failed,
    ].contains(delivery.status)) {
      actions.add(
        OutlinedButton.icon(
          icon: const Icon(Icons.report_problem_outlined),
          label: const Text('Mark failed'),
          onPressed: saving
              ? null
              : () => _showFailureDialog(context, ref, delivery.deliveryId),
        ),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: actions
          .map(
            (action) => Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: action,
            ),
          )
          .toList(),
    );
  }
}

class DeliveryListTile extends StatelessWidget {
  const DeliveryListTile({
    super.key,
    required this.reference,
    required this.status,
    required this.date,
    required this.subtitle,
    required this.tracking,
    required this.onTap,
  });
  final String reference;
  final DeliveryStatus status;
  final DateTime date;
  final String subtitle;
  final bool tracking;
  final VoidCallback onTap;
  @override
  Widget build(BuildContext context) => Card(
    child: ListTile(
      leading: Icon(
        tracking ? Icons.location_searching : Icons.local_shipping_outlined,
      ),
      title: Text(reference),
      subtitle: Text(
        '${formatDeliveryDate(date)} · ${status.label}\n$subtitle',
      ),
      isThreeLine: true,
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
    ),
  );
}

class _DeliveryHeader extends StatelessWidget {
  const _DeliveryHeader({
    required this.reference,
    required this.source,
    required this.status,
    required this.date,
  });
  final String reference;
  final DeliverySourceType source;
  final DeliveryStatus status;
  final DateTime date;
  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(reference, style: Theme.of(context).textTheme.headlineSmall),
      const SizedBox(height: 4),
      Text('${source.label} · ${formatDeliveryDate(date)}'),
      const SizedBox(height: 8),
      DoodhStatusPill(
        label: status.label,
        tone: switch (status) {
          DeliveryStatus.delivered => DoodhStatusTone.success,
          DeliveryStatus.failed => DoodhStatusTone.error,
          DeliveryStatus.outForDelivery ||
          DeliveryStatus.arrived => DoodhStatusTone.warning,
          _ => DoodhStatusTone.neutral,
        },
      ),
    ],
  );
}

class _CustomerOtpCard extends StatelessWidget {
  const _CustomerOtpCard({required this.code});

  final String code;

  @override
  Widget build(BuildContext context) => Card(
    child: ListTile(
      leading: const Icon(Icons.password_outlined),
      title: const Text('Delivery OTP'),
      subtitle: const Text(
        'Share this code with the delivery staff at your door.',
      ),
      trailing: Text(
        code,
        style: Theme.of(context).textTheme.titleLarge
            ?.copyWith(fontWeight: FontWeight.w700, letterSpacing: 2),
      ),
    ),
  );
}

class _InfoTile extends StatelessWidget {
  const _InfoTile({
    required this.icon,
    required this.title,
    required this.text,
  });
  final IconData icon;
  final String title;
  final String text;
  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    leading: Icon(icon),
    title: Text(title),
    subtitle: Text(text),
  );
}

class _LiveLocationCard extends StatelessWidget {
  const _LiveLocationCard();

  @override
  Widget build(BuildContext context) => Card(
    color: DoodhColors.mint,
    child: const ListTile(
      leading: Icon(Icons.my_location_outlined, color: DoodhColors.tealDark),
      title: Text('Live tracking is active'),
      subtitle: Text(
        'Your delivery partner is currently sharing an updated location.',
      ),
      trailing: Icon(Icons.circle, size: 12, color: DoodhColors.teal),
    ),
  );
}

class _DeliveryProgress extends StatelessWidget {
  const _DeliveryProgress({required this.status});
  final DeliveryStatus status;

  @override
  Widget build(BuildContext context) {
    final stages = [
      (DeliveryStatus.assigned, 'Assigned', Icons.assignment_ind_outlined),
      (DeliveryStatus.pickedUp, 'Picked up', Icons.inventory_2_outlined),
      (DeliveryStatus.outForDelivery, 'Out for delivery', Icons.route_outlined),
      (DeliveryStatus.arrived, 'Arrived', Icons.location_on_outlined),
      (DeliveryStatus.delivered, 'Delivered', Icons.check_circle_outline),
    ];
    final current = stages.indexWhere((stage) => stage.$1 == status);
    final activeIndex = current < 0
        ? (status == DeliveryStatus.failed ? 3 : 0)
        : current;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Delivery progress',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 14),
            for (var index = 0; index < stages.length; index++)
              _ProgressStage(
                icon: stages[index].$3,
                label: stages[index].$2,
                active: index <= activeIndex,
                current: index == activeIndex,
                last: index == stages.length - 1,
              ),
            if (status == DeliveryStatus.failed)
              const DoodhStatusPill(
                label: 'Delivery needs attention',
                tone: DoodhStatusTone.error,
              ),
          ],
        ),
      ),
    );
  }
}

class _ProgressStage extends StatelessWidget {
  const _ProgressStage({
    required this.icon,
    required this.label,
    required this.active,
    required this.current,
    required this.last,
  });
  final IconData icon;
  final String label;
  final bool active;
  final bool current;
  final bool last;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      SizedBox(
        width: 28,
        child: Icon(
          icon,
          size: 19,
          color: active ? DoodhColors.tealDark : DoodhColors.muted,
        ),
      ),
      Expanded(
        child: Text(
          label,
          style: TextStyle(
            fontWeight: current ? FontWeight.w800 : FontWeight.w500,
            color: active ? DoodhColors.ink : DoodhColors.muted,
          ),
        ),
      ),
      if (!last)
        SizedBox(
          width: 30,
          child: Icon(
            Icons.chevron_right,
            size: 18,
            color: active ? DoodhColors.teal : DoodhColors.line,
          ),
        ),
    ],
  );
}

class _ErrorText extends StatelessWidget {
  const _ErrorText(this.message);
  final String message;
  @override
  Widget build(BuildContext context) => Text(
    message,
    style: TextStyle(color: Theme.of(context).colorScheme.error),
  );
}

Widget _listBody<T>({
  required DeliveryState state,
  required List<T> items,
  required String emptyTitle,
  required String emptyMessage,
  required Future<void> Function() reload,
  required Widget Function(T) itemBuilder,
}) {
  if (state.isLoading && items.isEmpty) {
    return const LoadingStatePanel(message: 'Loading deliveries...');
  }
  if (state.errorMessage != null && items.isEmpty) {
    return ErrorStatePanel(message: state.errorMessage!, onRetry: reload);
  }
  if (items.isEmpty) {
    return EmptyStatePanel(title: emptyTitle, message: emptyMessage);
  }
  return RefreshIndicator(
    onRefresh: reload,
    child: ListView.builder(
      padding: const EdgeInsets.all(12),
      itemCount: items.length,
      itemBuilder: (context, index) => itemBuilder(items[index]),
    ),
  );
}

Widget _missingBody(DeliveryState state, Future<void> Function() reload) =>
    state.isLoading
    ? const LoadingStatePanel(message: 'Loading delivery...')
    : ErrorStatePanel(
        message: state.errorMessage ?? 'Delivery could not be loaded.',
        onRetry: reload,
      );

Future<void> _showOtpDialog(
  BuildContext context,
  WidgetRef ref,
  String id,
) async {
  final input = TextEditingController();
  final code = await showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Verify delivery OTP'),
      content: TextField(
        controller: input,
        keyboardType: TextInputType.number,
        maxLength: 6,
        decoration: const InputDecoration(
          labelText: 'OTP code',
          border: OutlineInputBorder(),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(context, input.text.trim()),
          child: const Text('Verify'),
        ),
      ],
    ),
  );
  input.dispose();
  if (code != null && code.isNotEmpty) {
    final verified = await ref
        .read(deliveryControllerProvider.notifier)
        .verifyOtp(id, code);
    if (verified && context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Delivery completed successfully')),
      );
    }
  }
}

class _NavigateButton extends ConsumerWidget {
  const _NavigateButton({required this.delivery});
  final DeliveryDetails delivery;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Align(
    alignment: Alignment.centerLeft,
    child: OutlinedButton.icon(
      icon: const Icon(Icons.navigation_outlined),
      label: const Text('Navigate'),
      onPressed: () async {
        final destination = deliveryNavigationUri(
          latitude: delivery.destinationLatitude,
          longitude: delivery.destinationLongitude,
          address: delivery.destinationAddress,
        );
        if (destination == null) {
          ScaffoldMessenger.of(context).showSnackBar(
            const SnackBar(
              content: Text('A delivery address is not available.'),
            ),
          );
          return;
        }
        final opened = await ref
            .read(deliveryNavigationLauncherProvider)
            .open(destination);
        if (!context.mounted || opened) return;
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(const SnackBar(content: Text('Unable to open maps.')));
      },
    ),
  );
}

Future<void> _showFailureDialog(
  BuildContext context,
  WidgetRef ref,
  String id,
) async {
  var reason = DeliveryFailureReasons.customerNotAvailable;
  final remarks = TextEditingController();
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: const Text('Mark delivery failed'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            DropdownButtonFormField<String>(
              initialValue: reason,
              decoration: const InputDecoration(
                labelText: 'Failure reason',
                border: OutlineInputBorder(),
              ),
              items: DeliveryFailureReasons.all
                  .map(
                    (value) => DropdownMenuItem<String>(
                      value: value,
                      child: Text(value),
                    ),
                  )
                  .toList(),
              onChanged: (value) {
                if (value != null) setState(() => reason = value);
              },
            ),
            const SizedBox(height: 12),
            TextField(
              controller: remarks,
              decoration: const InputDecoration(
                labelText: 'Remarks (optional)',
                border: OutlineInputBorder(),
              ),
              maxLines: 2,
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Mark failed'),
          ),
        ],
      ),
    ),
  );
  if (confirmed == true) {
    await ref
        .read(deliveryControllerProvider.notifier)
        .fail(
          id,
          reason: reason,
          remarks: remarks.text.trim().isEmpty ? null : remarks.text.trim(),
        );
  }
  remarks.dispose();
}

class _BulkAssignmentChoice {
  const _BulkAssignmentChoice({required this.employeeId, required this.reason});
  final String employeeId;
  final String? reason;
}

class _ManagedDeliveryTile extends StatelessWidget {
  const _ManagedDeliveryTile({
    required this.delivery,
    required this.selected,
    required this.onSelected,
    required this.onTap,
  });
  final DeliveryDetails delivery;
  final bool selected;
  final VoidCallback? onSelected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final detail =
        delivery.sourceType == DeliverySourceType.subscriptionOccurrence
        ? '${delivery.subscriptionSlot?.label ?? 'Slot unavailable'} · ${delivery.quantity?.toStringAsFixed(2) ?? '-'} quantity'
        : delivery.orderSummary == null
        ? 'One-time order'
        : 'Order ${delivery.orderSummary!.orderNumber} · '
              '${delivery.orderSummary!.totalQuantity.toStringAsFixed(2)} quantity';
    return Card(
      child: ListTile(
        leading: onSelected == null
            ? Icon(
                delivery.isTrackingActive
                    ? Icons.location_searching
                    : Icons.local_shipping_outlined,
              )
            : Checkbox(value: selected, onChanged: (_) => onSelected!()),
        title: Text(delivery.referenceNumber),
        subtitle: Text(
          '${formatDeliveryDate(delivery.scheduledDate)} · '
          '${delivery.sourceType.label} · ${delivery.status.label}\n'
          '$detail\n${delivery.assignedEmployeeName ?? 'Unassigned'}',
        ),
        isThreeLine: true,
        trailing: const Icon(Icons.chevron_right),
        onTap: onTap,
      ),
    );
  }
}

Future<_BulkAssignmentChoice?> _showBulkAssignmentDialog(
  BuildContext context,
  List<DeliveryEmployee> employees,
) async {
  String? employeeId;
  final reason = TextEditingController();
  final result = await showDialog<_BulkAssignmentChoice>(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: const Text('Assign selected deliveries'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            DropdownButtonFormField<String>(
              initialValue: null,
              decoration: const InputDecoration(
                labelText: 'Employee',
                border: OutlineInputBorder(),
              ),
              items: employees
                  .map(
                    (item) => DropdownMenuItem(
                      value: item.employeeId,
                      child: Text(item.displayName),
                    ),
                  )
                  .toList(),
              onChanged: (value) => setState(() => employeeId = value),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: reason,
              decoration: const InputDecoration(
                labelText: 'Reason (optional)',
                border: OutlineInputBorder(),
              ),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: employeeId == null
                ? null
                : () => Navigator.pop(
                    context,
                    _BulkAssignmentChoice(
                      employeeId: employeeId!,
                      reason: reason.text.trim().isEmpty
                          ? null
                          : reason.text.trim(),
                    ),
                  ),
            child: const Text('Confirm assignment'),
          ),
        ],
      ),
    ),
  );
  reason.dispose();
  return result;
}

Future<void> _showAssignmentDialog(
  BuildContext context,
  WidgetRef ref,
  DeliveryDetails delivery,
  List<DeliveryEmployee> employees,
) async {
  String? employeeId = delivery.assignedEmployeeId;
  var reason = '';
  final result = await showDialog<String>(
    context: context,
    builder: (context) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: const Text('Assign employee'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            DropdownButtonFormField<String>(
              initialValue:
                  employees.any((item) => item.employeeId == employeeId)
                  ? employeeId
                  : null,
              decoration: const InputDecoration(
                labelText: 'Employee',
                border: OutlineInputBorder(),
              ),
              items: employees
                  .map(
                    (item) => DropdownMenuItem(
                      value: item.employeeId,
                      child: Text(item.displayName),
                    ),
                  )
                  .toList(),
              onChanged: (value) => setState(() => employeeId = value),
            ),
            const SizedBox(height: 12),
            TextField(
              onChanged: (value) => reason = value,
              decoration: const InputDecoration(
                labelText: 'Reason',
                border: OutlineInputBorder(),
              ),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: employeeId == null
                ? null
                : () => Navigator.pop(context, employeeId),
            child: const Text('Assign'),
          ),
        ],
      ),
    ),
  );
  if (result != null) {
    final trimmedReason = reason.trim();
    await ref
        .read(deliveryControllerProvider.notifier)
        .assign(
          delivery.deliveryId,
          result,
          reason: trimmedReason.isEmpty ? null : trimmedReason,
        );
  }
}
