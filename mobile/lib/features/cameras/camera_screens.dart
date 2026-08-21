import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:video_player/video_player.dart';

import 'camera_controller.dart';
import 'camera_models.dart';

class LiveDairyCameraListScreen extends ConsumerStatefulWidget {
  const LiveDairyCameraListScreen({super.key});

  @override
  ConsumerState<LiveDairyCameraListScreen> createState() =>
      _LiveDairyCameraListScreenState();
}

class _LiveDairyCameraListScreenState
    extends ConsumerState<LiveDairyCameraListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() =>
      ref.read(cameraControllerProvider.notifier).loadPublic();

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(cameraControllerProvider);
    return Scaffold(
      appBar: AppBar(title: const Text('Live Dairy')),
      body: _CameraListBody(state: state, onRetry: _load),
    );
  }
}

class _CameraListBody extends StatelessWidget {
  const _CameraListBody({required this.state, required this.onRetry});

  final CameraState state;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) {
    if (state.isLoading && state.publicCameras.isEmpty) {
      return const LoadingStatePanel(message: 'Loading dairy cameras...');
    }
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isOffline && state.publicCameras.isEmpty) {
      return OfflineStatePanel(onRetry: onRetry);
    }
    if (state.errorMessage != null && state.publicCameras.isEmpty) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: onRetry);
    }
    if (state.publicCameras.isEmpty) {
      return EmptyStatePanel(
        title: 'No live cameras available',
        message: 'Public dairy cameras will appear here when enabled.',
        action: OutlinedButton.icon(
          onPressed: onRetry,
          icon: const Icon(Icons.refresh),
          label: const Text('Refresh'),
        ),
      );
    }

    return RefreshIndicator(
      onRefresh: onRetry,
      child: ListView.separated(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 20, 16, 28),
        itemCount: state.publicCameras.length + 1,
        separatorBuilder: (_, index) => const SizedBox(height: 10),
        itemBuilder: (context, index) {
          if (index == 0) {
            return Row(
              children: [
                const Icon(Icons.live_tv_outlined, color: DoodhColors.teal),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    'Watch dairy activity live',
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                ),
              ],
            );
          }
          final camera = state.publicCameras[index - 1];
          final available = camera.isAvailable;
          return Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Container(
                    width: 48,
                    height: 48,
                    decoration: BoxDecoration(
                      color: available ? DoodhColors.mint : DoodhColors.line,
                      borderRadius: DoodhRadii.sm,
                    ),
                    child: Icon(
                      available
                          ? Icons.videocam_outlined
                          : Icons.videocam_off_outlined,
                      color: available
                          ? DoodhColors.tealDark
                          : DoodhColors.muted,
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: ListTile(
                      contentPadding: EdgeInsets.zero,
                      enabled: available,
                      title: Text(
                        camera.displayName,
                        style: Theme.of(context).textTheme.titleMedium,
                      ),
                      subtitle: Padding(
                        padding: const EdgeInsets.only(top: 4),
                        child: DoodhStatusPill(
                          label: available ? 'Live now' : 'Temporarily offline',
                          tone: available
                              ? DoodhStatusTone.success
                              : DoodhStatusTone.neutral,
                        ),
                      ),
                      onTap: available
                          ? () => context.push('/cameras/${camera.cameraId}')
                          : null,
                    ),
                  ),
                  if (available)
                    const Icon(Icons.chevron_right, color: DoodhColors.teal),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}

class LiveDairyCameraViewerScreen extends ConsumerStatefulWidget {
  const LiveDairyCameraViewerScreen({
    super.key,
    required this.cameraId,
    this.playerBuilder,
  });

  final String cameraId;
  final Widget Function(CameraStreamDescriptor stream, VoidCallback onFailure)?
  playerBuilder;

  @override
  ConsumerState<LiveDairyCameraViewerScreen> createState() =>
      _LiveDairyCameraViewerScreenState();
}

class _LiveDairyCameraViewerScreenState
    extends ConsumerState<LiveDairyCameraViewerScreen> {
  bool _playerFailed = false;

  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() async {
    if (mounted) setState(() => _playerFailed = false);
    await ref
        .read(cameraControllerProvider.notifier)
        .loadStream(widget.cameraId);
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(cameraControllerProvider);
    final result = state.stream?.cameraId == widget.cameraId
        ? state.stream
        : null;
    return Scaffold(
      appBar: AppBar(title: Text(result?.displayName ?? 'Live camera')),
      body: _buildBody(state, result),
    );
  }

  Widget _buildBody(CameraState state, PublicCameraStream? result) {
    if (state.isLoading && result == null) {
      return const LoadingStatePanel(message: 'Connecting to live camera...');
    }
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isOffline && result == null) {
      return OfflineStatePanel(onRetry: _load);
    }
    if (state.isUnavailable) {
      return StatePanel(
        icon: Icons.videocam_off_outlined,
        title: 'Camera temporarily unavailable',
        message:
            state.errorMessage ??
            'The live stream is offline. Please try again shortly.',
        action: FilledButton.icon(
          onPressed: _load,
          icon: const Icon(Icons.refresh),
          label: const Text('Retry'),
        ),
      );
    }
    if (state.errorMessage != null && result == null) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    }
    if (result == null) {
      return ErrorStatePanel(
        message: 'The live camera stream could not be loaded.',
        onRetry: _load,
      );
    }
    if (result.stream.isExpired) {
      return StatePanel(
        icon: Icons.timer_off_outlined,
        title: 'Stream session expired',
        message: 'Refresh to request a new secure playback session.',
        action: FilledButton.icon(
          onPressed: _load,
          icon: const Icon(Icons.refresh),
          label: const Text('Refresh stream'),
        ),
      );
    }
    if (result.stream.protocol != CameraStreamProtocol.hls) {
      return StatePanel(
        icon: Icons.devices_other_outlined,
        title: 'Stream format unavailable',
        message: 'This device cannot play the selected camera format.',
        action: FilledButton.icon(
          onPressed: _load,
          icon: const Icon(Icons.refresh),
          label: const Text('Retry'),
        ),
      );
    }
    if (_playerFailed) {
      return StatePanel(
        icon: Icons.play_disabled_outlined,
        title: 'Stream playback failed',
        message: 'The camera was reached, but playback could not start.',
        action: FilledButton.icon(
          onPressed: _load,
          icon: const Icon(Icons.refresh),
          label: const Text('Retry stream'),
        ),
      );
    }

    final player =
        widget.playerBuilder?.call(
          result.stream,
          () => setState(() => _playerFailed = true),
        ) ??
        _HlsCameraPlayer(
          key: ValueKey(result.stream.playbackUri),
          stream: result.stream,
          onFailure: () => setState(() => _playerFailed = true),
        );
    return Column(
      children: [
        if (result.stream.isDevelopmentStream)
          MaterialBanner(
            content: const Text('Development stream - not a production camera'),
            leading: const Icon(Icons.science_outlined),
            actions: const [SizedBox.shrink()],
          ),
        Expanded(child: Center(child: player)),
      ],
    );
  }
}

class _HlsCameraPlayer extends StatefulWidget {
  const _HlsCameraPlayer({
    super.key,
    required this.stream,
    required this.onFailure,
  });

  final CameraStreamDescriptor stream;
  final VoidCallback onFailure;

  @override
  State<_HlsCameraPlayer> createState() => _HlsCameraPlayerState();
}

class _HlsCameraPlayerState extends State<_HlsCameraPlayer> {
  late final VideoPlayerController _controller;
  bool _initializing = true;

  @override
  void initState() {
    super.initState();
    _controller = VideoPlayerController.networkUrl(
      widget.stream.playbackUri,
      formatHint: VideoFormat.hls,
      videoPlayerOptions: VideoPlayerOptions(mixWithOthers: true),
    );
    _initialize();
  }

  Future<void> _initialize() async {
    try {
      await _controller.initialize();
      await _controller.setLooping(true);
      await _controller.play();
      if (mounted) setState(() => _initializing = false);
    } on Object {
      if (mounted) widget.onFailure();
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (_initializing) {
      return const LoadingStatePanel(message: 'Starting live stream...');
    }
    final aspectRatio = _controller.value.aspectRatio > 0
        ? _controller.value.aspectRatio
        : 16 / 9;
    return Semantics(
      label: 'Live dairy camera stream',
      child: AspectRatio(
        aspectRatio: aspectRatio,
        child: Stack(
          alignment: Alignment.bottomCenter,
          children: [
            VideoPlayer(_controller),
            VideoProgressIndicator(_controller, allowScrubbing: false),
          ],
        ),
      ),
    );
  }
}

class AdminCameraListScreen extends ConsumerStatefulWidget {
  const AdminCameraListScreen({super.key});

  @override
  ConsumerState<AdminCameraListScreen> createState() =>
      _AdminCameraListScreenState();
}

class _AdminCameraListScreenState extends ConsumerState<AdminCameraListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_load);
  }

  Future<void> _load() =>
      ref.read(cameraControllerProvider.notifier).loadManaged();

  bool get _canManage {
    final permissions =
        ref.read(sessionControllerProvider).session?.user.permissions ??
        const <String>[];
    return permissions.contains('CAMERAS.MANAGE');
  }

  Future<void> _openForm([ManagedCamera? camera]) async {
    final user = ref.read(sessionControllerProvider).session?.user;
    if (user == null || !_canManage) return;
    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (context) => _CameraFormDialog(
        camera: camera,
        branchIds: user.branchIds,
        hasGlobalAccess: user.permissions.contains('ACCESS.GLOBAL'),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(cameraControllerProvider);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Camera management'),
        actions: [
          IconButton(
            tooltip: 'Refresh cameras',
            onPressed: state.isLoading ? null : _load,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      floatingActionButton: _canManage
          ? FloatingActionButton(
              tooltip: 'Add camera',
              onPressed: state.isSaving ? null : () => _openForm(),
              child: const Icon(Icons.add),
            )
          : null,
      body: _buildBody(state),
    );
  }

  Widget _buildBody(CameraState state) {
    if (state.isLoading && state.managedCameras.isEmpty) {
      return const LoadingStatePanel(message: 'Loading camera metadata...');
    }
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isOffline && state.managedCameras.isEmpty) {
      return OfflineStatePanel(onRetry: _load);
    }
    if (state.errorMessage != null && state.managedCameras.isEmpty) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: _load);
    }
    if (state.managedCameras.isEmpty) {
      return EmptyStatePanel(
        title: 'No cameras configured',
        message: 'Add camera metadata for an authorized dairy branch.',
        action: _canManage
            ? FilledButton.icon(
                onPressed: () => _openForm(),
                icon: const Icon(Icons.add),
                label: const Text('Add camera'),
              )
            : null,
      );
    }

    final cameras = [...state.managedCameras]
      ..sort((left, right) {
        final branchOrder = left.branchName.compareTo(right.branchName);
        return branchOrder != 0
            ? branchOrder
            : left.displayOrder.compareTo(right.displayOrder);
      });
    return RefreshIndicator(
      onRefresh: _load,
      child: ListView.builder(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(12, 12, 12, 88),
        itemCount: cameras.length,
        itemBuilder: (context, index) {
          final camera = cameras[index];
          return Card(
            child: ListTile(
              leading: Icon(
                camera.isActive
                    ? Icons.videocam_outlined
                    : Icons.videocam_off_outlined,
              ),
              title: Text(camera.displayName),
              subtitle: Text(
                '${camera.branchName} | ${camera.internalIdentifier}\n'
                '${camera.protocol.label} | Order ${camera.displayOrder} | '
                '${camera.isPublic ? 'Public' : 'Private'} | '
                '${camera.isActive ? 'Active' : 'Inactive'}',
              ),
              isThreeLine: true,
              trailing: _canManage ? const Icon(Icons.edit_outlined) : null,
              onTap: _canManage ? () => _openForm(camera) : null,
            ),
          );
        },
      ),
    );
  }
}

class _CameraFormDialog extends ConsumerStatefulWidget {
  const _CameraFormDialog({
    required this.camera,
    required this.branchIds,
    required this.hasGlobalAccess,
  });

  final ManagedCamera? camera;
  final List<int> branchIds;
  final bool hasGlobalAccess;

  @override
  ConsumerState<_CameraFormDialog> createState() => _CameraFormDialogState();
}

class _CameraFormDialogState extends ConsumerState<_CameraFormDialog> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _branchController;
  late final TextEditingController _identifierController;
  late final TextEditingController _nameController;
  late final TextEditingController _orderController;
  late final TextEditingController _providerController;
  late final TextEditingController _referenceController;
  late int? _branchId;
  late bool _isPublic;
  late bool _isActive;
  late CameraStreamProtocol _protocol;

  bool get _isEditing => widget.camera != null;

  @override
  void initState() {
    super.initState();
    final camera = widget.camera;
    _branchId =
        camera?.branchId ??
        (widget.branchIds.isEmpty ? null : widget.branchIds.first);
    _branchController = TextEditingController(
      text: camera?.branchId.toString() ?? '',
    );
    _identifierController = TextEditingController(
      text: camera?.internalIdentifier ?? '',
    );
    _nameController = TextEditingController(text: camera?.displayName ?? '');
    _orderController = TextEditingController(
      text: camera?.displayOrder.toString() ?? '0',
    );
    _providerController = TextEditingController(
      text: camera?.providerCode ?? '',
    );
    _referenceController = TextEditingController(
      text: camera?.providerStreamReference ?? '',
    );
    _isPublic = camera?.isPublic ?? false;
    _isActive = camera?.isActive ?? true;
    _protocol = camera?.protocol == CameraStreamProtocol.webRtc
        ? CameraStreamProtocol.webRtc
        : CameraStreamProtocol.hls;
  }

  @override
  void dispose() {
    _branchController.dispose();
    _identifierController.dispose();
    _nameController.dispose();
    _orderController.dispose();
    _providerController.dispose();
    _referenceController.dispose();
    super.dispose();
  }

  String? _required(String? value) =>
      value == null || value.trim().isEmpty ? 'This field is required.' : null;

  String? _nonNegativeInteger(String? value) {
    final number = int.tryParse(value ?? '');
    return number == null || number < 0
        ? 'Enter a whole number of zero or more.'
        : null;
  }

  int? _selectedBranch() => widget.hasGlobalAccess
      ? int.tryParse(_branchController.text.trim())
      : _branchId;

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;
    final branchId = _selectedBranch();
    if (branchId == null || branchId <= 0) return;
    final request = SaveCameraRequest(
      branchId: branchId,
      internalIdentifier: _identifierController.text,
      displayName: _nameController.text,
      isPublic: _isPublic,
      isActive: _isActive,
      displayOrder: int.parse(_orderController.text),
      protocol: _protocol,
      providerCode: _providerController.text,
      providerStreamReference: _referenceController.text,
    );
    final controller = ref.read(cameraControllerProvider.notifier);
    final saved = _isEditing
        ? await controller.update(widget.camera!.cameraId, request)
        : await controller.create(request);
    if (!mounted) return;
    if (saved) {
      Navigator.of(context).pop();
      return;
    }
    final message = ref.read(cameraControllerProvider).errorMessage;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(message ?? 'Camera metadata could not be saved.')),
    );
  }

  @override
  Widget build(BuildContext context) {
    final saving = ref.watch(cameraControllerProvider).isSaving;
    return AlertDialog(
      title: Text(_isEditing ? 'Edit camera' : 'Add camera'),
      content: SizedBox(
        width: 520,
        child: Form(
          key: _formKey,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                _buildBranchField(),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _identifierController,
                  decoration: const InputDecoration(
                    labelText: 'Internal identifier',
                    border: OutlineInputBorder(),
                  ),
                  textCapitalization: TextCapitalization.characters,
                  validator: _required,
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _nameController,
                  decoration: const InputDecoration(
                    labelText: 'Display name',
                    border: OutlineInputBorder(),
                  ),
                  validator: _required,
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _orderController,
                  decoration: const InputDecoration(
                    labelText: 'Display order',
                    border: OutlineInputBorder(),
                  ),
                  keyboardType: TextInputType.number,
                  validator: _nonNegativeInteger,
                ),
                const SizedBox(height: 12),
                DropdownButtonFormField<CameraStreamProtocol>(
                  initialValue: _protocol,
                  decoration: const InputDecoration(
                    labelText: 'Stream protocol',
                    border: OutlineInputBorder(),
                  ),
                  items:
                      const [
                            CameraStreamProtocol.hls,
                            CameraStreamProtocol.webRtc,
                          ]
                          .map(
                            (protocol) => DropdownMenuItem(
                              value: protocol,
                              child: Text(protocol.label),
                            ),
                          )
                          .toList(),
                  onChanged: saving
                      ? null
                      : (value) => setState(() => _protocol = value!),
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _providerController,
                  decoration: const InputDecoration(
                    labelText: 'Provider code',
                    border: OutlineInputBorder(),
                  ),
                  validator: _required,
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _referenceController,
                  decoration: const InputDecoration(
                    labelText: 'Provider stream reference',
                    helperText: 'Opaque reference only; do not enter a URL or credential.',
                    border: OutlineInputBorder(),
                  ),
                  validator: _required,
                ),
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Visible to customers'),
                  value: _isPublic,
                  onChanged: saving
                      ? null
                      : (value) => setState(() => _isPublic = value),
                ),
                if (_isEditing)
                  SwitchListTile(
                    contentPadding: EdgeInsets.zero,
                    title: const Text('Active'),
                    value: _isActive,
                    onChanged: saving
                        ? null
                        : (value) => setState(() => _isActive = value),
                  ),
              ],
            ),
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: saving ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton.icon(
          onPressed: saving ? null : _save,
          icon: saving
              ? const SizedBox.square(
                  dimension: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.save_outlined),
          label: Text(saving ? 'Saving' : 'Save'),
        ),
      ],
    );
  }

  Widget _buildBranchField() {
    if (widget.hasGlobalAccess) {
      return TextFormField(
        controller: _branchController,
        decoration: const InputDecoration(
          labelText: 'Branch ID',
          border: OutlineInputBorder(),
        ),
        keyboardType: TextInputType.number,
        validator: (value) {
          final branchId = int.tryParse(value ?? '');
          return branchId == null || branchId <= 0
              ? 'Enter a valid branch ID.'
              : null;
        },
      );
    }
    if (widget.branchIds.isEmpty) {
      return const StatePanel(
        icon: Icons.location_off_outlined,
        title: 'No branch assigned',
        message: 'A branch assignment is required to manage cameras.',
      );
    }
    return DropdownButtonFormField<int>(
      initialValue: _branchId,
      decoration: const InputDecoration(
        labelText: 'Branch',
        border: OutlineInputBorder(),
      ),
      items: widget.branchIds
          .map(
            (branchId) => DropdownMenuItem(
              value: branchId,
              child: Text('Branch $branchId'),
            ),
          )
          .toList(),
      onChanged: (value) => setState(() => _branchId = value),
      validator: (value) => value == null ? 'Select a branch.' : null,
    );
  }
}
