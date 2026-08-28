import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

import 'milk_test_controller.dart';
import 'milk_test_models.dart';

typedef MilkTestImagePicker = Future<XFile?> Function({required ImageSource source});

/// Raised when a protected milk test image is requested without an
/// authenticated session token. Distinct from an HTTP 403 so the UI can show a
/// controlled "no access" state without ever requesting the endpoint
/// unauthenticated.
class ApiByteUnauthenticatedException implements Exception {
  const ApiByteUnauthenticatedException();

  @override
  String toString() => 'No authenticated session for protected image content.';
}

mixin _MilkTestImagePicking<T extends ConsumerStatefulWidget>
    on ConsumerState<T> {
  MilkTestImagePicker? get pickImage;

  Future<_MilkTestImageCandidate?> _pickMilkTestImage() async {
    try {
      final source = await _chooseImageSource();
      if (source == null || !mounted) return null;
      final picker = pickImage ?? ImagePicker().pickImage;
      final image = await picker(source: source);
      if (image == null || !mounted) return null;
      final contentType = resolveMilkTestImageContentType(
        image.name,
        image.mimeType,
      );
      if (contentType == null) {
        _showMessage('Select a JPEG or PNG image.');
        return null;
      }
      final fileName = image.name.trim().isEmpty
          ? 'milk-test-image.${contentType == 'image/png' ? 'png' : 'jpg'}'
          : image.name;
      final bytes = await image.readAsBytes();
      if (!mounted) return null;
      final candidate = _MilkTestImageCandidate(
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

  Future<bool?> _showImagePreview(_MilkTestImageCandidate candidate) =>
      showDialog<bool>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: const Text('Use this test image?'),
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

class CustomerMilkTestScreen extends ConsumerStatefulWidget {
  const CustomerMilkTestScreen({
    super.key,
    required this.deliveryId,
    this.pickImage,
  });

  final String deliveryId;
  final MilkTestImagePicker? pickImage;

  @override
  ConsumerState<CustomerMilkTestScreen> createState() =>
      _CustomerMilkTestScreenState();
}

class _CustomerMilkTestScreenState
    extends ConsumerState<CustomerMilkTestScreen>
    with _MilkTestImagePicking<CustomerMilkTestScreen> {
  @override
  MilkTestImagePicker? get pickImage => widget.pickImage;
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() => ref
      .read(milkTestControllerProvider.notifier)
      .loadForCustomer(widget.deliveryId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(milkTestControllerProvider);
    final test = state.customerTest?.deliveryId == widget.deliveryId
        ? state.customerTest
        : null;

    return Scaffold(
      appBar: AppBar(title: const Text('Doorstep milk test')),
      body: _MilkTestBodyState(
        state: state,
        hasData: test != null,
        onRetry: _load,
        child: test == null
            ? EmptyStatePanel(
                title: 'No test requested',
                message: 'Request a doorstep milk test for this delivery.',
                action: FilledButton.icon(
                  onPressed: state.isSaving
                      ? null
                      : () => ref
                            .read(milkTestControllerProvider.notifier)
                            .request(widget.deliveryId),
                  icon: state.isSaving
                      ? const _ButtonProgress()
                      : const Icon(Icons.science_outlined),
                  label: const Text('Request test'),
                ),
              )
            : RefreshIndicator(
                onRefresh: _load,
                child: ListView(
                  padding: const EdgeInsets.all(16),
                  children: [
                    _StatusHeader(
                      status: test.status,
                      decision: test.customerDecision,
                    ),
                    const SizedBox(height: 16),
                    _TimelineRow(
                      icon: Icons.outbox_outlined,
                      label: 'Requested',
                      value: formatMilkTestDateTime(test.requestedAtUtc),
                    ),
                    if (test.completedAtUtc != null)
                      _TimelineRow(
                        icon: Icons.task_alt,
                        label: 'Completed',
                        value: formatMilkTestDateTime(test.completedAtUtc!),
                      ),
                    if (test.confirmedAtUtc != null)
                      _TimelineRow(
                        icon: Icons.thumb_up_outlined,
                        label: 'Confirmed',
                        value: formatMilkTestDateTime(test.confirmedAtUtc!),
                      ),
                    if (test.rejectedAtUtc != null)
                      _TimelineRow(
                        icon: Icons.thumb_down_outlined,
                        label: 'Rejected',
                        value: formatMilkTestDateTime(test.rejectedAtUtc!),
                      ),
                    if (state.errorMessage != null)
                      _InlineError(message: state.errorMessage!),
                    if (test.status == MilkTestStatus.unknown ||
                        test.customerDecision ==
                            MilkTestCustomerDecision.unknown)
                      const _Notice(
                        icon: Icons.info_outline,
                        text: 'This milk test has a status the app does not yet support. Refresh later or contact support.',
                      ),
                    if (test.status == MilkTestStatus.requested)
                      const _Notice(
                        icon: Icons.hourglass_top,
                        text: 'The assigned delivery employee will perform the test at your doorstep.',
                      ),
                    if (test.status == MilkTestStatus.completed) ...[
                      const SizedBox(height: 20),
                      Text(
                        'Test images',
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      const SizedBox(height: 8),
                      _AuthenticatedImages(
                        milkTestId: test.milkTestId,
                        images: test.images,
                        onReplace: test.canDecide
                            ? (image) => _replaceImage(test, image)
                            : null,
                      ),
                    ],
                    if (test.customerRemarks?.isNotEmpty ?? false)
                      _Remarks(
                        label: 'Your remarks',
                        text: test.customerRemarks!,
                      ),
                    if (test.canDecide) ...[
                      const SizedBox(height: 24),
                      Row(
                        children: [
                          Expanded(
                            child: OutlinedButton.icon(
                              onPressed: state.isSaving
                                  ? null
                                  : () => _showDecision(
                                      confirm: false,
                                      test: test,
                                    ),
                              icon: const Icon(Icons.close),
                              label: const Text('Reject'),
                            ),
                          ),
                          const SizedBox(width: 12),
                          Expanded(
                            child: FilledButton.icon(
                              onPressed: state.isSaving
                                  ? null
                                  : () => _showDecision(
                                      confirm: true,
                                      test: test,
                                    ),
                              icon: state.isSaving
                                  ? const _ButtonProgress()
                                  : const Icon(Icons.check),
                              label: const Text('Confirm'),
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

  Future<void> _showDecision({
    required bool confirm,
    required CustomerMilkTest test,
  }) async {
    var remarks = '';
    final decided = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(confirm ? 'Confirm milk test?' : 'Reject milk test?'),
        content: TextField(
          onChanged: (value) => remarks = value,
          maxLength: 500,
          maxLines: 3,
          decoration: const InputDecoration(
            labelText: 'Remarks (optional)',
            border: OutlineInputBorder(),
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: Text(confirm ? 'Confirm' : 'Reject'),
          ),
        ],
      ),
    );
    final value = remarks.trim();
    if (decided != true || !mounted) return;

    final controller = ref.read(milkTestControllerProvider.notifier);
    if (confirm) {
      await controller.confirm(
        test.milkTestId,
        remarks: value.isEmpty ? null : value,
      );
    } else {
      await controller.reject(
        test.milkTestId,
        remarks: value.isEmpty ? null : value,
      );
    }
  }

  Future<void> _replaceImage(CustomerMilkTest test, MilkTestImage image) async {
    final candidate = await _pickMilkTestImage();
    if (candidate == null || !mounted) return;
    final replaced = await ref
        .read(milkTestControllerProvider.notifier)
        .replaceImageAsCustomer(
          widget.deliveryId,
          test.milkTestId,
          image.imageId,
          bytes: candidate.bytes,
          fileName: candidate.fileName,
          contentType: candidate.contentType,
        );
    if (replaced && mounted) _showMessage('Test image replaced.');
  }
}

class StaffMilkTestScreen extends ConsumerStatefulWidget {
  const StaffMilkTestScreen({
    super.key,
    required this.deliveryId,
    this.pickImage,
  });

  final String deliveryId;
  final MilkTestImagePicker? pickImage;

  @override
  ConsumerState<StaffMilkTestScreen> createState() =>
      _StaffMilkTestScreenState();
}

class _StaffMilkTestScreenState
    extends ConsumerState<StaffMilkTestScreen>
    with _MilkTestImagePicking<StaffMilkTestScreen> {
  @override
  MilkTestImagePicker? get pickImage => widget.pickImage;

  final _formKey = GlobalKey<FormState>();
  final _remarks = TextEditingController();
  _MilkTestImageCandidate? _selectedImage;
  final List<_ReadingInput> _readings = [
    _ReadingInput(code: 'FAT', name: 'Fat', unit: '%'),
    _ReadingInput(code: 'SNF', name: 'SNF', unit: '%'),
  ];

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  @override
  void dispose() {
    _remarks.dispose();
    for (final reading in _readings) {
      reading.dispose();
    }
    super.dispose();
  }

  Future<void> _load() => ref
      .read(milkTestControllerProvider.notifier)
      .loadForStaff(widget.deliveryId);

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(milkTestControllerProvider);
    final test = state.staffTest?.deliveryId == widget.deliveryId
        ? state.staffTest
        : null;

    return Scaffold(
      appBar: AppBar(title: const Text('Perform milk test')),
      body: _MilkTestBodyState(
        state: state,
        hasData: test != null,
        onRetry: _load,
        child: test == null
            ? const EmptyStatePanel(
                title: 'No test request',
                message:
                    'The customer has not requested a test for this delivery.',
              )
            : RefreshIndicator(
                onRefresh: _load,
                child: ListView(
                  padding: const EdgeInsets.all(16),
                  children: [
                    _StatusHeader(
                      status: test.status,
                      decision: test.customerDecision,
                    ),
                    const SizedBox(height: 16),
                    _TimelineRow(
                      icon: Icons.outbox_outlined,
                      label: 'Requested',
                      value: formatMilkTestDateTime(test.requestedAtUtc),
                    ),
                    if (test.completedAtUtc != null)
                      _TimelineRow(
                        icon: Icons.task_alt,
                        label: 'Completed',
                        value: formatMilkTestDateTime(test.completedAtUtc!),
                      ),
                    if (state.errorMessage != null)
                      _InlineError(message: state.errorMessage!),
                    const SizedBox(height: 20),
                    Text(
                      'Test images',
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                    const SizedBox(height: 8),
                    if (_selectedImage != null)
                      _LocalMilkTestImagePreview(candidate: _selectedImage!)
                    else
                      _AuthenticatedImages(
                        milkTestId: test.milkTestId,
                        images: test.images,
                        onDelete: test.status == MilkTestStatus.requested
                            ? (image) => _confirmDelete(test, image)
                            : null,
                        onReplace: test.status == MilkTestStatus.requested
                            ? (image) => _pickAndUpload(
                                  test.milkTestId,
                                  replaceImageId: image.imageId,
                                )
                            : null,
                      ),
                    if (test.status == MilkTestStatus.requested) ...[
                      const SizedBox(height: 12),
                      OutlinedButton.icon(
                        onPressed: state.isSaving
                            ? null
                            : () => _pickAndUpload(test.milkTestId),
                        icon: Icon(
                          _selectedImage == null
                              ? Icons.add_a_photo_outlined
                              : Icons.refresh_outlined,
                        ),
                        label: Text(
                          _selectedImage == null && test.images.isEmpty
                              ? 'Add Test Image'
                              : 'Replace Image',
                        ),
                      ),
                      const SizedBox(height: 24),
                      Form(
                        key: _formKey,
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            Row(
                              children: [
                                Expanded(
                                  child: Text(
                                    'Readings',
                                    style: Theme.of(context)
                                        .textTheme
                                        .titleMedium,
                                  ),
                                ),
                                IconButton(
                                  tooltip: 'Add reading',
                                  onPressed: state.isSaving
                                      ? null
                                      : _addReading,
                                  icon: const Icon(Icons.add_circle_outline),
                                ),
                              ],
                            ),
                            ..._readings.asMap().entries.map(
                              (entry) => _ReadingFields(
                                key: ValueKey(entry.value),
                                input: entry.value,
                                canRemove: _readings.length > 1,
                                onRemove: () => _removeReading(entry.key),
                              ),
                            ),
                            TextFormField(
                              controller: _remarks,
                              maxLength: 500,
                              maxLines: 3,
                              decoration: const InputDecoration(
                                labelText: 'Staff remarks (optional)',
                                border: OutlineInputBorder(),
                              ),
                            ),
                            const SizedBox(height: 12),
                            FilledButton.icon(
                              onPressed: state.isSaving || test.images.isEmpty
                                  ? null
                                  : () => _complete(test.milkTestId),
                              icon: state.isSaving
                                  ? const _ButtonProgress()
                                  : const Icon(Icons.task_alt),
                              label: const Text('Complete test'),
                            ),
                            if (test.images.isEmpty)
                              const Padding(
                                padding: EdgeInsets.only(top: 8),
                                child: Text(
                                  'Upload at least one test image before completion.',
                                  textAlign: TextAlign.center,
                                ),
                              ),
                          ],
                        ),
                      ),
                    ] else ...[
                      const SizedBox(height: 24),
                      Text(
                        'Recorded readings',
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      const SizedBox(height: 4),
                      ...test.parameters.map(
                        (parameter) => ListTile(
                          contentPadding: EdgeInsets.zero,
                          title: Text(parameter.name),
                          subtitle: Text(parameter.code),
                          trailing: Text(
                            '${parameter.value} ${parameter.unit}',
                          ),
                        ),
                      ),
                      if (test.staffRemarks?.isNotEmpty ?? false)
                        _Remarks(
                          label: 'Staff remarks',
                          text: test.staffRemarks!,
                        ),
                      _Notice(
                        icon: test.customerDecision.isTerminal
                            ? Icons.fact_check_outlined
                            : Icons.schedule,
                        text: test.customerDecision.label,
                      ),
                      if (test.customerRemarks?.isNotEmpty ?? false)
                        _Remarks(
                          label: 'Customer remarks',
                          text: test.customerRemarks!,
                        ),
                    ],
                  ],
                ),
              ),
      ),
    );
  }

  void _addReading() => setState(() => _readings.add(_ReadingInput()));

  void _removeReading(int index) {
    final removed = _readings.removeAt(index);
    removed.dispose();
    setState(() {});
  }

  Future<void> _pickAndUpload(String milkTestId, {String? replaceImageId}) async {
    final candidate = await _pickMilkTestImage();
    if (candidate == null || !mounted) return;
    setState(() => _selectedImage = candidate);
    final controller = ref.read(milkTestControllerProvider.notifier);
    final bool succeeded;
    if (replaceImageId == null) {
      succeeded = await controller.uploadImage(
        widget.deliveryId,
        milkTestId,
        bytes: candidate.bytes,
        fileName: candidate.fileName,
        contentType: candidate.contentType,
      );
    } else {
      succeeded = await controller.replaceImageAsStaff(
        widget.deliveryId,
        milkTestId,
        replaceImageId,
        bytes: candidate.bytes,
        fileName: candidate.fileName,
        contentType: candidate.contentType,
      );
    }
    if (!mounted) return;
    // Clear the local candidate either way: on success the persisted list is
    // re-rendered with the new image; on failure the old image was kept
    // (atomic replacement), so the persisted list must remain visible.
    setState(() => _selectedImage = null);
    if (succeeded) {
      _showMessage(
        replaceImageId == null
            ? 'Test image uploaded.'
            : 'Test image replaced.',
      );
    } else {
      _showMessage(
        replaceImageId == null
            ? 'Upload failed. No image was changed.'
            : 'Replacement failed. The original image was kept.',
      );
    }
  }

  Future<void> _confirmDelete(StaffMilkTest test, MilkTestImage image) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Delete this test image?'),
        content: Text('${image.fileName} will be removed from this milk test.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    final deleted = await ref
        .read(milkTestControllerProvider.notifier)
        .deleteImage(widget.deliveryId, test.milkTestId, image.imageId);
    if (deleted && mounted) _showMessage('Test image deleted.');
  }

  Future<void> _complete(String milkTestId) async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    final parameters = _readings
        .map(
          (reading) => MilkTestParameter(
            code: reading.code.text.trim(),
            name: reading.name.text.trim(),
            value: double.parse(reading.value.text.trim()),
            unit: reading.unit.text.trim(),
          ),
        )
        .toList(growable: false);
    final remarks = _remarks.text.trim();
    final completed = await ref
        .read(milkTestControllerProvider.notifier)
        .complete(
          milkTestId,
          parameters: parameters,
          remarks: remarks.isEmpty ? null : remarks,
        );
    if (completed && mounted) _showMessage('Milk test completed.');
  }
}

class _MilkTestImageCandidate {
  const _MilkTestImageCandidate({
    required this.bytes,
    required this.fileName,
    required this.contentType,
  });

  final Uint8List bytes;
  final String fileName;
  final String contentType;
}

class _LocalMilkTestImagePreview extends StatelessWidget {
  const _LocalMilkTestImagePreview({required this.candidate});

  final _MilkTestImageCandidate candidate;

  @override
  Widget build(BuildContext context) => Semantics(
    label: 'Selected milk test image preview',
    image: true,
    child: ClipRRect(
      borderRadius: BorderRadius.circular(12),
      child: Image.memory(
        candidate.bytes,
        height: 220,
        width: double.infinity,
        fit: BoxFit.cover,
        errorBuilder: (_, _, _) => const SizedBox(
          height: 120,
          child: Center(child: Text('Preview unavailable')),
        ),
      ),
    ),
  );
}

class _ReadingInput {
  _ReadingInput({String code = '', String name = '', String unit = ''})
    : code = TextEditingController(text: code),
      name = TextEditingController(text: name),
      value = TextEditingController(),
      unit = TextEditingController(text: unit);

  final TextEditingController code;
  final TextEditingController name;
  final TextEditingController value;
  final TextEditingController unit;

  void dispose() {
    code.dispose();
    name.dispose();
    value.dispose();
    unit.dispose();
  }
}

class _ReadingFields extends StatelessWidget {
  const _ReadingFields({
    super.key,
    required this.input,
    required this.canRemove,
    required this.onRemove,
  });

  final _ReadingInput input;
  final bool canRemove;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 16),
    child: Column(
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: TextFormField(
                controller: input.code,
                textCapitalization: TextCapitalization.characters,
                decoration: const InputDecoration(
                  labelText: 'Code',
                  border: OutlineInputBorder(),
                ),
                validator: _required,
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              flex: 2,
              child: TextFormField(
                controller: input.name,
                decoration: const InputDecoration(
                  labelText: 'Reading name',
                  border: OutlineInputBorder(),
                ),
                validator: _required,
              ),
            ),
            IconButton(
              tooltip: 'Remove reading',
              onPressed: canRemove ? onRemove : null,
              icon: const Icon(Icons.remove_circle_outline),
            ),
          ],
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            Expanded(
              flex: 2,
              child: TextFormField(
                controller: input.value,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                  signed: true,
                ),
                decoration: const InputDecoration(
                  labelText: 'Value',
                  border: OutlineInputBorder(),
                ),
                validator: (value) {
                  if (value == null || double.tryParse(value.trim()) == null) {
                    return 'Enter a number';
                  }
                  return null;
                },
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: TextFormField(
                controller: input.unit,
                decoration: const InputDecoration(
                  labelText: 'Unit',
                  border: OutlineInputBorder(),
                ),
                validator: _required,
              ),
            ),
          ],
        ),
      ],
    ),
  );
}

class _MilkTestBodyState extends StatelessWidget {
  const _MilkTestBodyState({
    required this.state,
    required this.hasData,
    required this.onRetry,
    required this.child,
  });

  final MilkTestState state;
  final bool hasData;
  final Future<void> Function() onRetry;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    if (state.isLoading && !hasData) {
      return const LoadingStatePanel(message: 'Loading milk test...');
    }
    if (state.isUnauthorized && !hasData) return const UnauthorizedStatePanel();
    if (state.isOffline && !hasData) return OfflineStatePanel(onRetry: onRetry);
    if (state.errorMessage != null && !hasData) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: onRetry);
    }
    return child;
  }
}

class _StatusHeader extends StatelessWidget {
  const _StatusHeader({required this.status, required this.decision});

  final MilkTestStatus status;
  final MilkTestCustomerDecision decision;

  @override
  Widget build(BuildContext context) => Card(
    color: DoodhColors.mint,
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Row(
        children: [
          const CircleAvatar(
            backgroundColor: DoodhColors.teal,
            foregroundColor: Colors.white,
            child: Icon(Icons.science_outlined),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Doorstep milk test',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 4),
                Text(
                  status == MilkTestStatus.completed
                      ? 'Results are ready to review.'
                      : 'Your delivery test status',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ),
          DoodhStatusPill(
            label: decision.isTerminal ? decision.label : status.label,
            tone: _milkTestTone(status, decision),
          ),
        ],
      ),
    ),
  );
}

DoodhStatusTone _milkTestTone(
  MilkTestStatus status,
  MilkTestCustomerDecision decision,
) {
  if (decision == MilkTestCustomerDecision.confirmed) {
    return DoodhStatusTone.success;
  }
  if (decision == MilkTestCustomerDecision.rejected) {
    return DoodhStatusTone.error;
  }
  if (status == MilkTestStatus.completed) return DoodhStatusTone.warning;
  return DoodhStatusTone.neutral;
}

class _TimelineRow extends StatelessWidget {
  const _TimelineRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  final IconData icon;
  final String label;
  final String value;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    leading: Icon(icon),
    title: Text(label),
    subtitle: Text(value),
  );
}

class _AuthenticatedImages extends ConsumerWidget {
  const _AuthenticatedImages({
    required this.milkTestId,
    required this.images,
    this.onDelete,
    this.onReplace,
  });

  final String milkTestId;
  final List<MilkTestImage> images;
  final void Function(MilkTestImage image)? onDelete;
  final void Function(MilkTestImage image)? onReplace;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (images.isEmpty) {
      return const _Notice(
        icon: Icons.image_not_supported_outlined,
        text: 'No test images have been uploaded.',
      );
    }
    final editable = onDelete != null || onReplace != null;
    return SizedBox(
      height: editable ? 280 : 200,
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        itemCount: images.length,
        separatorBuilder: (_, _) => const SizedBox(width: 12),
        itemBuilder: (context, index) {
          final image = images[index];
          return SizedBox(
            width: editable ? 240 : 200,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: [
                _AuthenticatedImageTile(
                  milkTestId: milkTestId,
                  image: image,
                  index: index,
                ),
                const SizedBox(height: 4),
                Text(
                  image.fileName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
                if (editable) ...[
                  const SizedBox(height: 4),
                  Wrap(
                    alignment: WrapAlignment.center,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    spacing: 4,
                    children: [
                      if (onReplace != null)
                        TextButton.icon(
                          onPressed: () => onReplace!(image),
                          icon: const Icon(Icons.refresh_outlined, size: 18),
                          label: const Text('Replace'),
                          style: TextButton.styleFrom(
                            visualDensity: VisualDensity.compact,
                            padding: const EdgeInsets.symmetric(horizontal: 10),
                            minimumSize: const Size(0, 36),
                            tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                          ),
                        ),
                      if (onDelete != null)
                        TextButton.icon(
                          onPressed: () => onDelete!(image),
                          icon: const Icon(Icons.delete_outline, size: 18),
                          label: const Text('Delete'),
                          style: TextButton.styleFrom(
                            visualDensity: VisualDensity.compact,
                            padding: const EdgeInsets.symmetric(horizontal: 10),
                            minimumSize: const Size(0, 36),
                            tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                          ),
                        ),
                    ],
                  ),
                ],
              ],
            ),
          );
        },
      ),
    );
  }
}

/// Loads a single protected milk test image over the authenticated channel and
/// renders it from bytes.
///
/// On the web, [Image.network] cannot attach the JWT Authorization header
/// because the browser's `<img>` element does not expose request headers, so a
/// request to the protected content endpoint fails with 403. Instead the bytes
/// are fetched through the authenticated [milkTestRepositoryProvider] and
/// rendered with [Image.memory]. The media endpoint is never requested
/// unauthenticated; if no session token is available a controlled
/// not-available state is shown.
class _AuthenticatedImageTile extends ConsumerStatefulWidget {
  const _AuthenticatedImageTile({
    required this.milkTestId,
    required this.image,
    required this.index,
  });

  final String milkTestId;
  final MilkTestImage image;
  final int index;

  @override
  ConsumerState<_AuthenticatedImageTile> createState() =>
      _AuthenticatedImageTileState();
}

class _AuthenticatedImageTileState extends ConsumerState<_AuthenticatedImageTile> {
  Future<Uint8List>? _bytesFuture;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (_bytesFuture == null) {
      final token = ref
          .read(sessionControllerProvider)
          .session
          ?.accessToken;
      _bytesFuture = token == null
          ? Future<Uint8List>.error(
              const ApiByteUnauthenticatedException())
          : ref.read(milkTestRepositoryProvider).getImageContent(
                token,
                widget.milkTestId,
                widget.image.imageId,
              ).then((response) => response.bytes);
    }
  }

  @override
  Widget build(BuildContext context) => Semantics(
    label: 'Milk test image ${widget.index + 1}',
    image: true,
    container: true,
    child: ClipRRect(
      borderRadius: BorderRadius.circular(6),
      child: AspectRatio(
        aspectRatio: 4 / 3,
        child: FutureBuilder<Uint8List>(
          future: _bytesFuture,
          builder: (context, snapshot) {
            if (snapshot.hasData) {
              return Image.memory(
                snapshot.data!,
                fit: BoxFit.cover,
                gaplessPlayback: true,
                errorBuilder: (_, _, _) => _imageStateBox(
                  context,
                  const Icon(Icons.broken_image_outlined),
                ),
              );
            }
            if (snapshot.hasError) {
              final error = snapshot.error;
              final isUnauthorized =
                  error is ApiByteUnauthenticatedException ||
                      (error is ApiException && error.statusCode == 403);
              return _imageStateBox(
                context,
                Icon(
                  isUnauthorized
                      ? Icons.lock_outline
                      : Icons.broken_image_outlined,
                ),
                message: isUnauthorized
                    ? 'You do not have access to this image.'
                    : 'Unable to load this image.',
              );
            }
            return const ColoredBox(
              color: Color(0xFFEEEEEE),
              child: Center(
                child: SizedBox.square(
                  dimension: 24,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ),
            );
          },
        ),
      ),
    ),
  );

  Widget _imageStateBox(BuildContext context, Widget child, {String? message}) {
    final theme = Theme.of(context);
    return ColoredBox(
      color: theme.colorScheme.surfaceContainerHighest,
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            child,
            if (message != null) ...[
              const SizedBox(height: 6),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 8),
                child: Text(
                  message,
                  textAlign: TextAlign.center,
                  style: theme.textTheme.bodySmall,
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _Notice extends StatelessWidget {
  const _Notice({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 12),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 20),
        const SizedBox(width: 10),
        Expanded(child: Text(text)),
      ],
    ),
  );
}

class _Remarks extends StatelessWidget {
  const _Remarks({required this.label, required this.text});

  final String label;
  final String text;

  @override
  Widget build(BuildContext context) => ListTile(
    contentPadding: EdgeInsets.zero,
    leading: const Icon(Icons.notes_outlined),
    title: Text(label),
    subtitle: Text(text),
  );
}

class _InlineError extends StatelessWidget {
  const _InlineError({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Material(
    color: Theme.of(context).colorScheme.errorContainer,
    borderRadius: BorderRadius.circular(6),
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Row(
        children: [
          const Icon(Icons.error_outline),
          const SizedBox(width: 8),
          Expanded(child: Text(message)),
        ],
      ),
    ),
  );
}

class _ButtonProgress extends StatelessWidget {
  const _ButtonProgress();

  @override
  Widget build(BuildContext context) => const SizedBox.square(
    dimension: 18,
    child: CircularProgressIndicator(strokeWidth: 2),
  );
}

String? _required(String? value) =>
    value == null || value.trim().isEmpty ? 'Required' : null;

String? resolveMilkTestImageContentType(String fileName, String? declaredType) {
  final normalized = declaredType?.trim().toLowerCase();
  if (normalized == 'image/jpeg' || normalized == 'image/png') {
    return normalized;
  }
  final lowerName = fileName.toLowerCase();
  if (lowerName.endsWith('.jpg') || lowerName.endsWith('.jpeg')) {
    return 'image/jpeg';
  }
  if (lowerName.endsWith('.png')) return 'image/png';
  return null;
}

String formatMilkTestDateTime(DateTime value) {
  final local = toIndiaTime(value);
  String two(int number) => number.toString().padLeft(2, '0');
  return '${two(local.day)}/${two(local.month)}/${local.year} '
      '${two(local.hour)}:${two(local.minute)}';
}
