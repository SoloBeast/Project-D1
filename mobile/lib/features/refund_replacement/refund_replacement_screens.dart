import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';

import '../deliveries/delivery_models.dart';
import 'refund_replacement_controller.dart';
import 'refund_replacement_models.dart';

typedef RefundReplacementImagePicker = Future<XFile?> Function({
  required ImageSource source,
});

/// Raised when a protected proof image is requested without an authenticated
/// session token. Distinct from an HTTP 403 so the UI can show a controlled
/// "no access" state without ever requesting the endpoint unauthenticated.
class RefundReplacementImageUnauthenticatedException implements Exception {
  const RefundReplacementImageUnauthenticatedException();

  @override
  String toString() =>
      'No authenticated session for protected refund/replacement image.';
}

class _RefundReplacementImageCandidate {
  const _RefundReplacementImageCandidate({
    required this.bytes,
    required this.fileName,
    required this.contentType,
  });

  final Uint8List bytes;
  final String fileName;
  final String contentType;
}

mixin _RefundReplacementImagePicking<T extends ConsumerStatefulWidget>
    on ConsumerState<T> {
  RefundReplacementImagePicker? get pickImage;

  Future<_RefundReplacementImageCandidate?> _pickProofImage() async {
    try {
      final source = await _chooseImageSource();
      if (source == null || !mounted) return null;
      final picker = pickImage ?? ImagePicker().pickImage;
      final image = await picker(source: source);
      if (image == null || !mounted) return null;
      final contentType = resolveRefundReplacementImageContentType(
        image.name,
        image.mimeType,
      );
      if (contentType == null) {
        _showMessage('Select a JPEG or PNG image.');
        return null;
      }
      final fileName = image.name.trim().isEmpty
          ? 'refund-replacement.${contentType == 'image/png' ? 'png' : 'jpg'}'
          : image.name;
      final bytes = await image.readAsBytes();
      if (!mounted) return null;
      final candidate = _RefundReplacementImageCandidate(
        bytes: bytes,
        fileName: fileName,
        contentType: contentType,
      );
      final usePhoto = await _showImagePreview(candidate);
      if (usePhoto != true || !mounted) return null;
      return candidate;
    } on Object {
      if (mounted) _showMessage('Unable to read the selected image.');
      return null;
    }
  }

  Future<ImageSource?> _chooseImageSource() =>
      showModalBottomSheet<ImageSource>(
        context: context,
        builder: (sheetContext) => SafeArea(
          child: Wrap(
            children: [
              ListTile(
                leading: const Icon(Icons.photo_camera_outlined),
                title: Text(kIsWeb ? 'Capture or choose image' : 'Take photo'),
                subtitle: Text(
                  kIsWeb
                      ? 'Use the browser camera or image picker'
                      : 'Open the device camera',
                ),
                onTap: () => Navigator.pop(sheetContext, ImageSource.camera),
              ),
              ListTile(
                leading: const Icon(Icons.photo_library_outlined),
                title: Text(
                  kIsWeb ? 'Choose image file' : 'Choose from gallery',
                ),
                subtitle: const Text('Select an existing JPEG or PNG image'),
                onTap: () => Navigator.pop(sheetContext, ImageSource.gallery),
              ),
              ListTile(
                leading: const Icon(Icons.close),
                title: const Text('Cancel'),
                onTap: () => Navigator.pop(sheetContext),
              ),
            ],
          ),
        ),
      );

  Future<bool?> _showImagePreview(_RefundReplacementImageCandidate candidate) =>
      showDialog<bool>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: const Text('Use this proof image?'),
          content: ClipRRect(
            borderRadius: BorderRadius.circular(12),
            child: Image.memory(
              candidate.bytes,
              fit: BoxFit.contain,
              height: 240,
              errorBuilder: (_, _, _) => const SizedBox(
                height: 120,
                child: Center(child: Text('Preview unavailable')),
              ),
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(dialogContext, false),
              child: const Text('Retake'),
            ),
            TextButton(
              onPressed: () => Navigator.pop(dialogContext),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(dialogContext, true),
              child: const Text('Use Photo'),
            ),
          ],
        ),
      );

  void _showMessage(String message) =>
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(message)));
}

/// Customer entry point for creating a refund or replacement request for a
/// delivered order. The window is server-authoritative: this screen only renders
/// the eligibility answer and hides disables submission when the backend says the
/// request is out of window.
///
/// When [milkTestId] is supplied the screen is the special milk-test-rejection
/// flow: the request is pre-linked to that test and no proof image is required.
///
/// [sourceType] is the authoritative delivery source from the deliveries API
/// (`sourceType` on the customer/delivery detail). One-time order deliveries
/// support Refund only in this release; subscription occurrences keep Refund
/// and Replacement. A null/unknown source fails safe to refund-only so the UI
/// never offers an unsupported Replacement option.
class CustomerRefundReplacementScreen extends ConsumerStatefulWidget {
  const CustomerRefundReplacementScreen({
    super.key,
    required this.deliveryId,
    this.milkTestId,
    this.sourceType,
    this.pickImage,
  });

  final String deliveryId;
  final String? milkTestId;
  final DeliverySourceType? sourceType;
  final RefundReplacementImagePicker? pickImage;

  @override
  ConsumerState<CustomerRefundReplacementScreen> createState() =>
      _CustomerRefundReplacementScreenState();
}

class _CustomerRefundReplacementScreenState
    extends ConsumerState<CustomerRefundReplacementScreen>
    with _RefundReplacementImagePicking<CustomerRefundReplacementScreen> {
  final _reasonController = TextEditingController();
  final _remarksController = TextEditingController();
  RefundReplacementType _type = RefundReplacementType.refund;
  _RefundReplacementImageCandidate? _proof;

  @override
  RefundReplacementImagePicker? get pickImage => widget.pickImage;

  bool get _isMilkTestFlow => widget.milkTestId != null;

  /// True only for subscription occurrences, the single source that supports
  /// Replacement. Everything else (one-time order, null, unknown) is
  /// refund-only: the fail-safe default when the source cannot be determined
  /// reliably.
  bool get _isSubscriptionDelivery =>
      widget.sourceType == DeliverySourceType.subscriptionOccurrence;

  bool get _isRefundOnly => !_isSubscriptionDelivery;

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  @override
  void dispose() {
    _reasonController.dispose();
    _remarksController.dispose();
    super.dispose();
  }

  Future<void> _load() => ref
      .read(refundReplacementControllerProvider.notifier)
      .loadEligibility(widget.deliveryId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(refundReplacementControllerProvider);
    final eligibility = state.eligibility;

    return Scaffold(
      appBar: AppBar(title: const Text('Refund or replacement')),
      bottomNavigationBar: CustomerBottomNavigation(
        currentPath: '/deliveries/${widget.deliveryId}/refund-replacement',
      ),
      body: _body(state, eligibility),
    );
  }

  Widget _body(
    RefundReplacementState state,
    RefundReplacementEligibility? eligibility,
  ) {
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isOffline && eligibility == null) {
      return OfflineStatePanel(onRetry: _load);
    }
    if (state.isLoading && eligibility == null) {
      return const LoadingStatePanel(message: 'Checking eligibility...');
    }
    if (eligibility == null) {
      return ErrorStatePanel(
        message: state.errorMessage ?? 'Eligibility could not be loaded.',
        onRetry: _load,
      );
    }
    if (!eligibility.isEligible) {
      return EmptyStatePanel(
        title: 'Request not available',
        message: eligibility.ineligibleReason ?? 'This delivery is not eligible for a refund or replacement request.',
        action: FilledButton.icon(
          onPressed: _load,
          icon: const Icon(Icons.refresh),
          label: const Text('Refresh'),
        ),
      );
    }
    final proofRequired = eligibility.proofImageRequired && !_isMilkTestFlow;
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
        children: [
          if (_isMilkTestFlow)
            const _Notice(
              icon: Icons.science_outlined,
              text: 'You rejected the doorstep milk test. You can raise a refund or replacement request for this delivery right away.',
            ),
          if (eligibility.deadlineUtc != null)
            _InfoRow(
              icon: Icons.schedule,
              label: 'Raise before',
              value: formatRefundReplacementDateTime(eligibility.deadlineUtc!),
            ),
          if (eligibility.hasActiveRequest)
            const _Notice(
              icon: Icons.info_outline,
              text: 'There is already an open request for this delivery. Track it from your requests list.',
            ),
          const SizedBox(height: 8),
          DoodhSectionHeader(title: 'Request type'),
          const SizedBox(height: 8),
          if (_isRefundOnly) ...[
            const _Notice(
              icon: Icons.currency_rupee,
              text:
                  'Only Refund is supported for one-time order deliveries.',
            ),
            const SizedBox(height: 8),
            const _InfoRow(
              icon: Icons.currency_rupee,
              label: 'Request type',
              value: 'Refund',
            ),
          ] else
            SegmentedButton<RefundReplacementType>(
              segments: const [
                ButtonSegment(
                  value: RefundReplacementType.refund,
                  label: Text('Refund'),
                  icon: Icon(Icons.currency_rupee),
                ),
                ButtonSegment(
                  value: RefundReplacementType.replacement,
                  label: Text('Replacement'),
                  icon: Icon(Icons.autorenew),
                ),
              ],
              selected: {_type},
              onSelectionChanged: state.isSaving
                  ? null
                  : (selection) => setState(() => _type = selection.first),
            ),
          const SizedBox(height: 20),
          TextField(
            controller: _reasonController,
            maxLength: 500,
            maxLines: 3,
            decoration: const InputDecoration(
              labelText: 'Reason',
              hintText: 'Tell us what went wrong with this delivery.',
              border: OutlineInputBorder(),
            ),
          ),
          TextField(
            controller: _remarksController,
            maxLength: 500,
            maxLines: 3,
            decoration: const InputDecoration(
              labelText: 'Additional remarks (optional)',
              border: OutlineInputBorder(),
            ),
          ),
          const SizedBox(height: 12),
          if (proofRequired) ...[
            const _Notice(
              icon: Icons.photo_camera_outlined,
              text:
                  'A proof image is required for this request. Please attach a clear photo of the delivered product showing the issue.',
            ),
            const SizedBox(height: 8),
            _ProofImagePicker(
              candidate: _proof,
              enabled: !state.isSaving,
              onPick: _pickProof,
              onClear: () => setState(() => _proof = null),
            ),
            const SizedBox(height: 16),
          ] else if (eligibility.isFailedDeliveryWithoutProduct)
            const _Notice(
              icon: Icons.image_not_supported_outlined,
              text:
                  'No photo is needed for this request. Our records confirm this delivery failed and you did not receive the product, so you can submit without an attachment.',
            )
          else
            const _Notice(
              icon: Icons.image_not_supported_outlined,
              text: 'No proof image is required for this request.',
            ),
          if (state.errorMessage != null)
            _InlineError(message: state.errorMessage!),
          const SizedBox(height: 20),
          FilledButton.icon(
            onPressed: state.isSaving ? null : () => _submit(proofRequired),
            icon: state.isSaving
                ? const _ButtonProgress()
                : const Icon(Icons.send_outlined),
            label: const Text('Submit request'),
          ),
        ],
      ),
    );
  }

  Future<void> _pickProof() async {
    final candidate = await _pickProofImage();
    if (candidate == null || !mounted) return;
    setState(() => _proof = candidate);
  }

  Future<void> _submit(bool proofRequired) async {
    final reason = _reasonController.text.trim();
    if (reason.isEmpty) {
      _showMessage('Enter a reason for your request.');
      return;
    }
    if (proofRequired && _proof == null) {
      _showMessage('Attach a proof image before submitting.');
      return;
    }

    // Stage 2.1: evidence rides with the submission in a single atomic server
    // transaction — no separate post-submit upload. The server re-derives the
    // requirement and rejects photo-required tickets without valid evidence.
    final controller = ref.read(refundReplacementControllerProvider.notifier);
    final submitted = await controller.submitRequest(
      widget.deliveryId,
      // Refund-only sources always submit Refund, regardless of local state.
      type: _isRefundOnly ? RefundReplacementType.refund : _type,
      reason: reason,
      remarks: _remarksController.text.trim().isEmpty
          ? null
          : _remarksController.text.trim(),
      milkTestId: widget.milkTestId,
      imageBytes: _proof?.bytes,
      imageFileName: _proof?.fileName,
      imageContentType: _proof?.contentType,
    );
    if (!submitted || !mounted) return;
    _showMessage('Request submitted.');
    context.pop();
  }
}

class _ProofImagePicker extends StatelessWidget {
  const _ProofImagePicker({
    required this.candidate,
    required this.enabled,
    required this.onPick,
    required this.onClear,
  });

  final _RefundReplacementImageCandidate? candidate;
  final bool enabled;
  final VoidCallback onPick;
  final VoidCallback onClear;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text('Proof image (required)',
          style: Theme.of(context).textTheme.titleMedium),
      const SizedBox(height: 8),
      if (candidate == null)
        OutlinedButton.icon(
          onPressed: enabled ? onPick : null,
          icon: const Icon(Icons.add_a_photo_outlined),
          label: const Text('Attach proof image'),
        )
      else
        Card(
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                ClipRRect(
                  borderRadius: BorderRadius.circular(12),
                  child: Image.memory(
                    candidate!.bytes,
                    fit: BoxFit.contain,
                    height: 200,
                    errorBuilder: (_, _, _) => const SizedBox(
                      height: 120,
                      child: Center(child: Text('Preview unavailable')),
                    ),
                  ),
                ),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: enabled ? onPick : null,
                        icon: const Icon(Icons.refresh),
                        label: const Text('Replace'),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextButton.icon(
                        onPressed: enabled ? onClear : null,
                        icon: const Icon(Icons.delete_outline),
                        label: const Text('Remove'),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
    ],
  );
}

/// Customer "my refund/replacement requests" list.
class CustomerRefundReplacementListScreen extends ConsumerStatefulWidget {
  const CustomerRefundReplacementListScreen({super.key});

  @override
  ConsumerState<CustomerRefundReplacementListScreen> createState() =>
      _CustomerRefundReplacementListScreenState();
}

class _CustomerRefundReplacementListScreenState
    extends ConsumerState<CustomerRefundReplacementListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(refundReplacementControllerProvider.notifier)
      .loadCustomerRequests();

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(refundReplacementControllerProvider);
    Widget body;
    if (state.isUnauthorized) {
      body = const UnauthorizedStatePanel();
    } else if (state.isLoading && state.customerRequests.isEmpty) {
      body = const LoadingStatePanel(message: 'Loading your requests...');
    } else if (state.isOffline && state.customerRequests.isEmpty) {
      body = OfflineStatePanel(onRetry: _load);
    } else if (state.errorMessage != null && state.customerRequests.isEmpty) {
      body = ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    } else if (state.customerRequests.isEmpty) {
      body = const EmptyStatePanel(
        title: 'No requests yet',
        message: 'Refund or replacement requests you raise for your deliveries will appear here.',
      );
    } else {
      body = RefreshIndicator(
        onRefresh: _load,
        child: ListView.separated(
          padding: DoodhSpacing.pagePadding,
          itemCount: state.customerRequests.length,
          separatorBuilder: (_, _) => const SizedBox(height: 12),
          itemBuilder: (context, index) {
            final request = state.customerRequests[index];
            return _CustomerRequestCard(
              request: request,
              onTap: () =>
                  context.push('/refund-replacements/${request.requestId}'),
            );
          },
        ),
      );
    }
    return Scaffold(
      appBar: AppBar(title: const Text('Support tickets')),
      bottomNavigationBar: const CustomerBottomNavigation(
        currentPath: '/refund-replacements',
      ),
      body: body,
    );
  }
}

/// Customer request detail. Read-only status view for the requesting customer.
class CustomerRefundReplacementDetailScreen extends ConsumerStatefulWidget {
  const CustomerRefundReplacementDetailScreen({
    super.key,
    required this.requestId,
  });

  final String requestId;

  @override
  ConsumerState<CustomerRefundReplacementDetailScreen> createState() =>
      _CustomerRefundReplacementDetailScreenState();
}

class _CustomerRefundReplacementDetailScreenState
    extends ConsumerState<CustomerRefundReplacementDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(refundReplacementControllerProvider.notifier)
      .loadCustomerRequest(widget.requestId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(refundReplacementControllerProvider);
    final request = state.selectedCustomerRequest?.requestId == widget.requestId
        ? state.selectedCustomerRequest
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Request details')),
      bottomNavigationBar: CustomerBottomNavigation(
        currentPath: '/refund-replacements/${widget.requestId}',
      ),
      body: request == null
          ? _missingBody(state)
          : _RefundReplacementDetailBody(request: request),
    );
  }

  Widget _missingBody(RefundReplacementState state) {
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
      message: 'This request is not available to your account.',
    );
  }
}

class _RefundReplacementDetailBody extends ConsumerWidget {
  const _RefundReplacementDetailBody({required this.request});

  final RefundReplacementRequest request;

  @override
  Widget build(BuildContext context, WidgetRef ref) => ListView(
    padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
    children: [
      Row(
        children: [
          Expanded(
            child: Text(
              request.requestNumber,
              style: Theme.of(context).textTheme.titleLarge,
            ),
          ),
          DoodhStatusPill(
            label: request.status.label,
            tone: _statusTone(request.status),
          ),
        ],
      ),
      const SizedBox(height: 4),
      Text(
        '${request.type.label} · ${request.source.label}',
        style: Theme.of(context).textTheme.bodyMedium,
      ),
      const SizedBox(height: 16),
      _InfoRow(
        icon: Icons.receipt_long_outlined,
        label: 'Order',
        value: request.orderNumber ?? 'Subscription delivery',
      ),
      _InfoRow(
        icon: Icons.local_shipping_outlined,
        label: 'Delivery',
        // D1-D slice 7: internal GUIDs are never business references. A
        // subscription ticket without a delivery number renders a generic
        // label instead of the operational delivery's public identifier.
        value:
            request.deliveryNumber ??
            (request.orderNumber == null
                ? 'Subscription delivery'
                : request.deliveryId),
      ),
      _InfoRow(
        icon: Icons.storefront_outlined,
        label: 'Branch',
        value: '${request.branchName} (${request.branchCode})',
      ),
      _InfoRow(
        icon: Icons.event_available_outlined,
        label: 'Submitted',
        value: formatRefundReplacementDateTime(request.submittedAtUtc),
      ),
      if (request.deadlineUtc != null)
        _InfoRow(
          icon: Icons.schedule,
          label: 'Deadline',
          value: formatRefundReplacementDateTime(request.deadlineUtc!),
        ),
      if (request.milkTestPublicId != null)
        _InfoRow(
          icon: Icons.science_outlined,
          label: 'Milk test',
          value: request.milkTestPublicId!,
        ),
      const SizedBox(height: 16),
      if (request.failureReasonSnapshot?.isNotEmpty ?? false)
        _Remarks(
          label: 'Delivery failure',
          text: request.failureReasonSnapshot!,
        ),
      _Remarks(label: 'Reason', text: request.reason),
      if (request.remarks?.isNotEmpty ?? false)
        _Remarks(label: 'Your remarks', text: request.remarks!),
      _CustomerChoicePanel(request: request),
      const SizedBox(height: 8),
      Text('Decision', style: Theme.of(context).textTheme.titleMedium),
      const SizedBox(height: 8),
      if (request.decidedAtUtc != null)
        _InfoRow(
          icon: request.status == RefundReplacementStatus.rejected
              ? Icons.thumb_down_outlined
              : Icons.thumb_up_outlined,
          label: request.status == RefundReplacementStatus.rejected
              ? 'Rejected'
              : 'Approved',
          value:
              '${request.decidedByName ?? 'Support'} · '
              '${formatRefundReplacementDateTime(request.decidedAtUtc!)}',
        )
      else
        const _Notice(
          icon: Icons.hourglass_top,
          text: 'Your request is awaiting a decision from the support team.',
        ),
      if (request.decisionRemarks?.isNotEmpty ?? false)
        _Remarks(label: 'Decision remarks', text: request.decisionRemarks!),
      // Stage 2: the confirmed deduction outcome, rendered only from
      // authoritative API fields. Approval alone is never presented as a
      // wallet credit — only a completed ticket names the refunded result.
      if ((request.isOrderRefundTicket ||
                  request.isSubscriptionDeliveryFailRefund) &&
          request.appliedDeductionPercentage != null)
        _InfoRow(
          icon: request.deductionWaived
              ? Icons.money_off_outlined
              : Icons.percent_outlined,
          label: 'Deduction decision',
          value: request.deductionWaived
              ? 'Deduction waived'
              : 'Deduction of ${request.appliedDeductionPercentage!.toStringAsFixed(2)}% applied',
        ),
      // D1-D slice 7: the persisted replacement date survives reloads
      // because it is read from the backend on every load, never from the
      // approval response. The delivery number is the real public
      // DELIVERY-series reference once materialized — internal GUIDs are
      // never shown as business references.
      if (request.replacementDate != null)
        _InfoRow(
          icon: Icons.event_repeat_outlined,
          label: 'Replacement date',
          value: formatRefundReplacementDate(request.replacementDate!),
        ),
      if (request.replacementDeliveryNumber != null)
        _InfoRow(
          icon: Icons.local_shipping_outlined,
          label: 'Replacement delivery',
          value: request.replacementDeliveryNumber!,
        ),
      if (request.status == RefundReplacementStatus.failed)
        const _Notice(
          icon: Icons.cancel_outlined,
          text:
              'This ticket was closed without delivery. The replacement '
              'could not be delivered, so the refund-only remedy has been '
              'resolved instead. No further re-delivery is available for '
              'this failure.',
        ),
      // Legacy remediation: the unsupported one-time Replacement request
      // was replaced by a supported Refund request. The linked Refund
      // ticket number is shown as text only — the customer API exposes no
      // public id for the replacement ticket, so there is no deep link.
      // Nothing here claims money already moved; the Refund ticket's own
      // wallet settlement (on completion) is the only refund proof.
      if (request.isSuperseded) ...[
        const _Notice(
          icon: Icons.swap_horiz_outlined,
          text:
              'This replacement request is not supported for one-time '
              'orders, so it was superseded by a Refund request for the '
              'same delivery. Refund is the supported remedy. No refund '
              'has been issued by this change — watch the linked Refund '
              'request for the wallet settlement.',
        ),
        if (request.supersededByRequestNumber != null)
          _InfoRow(
            icon: Icons.currency_rupee_outlined,
            label: 'Refund request',
            value: request.supersededByRequestNumber!,
          ),
      ],
      // The Refund half of a remediation pair names the legacy ticket it
      // replaced. The customer API returns the legacy ticket's public id,
      // so navigation to the superseded request is authorized.
      if (request.isRemediationRefund &&
          request.supersedesRequestNumber != null &&
          request.supersedesRequestPublicId != null)
        _LinkedRefundReplacementRow(
          icon: Icons.history_outlined,
          label: 'Supersedes',
          value: request.supersedesRequestNumber!,
          onTap: () => context.push(
            '/refund-replacements/${request.supersedesRequestPublicId}',
          ),
        ),
      // D1-D slice 7.1: subscription-cancellation resolution is distinct
      // from replacement failure — voided, refunded, never delivered, and
      // never presented as a successful fulfillment.
      if (request.status == RefundReplacementStatus.cancelled)
        const _Notice(
          icon: Icons.cancel_outlined,
          text:
              'Your subscription was cancelled while this replacement was '
              'still pending. The replacement was voided and the refund '
              'has been resolved. No further action is needed.',
        ),
      if (request.completedAtUtc != null) ...[
        const SizedBox(height: 8),
        _InfoRow(
          icon:
              request.status == RefundReplacementStatus.failed ||
                  request.status == RefundReplacementStatus.cancelled
              ? Icons.cancel_outlined
              : Icons.task_alt,
          label:
              request.status == RefundReplacementStatus.failed ||
                  request.status == RefundReplacementStatus.cancelled
              ? 'Closed'
              : 'Completed',
          value:
              '${request.completedByName ?? 'Support'} · '
              '${formatRefundReplacementDateTime(request.completedAtUtc!)}',
        ),
        if (request.completionRemarks?.isNotEmpty ?? false)
          _Remarks(
            label: 'Completion remarks',
            text: request.completionRemarks!,
          ),
      ],
      if (request.images.isNotEmpty) ...[
        const SizedBox(height: 20),
        Text('Proof images', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),
        _AuthenticatedImages(
          requestId: request.requestId,
          images: request.images,
        ),
      ],
    ],
  );
}

class _CustomerRequestCard extends StatelessWidget {
  const _CustomerRequestCard({required this.request, required this.onTap});

  final RefundReplacementRequest request;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Card(
    child: InkWell(
      borderRadius: DoodhRadii.md,
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    request.requestNumber,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                DoodhStatusPill(
                  label: request.status.label,
                  tone: _statusTone(request.status),
                ),
              ],
            ),
            const SizedBox(height: 6),
            Text('${request.type.label} · ${request.source.label}'),
            const SizedBox(height: 4),
            Text(
              request.orderNumber == null
                  ? 'Subscription delivery ${request.deliveryNumber ?? ''}'
                        .trim()
                  : 'Order ${request.orderNumber}',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            if (request.status.isAwaitingChoice)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  request.customerChoice == null
                      ? 'Awaiting your choice'
                      : 'You chose ${request.customerChoice!.label} · Waiting for approval',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            // D1-D slice 7: the persisted replacement date on approved
            // re-delivery tickets, read from the backend list (not the
            // approval response), plus the real delivery number once the
            // replacement materializes.
            if (request.replacementDate != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  'Re-delivery on '
                  '${formatRefundReplacementDate(request.replacementDate!)}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            if (request.replacementDeliveryNumber != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  'Delivery ${request.replacementDeliveryNumber}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            const SizedBox(height: 4),
            Text(
              formatRefundReplacementDateTime(request.submittedAtUtc),
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ],
        ),
      ),
    ),
  );
}

class _AuthenticatedImages extends ConsumerStatefulWidget {
  const _AuthenticatedImages({required this.requestId, required this.images});

  final String requestId;
  final List<RefundReplacementImage> images;

  @override
  ConsumerState<_AuthenticatedImages> createState() =>
      _AuthenticatedImagesState();
}

class _AuthenticatedImagesState extends ConsumerState<_AuthenticatedImages> {
  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 12,
    runSpacing: 12,
    children: widget.images
        .map(
          (image) => _AuthenticatedImageTile(
            requestId: widget.requestId,
            image: image,
          ),
        )
        .toList(growable: false),
  );
}

class _AuthenticatedImageTile extends ConsumerWidget {
  const _AuthenticatedImageTile({required this.requestId, required this.image});

  final String requestId;
  final RefundReplacementImage image;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return SizedBox(
      width: 140,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ClipRRect(
            borderRadius: BorderRadius.circular(12),
            child: _ImageContent(requestId: requestId, image: image),
          ),
          const SizedBox(height: 4),
          Text(
            image.fileName,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ],
      ),
    );
  }
}

class _ImageContent extends ConsumerWidget {
  const _ImageContent({required this.requestId, required this.image});

  final String requestId;
  final RefundReplacementImage image;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return FutureBuilder<ApiByteResponse>(
      future: _loadBytes(ref),
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const SizedBox(
            height: 120,
            child: Center(child: CircularProgressIndicator()),
          );
        }
        if (snapshot.hasError || snapshot.data == null) {
          return const SizedBox(
            height: 120,
            child: Center(child: Icon(Icons.lock_outline, size: 32)),
          );
        }
        return Image.memory(
          snapshot.data!.bytes,
          fit: BoxFit.cover,
          height: 120,
          errorBuilder: (_, _, _) => const SizedBox(
            height: 120,
            child: Center(child: Text('Preview unavailable')),
          ),
        );
      },
    );
  }

  Future<ApiByteResponse> _loadBytes(WidgetRef ref) {
    final token = ref.read(sessionControllerProvider).session?.accessToken;
    if (token == null) {
      throw const RefundReplacementImageUnauthenticatedException();
    }
    return ref
        .read(refundReplacementRepositoryProvider)
        .getImageContent(token, requestId, image.imageId);
  }
}

class _Notice extends StatelessWidget {
  const _Notice({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 18, color: DoodhColors.muted),
        const SizedBox(width: 8),
        Expanded(child: Text(text)),
      ],
    ),
  );
}

class _InfoRow extends StatelessWidget {
  const _InfoRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 18, color: DoodhColors.muted),
        const SizedBox(width: 8),
        SizedBox(
          width: 110,
          child: Text(label, style: const TextStyle(color: DoodhColors.muted)),
        ),
        Expanded(child: Text(value)),
      ],
    ),
  );
}

/// Tappable info row for an authorized linked-ticket reference. Used only
/// when the API returns the linked ticket's public id; otherwise the number
/// is rendered as plain text via [_InfoRow] and never deep-linked.
class _LinkedRefundReplacementRow extends StatelessWidget {
  const _LinkedRefundReplacementRow({
    required this.icon,
    required this.label,
    required this.value,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final String value;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 18, color: DoodhColors.muted),
        const SizedBox(width: 8),
        SizedBox(
          width: 110,
          child: Text(label, style: const TextStyle(color: DoodhColors.muted)),
        ),
        Expanded(
          child: InkWell(
            onTap: onTap,
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    value,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.primary,
                      decoration: TextDecoration.underline,
                    ),
                  ),
                ),
                const Icon(Icons.open_in_new, size: 16),
              ],
            ),
          ),
        ),
      ],
    ),
  );
}

class _Remarks extends StatelessWidget {
  const _Remarks({required this.label, required this.text});

  final String label;
  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: const TextStyle(color: DoodhColors.muted, fontSize: 12),
        ),
        const SizedBox(height: 2),
        Text(text),
      ],
    ),
  );
}

class _InlineError extends StatelessWidget {
  const _InlineError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 12),
    child: Text(
      message,
      style: TextStyle(color: Theme.of(context).colorScheme.error),
    ),
  );
}

class _ButtonProgress extends StatelessWidget {
  const _ButtonProgress();

  @override
  Widget build(BuildContext context) => const SizedBox(
    width: 16,
    height: 16,
    child: CircularProgressIndicator(strokeWidth: 2),
  );
}

/// D1-B: customer choice panel for a delivery-fail ticket awaiting choice.
/// Recording a choice only stores the selection and routes the ticket into
/// the normal approval workflow. This panel never calculates refund amounts,
/// applies deductions, credits wallets, or creates replacement deliveries.
class _CustomerChoicePanel extends ConsumerWidget {
  const _CustomerChoicePanel({required this.request});

  final RefundReplacementRequest request;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final choice = request.customerChoice;
    final awaiting = request.status.isAwaitingChoice;
    // Legacy tickets that already resolved show the recorded choice (if any)
    // as context above the staff decision outcome.
    if (!awaiting) {
      if (choice == null) return const SizedBox.shrink();
      // D1-D slice 7: a failed replacement is terminal — closed without
      // delivery with the refund-only remedy resolved. No further
      // re-delivery is offered here (the choice buttons below only render
      // while awaiting), and the backend stays authoritative. A
      // subscription-cancellation resolution (slice 7.1) is terminal too
      // and gets its own copy below.
      if (request.status == RefundReplacementStatus.cancelled) {
        return Padding(
          padding: const EdgeInsets.only(top: 12),
          child: _Notice(
            icon: Icons.cancel_outlined,
            text:
                'You chose ${choice.label}. Your subscription was cancelled '
                'while the replacement was still pending, so it was voided '
                'and the refund has been resolved. No further action is '
                'needed.',
          ),
        );
      }
      if (request.status == RefundReplacementStatus.failed) {
        return Padding(
          padding: const EdgeInsets.only(top: 12),
          child: _Notice(
            icon: Icons.cancel_outlined,
            text:
                'You chose ${choice.label}. The replacement could not be '
                'delivered, so this ticket was closed without delivery and '
                'the refund-only remedy has been resolved. No further '
                're-delivery is available for this failure.',
          ),
        );
      }
      return Padding(
        padding: const EdgeInsets.only(top: 12),
        child: _Notice(
          icon: choice == CustomerChoice.refund
              ? Icons.currency_rupee
              : Icons.autorenew,
          text: 'You chose ${choice.label}.',
        ),
      );
    }
    final state = ref.watch(refundReplacementControllerProvider);
    final expired = request.isChoiceExpired;
    final locked = choice != null;
    return Card(
      margin: const EdgeInsets.only(top: 16),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Choose how to resolve this delivery failure',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 4),
            if (request.deadlineUtc != null)
              Text(
                'Choose before '
                '${formatRefundReplacementDateTime(request.deadlineUtc!)}',
                style: Theme.of(context).textTheme.bodySmall,
              ),
            const SizedBox(height: 12),
            // Stage 3: the customer must know the price of choosing Refund
            // before choosing — the frozen customer-fault deduction
            // percentage from the authoritative ticket snapshot. Support
            // reviews each case at approval and can waive it.
            if (request.isSubscriptionDeliveryFailTicket &&
                request.deliveryFailDeductionPercentageSnapshot != null)
              _Notice(
                icon: Icons.percent_outlined,
                text:
                    'If you choose Refund, a customer-fault deduction of '
                    '${request.deliveryFailDeductionPercentageSnapshot!.toStringAsFixed(2)}% '
                    'will be applied to your refund (for example, when no '
                    'one was available to receive the delivery). Support '
                    'reviews each case and can waive the deduction.',
              ),
            if (locked) ...[
              _Notice(
                icon: choice == CustomerChoice.refund
                    ? Icons.currency_rupee
                    : Icons.autorenew,
                text: 'You chose ${choice.label}.',
              ),
              const _Notice(
                icon: Icons.hourglass_top,
                text: 'Waiting for approval',
              ),
            ] else if (expired) ...[
              const _Notice(
                icon: Icons.timer_off_outlined,
                text:
                    'The selection window has expired. Your ticket is waiting '
                    'to be resolved and no further choice can be made here.',
              ),
            ],
            if (state.errorMessage != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  state.errorMessage!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ),
            Row(
              children: [
                Expanded(
                  child: _ChoiceButton(
                    label: 'Refund',
                    icon: Icons.currency_rupee,
                    selected: choice == CustomerChoice.refund,
                    enabled: !locked && !expired && !state.isSaving,
                    saving: state.isSaving,
                    onPressed: () =>
                        _choose(ref, context, CustomerChoice.refund),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _ChoiceButton(
                    label: 'Re-Delivery',
                    icon: Icons.autorenew,
                    selected: choice == CustomerChoice.reDelivery,
                    enabled: !locked && !expired && !state.isSaving,
                    saving: state.isSaving,
                    onPressed: () =>
                        _choose(ref, context, CustomerChoice.reDelivery),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _choose(
    WidgetRef ref,
    BuildContext context,
    CustomerChoice choice,
  ) async {
    final ok = await ref
        .read(refundReplacementControllerProvider.notifier)
        .chooseCustomerChoice(request.requestId, choice);
    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          ok
              ? 'Your choice has been recorded. Waiting for approval.'
              : ref.read(refundReplacementControllerProvider).errorMessage ??
                    'Your choice could not be recorded.',
        ),
      ),
    );
  }
}

class _ChoiceButton extends StatelessWidget {
  const _ChoiceButton({
    required this.label,
    required this.icon,
    required this.selected,
    required this.enabled,
    required this.saving,
    required this.onPressed,
  });

  final String label;
  final IconData icon;
  final bool selected;
  final bool enabled;
  final bool saving;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    final child = saving
        ? const SizedBox(
            width: 16,
            height: 16,
            child: CircularProgressIndicator(strokeWidth: 2),
          )
        : Icon(icon);
    if (selected) {
      return FilledButton.icon(
        onPressed: null,
        icon: child,
        label: Text(label),
      );
    }
    return OutlinedButton.icon(
      onPressed: enabled ? onPressed : null,
      icon: child,
      label: Text(label),
    );
  }
}

DoodhStatusTone _statusTone(RefundReplacementStatus status) => switch (status) {
  RefundReplacementStatus.pending => DoodhStatusTone.warning,
  RefundReplacementStatus.awaitingCustomerChoice => DoodhStatusTone.warning,
  RefundReplacementStatus.approved => DoodhStatusTone.success,
  RefundReplacementStatus.rejected => DoodhStatusTone.error,
  RefundReplacementStatus.completed => DoodhStatusTone.success,
  RefundReplacementStatus.failed => DoodhStatusTone.error,
  RefundReplacementStatus.cancelled => DoodhStatusTone.neutral,
  // Superseded is terminal but neutral: it is not a failure, a refund
  // settlement, or a rejection — the linked Refund ticket carries on.
  RefundReplacementStatus.superseded => DoodhStatusTone.neutral,
  RefundReplacementStatus.unknown => DoodhStatusTone.neutral,
};

String formatRefundReplacementDateTime(DateTime value) {
  final local = toIndiaTime(value);
  String two(int number) => number.toString().padLeft(2, '0');
  return '${two(local.day)}/${two(local.month)}/${local.year} '
      '${two(local.hour)}:${two(local.minute)}';
}

/// Formats a date-only value (replacement proposal/approval) as DD/MM/YYYY
/// without timezone conversion, so the server-sent calendar day is shown
/// exactly.
String formatRefundReplacementDate(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/'
    '${value.month.toString().padLeft(2, '0')}/'
    '${value.year.toString().padLeft(4, '0')}';

/// Resolves the content type for a picked proof image, accepting only JPEG and
/// PNG. Returns null when the file type is unsupported.
String? resolveRefundReplacementImageContentType(
  String fileName,
  String? declaredType,
) {
  final normalized = (declaredType ?? '').toLowerCase().trim();
  if (normalized == 'image/jpeg' || normalized == 'image/png') {
    return normalized;
  }
  final lower = fileName.toLowerCase();
  if (lower.endsWith('.jpg') || lower.endsWith('.jpeg')) return 'image/jpeg';
  if (lower.endsWith('.png')) return 'image/png';
  return null;
}

/// Branch-scoped staff/manager list of refund and replacement requests.
class StaffRefundReplacementListScreen extends ConsumerStatefulWidget {
  const StaffRefundReplacementListScreen({super.key, this.branchId});

  /// When provided, the list is scoped to this branch. Managers with multiple
  /// branches pass the branch being viewed; support staff leave it null.
  final int? branchId;

  @override
  ConsumerState<StaffRefundReplacementListScreen> createState() =>
      _StaffRefundReplacementListScreenState();
}

class _StaffRefundReplacementListScreenState
    extends ConsumerState<StaffRefundReplacementListScreen> {
  RefundReplacementStatus? _status;

  /// Client-side submission-day filter (the staff list API supports only
  /// branch + status server-side). Null means all dates.
  DateTime? _date;

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(refundReplacementControllerProvider.notifier)
      .loadStaffPage(branchId: widget.branchId, status: _status);

  void _selectStatus(RefundReplacementStatus? status) {
    setState(() => _status = status);
    _load();
  }

  static bool _isSameDay(DateTime a, DateTime b) =>
      a.year == b.year && a.month == b.month && a.day == b.day;

  List<RefundReplacementListItem> _visibleItems(
    List<RefundReplacementListItem> items,
  ) {
    final date = _date;
    if (date == null) return items;
    return items
        .where((item) => _isSameDay(item.submittedAtUtc, date))
        .toList(growable: false);
  }

  Future<void> _chooseDate(BuildContext dialogContext) async {
    final selected = await showDatePicker(
      context: dialogContext,
      initialDate: _date ?? DateTime.now(),
      firstDate: DateTime(2020),
      lastDate: DateTime(2100),
    );
    if (selected == null) return;
    setState(() => _date = selected);
  }

  /// Date and status filters live in this sheet behind the AppBar icons.
  /// The front screen keeps only the request list.
  Future<void> _openFilters() async {
    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      builder: (sheetContext) => StatefulBuilder(
        builder: (sheetContext, setSheetState) => SafeArea(
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
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton.icon(
                        icon: const Icon(
                          Icons.calendar_today_outlined,
                          size: 18,
                        ),
                        label: Text(
                          _date == null
                              ? 'All dates'
                              : formatRefundReplacementDate(_date!),
                          overflow: TextOverflow.ellipsis,
                        ),
                        onPressed: () async {
                          await _chooseDate(sheetContext);
                          setSheetState(() {});
                        },
                      ),
                    ),
                    if (_date != null) ...[
                      const SizedBox(width: 8),
                      TextButton(
                        onPressed: () => setSheetState(() => _date = null),
                        child: const Text('Clear date'),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 12),
                Text(
                  'Status',
                  style: Theme.of(sheetContext).textTheme.titleSmall
                      ?.copyWith(fontWeight: FontWeight.w800),
                ),
                _StaffStatusFilter(
                  selected: _status,
                  onSelected: (status) {
                    setSheetState(() => _status = status);
                    _selectStatus(status);
                  },
                ),
                const SizedBox(height: 4),
                TextButton.icon(
                  onPressed: _status != null || _date != null
                      ? () {
                          setSheetState(() {
                            _status = null;
                            _date = null;
                          });
                          _selectStatus(null);
                        }
                      : null,
                  icon: const Icon(Icons.filter_alt_off_outlined, size: 18),
                  label: const Text('Reset filters'),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    if (mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(refundReplacementControllerProvider);
    final items = _visibleItems(state.staffPage?.items ?? const []);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Refund / replacement requests'),
        actions: [
          IconButton(
            key: const ValueKey('staff-refund-date-filter-action'),
            tooltip: 'Filter by date',
            icon: Badge(
              isLabelVisible: _date != null,
              child: const Icon(Icons.calendar_today_outlined),
            ),
            onPressed: () => _chooseDate(context),
          ),
          IconButton(
            key: const ValueKey('staff-refund-filters-action'),
            tooltip: 'Filters',
            icon: Badge(
              isLabelVisible: _status != null || _date != null,
              child: const Icon(Icons.tune_outlined),
            ),
            onPressed: _openFilters,
          ),
        ],
      ),
      body: Column(
        children: [Expanded(child: _body(state, items))],
      ),
    );
  }

  Widget _body(
    RefundReplacementState state,
    List<RefundReplacementListItem> items,
  ) {
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isLoading && items.isEmpty) {
      return const LoadingStatePanel(message: 'Loading requests...');
    }
    if (state.isOffline && items.isEmpty) {
      return OfflineStatePanel(onRetry: _load);
    }
    if (state.errorMessage != null && items.isEmpty) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    }
    if (items.isEmpty) {
      return const EmptyStatePanel(
        title: 'No requests found',
        message: 'No refund or replacement requests match these filters.',
      );
    }
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView.separated(
        padding: DoodhSpacing.pagePadding,
        itemCount: items.length,
        separatorBuilder: (_, _) => const SizedBox(height: 12),
        itemBuilder: (context, index) {
          final item = items[index];
          return _StaffRequestCard(
            item: item,
            onTap: () =>
                context.push('/staff/refund-replacements/${item.requestId}'),
          );
        },
      ),
    );
  }
}

class _StaffStatusFilter extends StatelessWidget {
  const _StaffStatusFilter({required this.selected, required this.onSelected});

  final RefundReplacementStatus? selected;
  final ValueChanged<RefundReplacementStatus?> onSelected;

  static const _options = <RefundReplacementStatus?>[
    null,
    RefundReplacementStatus.pending,
    RefundReplacementStatus.awaitingCustomerChoice,
    RefundReplacementStatus.approved,
    RefundReplacementStatus.rejected,
    RefundReplacementStatus.completed,
    RefundReplacementStatus.failed,
    RefundReplacementStatus.cancelled,
    RefundReplacementStatus.superseded,
  ];

  @override
  Widget build(BuildContext context) => SingleChildScrollView(
    scrollDirection: Axis.horizontal,
    padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
    child: Row(
      children: [
        for (final option in _options) ...[
          ChoiceChip(
            label: Text(option?.label ?? 'All'),
            selected: selected == option,
            onSelected: (_) => onSelected(option),
          ),
          const SizedBox(width: 8),
        ],
      ],
    ),
  );
}

class _StaffRequestCard extends StatelessWidget {
  const _StaffRequestCard({required this.item, required this.onTap});

  final RefundReplacementListItem item;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Card(
    child: InkWell(
      borderRadius: DoodhRadii.md,
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    item.requestNumber,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                DoodhStatusPill(
                  label: item.status.label,
                  tone: _statusTone(item.status),
                ),
              ],
            ),
            const SizedBox(height: 6),
            Text('${item.type.label} · ${item.source.label}'),
            const SizedBox(height: 4),
            Text(
              item.orderNumber == null
                  ? 'Subscription delivery · ${item.customerName}'
                  : 'Order ${item.orderNumber} · ${item.customerName}',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            if (item.customerChoice != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  'Customer chose ${item.customerChoice!.label}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            // Legacy remediation pair linkage, rendered from the
            // authoritative list field. The superseded legacy row names
            // its replacement Refund ticket; a remediation Refund row
            // names the legacy ticket it supersedes. Numbers only — the
            // list API exposes no linked public id, so there is no
            // deep link here. Never presented as money already moved.
            if (item.isRemediationRefund &&
                item.supersedesRequestNumber != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  'Supersedes ${item.supersedesRequestNumber}',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            const SizedBox(height: 4),
            Text(
              '${item.branchName} (${item.branchCode})',
              style: Theme.of(context).textTheme.bodySmall,
            ),
            const SizedBox(height: 4),
            Text(
              formatRefundReplacementDateTime(item.submittedAtUtc),
              style: Theme.of(context).textTheme.bodySmall,
            ),
            if (item.proofImageRequired && item.imageCount == 0)
              const Padding(
                padding: EdgeInsets.only(top: 6),
                child: Text(
                  'Proof image missing',
                  style: TextStyle(color: DoodhColors.muted, fontSize: 12),
                ),
              ),
          ],
        ),
      ),
    ),
  );
}

/// Branch-scoped staff/manager detail for a single request, with navigation to
/// the linked order, delivery, and milk test and approve/reject/complete
/// actions for permitted roles.
class StaffRefundReplacementDetailScreen extends ConsumerStatefulWidget {
  const StaffRefundReplacementDetailScreen({
    super.key,
    required this.requestId,
  });

  final String requestId;

  @override
  ConsumerState<StaffRefundReplacementDetailScreen> createState() =>
      _StaffRefundReplacementDetailScreenState();
}

class _StaffRefundReplacementDetailScreenState
    extends ConsumerState<StaffRefundReplacementDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(refundReplacementControllerProvider.notifier)
      .loadStaffRequest(widget.requestId);

  bool get _canManage {
    final permissions =
        ref.read(sessionControllerProvider).session?.user.permissions ??
        const [];
    return permissions.contains('REFUND_REPLACEMENT.MANAGE_BRANCH');
  }

  /// Employee-origin policy: only an Owner or System Administrator may
  /// decide a request that belongs to a staff account. The backend
  /// enforces this; the UI only hides the actions to stay consistent.
  bool get _isAdmin {
    final roles =
        ref.read(sessionControllerProvider).session?.user.roles ??
        const [];
    return roles.contains('OWNER') || roles.contains('SYSTEM_ADMIN');
  }

  /// Forwards the staff decision plus the remarks collected in the confirm
  /// dialog. The dialog returns null on cancel and a (possibly empty)
  /// string on confirm; empty input is normalized to null so the backend
  /// stores no blank remarks. Without this the typed remarks never left
  /// the device and customers could not see them on their ticket.
  Future<void> _decide(String decision, [String? remarks]) async {
    final normalized = remarks != null && remarks.trim().isEmpty
        ? null
        : remarks?.trim();
    final controller = ref.read(refundReplacementControllerProvider.notifier);
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
      'approve' => 'Request approved.',
      'reject' => 'Request rejected.',
      _ => 'Request marked completed.',
    };
    ScaffoldMessenger.of(context)
        .showSnackBar(SnackBar(content: Text(message)));
  }

  /// Legacy remediation: collects the mandatory business justification,
  /// executes the single authorized backend operation, and refreshes both
  /// sides of the new pair. Duplicate submissions are blocked by
  /// [RefundReplacementState.isSaving] (button disabled plus a controller
  /// re-entry guard). On conflict or failure the authoritative detail is
  /// reloaded before another attempt is permitted — never retried blindly.
  /// Nothing here is executed automatically; staff confirm every run.
  Future<void> _remediate(RefundReplacementRequest request) async {
    final reason = await _promptRemediationReason(context, request: request);
    if (reason == null || !mounted) return;
    final controller = ref.read(refundReplacementControllerProvider.notifier);
    final pair = await controller.remediateLegacyReplacement(
      widget.requestId,
      reason: reason,
    );
    if (!mounted) return;
    if (pair != null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            'Remediation complete. Linked Refund request '
            '${pair.refundTicket.requestNumber} is Pending — '
            'no refund has been issued yet.',
          ),
        ),
      );
      // Refresh the superseded legacy detail, then open the new Refund
      // ticket so its Pending state and number are shown immediately.
      await controller.loadStaffRequest(widget.requestId);
      if (!mounted) return;
      context.push(
        '/staff/refund-replacements/${pair.refundTicket.requestId}',
      );
      return;
    }
    final state = ref.read(refundReplacementControllerProvider);
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          state.errorMessage ??
              'Remediation could not be completed. Reloaded the latest state.',
        ),
      ),
    );
    await controller.loadStaffRequest(widget.requestId);
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(refundReplacementControllerProvider);
    final request = state.selectedStaffRequest?.requestId == widget.requestId
        ? state.selectedStaffRequest
        : null;
    return Scaffold(
      appBar: AppBar(title: const Text('Request details')),
      body: request == null
          ? _missingBody(state)
          : Column(
              children: [
                Expanded(
                  child: _StaffRequestDetailBody(
                    request: request,
                    // Subscription tickets have no order: the Order row
                    // opens the authoritative linked delivery instead of a
                    // dead link, so staff always reach the fulfillment
                    // record (items/quantity for order-backed tickets, the
                    // delivery detail for subscription occurrences).
                    onOrderTap: request.orderPublicId == null
                        ? () => context.push(
                            '/staff/delivery/${request.deliveryId}',
                          )
                        : () => context.push(
                            '/staff/orders/${request.orderPublicId}',
                          ),
                    onDeliveryTap: () =>
                        context.push('/staff/delivery/${request.deliveryId}'),
                    onMilkTestTap: request.milkTestPublicId == null
                        ? null
                        : () => context.push(
                            '/staff/delivery/${request.deliveryId}/milk-test',
                          ),
                    // The refund half carries the legacy ticket's public
                    // id, so staff navigation to the superseded request is
                    // authorized. The superseded ticket itself exposes only
                    // the replacement number (no public id) and is shown
                    // as text — never deep-linked or inferred.
                    onLegacyTicketTap:
                        request.supersedesRequestPublicId == null
                        ? null
                        : () => context.push(
                            '/staff/refund-replacements/${request.supersedesRequestPublicId}',
                          ),
                  ),
                ),
                if (_canManage)
                  _StaffDecisionActions(
                    request: request,
                    isSaving: state.isSaving,
                    errorMessage: state.errorMessage,
                    onDecide: _decide,
                    onRemediate: request.isLegacyRemediationEligible
                        ? () => _remediate(request)
                        : null,
                    // Employee-originated tickets are hidden from
                    // non-administrators; the backend still enforces this.
                    employeeOriginBlocked:
                        request.isEmployeeOriginated && !_isAdmin,
                  ),
              ],
            ),
    );
  }

  Widget _missingBody(RefundReplacementState state) {
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
      message: 'This request is outside your branch scope.',
    );
  }
}

class _StaffRequestDetailBody extends StatelessWidget {
  const _StaffRequestDetailBody({
    required this.request,
    required this.onOrderTap,
    required this.onDeliveryTap,
    required this.onMilkTestTap,
    this.onLegacyTicketTap,
  });

  final RefundReplacementRequest request;
  final VoidCallback? onOrderTap;
  final VoidCallback? onDeliveryTap;
  final VoidCallback? onMilkTestTap;
  final VoidCallback? onLegacyTicketTap;

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
    children: [
      Row(
        children: [
          Expanded(
            child: Text(
              request.requestNumber,
              style: Theme.of(context).textTheme.titleLarge,
            ),
          ),
          DoodhStatusPill(
            label: request.status.label,
            tone: _statusTone(request.status),
          ),
        ],
      ),
      const SizedBox(height: 4),
      Text(
        '${request.type.label} · ${request.source.label}',
        style: Theme.of(context).textTheme.bodyMedium,
      ),
      const SizedBox(height: 12),
      _InfoRow(
        icon: Icons.person_outline,
        label: 'Customer',
        value: '${request.customerName} · ${request.customerMobile}',
      ),
      _InfoRow(
        icon: Icons.storefront_outlined,
        label: 'Branch',
        value: '${request.branchName} (${request.branchCode})',
      ),
      _InfoRow(
        icon: Icons.event_available_outlined,
        label: 'Submitted',
        value: formatRefundReplacementDateTime(request.submittedAtUtc),
      ),
      if (request.deadlineUtc != null)
        _InfoRow(
          icon: Icons.schedule,
          label: 'Deadline',
          value: formatRefundReplacementDateTime(request.deadlineUtc!),
        ),
      const SizedBox(height: 12),
      Text('Linked records', style: Theme.of(context).textTheme.titleMedium),
      const SizedBox(height: 4),
      _StaffLinkedRow(
        icon: Icons.receipt_long_outlined,
        label: 'Order',
        value: request.orderNumber ?? 'Subscription delivery',
        onTap: onOrderTap,
      ),
      if (request.customerChoice != null)
        _InfoRow(
          icon: request.customerChoice == CustomerChoice.refund
              ? Icons.currency_rupee
              : Icons.autorenew,
          label: 'Customer choice',
          value: request.customerChoiceAtUtc == null
              ? request.customerChoice!.label
              : '${request.customerChoice!.label} · '
                    '${formatRefundReplacementDateTime(request.customerChoiceAtUtc!)}',
        )
      else if (request.status.isAwaitingChoice)
        const _Notice(
          icon: Icons.hourglass_top,
          text: 'Waiting for the customer to choose Refund or Re-Delivery.',
        ),
      if (request.status == RefundReplacementStatus.failed)
        const _Notice(
          icon: Icons.cancel_outlined,
          text:
              'The replacement delivery failed, so this ticket was closed '
              'without delivery. The refund-only remedy was resolved instead. '
              'No further re-delivery is available for this failure.',
        ),
      if (request.status == RefundReplacementStatus.cancelled)
        const _Notice(
          icon: Icons.cancel_outlined,
          text:
              'The subscription was cancelled while the replacement was '
              'still pending, so the replacement was voided and the frozen '
              'entitlement refund was resolved instead. This is not a '
              'replacement failure.',
        ),
      // Legacy remediation: the unsupported one-time Replacement was
      // replaced by a supported Refund request. Shown as terminal but
      // never as Completed, Failed, Rejected, or Refunded — and never as
      // money already moved. The replacement number is text only (the
      // detail API exposes no linked public id, so no deep link).
      if (request.isSuperseded) ...[
        const _Notice(
          icon: Icons.swap_horiz_outlined,
          text:
              'This replacement request is not supported for one-time '
              'orders, so it was superseded by a Refund request for the '
              'same delivery. Refund is the supported remedy. Superseding '
              'issued no refund and credited nothing — the linked Refund '
              'ticket follows the normal approval and wallet-settlement '
              'workflow.',
        ),
        if (request.supersededByRequestNumber != null)
          _InfoRow(
            icon: Icons.currency_rupee_outlined,
            label: 'Refund request',
            value: request.supersededByRequestNumber!,
          ),
      ],
      // The Refund half of a remediation pair. Navigation to the legacy
      // ticket is offered only because the API returns its public id.
      if (request.isRemediationRefund &&
          request.supersedesRequestNumber != null)
        onLegacyTicketTap == null
            ? _InfoRow(
                icon: Icons.history_outlined,
                label: 'Supersedes',
                value: request.supersedesRequestNumber!,
              )
            : _StaffLinkedRow(
                icon: Icons.history_outlined,
                label: 'Supersedes',
                value: request.supersedesRequestNumber!,
                onTap: onLegacyTicketTap,
              ),
      _StaffLinkedRow(
        icon: Icons.local_shipping_outlined,
        label: 'Delivery',
        value: request.deliveryNumber ?? request.deliveryId,
        onTap: onDeliveryTap,
      ),
      if (request.milkTestPublicId != null)
        _StaffLinkedRow(
          icon: Icons.science_outlined,
          label: 'Milk test',
          value: request.milkTestPublicId!,
          onTap: onMilkTestTap,
        ),
      const SizedBox(height: 16),
      _Remarks(label: 'Reason', text: request.reason),
      if (request.remarks?.isNotEmpty ?? false)
        _Remarks(label: 'Customer remarks', text: request.remarks!),
      if (request.proofImageRequired) ...[
        const SizedBox(height: 8),
        const _Notice(
          icon: Icons.photo_outlined,
          text: 'A proof image is required for this request.',
        ),
      ],
      if (request.decidedAtUtc != null) ...[
        const SizedBox(height: 8),
        _InfoRow(
          icon: request.status == RefundReplacementStatus.rejected
              ? Icons.thumb_down_outlined
              : Icons.thumb_up_outlined,
          label: request.status == RefundReplacementStatus.rejected
              ? 'Rejected'
              : 'Approved',
          value:
              '${request.decidedByName ?? 'Support'} · '
              '${formatRefundReplacementDateTime(request.decidedAtUtc!)}',
        ),
      ],
      if (request.decisionRemarks?.isNotEmpty ?? false)
        _Remarks(label: 'Decision remarks', text: request.decisionRemarks!),
      // Stage 2: the persisted support deduction decision for order-backed
      // Refund tickets. Rendered only from authoritative API fields — the
      // UI never infers a deduction from the delivery state.
      if ((request.isOrderRefundTicket ||
                  request.isSubscriptionDeliveryFailRefund) &&
          request.appliedDeductionPercentage != null)
        _InfoRow(
          icon: request.deductionWaived
              ? Icons.money_off_outlined
              : Icons.percent_outlined,
          label: 'Customer-fault deduction',
          value: request.deductionWaived
              ? 'Waived (0%)'
                    '${request.deductionWaiverReason?.isNotEmpty ?? false ? ' · ${request.deductionWaiverReason}' : ''}'
              : '${request.appliedDeductionPercentage!.toStringAsFixed(2)}% applied',
        ),
      if (request.replacementDate != null)
        _InfoRow(
          icon: Icons.event_repeat_outlined,
          label: 'Replacement date',
          value: formatRefundReplacementDate(request.replacementDate!),
        ),
      if (request.completedAtUtc != null) ...[
        _InfoRow(
          icon:
              request.status == RefundReplacementStatus.failed ||
              request.status == RefundReplacementStatus.cancelled
              ? Icons.cancel_outlined
              : Icons.task_alt,
          label:
              request.status == RefundReplacementStatus.failed ||
              request.status == RefundReplacementStatus.cancelled
              ? 'Closed'
              : 'Completed',
          value:
              '${request.completedByName ?? 'Support'} · '
              '${formatRefundReplacementDateTime(request.completedAtUtc!)}',
        ),
        if (request.completionRemarks?.isNotEmpty ?? false)
          _Remarks(
            label: 'Completion remarks',
            text: request.completionRemarks!,
          ),
      ],
      if (request.images.isNotEmpty) ...[
        const SizedBox(height: 20),
        Text('Proof images', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),
        _AuthenticatedImages(
          requestId: request.requestId,
          images: request.images,
        ),
      ],
    ],
  );
}

class _StaffLinkedRow extends StatelessWidget {
  const _StaffLinkedRow({
    required this.icon,
    required this.label,
    required this.value,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final String value;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => InkWell(
    onTap: onTap,
    child: Padding(
      padding: const EdgeInsets.symmetric(vertical: 10),
      child: Row(
        children: [
          Icon(icon, size: 18, color: DoodhColors.muted),
          const SizedBox(width: 8),
          SizedBox(
            width: 110,
            child: Text(
              label,
              style: const TextStyle(color: DoodhColors.muted),
            ),
          ),
          Expanded(child: Text(value)),
          const SizedBox(width: 8),
          Icon(
            Icons.open_in_new,
            size: 16,
            color: onTap == null
                ? DoodhColors.muted.withValues(alpha: 0.4)
                : DoodhColors.muted,
          ),
        ],
      ),
    ),
  );
}

/// Approve / reject / complete actions shown to branch managers. Refund
/// settlement and replacement delivery creation remain explicit, separate
/// operational steps — this only advances the request's own lifecycle.
class _StaffDecisionActions extends StatelessWidget {
  const _StaffDecisionActions({
    required this.request,
    required this.isSaving,
    required this.errorMessage,
    required this.onDecide,
    this.onRemediate,
    this.employeeOriginBlocked = false,
  });

  final RefundReplacementRequest request;
  final bool isSaving;
  final String? errorMessage;
  final Future<void> Function(String decision, String? remarks) onDecide;

  /// Legacy remediation entry point. Non-null only when the loaded ticket
  /// is remediation-eligible from authoritative API fields (order-backed,
  /// approved Replacement, not already superseded) and the viewer may
  /// manage branch requests. The backend re-validates the Failed-delivery
  /// precondition and returns 409 otherwise.
  final Future<void> Function()? onRemediate;

  /// Employee-origin policy: true when this ticket belongs to a staff
  /// account and the viewer is not an Owner/System Administrator. All
  /// decision actions (including remediation) are replaced with an
  /// explanatory notice. Enforcement lives in the backend service guard.
  final bool employeeOriginBlocked;

  @override
  Widget build(BuildContext context) {
    // Pending normal requests plus delivery-fail tickets whose customer
    // choice is recorded are both awaiting the staff decision. Terminal
    // tickets (rejected, completed, failed) and choice-pending tickets with
    // no choice yet offer no actions — and a failed replacement is never
    // offered another re-delivery.
    final canDecide = request.isStaffDecidable && !employeeOriginBlocked;
    final isApproved = request.status == RefundReplacementStatus.approved;
    // Audit remediation B: a failed replacement is refund-only — rejecting
    // it would strand the original ticket, and the backend refuses the
    // rejection. Only Approve stays available, with an explicit explanation.
    final isRefundOnly = request.isRefundOnly;
    // Stage 2: one-time order Refund tickets follow the explicit deduction
    // decision flow. Replacement/re-delivery is never offered — the backend
    // policy is refund-only for one-time orders.
    final isOrderRefund = request.isOrderRefundTicket;
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
              if (canDecide && isRefundOnly) ...[
                const _Notice(
                  icon: Icons.info_outline,
                  text:
                      'Only the refund remedy is available for this failed '
                      'replacement. Reject is disabled — approve the refund '
                      'to resolve it.',
                ),
                const SizedBox(height: 12),
              ],
              if (canDecide && isOrderRefund) ...[
                const _Notice(
                  icon: Icons.currency_rupee,
                  text:
                      'Refund only — replacement/re-delivery is not available '
                      'for one-time orders. Approving records your deduction '
                      'decision; the wallet is credited only on completion.',
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton.icon(
                        onPressed: isSaving
                            ? null
                            : () async {
                                final remarks = await _promptStaffDecision(
                                  context,
                                  title: 'Reject request',
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
                                final approved =
                                    await _promptOrderRefundApproval(
                                      context,
                                      request: request,
                                    );
                                if (!context.mounted || !approved) return;
                                ScaffoldMessenger.of(context).showSnackBar(
                                  const SnackBar(
                                    content: Text(
                                      'Refund decision approved. No money has moved yet — '
                                      'wallet refunds settle on completion; bank '
                                      'transfers are confirmed separately.',
                                    ),
                                  ),
                                );
                              },
                        icon: const Icon(Icons.check),
                        label: const Text('Approve refund decision'),
                      ),
                    ),
                  ],
                ),
              ],
              if (canDecide && !isOrderRefund)
                Row(
                  children: [
                    if (!isRefundOnly)
                      Expanded(
                        child: OutlinedButton.icon(
                          onPressed: isSaving
                              ? null
                              : () async {
                                  final remarks = await _promptStaffDecision(
                                    context,
                                    title: 'Reject request',
                                    confirmLabel: 'Reject',
                                  );
                                  if (remarks == null) return;
                                  await onDecide('reject', remarks);
                                },
                          icon: const Icon(Icons.close),
                          label: const Text('Reject'),
                        ),
                      ),
                    if (!isRefundOnly) const SizedBox(width: 12),
                    Expanded(
                      child: FilledButton.icon(
                        onPressed: isSaving
                            ? null
                            : () async {
                                // The re-delivery dialog submits the approval
                                // itself (single flight, guarded against
                                // duplicates) through the shared controller, so
                                // the detail state refreshes and the approved
                                // date shows inline. Here only the confirmation
                                // is announced; nothing is submitted twice.
                                // Stage 3: a subscription failed-delivery
                                // Refund approval must carry support's explicit
                                // apply/waive deduction decision — the backend
                                // rejects the approval without it.
                                if (request.isSubscriptionDeliveryFailRefund &&
                                    request.deliveryFailDeductionPercentageSnapshot !=
                                        null) {
                                  final approved =
                                      await _promptSubscriptionDeductionApproval(
                                        context,
                                        request: request,
                                      );
                                  if (!context.mounted || !approved) return;
                                  ScaffoldMessenger.of(context).showSnackBar(
                                    const SnackBar(
                                      content: Text(
                                        'Refund decision approved. No money has moved yet — '
                                        'complete the ticket to credit the wallet.',
                                      ),
                                    ),
                                  );
                                } else if (request.isRedeliveryApproval) {
                                  final result =
                                      await _promptRedeliveryApproval(
                                        context,
                                        request: request,
                                      );
                                  if (result == null || !context.mounted) {
                                    return;
                                  }
                                  ScaffoldMessenger.of(context).showSnackBar(
                                    SnackBar(
                                      content: Text(
                                        'Re-delivery approved for '
                                        '${formatRefundReplacementDate(result.replacementDate)}.',
                                      ),
                                    ),
                                  );
                                } else {
                                  final remarks =
                                      await _promptStaffDecision(
                                        context,
                                        title: 'Approve request',
                                        confirmLabel: 'Approve',
                                      );
                                  if (remarks == null) return;
                                  await onDecide('approve', remarks);
                                }
                              },
                        icon: const Icon(Icons.check),
                        label: const Text('Approve'),
                      ),
                    ),
                  ],
                )
              else if (isApproved && !employeeOriginBlocked) ...[
                // Stage 2: a manual-bank ticket never completes here — the
                // frozen amount is reserved for the bank transfer and the
                // transfer is confirmed separately. The complete action is
                // hidden and the backend rejects direct completion.
                if (request.isManualBankTransfer) ...[
                  const _Notice(
                    icon: Icons.account_balance_outlined,
                    text:
                        'Manual bank transfer approved. The frozen amount is '
                        'reserved for transfer to the customer\'s saved bank '
                        'account — completing here is disabled. Confirm the '
                        'transfer once it is made.',
                  ),
                ],
                // Stage 2: completing an approved one-time order Refund runs
                // the backend's authoritative wallet settlement — this is the
                // step where money actually moves. Approval alone credited
                // nothing. A failed settlement keeps the ticket Approved and
                // retryable, and the server message is shown verbatim.
                if (isOrderRefund && !request.isManualBankTransfer) ...[
                  _Notice(
                    icon: Icons.account_balance_wallet_outlined,
                    text:
                        'Completing credits '
                        '${request.appliedDeductionPercentage != null ? 'the frozen refund ' : 'the refund '}'
                        'to the customer\'s wallet now. '
                        'Approving the decision moved no money.',
                  ),
                  const SizedBox(height: 12),
                ],
                if (!request.isManualBankTransfer)
                  FilledButton.icon(
                  onPressed: isSaving
                      ? null
                      : () async {
                          final remarks = await _promptStaffDecision(
                            context,
                            title: isOrderRefund
                                ? 'Complete and credit wallet'
                                : 'Complete request',
                            confirmLabel: isOrderRefund
                                ? 'Complete and credit wallet'
                                : 'Complete',
                          );
                          if (remarks == null) return;
                          await onDecide('complete', remarks);
                        },
                  icon: const Icon(Icons.task_alt),
                  label: Text(
                    isOrderRefund
                        ? 'Complete and credit wallet'
                        : 'Mark completed',
                  ),
                ),
              ],
              // Legacy remediation: an eligible unsupported Replacement
              // gets an explicit staff action that creates the linked
              // Pending Refund ticket. Shown only for eligible tickets;
              // superseded tickets show their linkage inline instead.
              // Never automatic, never a refund itself.
              if (onRemediate != null && !employeeOriginBlocked) ...[
                const SizedBox(height: 12),
                const _Notice(
                  icon: Icons.swap_horiz_outlined,
                  text:
                      'One-time orders support Refund only — this approved '
                      'Replacement cannot be fulfilled. Replacing it creates '
                      'a linked Pending Refund ticket for the same delivery. '
                      'This issues no refund and credits nothing; the new '
                      'ticket follows the normal approval and wallet '
                      'settlement.',
                ),
                const SizedBox(height: 4),
                OutlinedButton.icon(
                  onPressed: isSaving ? null : onRemediate,
                  icon: isSaving
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.swap_horiz_outlined),
                  label: const Text(
                    'Replace unsupported request with Refund request',
                  ),
                ),
              ],
              if (onRemediate == null && !canDecide && !isApproved)
                const Text(
                  'This request is final and no further action is available.',
                  style: TextStyle(color: DoodhColors.muted),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Opens the one-time order refund approval dialog: support explicitly
/// chooses Apply/Waive deduction with a live authoritative preview, then the
/// dialog submits the decision itself (single flight, guarded against
/// duplicates) through the shared controller. Returns true when the backend
/// confirmed the approval; false/null means cancelled or failed. Approval
/// records the decision and frozen snapshot only — the wallet is credited on
/// completion, never here.
Future<bool> _promptOrderRefundApproval(
  BuildContext context, {
  required RefundReplacementRequest request,
}) async =>
    await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) =>
          _OrderRefundApprovalDialog(request: request),
    ) ??
    false;

/// Opens the subscription failed-delivery refund approval dialog: support
/// explicitly chooses Apply/Waive customer-fault deduction against the
/// frozen failure snapshot, then the dialog submits the decision itself
/// (single flight, guarded against duplicates) through the shared
/// controller. Returns true when the backend confirmed the approval;
/// false/null means cancelled or failed. Approval records the decision and
/// frozen snapshot only — the wallet is credited on completion, never here.
/// There is no live amount preview (no subscription preview endpoint): the
/// dialog surfaces the authoritative frozen percentage verbatim and never
/// computes money itself.
Future<bool> _promptSubscriptionDeductionApproval(
  BuildContext context, {
  required RefundReplacementRequest request,
}) async =>
    await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (dialogContext) =>
          _SubscriptionDeductionDialog(request: request),
    ) ??
    false;

class _SubscriptionDeductionDialog extends ConsumerStatefulWidget {
  const _SubscriptionDeductionDialog({required this.request});

  final RefundReplacementRequest request;

  @override
  ConsumerState<_SubscriptionDeductionDialog> createState() =>
      _SubscriptionDeductionDialogState();
}class _SubscriptionDeductionDialogState
extends ConsumerState<_SubscriptionDeductionDialog> {
  final _waiverController = TextEditingController();
  final _remarksController = TextEditingController();
  bool? _applyDeduction;
  RefundSettlementMethod? _settlementMethod;
  bool _submitting = false;
  String? _submitError;

  @override
  void dispose() {
    _waiverController.dispose();
    _remarksController.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    final apply = _applyDeduction;
    final settlement = _settlementMethod;
    if (apply == null || settlement == null || _submitting) return;
    final waiverReason = _waiverController.text.trim();
    if (apply == false && waiverReason.isEmpty) {
      setState(() {
        _submitError =
            'A waiver reason is required when waiving the deduction.';
      });
      return;
    }
    setState(() {
      _submitting = true;
      _submitError = null;
    });
    final remarks = _remarksController.text.trim();
    final controller = ref.read(refundReplacementControllerProvider.notifier);
    final succeeded = await controller.approve(
      widget.request.requestId,
      remarks: remarks.isEmpty ? null : remarks,
      applyDeduction: apply,
      waiverReason: apply ? null : waiverReason,
      settlementMethod: settlement.wireValue,
    );
    if (!mounted) return;
    if (succeeded) {
      Navigator.of(context).pop(true);
      return;
    }
    setState(() {
      _submitting = false;
      _submitError =
          ref.read(refundReplacementControllerProvider).errorMessage ??
          'The deduction decision could not be recorded. Please try again.';
    });
  }

  @override
  Widget build(BuildContext context) {
    final isSaving = ref.watch(refundReplacementControllerProvider).isSaving;
    final busy = _submitting || isSaving;
    final ready = _applyDeduction != null && _settlementMethod != null && !busy;
    final frozen =
        widget.request.deliveryFailDeductionPercentageSnapshot ?? 0;
    final failure =
        widget.request.failureReasonSnapshot?.isNotEmpty ?? false
        ? widget.request.failureReasonSnapshot!
        : widget.request.reason;
    return AlertDialog(
      title: const Text('Approve refund decision'),
      content: SizedBox(
        width: 440,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Text(
                'Approving records your decision only. The wallet is credited '
                'when the ticket is completed — not now.',
                style: TextStyle(color: DoodhColors.muted, fontSize: 12),
              ),
              const SizedBox(height: 12),
              _InfoRow(
                icon: Icons.report_outlined,
                label: 'Failure',
                value: failure,
              ),
              _InfoRow(
                icon: Icons.percent_outlined,
                label: 'Frozen deduction',
                value: '${frozen.toStringAsFixed(2)}%',
              ),
              const SizedBox(height: 12),
              SegmentedButton<bool>(
                segments: const [
                  ButtonSegment<bool>(
                    value: true,
                    label: Text('Apply deduction'),
                    icon: Icon(Icons.percent_outlined),
                  ),
                  ButtonSegment<bool>(
                    value: false,
                    label: Text('Waive deduction'),
                    icon: Icon(Icons.money_off_outlined),
                  ),
                ],
                // No preselected decision: support must consciously choose.
                emptySelectionAllowed: true,
                selected: _applyDeduction == null
                    ? const <bool>{}
                    : {_applyDeduction!},
                onSelectionChanged: busy
                    ? null
                    : (selection) => setState(() {
                        _applyDeduction = selection.first;
                        _submitError = null;
                      }),
              ),
              const SizedBox(height: 4),
              Text(
                'Apply deducts the frozen ${frozen.toStringAsFixed(2)}% for a '
                'customer-fault failure. Waive applies 0% and needs a reason.',
                style: const TextStyle(
                  color: DoodhColors.muted,
                  fontSize: 12,
                ),
              ),
              const SizedBox(height: 12),
              const Text(
                'Settlement method',
                style: TextStyle(fontWeight: FontWeight.w600, fontSize: 13),
              ),
              const SizedBox(height: 4),
              SegmentedButton<RefundSettlementMethod>(
                segments: const [
                  ButtonSegment<RefundSettlementMethod>(
                    value: RefundSettlementMethod.wallet,
                    label: Text('Wallet refund'),
                    icon: Icon(Icons.account_balance_wallet_outlined),
                  ),
                  ButtonSegment<RefundSettlementMethod>(
                    value: RefundSettlementMethod.manualBankTransfer,
                    label: Text('Manual bank transfer'),
                    icon: Icon(Icons.account_balance_outlined),
                  ),
                ],
                // No preselected method: support must consciously choose.
                emptySelectionAllowed: true,
                selected: _settlementMethod == null
                    ? const <RefundSettlementMethod>{}
                    : {_settlementMethod!},
                onSelectionChanged: busy
                    ? null
                    : (selection) => setState(() {
                        _settlementMethod = selection.first;
                        _submitError = null;
                      }),
              ),
              const SizedBox(height: 4),
              const Text(
                'Wallet refund credits the wallet on completion. Manual '
                'bank transfer reserves the frozen amount for a bank '
                'transfer to the customer\'s saved bank details — no '
                'wallet credit is made.',
                style: TextStyle(
                  color: DoodhColors.muted,
                  fontSize: 12,
                ),
              ),
              if (_applyDeduction == false) ...[
                const SizedBox(height: 12),
                TextField(
                  controller: _waiverController,
                  enabled: !busy,
                  maxLines: 2,
                  maxLength: 500,
                  decoration: const InputDecoration(
                    labelText: 'Waiver reason (required)',
                    alignLabelWithHint: true,
                  ),
                  onChanged: (_) {
                    if (_submitError != null) {
                      setState(() => _submitError = null);
                    }
                  },
                ),
              ],
              const SizedBox(height: 12),
              TextField(
                controller: _remarksController,
                enabled: !busy,
                maxLines: 2,
                maxLength: 500,
                decoration: const InputDecoration(
                  labelText: 'Remarks (optional)',
                  alignLabelWithHint: true,
                ),
              ),
              if (_submitError != null) ...[
                const SizedBox(height: 4),
                Text(
                  _submitError!,
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: busy ? null : () => Navigator.of(context).pop(false),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: ready ? _submit : null,
          child: busy
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Text('Approve refund decision'),
        ),
      ],
    );
  }
}

class _OrderRefundApprovalDialog extends ConsumerStatefulWidget {
  const _OrderRefundApprovalDialog({required this.request});

  final RefundReplacementRequest request;

  @override
  ConsumerState<_OrderRefundApprovalDialog> createState() =>
      _OrderRefundApprovalDialogState();
}

class _OrderRefundApprovalDialogState
    extends ConsumerState<_OrderRefundApprovalDialog> {
  static const _offlineMessage =
      'Unable to reach DoodhDirect. Check your connection and try again.';
  static const _processingMessage =
      'The refund preview could not be loaded. Please try again.';

  final _waiverController = TextEditingController();
  final _remarksController = TextEditingController();
  bool? _applyDeduction;
  RefundSettlementMethod? _settlementMethod;
  bool _loadingPreview = false;
  String? _previewError;
  OrderRefundPreview? _preview;
  bool _submitting = false;
  String? _submitError;

  @override
  void dispose() {
    _waiverController.dispose();
    _remarksController.dispose();
    super.dispose();
  }

  void _selectDecision(bool apply) {
    if (_submitting) return;
    setState(() {
      _applyDeduction = apply;
      _submitError = null;
    });
    _fetchPreview();
  }

  Future<void> _fetchPreview() async {
    final apply = _applyDeduction;
    if (apply == null) return;
    setState(() {
      _loadingPreview = true;
      _previewError = null;
    });
    try {
      final token = ref.read(sessionControllerProvider).session?.accessToken;
      if (token == null) {
        throw const ApiException(401, 'UNAUTHORIZED', 'Your session has expired.');
      }
      final preview = await ref
          .read(refundReplacementRepositoryProvider)
          .getOrderRefundPreview(
            token,
            widget.request.requestId,
            applyDeduction: apply,
          );
      if (!mounted) return;
      setState(() {
        _preview = preview;
        _loadingPreview = false;
      });
    } on ApiNetworkException {
      if (!mounted) return;
      setState(() {
        _previewError = _offlineMessage;
        _loadingPreview = false;
      });
    } on ApiException catch (error) {
      if (!mounted) return;
      setState(() {
        _previewError = error.message;
        _loadingPreview = false;
      });
    } on Object {
      if (!mounted) return;
      setState(() {
        _previewError = _processingMessage;
        _loadingPreview = false;
      });
    }
  }

  Future<void> _submit() async {
    final apply = _applyDeduction;
    final settlement = _settlementMethod;
    if (apply == null || settlement == null || _submitting) return;
    final waiverReason = _waiverController.text.trim();
    if (apply == false && waiverReason.isEmpty) {
      setState(() {
        _submitError =
            'A waiver reason is required when waiving the deduction.';
      });
      return;
    }
    setState(() {
      _submitting = true;
      _submitError = null;
    });
    final remarks = _remarksController.text.trim();
    final controller = ref.read(refundReplacementControllerProvider.notifier);
    final succeeded = await controller.approve(
      widget.request.requestId,
      remarks: remarks.isEmpty ? null : remarks,
      applyDeduction: apply,
      waiverReason: apply ? null : waiverReason,
      settlementMethod: settlement.wireValue,
    );
    if (!mounted) return;
    if (succeeded) {
      Navigator.of(context).pop(true);
      return;
    }
    setState(() {
      _submitting = false;
      _submitError =
          ref.read(refundReplacementControllerProvider).errorMessage ??
          _processingMessage;
    });
  }

  @override
  Widget build(BuildContext context) {
    final isSaving = ref.watch(refundReplacementControllerProvider).isSaving;
    final busy = _submitting || isSaving;
    final ready =
        _applyDeduction != null &&
        _settlementMethod != null &&
        !_loadingPreview &&
        !busy;
    return AlertDialog(
      title: const Text('Approve refund decision'),
      content: SizedBox(
        width: 440,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Text(
                'Approving records your decision only. The wallet is credited '
                'when the ticket is completed — not now.',
                style: TextStyle(color: DoodhColors.muted, fontSize: 12),
              ),
              const SizedBox(height: 12),
              SegmentedButton<bool>(
                segments: const [
                  ButtonSegment<bool>(
                    value: true,
                    label: Text('Apply deduction'),
                    icon: Icon(Icons.percent_outlined),
                  ),
                  ButtonSegment<bool>(
                    value: false,
                    label: Text('Waive deduction'),
                    icon: Icon(Icons.money_off_outlined),
                  ),
                ],
                // No preselected decision: support must consciously choose.
                emptySelectionAllowed: true,
                selected: _applyDeduction == null
                    ? const <bool>{}
                    : {_applyDeduction!},
                onSelectionChanged: busy
                    ? null
                    : (selection) => _selectDecision(selection.first),
              ),
              const SizedBox(height: 4),
              const Text(
                'Apply uses the shared Admin Centre deduction %. Waive '
                'applies 0% and needs a reason.',
                style: TextStyle(color: DoodhColors.muted, fontSize: 12),
              ),
              const SizedBox(height: 12),
              const Text(
                'Settlement method',
                style: TextStyle(fontWeight: FontWeight.w600, fontSize: 13),
              ),
              const SizedBox(height: 4),
              SegmentedButton<RefundSettlementMethod>(
                segments: const [
                  ButtonSegment<RefundSettlementMethod>(
                    value: RefundSettlementMethod.wallet,
                    label: Text('Wallet refund'),
                    icon: Icon(Icons.account_balance_wallet_outlined),
                  ),
                  ButtonSegment<RefundSettlementMethod>(
                    value: RefundSettlementMethod.manualBankTransfer,
                    label: Text('Manual bank transfer'),
                    icon: Icon(Icons.account_balance_outlined),
                  ),
                ],
                // No preselected method: support must consciously choose.
                emptySelectionAllowed: true,
                selected: _settlementMethod == null
                    ? const <RefundSettlementMethod>{}
                    : {_settlementMethod!},
                onSelectionChanged: busy
                    ? null
                    : (selection) => setState(() {
                        _settlementMethod = selection.first;
                        _submitError = null;
                      }),
              ),
              const SizedBox(height: 4),
              const Text(
                'Wallet refund credits the wallet on completion. Manual '
                'bank transfer reserves the previewed amount for a bank '
                'transfer to the customer\'s saved bank details — no '
                'wallet credit is made.',
                style: TextStyle(color: DoodhColors.muted, fontSize: 12),
              ),
              const SizedBox(height: 12),
              if (_applyDeduction == null)
                const Text(
                  'Select a decision above to load the authoritative refund preview.',
                  style: TextStyle(color: DoodhColors.muted, fontSize: 12),
                )
              else if (_loadingPreview) ...[
                const Row(
                  children: [
                    SizedBox(
                      width: 20,
                      height: 20,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                    SizedBox(width: 12),
                    Expanded(child: Text('Loading refund preview...')),
                  ],
                ),
              ] else if (_previewError != null) ...[
                Text(
                  _previewError!,
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
                const SizedBox(height: 8),
                Align(
                  alignment: Alignment.centerLeft,
                  child: TextButton.icon(
                    onPressed: busy ? null : _fetchPreview,
                    icon: const Icon(Icons.refresh),
                    label: const Text('Retry'),
                  ),
                ),
              ] else if (_preview != null)
                _OrderRefundPreviewCard(preview: _preview!),
              if (_applyDeduction == false) ...[
                const SizedBox(height: 12),
                TextField(
                  controller: _waiverController,
                  enabled: !busy,
                  maxLines: 2,
                  maxLength: 500,
                  decoration: const InputDecoration(
                    labelText: 'Waiver reason (required)',
                    alignLabelWithHint: true,
                  ),
                  onChanged: (_) {
                    if (_submitError != null) {
                      setState(() => _submitError = null);
                    }
                  },
                ),
              ],
              const SizedBox(height: 12),
              TextField(
                controller: _remarksController,
                enabled: !busy,
                maxLines: 2,
                maxLength: 500,
                decoration: const InputDecoration(
                  labelText: 'Remarks (optional)',
                  alignLabelWithHint: true,
                ),
              ),
              if (_submitError != null) ...[
                const SizedBox(height: 4),
                Text(
                  _submitError!,
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: busy ? null : () => Navigator.of(context).pop(false),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: ready ? _submit : null,
          child: busy
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Text('Approve refund decision'),
        ),
      ],
    );
  }
}

/// Renders the authoritative refund preview verbatim. Never computes money:
/// every figure comes from the backend response. A zero or missing refund is
/// explained, never presented as a success.
class _OrderRefundPreviewCard extends StatelessWidget {
  const _OrderRefundPreviewCard({required this.preview});

  final OrderRefundPreview preview;

  @override
  Widget build(BuildContext context) => Card(
    margin: EdgeInsets.zero,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            'Refund preview · ${preview.orderNumber}',
            style: Theme.of(context).textTheme.titleSmall,
          ),
          const SizedBox(height: 8),
          _previewRow(
            context,
            'Order amount',
            _money(preview.grossBasis, preview.currency),
          ),
          _previewRow(
            context,
            'Deduction (${preview.appliedPercentage.toStringAsFixed(2)}%)',
            _money(preview.deductionAmount, preview.currency),
          ),
          _previewRow(
            context,
            'Eligible paid',
            _money(preview.eligiblePaid, preview.currency),
          ),
          const Divider(),
          _previewRow(
            context,
            'Proposed wallet refund',
            _money(preview.proposedRefund, preview.currency),
            strong: true,
          ),
          if (preview.hasNoRefund) ...[
            const SizedBox(height: 8),
            const _Notice(
              icon: Icons.info_outline,
              text:
                  'The preview shows no refundable amount — the order may be '
                  'unpaid or already fully refunded. Approving is still '
                  'allowed, but completing will be blocked until an eligible '
                  'paid amount exists.',
            ),
          ] else ...[
            const SizedBox(height: 4),
            const Text(
              'Advisory only — settlement revalidates at completion.',
              style: TextStyle(color: DoodhColors.muted, fontSize: 12),
            ),
          ],
        ],
      ),
    ),
  );

  static String _money(double amount, String currency) =>
      '${amount.toStringAsFixed(2)} $currency';

  static Widget _previewRow(
    BuildContext context,
    String label,
    String value, {
    bool strong = false,
  }) =>
      Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(
          children: [
            Expanded(
              child: Text(
                label,
                style: const TextStyle(color: DoodhColors.muted),
              ),
            ),
            Text(
              value,
              style: strong
                  ? Theme.of(
                      context,
                    ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold)
                  : null,
            ),
          ],
        ),
      );
}

/// Result of the re-delivery approval dialog: optional remarks plus the
/// staff-confirmed replacement date. Null means the manager cancelled.
class _RedeliveryApprovalResult {
  const _RedeliveryApprovalResult({this.remarks, required this.replacementDate});

  final String? remarks;
  final DateTime replacementDate;
}

/// Opens the re-delivery approval dialog for an eligible ticket: loads the
/// server-proposed replacement date (with loading/error/retry), lets staff
/// confirm it or pick another day, and returns the confirmed date. The
/// backend stays authoritative for every date rule — the dialog only
/// displays the proposal and forwards the choice, surfacing API validation
/// errors verbatim.
Future<_RedeliveryApprovalResult?> _promptRedeliveryApproval(
  BuildContext context, {
  required RefundReplacementRequest request,
}) => showDialog<_RedeliveryApprovalResult>(
  context: context,
  barrierDismissible: false,
  builder: (dialogContext) => _RedeliveryApprovalDialog(request: request),
);

class _RedeliveryApprovalDialog extends ConsumerStatefulWidget {
  const _RedeliveryApprovalDialog({required this.request});

  final RefundReplacementRequest request;

  @override
  ConsumerState<_RedeliveryApprovalDialog> createState() =>
      _RedeliveryApprovalDialogState();
}

class _RedeliveryApprovalDialogState
    extends ConsumerState<_RedeliveryApprovalDialog> {
  static const _offlineMessage =
      'Unable to reach DoodhDirect. Check your connection and try again.';
  static const _processingMessage =
      'The replacement date could not be loaded. Please try again.';

  final _remarksController = TextEditingController();
  bool _loadingProposal = true;
  String? _proposalError;
  DateTime? _proposed;
  DateTime? _selected;
  bool _submitting = false;
  String? _submitError;

  @override
  void initState() {
    super.initState();
    Future.microtask(_fetchProposal);
  }

  @override
  void dispose() {
    _remarksController.dispose();
    super.dispose();
  }

  Future<void> _fetchProposal() async {
    setState(() {
      _loadingProposal = true;
      _proposalError = null;
    });
    try {
      final token = ref.read(sessionControllerProvider).session?.accessToken;
      if (token == null) {
        throw const ApiException(401, 'UNAUTHORIZED', 'Your session has expired.');
      }
      final proposal = await ref
          .read(refundReplacementRepositoryProvider)
          .getReplacementDateProposal(token, widget.request.requestId);
      if (!mounted) return;
      setState(() {
        _proposed = proposal.proposedDate;
        // Keep the selection date-only in local terms; only the calendar day
        // is ever sent on the wire, so the instant/timezone is irrelevant.
        _selected ??= _dateOnly(proposal.proposedDate);
        _loadingProposal = false;
      });
    } on ApiNetworkException {
      if (!mounted) return;
      setState(() {
        _proposalError = _offlineMessage;
        _loadingProposal = false;
      });
    } on ApiException catch (error) {
      if (!mounted) return;
      setState(() {
        _proposalError = error.message;
        _loadingProposal = false;
      });
    } on Object {
      if (!mounted) return;
      setState(() {
        _proposalError = _processingMessage;
        _loadingProposal = false;
      });
    }
  }

  Future<void> _pickDate() async {
    final today = _dateOnly(indiaNow());
    // Never constrain below a loaded proposal: a stale proposal stays
    // selectable and the backend re-validates authoritatively on approval.
    final earliest = _proposed != null && _proposed!.isBefore(today)
        ? _dateOnly(_proposed!)
        : today;
    final picked = await showDatePicker(
      context: context,
      initialDate: _selected ?? _proposed ?? today,
      firstDate: earliest,
      lastDate: today.add(const Duration(days: 366)),
    );
    if (picked != null && mounted) {
      setState(
        () => _selected = DateTime(picked.year, picked.month, picked.day),
      );
    }
  }

  Future<void> _submit() async {
    final selected = _selected;
    if (selected == null || _submitting) return;
    setState(() {
      _submitting = true;
      _submitError = null;
    });
    final remarks = _remarksController.text.trim();
    final controller = ref.read(refundReplacementControllerProvider.notifier);
    final succeeded = await controller.approve(
      widget.request.requestId,
      remarks: remarks.isEmpty ? null : remarks,
      replacementDate: selected,
    );
    if (!mounted) return;
    if (succeeded) {
      Navigator.of(context).pop(
        _RedeliveryApprovalResult(
          remarks: remarks.isEmpty ? null : remarks,
          replacementDate: selected,
        ),
      );
      return;
    }
    setState(() {
      _submitting = false;
      _submitError =
          ref.read(refundReplacementControllerProvider).errorMessage ??
          _processingMessage;
    });
  }

  @override
  Widget build(BuildContext context) {
    final isSaving = ref.watch(refundReplacementControllerProvider).isSaving;
    final busy = _submitting || isSaving;
    final ready = !_loadingProposal && _selected != null && !busy;
    return AlertDialog(
      title: const Text('Approve re-delivery'),
      content: SizedBox(
        width: 400,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (_loadingProposal) ...[
                const Row(
                  children: [
                    SizedBox(
                      width: 20,
                      height: 20,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                    SizedBox(width: 12),
                    Expanded(
                      child: Text('Loading proposed replacement date...'),
                    ),
                  ],
                ),
              ] else if (_proposalError != null) ...[
                Text(
                  _proposalError!,
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
                const SizedBox(height: 8),
                Align(
                  alignment: Alignment.centerLeft,
                  child: TextButton.icon(
                    onPressed: busy ? null : _fetchProposal,
                    icon: const Icon(Icons.refresh),
                    label: const Text('Retry'),
                  ),
                ),
              ] else ...[
                const Text(
                  'Proposed replacement date',
                  style: TextStyle(color: DoodhColors.muted, fontSize: 12),
                ),
                const SizedBox(height: 2),
                Text(
                  formatRefundReplacementDate(_selected!),
                  style: Theme.of(context).textTheme.titleLarge,
                  semanticsLabel:
                      'Replacement date ${formatRefundReplacementDate(_selected!)}',
                ),
                const SizedBox(height: 4),
                const Text(
                  'The backend validates the weekday, cutoff, branch, and '
                  'conflicts. Confirm this date or choose another day.',
                  style: TextStyle(color: DoodhColors.muted, fontSize: 12),
                ),
                const SizedBox(height: 8),
                Align(
                  alignment: Alignment.centerLeft,
                  child: OutlinedButton.icon(
                    onPressed: busy ? null : _pickDate,
                    icon: const Icon(Icons.calendar_month_outlined),
                    label: const Text('Change date'),
                  ),
                ),
              ],
              const SizedBox(height: 12),
              TextField(
                controller: _remarksController,
                enabled: !busy,
                maxLines: 2,
                maxLength: 500,
                decoration: const InputDecoration(
                  labelText: 'Remarks (optional)',
                  alignLabelWithHint: true,
                ),
              ),
              if (_submitError != null) ...[
                const SizedBox(height: 4),
                Text(
                  _submitError!,
                  style: TextStyle(
                    color: Theme.of(context).colorScheme.error,
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: ready ? _submit : null,
          child: busy
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Text('Approve re-delivery'),
        ),
      ],
    );
  }
}

DateTime _dateOnly(DateTime value) =>
    DateTime(value.year, value.month, value.day);

/// Prompts for optional decision remarks. Returns null when the manager
/// cancels, or the (possibly empty) remarks string when confirmed.
/// Legacy remediation confirmation. Shows the order, delivery, and ticket
/// numbers with the Refund-only policy, requires a nonblank business
/// justification, and states plainly that the operation creates a linked
/// Pending Refund ticket without issuing any refund or wallet credit.
/// Returns the trimmed justification, or null when staff cancel.
Future<String?> _promptRemediationReason(
  BuildContext context, {
  required RefundReplacementRequest request,
}) => showDialog<String>(
  context: context,
  builder: (dialogContext) => _RemediationConfirmDialog(request: request),
);

/// Confirmation dialog for legacy remediation. The confirm action stays
/// disabled until staff enter a nonblank justification. The dialog owns
/// its text controller (created and disposed with its state) so the
/// exit animation never touches a disposed controller.
class _RemediationConfirmDialog extends StatefulWidget {
  const _RemediationConfirmDialog({required this.request});

  final RefundReplacementRequest request;

  @override
  State<_RemediationConfirmDialog> createState() =>
      _RemediationConfirmDialogState();
}

class _RemediationConfirmDialogState
    extends State<_RemediationConfirmDialog> {
  late final TextEditingController _controller;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController()..addListener(_onChanged);
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _onChanged() => setState(() {});

  @override
  Widget build(BuildContext context) {
    final request = widget.request;
    final hasJustification = _controller.text.trim().isNotEmpty;
    return AlertDialog(
      title: const Text('Replace with Refund request'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Order ${request.orderNumber ?? request.orderPublicId ?? '—'}'),
            Text('Delivery ${request.deliveryNumber ?? request.deliveryId}'),
            Text('Ticket ${request.requestNumber}'),
            const SizedBox(height: 12),
            const Text(
              'One-time orders support Refund only: this approved '
              'Replacement cannot be fulfilled. Confirming supersedes it '
              'and opens a linked Pending Refund ticket for the same '
              'delivery. This does not issue a refund or credit the '
              'wallet — the new ticket needs its own approval and '
              'completion.',
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _controller,
              maxLines: 3,
              maxLength: 1000,
              decoration: const InputDecoration(
                labelText: 'Business justification (required)',
                alignLabelWithHint: true,
              ),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () {
            // Release field focus before popping so no disposed focus
            // node lingers (and the keyboard dismisses on device).
            FocusScope.of(context).unfocus();
            Navigator.of(context).pop();
          },
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: hasJustification
              ? () {
                  FocusScope.of(context).unfocus();
                  Navigator.of(
                    context,
                  ).pop(_controller.text.trim());
                }
              : null,
          child: const Text('Replace with Refund'),
        ),
      ],
    );
  }
}

Future<String?> _promptStaffDecision(
  BuildContext context, {
  required String title,
  required String confirmLabel,
}) => showDialog<String>(
  context: context,
  builder: (dialogContext) => _StaffDecisionDialog(
    title: title,
    confirmLabel: confirmLabel,
  ),
);

/// Confirm dialog for a staff approve/reject/complete decision with optional
/// remarks. The [TextEditingController] is owned by the dialog widget (not
/// the awaiting future) so it is disposed only when the dialog unmounts —
/// disposing it at pop time races the exit animation and crashes on the
/// still-mounted [TextField].
class _StaffDecisionDialog extends StatefulWidget {
  const _StaffDecisionDialog({
    required this.title,
    required this.confirmLabel,
  });

  final String title;
  final String confirmLabel;

  @override
  State<_StaffDecisionDialog> createState() => _StaffDecisionDialogState();
}

class _StaffDecisionDialogState extends State<_StaffDecisionDialog> {
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
      maxLines: 3,
      maxLength: 500,
      decoration: const InputDecoration(
        labelText: 'Remarks (optional)',
        alignLabelWithHint: true,
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.of(context).pop(),
        child: const Text('Cancel'),
      ),
      FilledButton(
        onPressed: () =>
            Navigator.of(context).pop(_controller.text.trim()),
        child: Text(widget.confirmLabel),
      ),
    ],
  );
}
