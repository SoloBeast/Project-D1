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

import 'refund_replacement_controller.dart';
import 'refund_replacement_models.dart';

typedef RefundReplacementImagePicker =
    Future<XFile?> Function({required ImageSource source});

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
class CustomerRefundReplacementScreen extends ConsumerStatefulWidget {
  const CustomerRefundReplacementScreen({
    super.key,
    required this.deliveryId,
    this.milkTestId,
    this.pickImage,
  });

  final String deliveryId;
  final String? milkTestId;
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
        message:
            eligibility.ineligibleReason ??
            'This delivery is not eligible for a refund or replacement request.',
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
              text:
                  'You rejected the doorstep milk test. You can raise a refund or replacement request for this delivery right away.',
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
              text:
                  'There is already an open request for this delivery. Track it from your requests list.',
            ),
          const SizedBox(height: 8),
          DoodhSectionHeader(title: 'Request type'),
          const SizedBox(height: 8),
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
            _ProofImagePicker(
              candidate: _proof,
              enabled: !state.isSaving,
              onPick: _pickProof,
              onClear: () => setState(() => _proof = null),
            ),
            const SizedBox(height: 16),
          ] else
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

    final controller = ref.read(refundReplacementControllerProvider.notifier);
    final submitted = await controller.submitRequest(
      widget.deliveryId,
      type: _type,
      reason: reason,
      remarks: _remarksController.text.trim().isEmpty
          ? null
          : _remarksController.text.trim(),
      milkTestId: widget.milkTestId,
    );
    if (!submitted || !mounted) return;

    final request = ref
        .read(refundReplacementControllerProvider)
        .selectedCustomerRequest;
    final imageId = request?.requestId;
    if (imageId != null && _proof != null) {
      await controller.uploadImage(
        imageId,
        bytes: _proof!.bytes,
        fileName: _proof!.fileName,
        contentType: _proof!.contentType,
      );
    }
    if (!mounted) return;
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
      Text('Proof image', style: Theme.of(context).textTheme.titleMedium),
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
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isLoading && state.customerRequests.isEmpty) {
      return const LoadingStatePanel(message: 'Loading your requests...');
    }
    if (state.isOffline && state.customerRequests.isEmpty) {
      return OfflineStatePanel(onRetry: _load);
    }
    if (state.errorMessage != null && state.customerRequests.isEmpty) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    }
    if (state.customerRequests.isEmpty) {
      return const EmptyStatePanel(
        title: 'No requests yet',
        message:
            'Refund or replacement requests you raise for your deliveries will appear here.',
      );
    }
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView.separated(
        padding: DoodhSpacing.pagePadding,
        itemCount: state.customerRequests.length,
        separatorBuilder: (_, _) => const SizedBox(height: 12),
        itemBuilder: (context, index) {
          final request = state.customerRequests[index];
          return _CustomerRequestCard(
            request: request,
            onTap: () => context.push(
              '/refund-replacements/${request.requestId}',
            ),
          );
        },
      ),
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

class _RefundReplacementDetailBody extends StatelessWidget {
  const _RefundReplacementDetailBody({required this.request});

  final RefundReplacementRequest request;

  @override
  Widget build(BuildContext context) => ListView(
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
        value: request.orderNumber,
      ),
      _InfoRow(
        icon: Icons.local_shipping_outlined,
        label: 'Delivery',
        value: request.deliveryNumber ?? request.deliveryId,
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
      _Remarks(label: 'Reason', text: request.reason),
      if (request.remarks?.isNotEmpty ?? false)
        _Remarks(label: 'Your remarks', text: request.remarks!),
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
      if (request.completedAtUtc != null) ...[
        const SizedBox(height: 8),
        _InfoRow(
          icon: Icons.task_alt,
          label: 'Completed',
          value:
              '${request.completedByName ?? 'Support'} · '
              '${formatRefundReplacementDateTime(request.completedAtUtc!)}',
        ),
        if (request.completionRemarks?.isNotEmpty ?? false)
          _Remarks(label: 'Completion remarks', text: request.completionRemarks!),
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
              'Order ${request.orderNumber}',
              style: Theme.of(context).textTheme.bodySmall,
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
          child: Text(
            label,
            style: const TextStyle(color: DoodhColors.muted),
          ),
        ),
        Expanded(child: Text(value)),
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

DoodhStatusTone _statusTone(RefundReplacementStatus status) => switch (status) {
  RefundReplacementStatus.pending => DoodhStatusTone.warning,
  RefundReplacementStatus.approved => DoodhStatusTone.success,
  RefundReplacementStatus.rejected => DoodhStatusTone.error,
  RefundReplacementStatus.completed => DoodhStatusTone.success,
  RefundReplacementStatus.unknown => DoodhStatusTone.neutral,
};

String formatRefundReplacementDateTime(DateTime value) {
  final local = toIndiaTime(value);
  String two(int number) => number.toString().padLeft(2, '0');
  return '${two(local.day)}/${two(local.month)}/${local.year} '
      '${two(local.hour)}:${two(local.minute)}';
}

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

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(refundReplacementControllerProvider);
    final items = state.staffPage?.items ?? const [];
    return Scaffold(
      appBar: AppBar(title: const Text('Refund / replacement requests')),
      body: Column(
        children: [
          _StaffStatusFilter(selected: _status, onSelected: _selectStatus),
          Expanded(child: _body(state, items)),
        ],
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
        message: 'No refund or replacement requests match this filter.',
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
            onTap: () => context.push(
              '/staff/refund-replacements/${item.requestId}',
            ),
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
    RefundReplacementStatus.approved,
    RefundReplacementStatus.rejected,
    RefundReplacementStatus.completed,
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
              'Order ${item.orderNumber} · ${item.customerName}',
              style: Theme.of(context).textTheme.bodySmall,
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

  Future<void> _decide(String decision) async {
    final controller = ref.read(refundReplacementControllerProvider.notifier);
    final succeeded = switch (decision) {
      'approve' => await controller.approve(widget.requestId),
      'reject' => await controller.reject(widget.requestId),
      _ => await controller.complete(widget.requestId),
    };
    if (!mounted || !succeeded) return;
    final message = switch (decision) {
      'approve' => 'Request approved.',
      'reject' => 'Request rejected.',
      _ => 'Request marked completed.',
    };
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(message)),
    );
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
                    onOrderTap: request.orderPublicId == null
                        ? null
                        : () => context.push(
                            '/staff/orders/${request.orderPublicId}',
                          ),
                    onDeliveryTap: () => context.push(
                      '/staff/delivery/${request.deliveryId}',
                    ),
                    onMilkTestTap: request.milkTestPublicId == null
                        ? null
                        : () => context.push(
                            '/staff/delivery/${request.deliveryId}/milk-test',
                          ),
                  ),
                ),
                if (_canManage)
                  _StaffDecisionActions(
                    request: request,
                    isSaving: state.isSaving,
                    errorMessage: state.errorMessage,
                    onDecide: _decide,
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
  });

  final RefundReplacementRequest request;
  final VoidCallback? onOrderTap;
  final VoidCallback? onDeliveryTap;
  final VoidCallback? onMilkTestTap;

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
        value: request.orderNumber,
        onTap: onOrderTap,
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
      if (request.completedAtUtc != null) ...[
        _InfoRow(
          icon: Icons.task_alt,
          label: 'Completed',
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
            width: 90,
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
  });

  final RefundReplacementRequest request;
  final bool isSaving;
  final String? errorMessage;
  final Future<void> Function(String decision) onDecide;

  @override
  Widget build(BuildContext context) {
    final isPending = request.status == RefundReplacementStatus.pending;
    final isApproved = request.status == RefundReplacementStatus.approved;
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
              if (isPending)
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
                                await onDecide('reject');
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
                                final remarks = await _promptStaffDecision(
                                  context,
                                  title: 'Approve request',
                                  confirmLabel: 'Approve',
                                );
                                if (remarks == null) return;
                                await onDecide('approve');
                              },
                        icon: const Icon(Icons.check),
                        label: const Text('Approve'),
                      ),
                    ),
                  ],
                )
              else if (isApproved)
                FilledButton.icon(
                  onPressed: isSaving
                      ? null
                      : () async {
                          final remarks = await _promptStaffDecision(
                            context,
                            title: 'Complete request',
                            confirmLabel: 'Complete',
                          );
                          if (remarks == null) return;
                          await onDecide('complete');
                        },
                  icon: const Icon(Icons.task_alt),
                  label: const Text('Mark completed'),
                )
              else
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

/// Prompts for optional decision remarks. Returns null when the manager
/// cancels, or the (possibly empty) remarks string when confirmed.
Future<String?> _promptStaffDecision(
  BuildContext context, {
  required String title,
  required String confirmLabel,
}) async {
  final controller = TextEditingController();
  try {
    return await showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(title),
        content: TextField(
          controller: controller,
          maxLines: 3,
          maxLength: 500,
          decoration: const InputDecoration(
            labelText: 'Remarks (optional)',
            alignLabelWithHint: true,
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () =>
                Navigator.of(dialogContext).pop(controller.text.trim()),
            child: Text(confirmLabel),
          ),
        ],
      ),
    );
  } finally {
    controller.dispose();
  }
}
