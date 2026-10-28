import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';

import 'delivery_controller.dart';
import 'delivery_models.dart';
import 'delivery_navigation.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_controller.dart';
// DeliveryStatus comes from delivery_models above; the refund models'
// same-named enum (staff ticket gating) is hidden to avoid ambiguity.
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_models.dart'
    hide DeliveryStatus;

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
      bottomNavigationBar: const CustomerBottomNavigation(
        currentPath: '/deliveries',
      ),
      body: _listBody<CustomerDelivery>(
        state: state,
        items: state.customerDeliveries,
        emptyTitle: 'No deliveries yet',
        emptyMessage:
            'Confirmed order and subscription deliveries will appear here.',
        reload: () => ref
            .read(deliveryControllerProvider.notifier)
            .loadCustomerDeliveries(),
        itemBuilder: (delivery) => _CustomerDeliveryCard(
          delivery: delivery,
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
  /// Guards the one-shot support-ticket lookup for a failed delivery so a
  /// loading or error state does not re-fire the request on every build.
  /// Reset by pull-to-refresh alongside the delivery reload.
  var _ticketPromptLoaded = false;

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
      bottomNavigationBar: CustomerBottomNavigation(
        currentPath: '/deliveries/${widget.deliveryId}',
      ),
      body: delivery == null
          ? _missingBody(
              state,
              () => ref
                  .read(deliveryControllerProvider.notifier)
                  .loadCustomerDelivery(widget.deliveryId),
            )
          : RefreshIndicator(
              onRefresh: () async {
                _ticketPromptLoaded = false;
                await ref
                    .read(deliveryControllerProvider.notifier)
                    .loadCustomerDelivery(widget.deliveryId);
              },
              child: DoodhPage(
                child: DoodhResponsive(
                  builder: (context, size) {
                    // Phones scan top to bottom: current status, timeline,
                    // then supporting details. Tablets and web get the same
                    // content as two balanced columns on a reading width.
                    // A failed delivery whose support ticket is waiting for
                    // the customer's Refund / Re-Delivery decision. Resolved
                    // from the customer's own ticket list (matched on the
                    // authoritative delivery id) — never inferred from the
                    // delivery status alone, so the prompt appears only when
                    // action is genuinely required.
                    final ticketState = ref.watch(
                      refundReplacementControllerProvider,
                    );
                    RefundReplacementRequest? awaitingTicket;
                    RefundReplacementRequest? resolvedTicket;
                    if (delivery.status == DeliveryStatus.failed) {
                      if (!_ticketPromptLoaded &&
                          !ticketState.isLoading &&
                          ticketState.customerRequests.isEmpty) {
                        _ticketPromptLoaded = true;
                        Future.microtask(
                          () => ref
                              .read(
                                refundReplacementControllerProvider.notifier,
                              )
                              .loadCustomerRequests(),
                        );
                      }
                      for (final request in ticketState.customerRequests) {
                        // Request-level check: a recorded (locked) choice
                        // waiting on staff approval needs no customer action.
                        if (request.deliveryId == widget.deliveryId &&
                            request.isAwaitingChoice) {
                          awaitingTicket = request;
                          break;
                        }
                      }
                      // Resolution status: once the failed-delivery ticket
                      // leaves awaiting-choice (approved/completed/rejected
                      // etc.), surface its current status so the tracking
                      // screen reflects the resolution outcome.
                      for (final request in ticketState.customerRequests) {
                        if (request.deliveryId == widget.deliveryId &&
                            !request.isAwaitingChoice) {
                          resolvedTicket = request;
                          break;
                        }
                      }
                    }
                    Widget primaryColumn() => Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        _TrackingHero(delivery: delivery),
                        const SizedBox(height: DoodhSpacing.md),
                        _TrackingTimeline(delivery: delivery),
                        if (delivery.activeOtp != null) ...[
                          const SizedBox(height: DoodhSpacing.md),
                          _TrackingOtpCard(code: delivery.activeOtp!),
                        ],
                        if (delivery.failureReason != null) ...[
                          const SizedBox(height: DoodhSpacing.md),
                          DoodhInfoBanner(
                            tone: DoodhTone.error,
                            title: 'Delivery failed',
                            message: delivery.failedAt == null
                                ? delivery.failureReason!
                                : '${delivery.failureReason!} · '
                                      'Attempted on '
                                      '${formatDeliveryDateTime(delivery.failedAt!)}',
                          ),
                        ],
                        if (awaitingTicket != null) ...[
                          const SizedBox(height: DoodhSpacing.md),
                          DoodhInfoBanner(
                            tone: DoodhTone.warning,
                            title: 'Action required',
                            message:
                                'Please check Support Ticket section and '
                                'choose your action for this order.',
                            action: TextButton(
                              onPressed: () => context.push(
                                '/refund-replacements/${awaitingTicket!.requestId}',
                              ),
                              child: const Text('View ticket'),
                            ),
                          ),
                        ],
                      ],
                    );

                    Widget secondaryColumn() => Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        DoodhSectionCard(
                          title: 'Delivery details',
                          icon: Icons.local_shipping_outlined,
                          children: [
                            DoodhInfoTile(
                              icon: Icons.event_outlined,
                              title: 'Scheduled for',
                              subtitle:
                                  '${formatDeliveryDate(delivery.scheduledDate)} · '
                                  '${delivery.sourceType.label}',
                            ),
                            DoodhInfoTile(
                              icon: Icons.location_on_outlined,
                              title: 'Delivery address',
                              subtitle: delivery.destinationAddress,
                            ),
                            _TrackingItemsTile(delivery: delivery),
                            _TrackingPersonTile(delivery: delivery),
                            if (delivery.isTrackingActive &&
                                delivery.latestLocation != null)
                              const _TrackingLiveLocationTile()
                            else
                              const DoodhInfoTile(
                                icon: Icons.location_searching_outlined,
                                title: 'Live location',
                                subtitle: 'Location becomes available while your delivery is on the way.',
                              ),
                          ],
                        ),
                        // Doorstep decisions happen before handover: once the
                        // delivery is delivered, failed, or cancelled the test
                        // workflow is over, so the entry point is hidden
                        // instead of leading to dead customer actions.
                        if (delivery.status != DeliveryStatus.delivered &&
                            delivery.status != DeliveryStatus.failed &&
                            delivery.status != DeliveryStatus.cancelled) ...[
                          const SizedBox(height: DoodhSpacing.md),
                          DoodhSectionCard(
                            title: 'Milk test',
                            icon: Icons.science_outlined,
                            subtitle:
                                'Check the dedicated doorstep test workflow',
                            children: [
                              DoodhActionTile(
                                icon: Icons.science_outlined,
                                title: 'Doorstep milk test',
                                subtitle:
                                    'Open the test details and customer actions',
                                onTap: () => context.push(
                                  '/deliveries/${widget.deliveryId}/milk-test'
                                  '?source=${delivery.sourceType.name}',
                                ),
                              ),
                            ],
                          ),
                        ],
                        const SizedBox(height: DoodhSpacing.md),
                        DoodhSectionCard(
                          title: 'Support',
                          icon: Icons.support_agent_outlined,
                          children: [
                            // Once the failed-delivery ticket resolves, show
                            // its current status inline so customers see the
                            // outcome without leaving tracking.
                            if (resolvedTicket != null)
                              DoodhInfoTile(
                                icon: Icons.assignment_turned_in_outlined,
                                title: 'Refund/replacement status',
                                subtitle:
                                    '${resolvedTicket.status.label}'
                                    '${resolvedTicket.customerChoice != null ? ' · ${resolvedTicket.customerChoice!.label}' : ''}',
                              ),
                            DoodhActionTile(
                              icon: Icons.assignment_return_outlined,
                              title: 'Refund or replacement',
                              subtitle: resolvedTicket != null
                                  ? 'Ticket ${resolvedTicket.requestNumber} · ${resolvedTicket.status.label}'
                                  : 'Check backend-confirmed availability for this delivery',
                              onTap: () => context.push(
                                '/deliveries/${widget.deliveryId}/refund-replacement'
                                '?source=${delivery.sourceType.name}',
                              ),
                            ),
                          ],
                        ),
                      ],
                    );

                    final content = size == DoodhWindowSize.compact
                        ? Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              primaryColumn(),
                              const SizedBox(height: DoodhSpacing.md),
                              secondaryColumn(),
                            ],
                          )
                        : Row(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Expanded(child: primaryColumn()),
                              const SizedBox(width: DoodhSpacing.lg),
                              Expanded(child: secondaryColumn()),
                            ],
                          );
                    return ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      padding: const EdgeInsets.only(bottom: DoodhSpacing.xl),
                      children: [content],
                    );
                  },
                ),
              ),
            ),
    );
  }
}

class DeliveryInspectionScreen extends ConsumerStatefulWidget {
  const DeliveryInspectionScreen({super.key, required this.deliveryId});

  final String deliveryId;

  @override
  ConsumerState<DeliveryInspectionScreen> createState() =>
      _DeliveryInspectionScreenState();
}

class _DeliveryInspectionScreenState
    extends ConsumerState<DeliveryInspectionScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(deliveryControllerProvider.notifier)
      .loadInspectionDelivery(widget.deliveryId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    final delivery = state.selectedDelivery?.deliveryId == widget.deliveryId
        ? state.selectedDelivery
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Delivery inspection')),
      body: delivery == null
          ? _missingBody(state, _load)
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
                children: [
                  _DeliveryHeader(
                    reference: delivery.referenceNumber,
                    source: delivery.sourceType,
                    status: delivery.status,
                    date: delivery.scheduledDate,
                  ),
                  const SizedBox(height: 12),
                  DoodhInfoTile(
                    icon: Icons.person_outline,
                    title: delivery.customerName,
                    subtitle: delivery.customerMobile,
                  ),
                  DoodhInfoTile(
                    icon: Icons.delivery_dining_outlined,
                    title: 'Delivery handled by',
                    subtitle:
                        delivery.assignedEmployeeName ??
                        (delivery.assignedEmployeeId != null
                            ? 'Employee ${delivery.assignedEmployeeId}'
                            : 'Not assigned'),
                  ),
                  DoodhInfoTile(
                    icon: Icons.location_on_outlined,
                    title: 'Destination',
                    subtitle: delivery.destinationAddress,
                  ),
                  _NavigateButton(delivery: delivery),
                  if (delivery.deliveryInstructions != null)
                    DoodhInfoTile(
                      icon: Icons.notes_outlined,
                      title: 'Instructions',
                      subtitle: delivery.deliveryInstructions!,
                    ),
                  if (delivery.orderSummary != null) ...[
                    const SizedBox(height: 8),
                    DoodhSectionHeader(title: 'Linked order'),
                    DoodhInfoTile(
                      icon: Icons.receipt_long_outlined,
                      title: delivery.orderSummary!.orderNumber,
                      subtitle: delivery.orderSummary!.items.join(', '),
                    ),
                  ],
                  const SizedBox(height: 8),
                  DoodhSectionHeader(title: 'Timeline'),
                  DoodhInfoTile(
                    icon: Icons.event_outlined,
                    title: 'Scheduled',
                    subtitle: formatDeliveryDateTime(delivery.scheduledDate),
                  ),
                  // Every lifecycle step carries the server-provided date
                  // and time; steps that have not happened yet stay hidden
                  // instead of showing a placeholder. Remarks carry no
                  // timestamp on the payload, so they render as text only.
                  if (delivery.assignedAt != null)
                    DoodhInfoTile(
                      icon: Icons.assignment_ind_outlined,
                      title: 'Assigned',
                      subtitle: formatDeliveryDateTime(delivery.assignedAt!),
                    ),
                  if (delivery.pickedUpAt != null)
                    DoodhInfoTile(
                      icon: Icons.inventory_2_outlined,
                      title: 'Picked up',
                      subtitle: formatDeliveryDateTime(delivery.pickedUpAt!),
                    ),
                  if (delivery.outForDeliveryAt != null)
                    DoodhInfoTile(
                      icon: Icons.route_outlined,
                      title: 'Out for delivery',
                      subtitle: formatDeliveryDateTime(
                        delivery.outForDeliveryAt!,
                      ),
                    ),
                  if (delivery.arrivedAt != null)
                    DoodhInfoTile(
                      icon: Icons.location_on_outlined,
                      title: 'Arrived',
                      subtitle: formatDeliveryDateTime(delivery.arrivedAt!),
                    ),
                  if (delivery.completedAt != null)
                    DoodhInfoTile(
                      icon: Icons.check_circle_outline,
                      title: 'Completed',
                      subtitle: formatDeliveryDateTime(delivery.completedAt!),
                    ),
                  if (delivery.failedAt != null)
                    DoodhInfoTile(
                      icon: Icons.error_outline,
                      title: 'Failed',
                      subtitle: delivery.failureReason == null
                          ? formatDeliveryDateTime(delivery.failedAt!)
                          : '${delivery.failureReason!} · '
                                '${formatDeliveryDateTime(delivery.failedAt!)}',
                    ),
                  if (delivery.remarks != null)
                    DoodhInfoTile(
                      icon: Icons.comment_outlined,
                      title: 'Remarks',
                      subtitle: delivery.remarks!,
                    ),
                  if (state.errorMessage != null)
                    _ErrorText(state.errorMessage!),
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

  DateTime _date = indiaNow();

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() =>
      ref.read(deliveryControllerProvider.notifier).loadToday(date: _date);

  Future<void> _selectStatus(DeliveryStatus? status) => ref
      .read(deliveryControllerProvider.notifier)
      .selectStaffStatus(status, date: _date);

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

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    final isToday = formatDeliveryDate(_date) == formatDeliveryDate(indiaNow());
    return Scaffold(
      appBar: AppBar(
        title: Text(isToday ? "Today's deliveries" : 'Deliveries'),
        actions: [
          IconButton(
            tooltip: 'Choose date',
            icon: const Icon(Icons.calendar_today_outlined),
            onPressed: state.isLoading ? null : _chooseDate,
          ),
        ],
      ),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  isToday
                      ? "Today's deliveries"
                      : 'Deliveries for ${formatDeliveryDate(_date)}',
                  style: Theme.of(context).textTheme.headlineSmall,
                ),
                const SizedBox(height: 8),
                DropdownButtonFormField<DeliveryStatus?>(
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
              ],
            ),
          ),
          Expanded(
            child: _listBody<DeliveryDetails>(
              state: state,
              items: state.staffDeliveries,
              emptyTitle: state.staffStatus == null
                  ? (isToday
                        ? 'No deliveries today'
                        : 'No deliveries on ${formatDeliveryDate(_date)}')
                  : 'No ${state.staffStatus!.label.toLowerCase()} deliveries',
              emptyMessage: isToday
                  ? 'Assigned deliveries for today will appear here.'
                  : 'Assigned deliveries for the selected date will appear here.',
              reload: _load,
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
              padding: DoodhSpacing.pagePadding,
              children: [
                _DeliveryHeader(
                  reference: delivery.referenceNumber,
                  source: delivery.sourceType,
                  status: delivery.status,
                  date: delivery.scheduledDate,
                ),
                const SizedBox(height: 12),
                DoodhInfoTile(
                  icon: Icons.person_outline,
                  title: delivery.customerName,
                  subtitle: delivery.customerMobile,
                ),
                DoodhInfoTile(
                  icon: Icons.location_on_outlined,
                  title: 'Destination',
                  subtitle: delivery.destinationAddress,
                ),
                _NavigateButton(delivery: delivery),
                if (delivery.deliveryInstructions != null)
                  DoodhInfoTile(
                    icon: Icons.notes_outlined,
                    title: 'Instructions',
                    subtitle: delivery.deliveryInstructions!,
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

/// Admin-centre catalogue look shared by the delivery-management screen:
/// light-grey page, white section cards and the panel-green primary action.
const _deliveryAdminPage = Color(0xFFF8F9FA);
const _deliveryAdminAction = Color(0xFF198754);
const _deliveryAdminCardBorder = Color(0xFFE9ECEF);

class DeliveryManagementScreen extends ConsumerStatefulWidget {
  const DeliveryManagementScreen({super.key, required this.branchId});
  final int branchId;
  @override
  ConsumerState<DeliveryManagementScreen> createState() =>
      _DeliveryManagementScreenState();
}

class _DeliveryManagementScreenState
    extends ConsumerState<DeliveryManagementScreen> {
  late int _branchId = widget.branchId;
  DateTime _date = indiaNow();
  DeliveryStatus? _status;
  DeliverySourceType? _sourceType;
  SubscriptionDeliverySlot? _slot;

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  @override
  void didUpdateWidget(DeliveryManagementScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.branchId != widget.branchId) {
      _branchId = widget.branchId;
      Future.microtask(_load);
    }
  }

  Future<void> _load() async {
    final controller = ref.read(deliveryControllerProvider.notifier);
    await controller.loadBranch(
      _branchId,
      date: _date,
      status: _status,
      sourceType: _sourceType,
      slot: _slot,
    );
    await controller.loadMissedOccurrences(_branchId);
  }

  /// Branch options the signed-in manager may switch between: resolved
  /// branch metadata when available, otherwise the raw assigned branch ids.
  /// The route branch is always included so the selector stays valid.
  List<AuthUserBranchInfo> _branchOptions(AuthSession? session) {
    final user = session?.user;
    final options = <AuthUserBranchInfo>[
      if (user != null && user.branchDetails.isNotEmpty)
        ...user.branchDetails
      else
        for (final id in user?.branchIds ?? const <int>[])
          AuthUserBranchInfo(id: id, code: '', name: 'Branch $id'),
    ];
    if (!options.any((branch) => branch.id == _branchId)) {
      options.add(
        AuthUserBranchInfo(id: _branchId, code: '', name: 'Branch $_branchId'),
      );
    }
    return options;
  }

  Future<void> _goToday() async {
    final today = indiaNow();
    if (formatDeliveryDate(_date) == formatDeliveryDate(today)) return;
    setState(() => _date = today);
    await _load();
  }

  /// Whether any sheet-hosted filter narrows the list (the date stays on the
  /// AppBar calendar icon and never counts here).
  bool get _hasActiveFilters =>
      _status != null || _sourceType != null || _slot != null;

  bool get _isOffToday =>
      formatDeliveryDate(_date) != formatDeliveryDate(indiaNow());

  void _resetFilters() {
    setState(() {
      _status = null;
      _sourceType = null;
      _slot = null;
    });
    _load();
  }

  /// Records a manager-confirmed miss for a past-due occurrence with no
  /// operational delivery. Uses the shared failure dialog without proof
  /// (the visit was never attempted); the server freezes a zero deduction
  /// because no service was provided. The queue refreshes afterwards.
  Future<void> _confirmMissedOccurrence(
    BuildContext context,
    WidgetRef ref,
    MissedOccurrence occurrence,
  ) async {
    final input = await showDeliveryFailDialog(
      context,
      requiresProof: false,
    );
    if (input == null || !context.mounted) return;
    final confirmed = await ref
        .read(deliveryControllerProvider.notifier)
        .failMissedOccurrence(
          _branchId,
          occurrence.occurrenceId,
          reason: input.reason,
          remarks: input.remarks,
        );
    if (!context.mounted) return;
    if (confirmed) {
      await _load();
      if (!context.mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Missed delivery recorded.')),
      );
    }
  }

  /// Branch, status, type and slot filters live in this sheet behind the
  /// AppBar filter icon. The front screen keeps only the date context (the
  /// subtitle plus the calendar icon) and the deliveries list.
  Future<void> _openFilters() async {
    final session = ref.read(sessionControllerProvider).session;
    final branchOptions = _branchOptions(session);
    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      builder: (sheetContext) => StatefulBuilder(
        builder: (sheetContext, setSheetState) {
          final canFilterSlot =
              _sourceType == null ||
              _sourceType == DeliverySourceType.subscriptionOccurrence;
          return SafeArea(
            child: SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(
                    'Filters',
                    style: Theme.of(sheetContext).textTheme.titleMedium
                        ?.copyWith(fontWeight: FontWeight.w800, fontSize: 16),
                  ),
                  const SizedBox(height: 12),
                  if (branchOptions.length > 1)
                    Padding(
                      padding: const EdgeInsets.only(bottom: 8),
                      child: DropdownButtonFormField<int>(
                        initialValue: _branchId,
                        decoration: const InputDecoration(
                          labelText: 'Branch',
                          border: OutlineInputBorder(),
                        ),
                        items: [
                          for (final branch in branchOptions)
                            DropdownMenuItem(
                              value: branch.id,
                              child: Text(
                                branch.code.isEmpty
                                    ? branch.name
                                    : '${branch.name} (${branch.code})',
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                        ],
                        onChanged: (value) async {
                          if (value == null || value == _branchId) return;
                          setSheetState(() => _branchId = value);
                          ref
                              .read(deliveryControllerProvider.notifier)
                              .clearManagedSelection();
                          await _load();
                        },
                      ),
                    ),
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
                      setSheetState(() => _status = value);
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
                            setSheetState(() {
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
                        child:
                            DropdownButtonFormField<SubscriptionDeliverySlot?>(
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
                                      setSheetState(() => _slot = value);
                                      _load();
                                    }
                                  : null,
                            ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),
                  TextButton.icon(
                    onPressed: _hasActiveFilters
                        ? () {
                            setSheetState(() {
                              _status = null;
                              _sourceType = null;
                              _slot = null;
                            });
                            _resetFilters();
                          }
                        : null,
                    icon: const Icon(Icons.filter_alt_off_outlined, size: 18),
                    label: const Text('Reset filters'),
                  ),
                ],
              ),
            ),
          );
        },
      ),
    );
  }

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
    final session = ref.watch(sessionControllerProvider).session;
    final branchOptions = _branchOptions(session);
    String branchLabel(int id) {
      for (final branch in branchOptions) {
        if (branch.id == id) return branch.name;
      }
      return 'Branch $id';
    }

    final branchName = branchLabel(_branchId);
    return Scaffold(
      backgroundColor: _deliveryAdminPage,
      appBar: AppBar(
        backgroundColor: _deliveryAdminPage,
        title: Text('$branchName deliveries'),
        actions: [
          IconButton(
            tooltip: 'Choose date',
            icon: const Icon(Icons.calendar_today_outlined),
            onPressed: state.isSaving ? null : _chooseDate,
          ),
          IconButton(
            key: const ValueKey('delivery-management-filters-action'),
            tooltip: 'Filters',
            icon: Badge(
              isLabelVisible: _hasActiveFilters,
              child: const Icon(Icons.tune_outlined),
            ),
            onPressed: state.isSaving ? null : _openFilters,
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
      body: _managementBody(
        context,
        state,
        branchName,
        readyCount,
        selectedCount,
      ),
    );
  }

  /// Catalogue Admin Centre page: grey canvas, centred readable column,
  /// headline + subtitle header, white filter card and a white deliveries
  /// card with divider-separated rows and the panel-green assign action.
  Widget _managementBody(
    BuildContext context,
    DeliveryState state,
    String branchName,
    int readyCount,
    int selectedCount,
  ) {
    if (state.isLoading && state.managedDeliveries.isEmpty) {
      return const LoadingStatePanel(message: 'Loading branch deliveries');
    }
    if (state.errorMessage != null && state.managedDeliveries.isEmpty) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    }
    final theme = Theme.of(context);
    return RefreshIndicator(
      onRefresh: _load,
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: DoodhContentMax.wide),
          child: ListView(
            padding: DoodhSpacing.pagePadding,
            children: [
              Text(
                'Delivery management',
                style: theme.textTheme.headlineSmall?.copyWith(
                  fontSize: 24,
                  fontWeight: FontWeight.w800,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                'Manage branch deliveries for ${formatDeliveryDate(_date)}',
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: DoodhColors.muted,
                  fontSize: 14,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                '${state.managedDeliveries.length} deliveries · $selectedCount selected',
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: DoodhColors.muted,
                ),
              ),
              const SizedBox(height: DoodhSpacing.sm),
              if (readyCount > 0) ...[
                _deliveryAdminCard(
                  child: Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    alignment: WrapAlignment.spaceBetween,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    children: [
                      Text(
                        '$selectedCount selected',
                        style: const TextStyle(fontWeight: FontWeight.w700),
                      ),
                      Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
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
                        ],
                      ),
                      FilledButton.icon(
                        icon: const Icon(Icons.assignment_ind_outlined),
                        label: const Text('Assign Selected'),
                        style: FilledButton.styleFrom(
                          backgroundColor: _deliveryAdminAction,
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(8),
                          ),
                          minimumSize: const Size(0, 40),
                          padding: const EdgeInsets.symmetric(horizontal: 12),
                        ),
                        onPressed: selectedCount == 0 || state.isSaving
                            ? null
                            : () => _assignSelected(state),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: DoodhSpacing.sm),
              ],
              _MissedOccurrenceCard(
                missed: state.missedOccurrences,
                isSaving: state.isSaving,
                onConfirm: (occurrence) =>
                    _confirmMissedOccurrence(context, ref, occurrence),
              ),
              const SizedBox(height: DoodhSpacing.sm),
              _deliveryAdminCard(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            'Deliveries (${state.managedDeliveries.length})',
                            style: const TextStyle(
                              fontWeight: FontWeight.w800,
                              fontSize: 16,
                            ),
                          ),
                        ),
                        if (_isOffToday)
                          TextButton(
                            onPressed: state.isSaving ? null : _goToday,
                            child: const Text('Today'),
                          ),
                      ],
                    ),
                    const SizedBox(height: DoodhSpacing.sm),
                    if (state.managedDeliveries.isEmpty)
                      const EmptyStatePanel(
                        title: 'No branch deliveries',
                        message: 'No deliveries match the selected filters.',
                      )
                    else
                      for (
                        var index = 0;
                        index < state.managedDeliveries.length;
                        index++
                      ) ...[
                        if (index > 0) const Divider(height: 1),
                        _ManagedDeliveryTile(
                          delivery: state.managedDeliveries[index],
                          selected: state.selectedManagedDeliveryIds.contains(
                            state.managedDeliveries[index].deliveryId,
                          ),
                          onSelected:
                              state.managedDeliveries[index].status ==
                                  DeliveryStatus.readyForAssignment
                              ? () => ref
                                    .read(deliveryControllerProvider.notifier)
                                    .toggleManagedDelivery(
                                      state.managedDeliveries[index].deliveryId,
                                    )
                              : null,
                          onTap: () => context.push(
                            '/delivery-management/${state.managedDeliveries[index].deliveryId}',
                          ),
                        ),
                      ],
                  ],
                ),
              ),
              const SizedBox(height: DoodhSpacing.xxxl),
            ],
          ),
        ),
      ),
    );
  }

  /// White Admin Centre section card (matches the catalogue management
  /// products card).
  Widget _deliveryAdminCard({required Widget child}) => Card(
    elevation: 1,
    color: Colors.white,
    margin: EdgeInsets.zero,
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(12),
      side: const BorderSide(color: _deliveryAdminCardBorder),
    ),
    child: Padding(
      padding: const EdgeInsets.all(DoodhSpacing.md),
      child: child,
    ),
  );
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
              padding: DoodhSpacing.pagePadding,
              children: [
                _DeliveryHeader(
                  reference: delivery.referenceNumber,
                  source: delivery.sourceType,
                  status: delivery.status,
                  date: delivery.scheduledDate,
                ),
                const SizedBox(height: 12),
                DoodhInfoTile(
                  icon: Icons.person_outline,
                  title: delivery.customerName,
                  subtitle: delivery.destinationAddress,
                ),
                DoodhInfoTile(
                  icon: Icons.badge_outlined,
                  title: 'Assigned employee',
                  subtitle: delivery.assignedEmployeeName ?? 'Unassigned',
                ),
                _DeliveryItemSection(delivery: delivery),
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
                const SizedBox(height: 20),
                _BatchAllocationSection(
                  delivery: delivery,
                  saving: state.isSaving,
                ),
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
              : () => _showFailureDialog(context, ref, delivery),
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

class _CustomerDeliveryCard extends StatelessWidget {
  const _CustomerDeliveryCard({required this.delivery, required this.onTap});

  final CustomerDelivery delivery;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => DoodhCard(
    onTap: onTap,
    semanticLabel:
        '${delivery.trackingReference}, ${delivery.status.label}, '
        '${formatDeliveryDate(delivery.scheduledDate)}, '
        '${delivery.destinationAddress}',
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                delivery.trackingReference,
                style: Theme.of(context).textTheme.titleLarge,
              ),
            ),
            DoodhStatusPill(
              label: delivery.status.label,
              tone: _deliveryTone(delivery.status),
            ),
          ],
        ),
        const SizedBox(height: DoodhSpacing.sm),
        Text(formatDeliveryDate(delivery.scheduledDate)),
        const SizedBox(height: DoodhSpacing.xs),
        Text(
          delivery.destinationAddress,
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          style: Theme.of(context).textTheme.bodySmall,
        ),
        const Divider(height: DoodhSpacing.lg),
        Row(
          children: [
            Icon(
              delivery.isTrackingActive
                  ? Icons.location_searching
                  : Icons.local_shipping_outlined,
              size: 18,
            ),
            const SizedBox(width: DoodhSpacing.xs),
            Expanded(
              child: Text(
                delivery.isTrackingActive
                    ? 'Live tracking is active'
                    : 'View delivery progress and support',
              ),
            ),
            const Icon(Icons.chevron_right),
          ],
        ),
      ],
    ),
  );
}

DoodhStatusTone _deliveryTone(DeliveryStatus status) => switch (status) {
  DeliveryStatus.delivered => DoodhStatusTone.success,
  DeliveryStatus.failed => DoodhStatusTone.error,
  DeliveryStatus.outForDelivery ||
  DeliveryStatus.arrived => DoodhStatusTone.warning,
  _ => DoodhStatusTone.neutral,
};

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

// ---------------------------------------------------------------------------
// Customer order tracking / delivery details
// ---------------------------------------------------------------------------

/// Reference-styled tracking hero: order identity plus the current server
/// status with a short factual explanation. Never implies progress that the
/// [DeliveryStatus] does not report.
class _TrackingHero extends StatelessWidget {
  const _TrackingHero({required this.delivery});

  final CustomerDelivery delivery;

  @override
  Widget build(BuildContext context) {
    final explanation = switch (delivery.status) {
      DeliveryStatus.readyForAssignment =>
        'Your delivery is confirmed and waiting to be assigned.',
      DeliveryStatus.assigned =>
        'A delivery partner has been assigned to your delivery.',
      DeliveryStatus.pickedUp =>
        'Your delivery has been picked up from the branch.',
      DeliveryStatus.outForDelivery =>
        'Your delivery is on the way to your address.',
      DeliveryStatus.arrived =>
        'Your delivery partner has reached your address.',
      DeliveryStatus.delivered => 'Your delivery has been completed.',
      DeliveryStatus.failed => 'This delivery attempt could not be completed.',
      DeliveryStatus.cancelled =>
        'This delivery was cancelled before pickup and will not be attempted.',
      DeliveryStatus.unknown =>
        'The current delivery status is being updated by the branch.',
    };
    final (statusIcon, _) = _customerStatusVisuals(delivery.status);
    return Semantics(
      container: true,
      liveRegion: true,
      label:
          'Delivery ${delivery.trackingReference}. Status: ${delivery.status.label}. '
          '$explanation',
      child: DoodhCard(
        color: DoodhColors.tealDark,
        padding: const EdgeInsets.all(DoodhSpacing.lg),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    delivery.trackingReference,
                    style: Theme.of(context).textTheme.headlineSmall
                        ?.copyWith(color: Colors.white),
                  ),
                ),
                DoodhChip(
                  label: delivery.sourceType.label,
                  tone: DoodhTone.info,
                ),
              ],
            ),
            const SizedBox(height: DoodhSpacing.xs),
            Text(
              'Scheduled for ${formatDeliveryDate(delivery.scheduledDate)}',
              style: Theme.of(context).textTheme.bodyMedium
                  ?.copyWith(color: Colors.white70),
            ),
            const Divider(color: Colors.white24, height: DoodhSpacing.lg),
            Row(
              children: [
                Container(
                  width: 52,
                  height: 52,
                  decoration: const BoxDecoration(
                    color: DoodhColors.mint,
                    shape: BoxShape.circle,
                  ),
                  child: ExcludeSemantics(
                    child: Icon(statusIcon, color: DoodhColors.tealDark),
                  ),
                ),
                const SizedBox(width: DoodhSpacing.md),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        delivery.status.label,
                        style: Theme.of(context).textTheme.titleLarge
                            ?.copyWith(color: Colors.white),
                      ),
                      const SizedBox(height: DoodhSpacing.xs),
                      Text(
                        explanation,
                        style: Theme.of(context).textTheme.bodyMedium
                            ?.copyWith(color: Colors.white70),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            if (delivery.status == DeliveryStatus.delivered &&
                delivery.completedAt != null) ...[
              const SizedBox(height: DoodhSpacing.md),
              Text(
                'Completed on ${formatDeliveryDateTime(delivery.completedAt!)}',
                style: Theme.of(context).textTheme.bodySmall
                    ?.copyWith(color: Colors.white70),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// Timeline of the real delivery stages. Only the server-provided timestamps
/// (assignedAt / completedAt / failedAt on the customer payload) are shown —
/// nothing is inferred or fabricated.
class _TrackingTimeline extends StatelessWidget {
  const _TrackingTimeline({required this.delivery});

  final CustomerDelivery delivery;

  @override
  Widget build(BuildContext context) {
    const stages = [
      (DeliveryStatus.assigned, 'Assigned', Icons.assignment_ind_outlined),
      (DeliveryStatus.pickedUp, 'Picked up', Icons.inventory_2_outlined),
      (DeliveryStatus.outForDelivery, 'Out for delivery', Icons.route_outlined),
      (DeliveryStatus.arrived, 'Arrived', Icons.location_on_outlined),
      (DeliveryStatus.delivered, 'Delivered', Icons.check_circle_outline),
    ];
    final current = stages.indexWhere((stage) => stage.$1 == delivery.status);
    final activeIndex = current < 0
        ? (delivery.status == DeliveryStatus.failed ? 3 : 0)
        : current;
    return DoodhSectionCard(
      title: 'Delivery progress',
      icon: Icons.timeline_outlined,
      children: [
        for (var index = 0; index < stages.length; index++)
          _TrackingStageRow(
            icon: stages[index].$3,
            label: stages[index].$2,
            state:
                delivery.status == DeliveryStatus.failed && index == activeIndex
                ? _TrackingStageState.failed
                : index < activeIndex
                ? _TrackingStageState.completed
                : index == activeIndex
                ? _TrackingStageState.current
                : _TrackingStageState.pending,
            last: index == stages.length - 1,
          ),
        if (delivery.status == DeliveryStatus.failed)
          Padding(
            padding: const EdgeInsets.only(top: DoodhSpacing.sm),
            child: DoodhStatusPill(
              label: 'Delivery needs attention',
              tone: DoodhStatusTone.error,
            ),
          ),
      ],
    );
  }
}

enum _TrackingStageState { completed, current, pending, failed }

/// One timeline row with a connector spine, kept screen-reader friendly:
/// each stage exposes its label and completion state as semantics.
class _TrackingStageRow extends StatelessWidget {
  const _TrackingStageRow({
    required this.icon,
    required this.label,
    required this.state,
    required this.last,
  });

  final IconData icon;
  final String label;
  final _TrackingStageState state;
  final bool last;

  @override
  Widget build(BuildContext context) {
    final color = switch (state) {
      _TrackingStageState.completed => DoodhColors.tealDark,
      _TrackingStageState.current => DoodhColors.tealDark,
      _TrackingStageState.failed => DoodhColors.coral,
      _TrackingStageState.pending => DoodhColors.muted,
    };
    final stateLabel = switch (state) {
      _TrackingStageState.completed => 'completed',
      _TrackingStageState.current => 'current step',
      _TrackingStageState.failed => 'not completed, delivery failed',
      _TrackingStageState.pending => 'pending',
    };
    final mark = switch (state) {
      _TrackingStageState.completed => Icons.check,
      _TrackingStageState.failed => Icons.close,
      _ => null,
    };
    return Semantics(
      container: true,
      label: '$label: $stateLabel',
      child: ExcludeSemantics(
        child: IntrinsicHeight(
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Column(
                children: [
                  Container(
                    width: 28,
                    height: 28,
                    decoration: BoxDecoration(
                      color: state == _TrackingStageState.pending
                          ? Colors.transparent
                          : color.withValues(alpha: 0.14),
                      shape: BoxShape.circle,
                      border: Border.all(color: color, width: 1.6),
                    ),
                    child: Icon(
                      mark ?? icon,
                      size: mark == null ? 16 : 16,
                      color: color,
                    ),
                  ),
                  if (!last)
                    Expanded(
                      child: Container(
                        width: 2,
                        color: state == _TrackingStageState.completed
                            ? DoodhColors.teal
                            : DoodhColors.line,
                      ),
                    ),
                ],
              ),
              const SizedBox(width: DoodhSpacing.sm),
              Expanded(
                child: Padding(
                  padding: const EdgeInsets.only(
                    top: 4,
                    bottom: DoodhSpacing.md,
                  ),
                  child: Text(
                    label,
                    style: TextStyle(
                      fontWeight: state == _TrackingStageState.current
                          ? FontWeight.w800
                          : FontWeight.w500,
                      color: state == _TrackingStageState.pending
                          ? DoodhColors.muted
                          : DoodhColors.ink,
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// In-app delivery OTP presentation. The code comes from the customer payload
/// (`activeOtp`) exactly as before; generation/verification logic is untouched.
class _TrackingOtpCard extends StatelessWidget {
  const _TrackingOtpCard({required this.code});

  final String code;

  @override
  Widget build(BuildContext context) => DoodhCard(
    color: DoodhColors.warningSurface,
    child: Row(
      children: [
        const ExcludeSemantics(
          child: Icon(Icons.password_outlined, color: DoodhColors.tealDark),
        ),
        const SizedBox(width: DoodhSpacing.md),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Delivery OTP',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              const SizedBox(height: DoodhSpacing.xs),
              Text(
                'Share this code with the delivery staff at your door.',
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ],
          ),
        ),
        Semantics(
          container: true,
          label: 'Delivery OTP code $code',
          child: ExcludeSemantics(
            child: Text(
              code,
              style: Theme.of(context).textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w800,
                letterSpacing: 2,
                color: DoodhColors.tealDark,
              ),
            ),
          ),
        ),
      ],
    ),
  );
}

/// Assigned delivery partner shown with the display name the customer API
/// already exposes — no internal IDs or contact details.
class _TrackingPersonTile extends StatelessWidget {
  const _TrackingPersonTile({required this.delivery});

  final CustomerDelivery delivery;

  @override
  Widget build(BuildContext context) => DoodhInfoTile(
    icon: Icons.delivery_dining_outlined,
    title: 'Delivery handled by',
    subtitle: delivery.assignedEmployeeName ?? 'Assignment pending',
  );
}

/// Item, quantity and amount for the tracked delivery. Subscription
/// occurrences render the frozen product snapshot; one-time orders render
/// the order summary items plus the payable total. Hidden when neither
/// source carries item data (older cached payloads).
class _TrackingItemsTile extends StatelessWidget {
  const _TrackingItemsTile({required this.delivery});

  final CustomerDelivery delivery;

  @override
  Widget build(BuildContext context) {
    final summary = delivery.orderSummary;
    final product = delivery.subscriptionProductName;
    final qty = delivery.quantity;
    if (summary != null) {
      final lines = summary.items.join('\n');
      final total =
          'Qty ${summary.totalQuantity.toStringAsFixed(2)} · ₹${summary.totalAmount.toStringAsFixed(2)}';
      return DoodhInfoTile(
        icon: Icons.shopping_basket_outlined,
        title: 'Items',
        subtitle: lines.isEmpty ? total : '$lines\n$total',
      );
    }
    if (product != null && product.isNotEmpty && qty != null) {
      final unit = delivery.subscriptionUnitOfMeasure;
      final qtyText = unit == null || unit.isEmpty
          ? qty.toStringAsFixed(2)
          : '${qty.toStringAsFixed(2)} $unit';
      return DoodhInfoTile(
        icon: Icons.shopping_basket_outlined,
        title: 'Items',
        subtitle: '$product x $qtyText',
      );
    }
    return const SizedBox.shrink();
  }
}

/// Live tracking availability. Coordinates are intentionally NOT rendered for
/// customers; the active/unavailable states match the previous behaviour.
class _TrackingLiveLocationTile extends StatelessWidget {
  const _TrackingLiveLocationTile();

  @override
  Widget build(BuildContext context) => DoodhInfoTile(
    icon: Icons.my_location_outlined,
    title: 'Live tracking is active',
    subtitle: 'Your delivery partner is currently sharing an updated location.',
  );
}

(IconData, DoodhStatusTone) _customerStatusVisuals(DeliveryStatus status) =>
    switch (status) {
      DeliveryStatus.delivered => (
        Icons.check_circle_outline,
        DoodhStatusTone.success,
      ),
      DeliveryStatus.failed => (
        Icons.report_problem_outlined,
        DoodhStatusTone.error,
      ),
      DeliveryStatus.arrived => (
        Icons.location_on_outlined,
        DoodhStatusTone.warning,
      ),
      DeliveryStatus.outForDelivery => (
        Icons.route_outlined,
        DoodhStatusTone.warning,
      ),
      DeliveryStatus.pickedUp => (
        Icons.inventory_2_outlined,
        DoodhStatusTone.warning,
      ),
      _ => (Icons.assignment_ind_outlined, DoodhStatusTone.neutral),
    };

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

/// Image picker injected in tests (same convention as the catalogue,
/// milk-test, and refund-replacement pickers).
typedef DeliveryFailImagePicker =
    Future<XFile?> Function({required ImageSource source});

/// Staff proof photo attached to a subscription delivery failure.
class DeliveryFailProof {
  const DeliveryFailProof({
    required this.bytes,
    required this.fileName,
    required this.contentType,
  });

  final Uint8List bytes;
  final String fileName;
  final String contentType;
}

/// Reason, remarks, and optional proof photo confirmed in [showDeliveryFailDialog].
class DeliveryFailInput {
  const DeliveryFailInput({
    required this.reason,
    this.remarks,
    this.proof,
  });

  final String reason;
  final String? remarks;
  final DeliveryFailProof? proof;
}

/// Resolves the content type for a picked failure proof photo, accepting only
/// what the server stores (JPEG, PNG, WebP). Returns null when unsupported.
String? resolveDeliveryFailImageContentType(
  String fileName,
  String? declaredType,
) {
  final normalized = (declaredType ?? '').toLowerCase().trim();
  if (normalized.isNotEmpty) {
    // A declared type that is not server-supported rejects the file even
    // when the extension looks right — the bytes would fail server-side.
    if (normalized == 'image/jpeg' ||
        normalized == 'image/png' ||
        normalized == 'image/webp') {
      return normalized;
    }
    return null;
  }
  final lower = fileName.toLowerCase();
  if (lower.endsWith('.jpg') || lower.endsWith('.jpeg')) {
    return 'image/jpeg';
  }
  if (lower.endsWith('.png')) return 'image/png';
  if (lower.endsWith('.webp')) return 'image/webp';
  return null;
}

/// Stable upload file name for a picked proof. Platform pickers can return a
/// path-only [XFile] whose `name` is empty; the server requires a non-empty
/// file name, so fall back to the path basename and finally to an extension
/// derived from the accepted content type.
String deliveryFailFileName(
  XFile file,
  String contentType,
) {
  final named = file.name.trim();
  if (named.isNotEmpty) return named;
  final segments = file.path
      .split(RegExp(r'[/\\]'))
      .where((segment) => segment.trim().isNotEmpty)
      .toList(growable: false);
  if (segments.isNotEmpty) return segments.last;
  return switch (contentType) {
    'image/png' => 'proof.png',
    'image/webp' => 'proof.webp',
    _ => 'proof.jpg',
  };
}

/// Failure dialog shared by one-time and subscription deliveries. When
/// [requiresProof] (subscription deliveries) a proof photo must be attached
/// before confirming — the server rejects the failure without one. Returns
/// the confirmed input, or null when the staff member cancels.
Future<DeliveryFailInput?> showDeliveryFailDialog(
  BuildContext context, {
  required bool requiresProof,
  DeliveryFailImagePicker? pickImage,
}) {
  var reason = DeliveryFailureReasons.customerNotAvailable;
  final remarks = TextEditingController();
  XFile? proofFile;
  Uint8List? proofBytes;
  String? validationError;
  return showDialog<DeliveryFailInput>(
    context: context,
    builder: (dialogContext) => StatefulBuilder(
      builder: (context, setState) => AlertDialog(
        title: const Text('Mark delivery failed'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
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
                  if (value != null) {
                    setState(() {
                      reason = value;
                      validationError = null;
                    });
                  }
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
                onChanged: (_) => setState(() => validationError = null),
              ),
              if (requiresProof) ...[
                const SizedBox(height: 12),
                Text(
                  'A proof image is required to fail this delivery. Attach a clear photo of the doorstep or package.',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
                const SizedBox(height: 8),
                if (proofBytes != null)
                  Stack(
                    children: [
                      ClipRRect(
                        borderRadius: BorderRadius.circular(8),
                        child: Image.memory(
                          proofBytes!,
                          height: 140,
                          fit: BoxFit.cover,
                        ),
                      ),
                      Positioned(
                        right: 4,
                        top: 4,
                        child: IconButton.filledTonal(
                          tooltip: 'Remove photo',
                          icon: const Icon(Icons.close),
                          onPressed: () => setState(() {
                            proofFile = null;
                            proofBytes = null;
                            validationError = null;
                          }),
                        ),
                      ),
                    ],
                  )
                else
                  Row(
                    children: [
                      Expanded(
                        child: OutlinedButton.icon(
                          icon: const Icon(Icons.photo_camera_outlined),
                          label: const Text('Take photo'),
                          onPressed: () => _pickFailProof(
                            setState,
                            pickImage,
                            ImageSource.camera,
                            (file, bytes) {
                              proofFile = file;
                              proofBytes = bytes;
                              validationError = null;
                            },
                            (message) => validationError = message,
                          ),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: OutlinedButton.icon(
                          icon: const Icon(Icons.photo_library_outlined),
                          label: const Text('Gallery'),
                          onPressed: () => _pickFailProof(
                            setState,
                            pickImage,
                            ImageSource.gallery,
                            (file, bytes) {
                              proofFile = file;
                              proofBytes = bytes;
                              validationError = null;
                            },
                            (message) => validationError = message,
                          ),
                        ),
                      ),
                    ],
                  ),
              ],
              if (validationError != null) ...[
                const SizedBox(height: 8),
                Text(
                  validationError!,
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
              ],
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              if (requiresProof && proofFile == null) {
                setState(() {
                  validationError =
                      'Attach a proof image before marking failed.';
                });
                return;
              }
              if (requiresProof && proofBytes == null) {
                setState(() {
                  validationError =
                      'Could not read the attached photo. Attach it again.';
                });
                return;
              }
              final file = proofFile;
              final bytes = proofBytes;
              final trimmed = remarks.text.trim();
              final contentType = file == null
                  ? null
                  : resolveDeliveryFailImageContentType(
                      file.name.isEmpty ? file.path : file.name,
                      file.mimeType,
                    );
              Navigator.pop(
                dialogContext,
                DeliveryFailInput(
                  reason: reason,
                  remarks: trimmed.isEmpty ? null : trimmed,
                  proof: file == null || bytes == null || contentType == null
                      ? null
                      : DeliveryFailProof(
                          bytes: bytes,
                          fileName: deliveryFailFileName(file, contentType),
                          contentType: contentType,
                        ),
                ),
              );
            },
            child: const Text('Mark failed'),
          ),
        ],
      ),
    ),
  ).whenComplete(remarks.dispose);
}

Future<void> _pickFailProof(
  StateSetter setState,
  DeliveryFailImagePicker? pickImage,
  ImageSource source,
  void Function(XFile file, Uint8List bytes) onPicked,
  void Function(String message) onError,
) async {
  try {
    final picker = pickImage ?? ImagePicker().pickImage;
    final file = await picker(source: source);
    if (file == null) return;
    if (resolveDeliveryFailImageContentType(
          file.name.isEmpty ? file.path : file.name,
          file.mimeType,
        ) ==
        null) {
      setState(() => onError('Photo must be a JPEG, PNG, or WebP image.'));
      return;
    }
    final bytes = await file.readAsBytes();
    if (bytes.isEmpty) {
      setState(() => onError('The selected photo is empty. Pick another.'));
      return;
    }
    setState(() => onPicked(file, bytes));
  } on Object {
    setState(
      () => onError('Could not open the photo. Check access and try again.'),
    );
  }
}

Future<void> _showFailureDialog(
  BuildContext context,
  WidgetRef ref,
  DeliveryDetails delivery,
) async {
  final input = await showDeliveryFailDialog(
    context,
    requiresProof:
        delivery.sourceType == DeliverySourceType.subscriptionOccurrence,
  );
  if (input == null || !context.mounted) return;
  final controller = ref.read(deliveryControllerProvider.notifier);
  final proof = input.proof;
  if (proof != null) {
    await controller.failWithProof(
      delivery.deliveryId,
      reason: input.reason,
      remarks: input.remarks,
      imageBytes: proof.bytes,
      fileName: proof.fileName,
      contentType: proof.contentType,
    );
  } else {
    await controller.fail(
      delivery.deliveryId,
      reason: input.reason,
      remarks: input.remarks,
    );
  }
}

class _BulkAssignmentChoice {
  const _BulkAssignmentChoice({required this.employeeId, required this.reason});
  final String employeeId;
  final String? reason;
}

/// Item breakdown + canonical references on the management detail screen.
/// Subscription occurrences show the frozen product snapshot (name · qty
/// unit) with the SUB-series subscription number; one-time orders list
/// their order-item snapshots with the ORD-series order number. Labels name
/// the identifier kind so subscription, order, and delivery references are
/// never confused.
class _DeliveryItemSection extends StatelessWidget {
  const _DeliveryItemSection({required this.delivery});
  final DeliveryDetails delivery;

  @override
  Widget build(BuildContext context) {
    final isSubscription =
        delivery.sourceType == DeliverySourceType.subscriptionOccurrence;
    final List<Widget> itemTiles;
    if (isSubscription) {
      final name = delivery.subscriptionProductName;
      final unit = delivery.subscriptionUnitOfMeasure;
      final quantity = delivery.quantity;
      final title = name != null && name.isNotEmpty
          ? '$name${quantity == null ? '' : ' · ${_formatQty(quantity)}${unit == null || unit.isEmpty ? '' : ' $unit'}'}'
          : '${delivery.subscriptionSlot?.label ?? 'Slot unavailable'} · ${quantity?.toStringAsFixed(2) ?? '-'} quantity';
      itemTiles = [
        DoodhInfoTile(
          icon: Icons.inventory_2_outlined,
          title: title,
          subtitle:
              'Subscription item · ${delivery.subscriptionSlot?.label ?? 'Slot unavailable'}',
        ),
      ];
    } else {
      final summary = delivery.orderSummary;
      itemTiles = [
        if (summary != null && summary.items.isNotEmpty)
          for (final item in summary.items)
            DoodhInfoTile(
              icon: Icons.inventory_2_outlined,
              title: item,
              subtitle: 'Order ${summary.orderNumber}',
            )
        else
          DoodhInfoTile(
            icon: Icons.inventory_2_outlined,
            title: summary == null
                ? 'One-time order'
                : 'Order ${summary.orderNumber}',
            subtitle:
                'Order items unavailable for this delivery',
          ),
      ];
    }
    final subscriptionNumber = delivery.subscriptionNumber;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: 8),
        DoodhSectionHeader(title: 'Items'),
        ...itemTiles,
        DoodhInfoTile(
          icon: Icons.numbers_outlined,
          title: isSubscription
              ? (subscriptionNumber != null && subscriptionNumber.isNotEmpty
                    ? 'Subscription $subscriptionNumber'
                    : 'Subscription reference unavailable')
              : (delivery.orderSummary == null
                    ? 'One-time order'
                    : 'Order ${delivery.orderSummary!.orderNumber}'),
          subtitle: isSubscription
              ? 'Delivery ${delivery.referenceNumber}'
              : 'Delivery ${delivery.referenceNumber}',
        ),
      ],
    );
  }
}

/// Past-due unmaterialized subscription occurrences awaiting manager
/// confirmation. Each row shows the customer, scheduled date, overdue age
/// and the estimated per-delivery refund; confirming opens the standard
/// failure dialog (no proof: the visit was never attempted) and records
/// the miss through the existing delivery-fail workflow.
class _MissedOccurrenceCard extends StatelessWidget {
  const _MissedOccurrenceCard({
    required this.missed,
    required this.isSaving,
    required this.onConfirm,
  });
  final List<MissedOccurrence> missed;
  final bool isSaving;
  final ValueChanged<MissedOccurrence> onConfirm;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      elevation: 1,
      color: Colors.white,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: const BorderSide(color: _deliveryAdminCardBorder),
      ),
      child: Padding(
        padding: const EdgeInsets.all(DoodhSpacing.md),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Missed deliveries (${missed.length})',
              style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16),
            ),
            const SizedBox(height: 4),
            Text(
              'Past-due subscription deliveries with no visit recorded. Confirm a miss to open the refund workflow.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: DoodhColors.muted,
              ),
            ),
            const SizedBox(height: DoodhSpacing.sm),
            if (missed.isEmpty)
              const Text('No missed deliveries.')
            else
              for (var index = 0; index < missed.length; index++) ...[
                if (index > 0) const Divider(height: 1),
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text(
                    '${missed[index].customerName} · ${formatDeliveryDate(missed[index].scheduledDate)}',
                    style: const TextStyle(fontWeight: FontWeight.w700),
                  ),
                  subtitle: Text(
                    '${missed[index].daysOverdue} day(s) overdue · est. refund \u20B9${missed[index].estimatedRefundAmount.toStringAsFixed(2)}',
                  ),
                  trailing: FilledButton(
                    onPressed: isSaving ? null : () => onConfirm(missed[index]),
                    style: FilledButton.styleFrom(
                      backgroundColor: _deliveryAdminAction,
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(8),
                      ),
                      minimumSize: const Size(0, 40),
                      padding: const EdgeInsets.symmetric(horizontal: 12),
                    ),
                    child: const Text('Confirm miss'),
                  ),
                ),
              ],
          ],
        ),
      ),
    );
  }
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

  /// Prominent item line: product name + quantity with its real unit, from
  /// authoritative snapshots (subscription snapshots for occurrences,
  /// order-item snapshots for one-time orders). Falls back to the slot/order
  /// reference only when snapshots are absent (historical payloads).
  String _itemLine() {
    final quantity = delivery.quantity;
    if (delivery.sourceType == DeliverySourceType.subscriptionOccurrence) {
      final name = delivery.subscriptionProductName;
      final unit = delivery.subscriptionUnitOfMeasure;
      if (name != null && name.isNotEmpty) {
        final qty = quantity == null
            ? ''
            : ' · ${_formatQty(quantity)}${unit == null || unit.isEmpty ? '' : ' $unit'}';
        return '$name$qty';
      }
      return '${delivery.subscriptionSlot?.label ?? 'Slot unavailable'} · ${quantity?.toStringAsFixed(2) ?? '-'} quantity';
    }
    final summary = delivery.orderSummary;
    if (summary == null) return 'One-time order';
    if (summary.items.isNotEmpty) return summary.items.join(', ');
    return 'Order ${summary.orderNumber} · '
        '${summary.totalQuantity.toStringAsFixed(2)} quantity';
  }

  String _referenceLine() {
    if (delivery.sourceType == DeliverySourceType.subscriptionOccurrence) {
      final number = delivery.subscriptionNumber;
      if (number != null && number.isNotEmpty) return 'Subscription $number';
    }
    return delivery.sourceType.label;
  }

  @override
  Widget build(BuildContext context) {
    final detail = _itemLine();
    // No Card here: rows live inside the white Admin Centre deliveries
    // card, separated by dividers like the catalogue product rows.
    return ListTile(
      contentPadding: EdgeInsets.zero,
      leading: onSelected == null
          ? Icon(
              delivery.isTrackingActive
                  ? Icons.location_searching
                  : Icons.local_shipping_outlined,
            )
          : Checkbox(value: selected, onChanged: (_) => onSelected!()),
      title: Row(
        children: [
          Expanded(
            child: Text(
              delivery.referenceNumber,
              style: Theme.of(context).textTheme.titleMedium,
            ),
          ),
          const SizedBox(width: 8),
          DoodhStatusPill(
            label: delivery.status.label,
            tone: _deliveryTone(delivery.status),
          ),
        ],
      ),
      subtitle: Padding(
        padding: const EdgeInsets.only(top: 4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              '${formatDeliveryDate(delivery.scheduledDate)} · ${_referenceLine()}',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            const SizedBox(height: 2),
            Text(detail, style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 2),
            Text(
              delivery.assignedEmployeeName ?? 'Unassigned',
              style: Theme.of(context).textTheme.bodySmall
                  ?.copyWith(color: DoodhColors.muted),
            ),
          ],
        ),
      ),
      trailing: const Icon(Icons.chevron_right),
      onTap: onTap,
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

String _formatQty(double value) {
  var text = value.toStringAsFixed(3);
  if (text.contains('.')) {
    text = text
        .replaceFirst(RegExp(r'0+$'), '')
        .replaceFirst(RegExp(r'\.$'), '');
  }
  return text;
}

class _BatchAllocationSection extends ConsumerWidget {
  const _BatchAllocationSection({required this.delivery, required this.saving});
  final DeliveryDetails delivery;
  final bool saving;

  bool get _canEdit =>
      delivery.status != DeliveryStatus.delivered &&
      delivery.status != DeliveryStatus.failed;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final allocations = delivery.batchAllocations;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                'Batch allocations',
                style: Theme.of(context).textTheme.titleMedium,
              ),
            ),
            if (_canEdit)
              TextButton.icon(
                icon: const Icon(Icons.tune),
                label: const Text('Set allocations'),
                onPressed: saving
                    ? null
                    : () => _openBatchAllocationEditor(context, ref, delivery),
              ),
          ],
        ),
        const SizedBox(height: 4),
        if (allocations.isEmpty)
          const DoodhInfoTile(
            icon: Icons.inventory_2_outlined,
            title: 'No allocations yet',
            subtitle: 'Select the milk batches that will fulfill this delivery before it is assigned.',
          )
        else
          Card(
            child: Column(
              children: [
                for (final allocation in allocations)
                  ListTile(
                    dense: true,
                    contentPadding: const EdgeInsets.symmetric(
                      horizontal: 16,
                      vertical: 2,
                    ),
                    leading: const Icon(Icons.local_drink_outlined),
                    title: Text(allocation.batchNumber),
                    subtitle: Text(
                      'Allocated ${_formatQty(allocation.quantityAllocated)} L',
                    ),
                    trailing: Icon(
                      delivery.status == DeliveryStatus.delivered
                          ? Icons.check_circle_outline
                          : Icons.schedule,
                      color: delivery.status == DeliveryStatus.delivered
                          ? DoodhColors.tealDark
                          : DoodhColors.muted,
                    ),
                  ),
                if (delivery.status == DeliveryStatus.delivered) ...[
                  const Divider(height: 1),
                  const Padding(
                    padding: EdgeInsets.all(12),
                    child: Text(
                      'These batches were consumed when the delivery was completed.',
                      style: TextStyle(fontSize: 12),
                    ),
                  ),
                ],
              ],
            ),
          ),
        if (delivery.status == DeliveryStatus.failed)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(
              'No milk was consumed for this delivery.',
              style: TextStyle(
                fontSize: 12,
                color: Theme.of(context).colorScheme.error,
              ),
            ),
          ),
      ],
    );
  }
}

Future<void> _openBatchAllocationEditor(
  BuildContext context,
  WidgetRef ref,
  DeliveryDetails delivery,
) async {
  final controller = ref.read(deliveryControllerProvider.notifier);
  final loaded = await controller.loadBatchAllocations(delivery.deliveryId);
  if (!loaded || !context.mounted) return;
  final info = ref.read(deliveryControllerProvider).batchAllocations;
  if (info == null) return;
  await Navigator.of(context).push(
    MaterialPageRoute<void>(
      builder: (_) => _BatchAllocationEditorScreen(
        deliveryId: delivery.deliveryId,
        initial: info,
      ),
    ),
  );
}

class _BatchAllocationEditorScreen extends ConsumerStatefulWidget {
  const _BatchAllocationEditorScreen({
    required this.deliveryId,
    required this.initial,
  });
  final String deliveryId;
  final DeliveryBatchAllocationsInfo initial;

  @override
  ConsumerState<_BatchAllocationEditorScreen> createState() =>
      _BatchAllocationEditorScreenState();
}

class _BatchAllocationEditorScreenState
    extends ConsumerState<_BatchAllocationEditorScreen> {
  final Map<int, TextEditingController> _quantity = {};
  late final Set<int> _selected = widget.initial.allocations
      .map((allocation) => allocation.batchId)
      .toSet();

  double get _required => widget.initial.totalRequiredQuantity;

  double get _allocated {
    var total = 0.0;
    for (final id in _selected) {
      total += double.tryParse(_quantity[id]?.text ?? '') ?? 0;
    }
    return total;
  }

  @override
  void initState() {
    super.initState();
    for (final batch in widget.initial.eligibleBatches) {
      DeliveryBatchAllocation? existing;
      for (final allocation in widget.initial.allocations) {
        if (allocation.batchId == batch.batchId) {
          existing = allocation;
          break;
        }
      }
      _quantity[batch.batchId] = TextEditingController(
        text: existing == null ? '' : _formatQty(existing.quantityAllocated),
      );
    }
  }

  @override
  void dispose() {
    for (final controller in _quantity.values) {
      controller.dispose();
    }
    super.dispose();
  }

  bool _isValid() {
    if (_selected.isEmpty) return false;
    final roundedAllocated = (_allocated * 1000).round();
    final roundedRequired = (_required * 1000).round();
    if (roundedAllocated != roundedRequired) return false;
    for (final batch in widget.initial.eligibleBatches) {
      if (!_selected.contains(batch.batchId)) continue;
      final value = double.tryParse(_quantity[batch.batchId]?.text ?? '') ?? 0;
      if (value <= 0) return false;
      if (value > batch.quantityAvailable + 0.0005) return false;
    }
    return true;
  }

  Future<void> _save() async {
    final allocations = <DeliveryBatchAllocation>[
      for (final batch in widget.initial.eligibleBatches)
        if (_selected.contains(batch.batchId))
          DeliveryBatchAllocation(
            batchId: batch.batchId,
            batchNumber: batch.batchNumber,
            quantityAllocated:
                double.tryParse(_quantity[batch.batchId]!.text) ?? 0,
            allocatedAt: DateTime.now(),
            updatedAt: DateTime.now(),
          ),
    ];
    final saved = await ref
        .read(deliveryControllerProvider.notifier)
        .saveBatchAllocations(widget.deliveryId, allocations);
    if (!saved || !mounted) return;
    await ref
        .read(deliveryControllerProvider.notifier)
        .loadManagedDelivery(widget.deliveryId);
    if (!mounted) return;
    Navigator.of(context).pop();
    ScaffoldMessenger.of(context)
        .showSnackBar(const SnackBar(content: Text('Batch allocations saved')));
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(deliveryControllerProvider);
    return Scaffold(
      appBar: AppBar(title: const Text('Set batch allocations')),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: Text(
              'Required ${_formatQty(_required)} L · '
              'Allocated ${_formatQty(_allocated)} L',
              style: Theme.of(context).textTheme.bodyMedium,
            ),
          ),
          Expanded(
            child: widget.initial.eligibleBatches.isEmpty
                ? const Center(
                    child: Text('No available milk batches for this branch.'),
                  )
                : ListView(
                    padding: DoodhSpacing.pagePadding,
                    children: [
                      for (final batch in widget.initial.eligibleBatches)
                        _EligibleBatchRow(
                          batch: batch,
                          selected: _selected.contains(batch.batchId),
                          controller: _quantity[batch.batchId]!,
                          onSelectedChanged: (value) {
                            setState(() {
                              if (value == true) {
                                _selected.add(batch.batchId);
                              } else {
                                _selected.remove(batch.batchId);
                              }
                            });
                          },
                          onTextChanged: () => setState(() {}),
                        ),
                    ],
                  ),
          ),
          SafeArea(
            top: false,
            child: Padding(
              padding: DoodhSpacing.cardPadding,
              child: FilledButton.icon(
                icon: const Icon(Icons.save_outlined),
                label: Text(state.isSaving ? 'Saving...' : 'Save allocations'),
                onPressed: state.isSaving || !_isValid() ? null : _save,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _EligibleBatchRow extends StatelessWidget {
  const _EligibleBatchRow({
    required this.batch,
    required this.selected,
    required this.controller,
    required this.onSelectedChanged,
    required this.onTextChanged,
  });
  final EligibleDeliveryBatch batch;
  final bool selected;
  final TextEditingController controller;
  final ValueChanged<bool?> onSelectedChanged;
  final VoidCallback onTextChanged;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: Row(
        children: [
          Checkbox(value: selected, onChanged: onSelectedChanged),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  batch.batchNumber,
                  style: const TextStyle(fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 2),
                Text(
                  'Produced ${_formatQty(batch.quantityProduced)} ${batch.unit} · '
                  'Available ${_formatQty(batch.quantityAvailable)} ${batch.unit}',
                  style: TextStyle(
                    fontSize: 12,
                    color: Theme.of(context).hintColor,
                  ),
                ),
              ],
            ),
          ),
          SizedBox(
            width: 110,
            child: TextField(
              controller: controller,
              enabled: selected,
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              decoration: InputDecoration(
                isDense: true,
                suffixText: batch.unit,
                border: const OutlineInputBorder(),
                hintText: '0',
              ),
              onChanged: (_) => onTextChanged(),
            ),
          ),
        ],
      ),
    ),
  );
}
