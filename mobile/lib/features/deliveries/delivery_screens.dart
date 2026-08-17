import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'delivery_controller.dart';
import 'delivery_models.dart';

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
                    icon: Icons.location_on_outlined,
                    title: 'Delivery address',
                    text: delivery.destinationAddress,
                  ),
                  _InfoTile(
                    icon: Icons.badge_outlined,
                    title: 'Assigned to',
                    text: delivery.assignedEmployeeName ?? 'Assignment pending',
                  ),
                  if (delivery.isTrackingActive &&
                      delivery.latestLocation != null)
                    _LocationPanel(location: delivery.latestLocation!)
                  else
                    const _InfoTile(
                      icon: Icons.location_searching_outlined,
                      title: 'Live location',
                      text: 'Location is shown only while delivery tracking is active.',
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
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(deliveryControllerProvider.notifier).loadToday(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    return Scaffold(
      appBar: AppBar(title: const Text("Today's deliveries")),
      body: _listBody<DeliveryDetails>(
        state: state,
        items: state.staffDeliveries,
        emptyTitle: 'No assigned deliveries',
        emptyMessage: 'Deliveries assigned for today will appear here.',
        reload: () => ref.read(deliveryControllerProvider.notifier).loadToday(),
        itemBuilder: (delivery) => DeliveryListTile(
          reference: delivery.referenceNumber,
          status: delivery.status,
          date: delivery.scheduledDate,
          subtitle: '${delivery.customerName} · ${delivery.destinationAddress}',
          tracking: delivery.isTrackingActive,
          onTap: () => context.push('/delivery/${delivery.deliveryId}'),
        ),
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
  DeliveryStatus? _status;
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(deliveryControllerProvider.notifier)
      .loadBranch(widget.branchId, date: DateTime.now(), status: _status);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    return Scaffold(
      appBar: AppBar(
        title: Text('Branch ${widget.branchId} deliveries'),
        actions: [
          IconButton(
            tooltip: 'Materialize eligible deliveries',
            icon: const Icon(Icons.sync),
            onPressed: state.isSaving
                ? null
                : () async {
                    await ref
                        .read(deliveryControllerProvider.notifier)
                        .materialize(DateTime.now());
                    await _load();
                  },
          ),
        ],
      ),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.all(12),
            child: DropdownButtonFormField<DeliveryStatus?>(
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
          ),
          Expanded(
            child: _listBody<DeliveryDetails>(
              state: state,
              items: state.managedDeliveries,
              emptyTitle: 'No branch deliveries',
              emptyMessage: 'No deliveries match the selected date and status.',
              reload: _load,
              itemBuilder: (delivery) => DeliveryListTile(
                reference: delivery.referenceNumber,
                status: delivery.status,
                date: delivery.scheduledDate,
                subtitle: delivery.assignedEmployeeName ?? 'Unassigned',
                tracking: delivery.isTrackingActive,
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
    if (delivery.status == DeliveryStatus.arrived &&
        delivery.otpVerifiedAtUtc == null) {
      actions.addAll([
        OutlinedButton.icon(
          icon: const Icon(Icons.sms_outlined),
          label: const Text('Send delivery OTP'),
          onPressed: saving
              ? null
              : () async {
                  final sent = await controller.issueOtp(delivery.deliveryId);
                  if (context.mounted && sent) {
                    ScaffoldMessenger.of(context).showSnackBar(
                      const SnackBar(content: Text('Delivery OTP sent.')),
                    );
                  }
                },
        ),
        FilledButton.icon(
          icon: const Icon(Icons.password_outlined),
          label: const Text('Verify OTP'),
          onPressed: saving
              ? null
              : () => _showOtpDialog(context, ref, delivery.deliveryId),
        ),
      ]);
    }
    if (delivery.status == DeliveryStatus.arrived &&
        delivery.otpVerifiedAtUtc != null) {
      actions.add(
        FilledButton.icon(
          icon: const Icon(Icons.check_circle_outline),
          label: const Text('Complete delivery'),
          onPressed: saving
              ? null
              : () => controller.complete(delivery.deliveryId),
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
      Chip(
        label: Text(status.label),
        avatar: const Icon(Icons.local_shipping_outlined, size: 18),
      ),
    ],
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

class _LocationPanel extends StatelessWidget {
  const _LocationPanel({required this.location});
  final DeliveryLocation location;
  @override
  Widget build(BuildContext context) => _InfoTile(
    icon: Icons.my_location_outlined,
    title: 'Latest live location',
    text:
        '${location.latitude.toStringAsFixed(5)}, ${location.longitude.toStringAsFixed(5)}\nUpdated ${location.recordedAtUtc.toLocal()}',
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
    await ref.read(deliveryControllerProvider.notifier).verifyOtp(id, code);
  }
}

Future<void> _showFailureDialog(
  BuildContext context,
  WidgetRef ref,
  String id,
) async {
  final reason = TextEditingController();
  final remarks = TextEditingController();
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Mark delivery failed'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          TextField(
            controller: reason,
            decoration: const InputDecoration(
              labelText: 'Failure reason',
              border: OutlineInputBorder(),
            ),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: remarks,
            decoration: const InputDecoration(
              labelText: 'Remarks',
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
  );
  if (confirmed == true && reason.text.trim().isNotEmpty) {
    await ref
        .read(deliveryControllerProvider.notifier)
        .fail(
          id,
          reason: reason.text.trim(),
          remarks: remarks.text.trim().isEmpty ? null : remarks.text.trim(),
        );
  }
  reason.dispose();
  remarks.dispose();
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
