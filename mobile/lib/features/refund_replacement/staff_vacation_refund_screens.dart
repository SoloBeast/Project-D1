import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'vacation_refund_card.dart';
import 'vacation_refund_controller.dart';
import 'vacation_refund_models.dart';

/// Staff vacation-refund queue. End-of-subscription vacation-refund requests
/// are a separate entity from per-delivery refund/replacement tickets, so
/// they have their own queue: the Support Tickets list queries a different
/// endpoint and can never show them.
///
/// Rows are server-scoped to the actor's branches (global access sees
/// everything); the client never filters by branch itself. Decisions reuse
/// the authorized approve/reject/complete workflow — approval never credits
/// the wallet, only completion does.
class StaffVacationRefundListScreen extends ConsumerStatefulWidget {
  const StaffVacationRefundListScreen({super.key});

  @override
  ConsumerState<StaffVacationRefundListScreen> createState() =>
      _StaffVacationRefundListScreenState();
}

class _StaffVacationRefundListScreenState
    extends ConsumerState<StaffVacationRefundListScreen> {
  VacationRefundStatus? _status;

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(vacationRefundStaffControllerProvider.notifier)
      .loadStaffRequests();

  List<VacationRefundRequest> _visibleItems(
    List<VacationRefundRequest> requests,
  ) {
    final status = _status;
    if (status == null) return requests;
    return requests
        .where((item) => item.status == status)
        .toList(growable: false);
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(vacationRefundStaffControllerProvider);
    final items = _visibleItems(state.staffRequests);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Vacation refunds'),
        actions: [
          IconButton(
            tooltip: 'Refresh vacation refunds',
            onPressed: state.isLoading ? null : _load,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: _listBody(state, items),
    );
  }

  Widget _listBody(
    VacationRefundStaffState state,
    List<VacationRefundRequest> items,
  ) {
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isLoading && state.staffRequests.isEmpty) {
      return const LoadingStatePanel(message: 'Loading vacation refunds...');
    }
    if (state.isOffline && state.staffRequests.isEmpty) {
      return OfflineStatePanel(onRetry: _load);
    }
    if (state.errorMessage != null && state.staffRequests.isEmpty) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    }
    if (items.isEmpty) {
      return EmptyStatePanel(
        title: _status == null
            ? 'No vacation refunds'
            : 'No ${VacationRefundStatusLabel.of(_status!)} requests',
        message: _status == null
            ? 'End-of-subscription vacation refunds will appear here for support review.'
            : 'No vacation-refund requests currently have this status.',
      );
    }
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView.separated(
        padding: DoodhSpacing.pagePadding,
        itemCount: items.length,
        separatorBuilder: (_, _) => const SizedBox(height: 12),
        itemBuilder: (context, index) {
          final request = items[index];
          return _StaffVacationRefundRow(
            request: request,
            onTap: () => context.push(
              '/staff/vacation-refunds/${request.requestId}',
            ),
          );
        },
      ),
    );
  }
}

class _StaffVacationRefundRow extends StatelessWidget {
  const _StaffVacationRefundRow({required this.request, required this.onTap});

  final VacationRefundRequest request;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      request.requestNumber,
                      style: theme.textTheme.titleMedium,
                    ),
                  ),
                  DoodhStatusPill(
                    label: request.status.label,
                    tone: switch (request.status) {
                      VacationRefundStatus.completed =>
                        DoodhStatusTone.success,
                      VacationRefundStatus.approved =>
                        DoodhStatusTone.success,
                      VacationRefundStatus.rejected => DoodhStatusTone.error,
                      _ => DoodhStatusTone.warning,
                    },
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                '${request.branchName} · ${request.occurrenceCount} skipped deliveries · ₹${request.totalAmount.toStringAsFixed(2)}',
                style: theme.textTheme.bodyMedium,
              ),
              const SizedBox(height: 4),
              Text(
                '${request.customerName} · ${request.customerMobile}',
                style: theme.textTheme.bodySmall,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

extension VacationRefundStatusLabel on VacationRefundStatus {
  static String of(VacationRefundStatus status) => status.label;
}

/// Staff vacation-refund detail. Shows the same frozen request the customer
/// sees, plus the authorized decision actions for managers:
/// Pending → approve/reject, Approved → complete. Terminal requests offer
/// no actions. Employee-originated requests stay actionable only for
/// Owners/System Administrators; the backend enforces this.
class StaffVacationRefundDetailScreen extends ConsumerStatefulWidget {
  const StaffVacationRefundDetailScreen({super.key, required this.requestId});

  final String requestId;

  @override
  ConsumerState<StaffVacationRefundDetailScreen> createState() =>
      _StaffVacationRefundDetailScreenState();
}

class _StaffVacationRefundDetailScreenState
    extends ConsumerState<StaffVacationRefundDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(vacationRefundStaffControllerProvider.notifier)
      .loadStaffRequest(widget.requestId);

  bool get _canManage {
    final permissions =
        ref.read(sessionControllerProvider).session?.user.permissions ??
        const [];
    return permissions.contains('REFUND_REPLACEMENT.MANAGE_BRANCH');
  }

  /// Employee-origin policy mirror: only an Owner or System Administrator
  /// may decide a request that belongs to a staff account. The backend
  /// enforces this; the UI only hides the actions to stay consistent.
  bool get _isAdmin {
    final roles =
        ref.read(sessionControllerProvider).session?.user.roles ?? const [];
    return roles.contains('OWNER') || roles.contains('SYSTEM_ADMIN');
  }

  Future<void> _decide(String decision, [String? remarks]) async {
    final normalized = remarks != null && remarks.trim().isEmpty
        ? null
        : remarks?.trim();
    final controller = ref.read(
      vacationRefundStaffControllerProvider.notifier,
    );
    final succeeded = switch (decision) {
      'approve' => await controller.approve(
        widget.requestId,
        remarks: normalized,
      ),
      'reject' => await controller.reject(
        widget.requestId,
        remarks: normalized,
      ),
      _ => await controller.complete(widget.requestId, remarks: normalized),
    };
    if (!mounted || !succeeded) return;
    final message = switch (decision) {
      'approve' => 'Vacation refund approved. No money has moved yet.',
      'reject' => 'Vacation refund rejected.',
      _ => 'Vacation refund completed and credited to the wallet.',
    };
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(message)),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(vacationRefundStaffControllerProvider);
    final request = state.selectedStaffRequest?.requestId == widget.requestId
        ? state.selectedStaffRequest
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Vacation refund details')),
      body: request == null
          ? _missingBody(state)
          : Column(
              children: [
                Expanded(
                  child: ListView(
                    padding: DoodhSpacing.pagePadding,
                    children: [VacationRefundCard(request: request)],
                  ),
                ),
                if (_canManage)
                  _VacationDecisionActions(
                    request: request,
                    isSaving: state.isSaving,
                    errorMessage: state.errorMessage,
                    onDecide: _decide,
                    employeeOriginBlocked:
                        request.isEmployeeOriginated && !_isAdmin,
                  ),
              ],
            ),
    );
  }

  Widget _missingBody(VacationRefundStaffState state) {
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isLoading) {
      return const LoadingStatePanel(message: 'Loading request...');
    }
    if (state.isOffline) return OfflineStatePanel(onRetry: _load);
    if (state.errorMessage != null) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    }
    return const EmptyStatePanel(
      title: 'Request not found',
      message: 'This vacation-refund request is not available to your account.',
    );
  }
}

class _VacationDecisionActions extends StatelessWidget {
  const _VacationDecisionActions({
    required this.request,
    required this.isSaving,
    required this.errorMessage,
    required this.onDecide,
    this.employeeOriginBlocked = false,
  });

  final VacationRefundRequest request;
  final bool isSaving;
  final String? errorMessage;
  final Future<void> Function(String decision, String? remarks) onDecide;
  final bool employeeOriginBlocked;

  @override
  Widget build(BuildContext context) {
    final canDecide =
        !employeeOriginBlocked &&
        (request.status == VacationRefundStatus.pending ||
            request.status == VacationRefundStatus.approved);
    return Material(
      elevation: 8,
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: [
              if (errorMessage != null) ...[
                Text(
                  errorMessage!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
                const SizedBox(height: 8),
              ],
              if (employeeOriginBlocked) ...[
                const _Notice(
                  icon: Icons.admin_panel_settings_outlined,
                  text:
                      'This request belongs to a staff account and can only '
                      'be decided by an Owner or System Administrator. The '
                      'actions are hidden for your role — the backend '
                      'enforces this rule.',
                ),
                const SizedBox(height: 12),
              ],
              if (canDecide) ...[
                if (request.status == VacationRefundStatus.approved)
                  const _Notice(
                    icon: Icons.info_outline,
                    text:
                        'Approved requests are settled only on completion: '
                        'completing credits the frozen 0%-deduction total '
                        'to the customer wallet.',
                  ),
                if (request.status == VacationRefundStatus.approved)
                  const SizedBox(height: 12),
                Row(
                  children: [
                    if (request.status == VacationRefundStatus.pending) ...[
                      Expanded(
                        child: OutlinedButton.icon(
                          onPressed: isSaving
                              ? null
                              : () async {
                                  final remarks =
                                      await _promptStaffDecision(
                                        context,
                                        title: 'Reject vacation refund',
                                        confirmLabel: 'Reject',
                                      );
                                  if (remarks == null) return;
                                  await onDecide('reject', remarks);
                                },
                          icon: const Icon(Icons.close),
                          label: const Text('Reject'),
                        ),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: FilledButton.icon(
                          onPressed: isSaving
                              ? null
                              : () async {
                                  final remarks =
                                      await _promptStaffDecision(
                                        context,
                                        title: 'Approve vacation refund',
                                        confirmLabel: 'Approve',
                                      );
                                  if (remarks == null) return;
                                  await onDecide('approve', remarks);
                                },
                          icon: const Icon(Icons.check),
                          label: const Text('Approve'),
                        ),
                      ),
                    ] else
                      Expanded(
                        child: FilledButton.icon(
                          onPressed: isSaving
                              ? null
                              : () async {
                                  final remarks =
                                      await _promptStaffDecision(
                                        context,
                                        title: 'Complete vacation refund',
                                        confirmLabel: 'Complete',
                                      );
                                  if (remarks == null) return;
                                  await onDecide('complete', remarks);
                                },
                          icon: const Icon(Icons.check),
                          label: const Text('Complete'),
                        ),
                      ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// Remarks dialog shared by the vacation decision buttons. Returns null on
/// cancel/dismiss and the (possibly empty, normalized by the caller) input
/// on confirm — typed remarks always reach the authorized backend call.
/// The dialog owns its controller through its own widget lifecycle, so
/// dismissal animations never touch a disposed controller.
Future<String?> _promptStaffDecision(
  BuildContext context, {
  required String title,
  required String confirmLabel,
}) => showDialog<String>(
  context: context,
  builder: (dialogContext) => _VacationDecisionDialog(
    title: title,
    confirmLabel: confirmLabel,
  ),
);

class _VacationDecisionDialog extends StatefulWidget {
  const _VacationDecisionDialog({
    required this.title,
    required this.confirmLabel,
  });

  final String title;
  final String confirmLabel;

  @override
  State<_VacationDecisionDialog> createState() =>
      _VacationDecisionDialogState();
}

class _VacationDecisionDialogState extends State<_VacationDecisionDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(widget.title),
    content: TextField(
      controller: _controller,
      autofocus: true,
      maxLines: 3,
      maxLength: 1000,
      decoration: const InputDecoration(
        labelText: 'Remarks (optional)',
        hintText: 'Visible to support and the customer',
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.of(context).pop(),
        child: const Text('Cancel'),
      ),
      FilledButton(
        onPressed: () => Navigator.of(context).pop(_controller.text),
        child: Text(widget.confirmLabel),
      ),
    ],
  );
}

class _Notice extends StatelessWidget {
  const _Notice({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 18, color: theme.colorScheme.primary),
        const SizedBox(width: 8),
        Expanded(child: Text(text, style: theme.textTheme.bodySmall)),
      ],
    );
  }
}
