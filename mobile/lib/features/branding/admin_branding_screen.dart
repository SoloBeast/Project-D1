import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/media_url.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/branding/admin_branding_controller.dart';
import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

const String kBrandingReadPermission = 'SETUP.BRANDING.READ';
const String kBrandingManagePermission = 'SETUP.BRANDING.MANAGE';

/// Injected file source so tests can simulate a picked file without the
/// platform image picker (same convention as CatalogueImagePicker).
typedef BrandingFilePicker =
    Future<Uint8List?> Function({
      required String acceptHint,
    });

/// Setup → Branding (Owner / System Admin).
///
/// Manages the two global branding assets — logo and startup animation.
/// Requires `SETUP.BRANDING.READ` to view and `SETUP.BRANDING.MANAGE` to
/// change. Uploads replace the active asset atomically on the backend; the
/// preview always reflects the server-confirmed state from the last response.
class AdminBrandingScreen extends ConsumerStatefulWidget {
  const AdminBrandingScreen({super.key, this.pickFile});

  final BrandingFilePicker? pickFile;

  @override
  ConsumerState<AdminBrandingScreen> createState() =>
      _AdminBrandingScreenState();
}

class _AdminBrandingScreenState extends ConsumerState<AdminBrandingScreen> {
  BrandingFilePicker get _pickFile =>
      widget.pickFile ?? _pickImageBytes;

  bool get _canManage {
    final permissions =
        ref.watch(sessionControllerProvider).session?.user.permissions ??
        const <String>[];
    return permissions.contains(kBrandingManagePermission);
  }

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(adminBrandingControllerProvider.notifier).load(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(adminBrandingControllerProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Branding')),
      body: Builder(
        builder: (context) {
          if (state.configuration == null && state.isLoading) {
            return const LoadingStatePanel(message: 'Loading branding');
          }
          if (state.configuration == null && state.errorMessage != null) {
            return ErrorStatePanel(
              message: state.errorMessage!,
              onRetry: () =>
                  ref.read(adminBrandingControllerProvider.notifier).load(),
            );
          }
          return _BrandingForm(
            state: state,
            canManage: _canManage,
            pickFile: _pickFile,
          );
        },
      ),
    );
  }
}

class _BrandingForm extends ConsumerStatefulWidget {
  const _BrandingForm({
    required this.state,
    required this.canManage,
    required this.pickFile,
  });

  final AdminBrandingState state;
  final bool canManage;
  final BrandingFilePicker pickFile;

  @override
  ConsumerState<_BrandingForm> createState() => _BrandingFormState();
}

class _BrandingFormState extends ConsumerState<_BrandingForm> {
  String? _lastShownMessage;

  void _showSnack(String message, {required bool isError}) {
    ScaffoldMessenger.of(context)
      ..hideCurrentSnackBar()
      ..showSnackBar(
        SnackBar(
          content: Text(message),
          backgroundColor: isError
              ? Theme.of(context).colorScheme.error
              : null,
        ),
      );
  }

  Future<void> _pickAndUpload(BrandingAssetKind kind) async {
    final acceptHint = kind == BrandingAssetKind.logo
        ? 'PNG, JPEG, or WebP up to 10 MB'
        : 'Animated GIF up to 5 MB';
    try {
      final bytes = await widget.pickFile(acceptHint: acceptHint);
      if (bytes == null) return; // User cancelled the picker.
      if (!mounted) return;
      final controller = ref.read(adminBrandingControllerProvider.notifier);
      if (kind == BrandingAssetKind.logo) {
        await controller.uploadLogo(
          bytes: bytes,
          fileName: _fileNameFor(kind),
          contentType: _contentTypeFor(bytes),
        );
      } else {
        final contentType = _gifContentType(bytes);
        if (contentType == null) {
          _showSnack('The startup animation must be an animated GIF.',
              isError: true);
          return;
        }
        await controller.uploadStartupAnimation(
          bytes: bytes,
          fileName: _fileNameFor(kind),
          contentType: contentType,
        );
      }
    } on Object {
      if (mounted) {
        _showSnack('Could not read the selected file.', isError: true);
      }
    }
  }

  String _fileNameFor(BrandingAssetKind kind) => kind == BrandingAssetKind.logo
      ? 'logo-${DateTime.now().millisecondsSinceEpoch}.png'
      : 'startup-animation-${DateTime.now().millisecondsSinceEpoch}.gif';

  String _contentTypeFor(Uint8List bytes) {
    if (_hasPrefix(bytes, [0x89, 0x50, 0x4E, 0x47])) return 'image/png';
    if (_hasPrefix(bytes, [0xFF, 0xD8, 0xFF])) return 'image/jpeg';
    if (bytes.length >= 12 &&
        _hasPrefix(bytes, [0x52, 0x49, 0x46, 0x46]) &&
        _hasPrefix(bytes.sublist(8), [0x57, 0x45, 0x42, 0x50])) {
      return 'image/webp';
    }
    return 'application/octet-stream';
  }

  /// Returns `image/gif` only when the bytes carry the GIF87a/GIF89a
  /// signature; the backend enforces the same rule, so failing fast locally
  /// gives the admin an immediate, understandable error.
  String? _gifContentType(Uint8List bytes) {
    if (bytes.length >= 6 &&
        _hasPrefix(bytes, 'GIF8'.codeUnits) &&
        (bytes[4] == 0x37 || bytes[4] == 0x39) &&
        bytes[5] == 0x61) {
      return 'image/gif';
    }
    return null;
  }

  bool _hasPrefix(Uint8List bytes, List<int> prefix) {
    if (bytes.length < prefix.length) return false;
    for (var i = 0; i < prefix.length; i++) {
      if (bytes[i] != prefix[i]) return false;
    }
    return true;
  }

  @override
  Widget build(BuildContext context) {
    final state = widget.state;
    final theme = Theme.of(context);

    // Surface one message per completed action (guarded so snack bars are not
    // re-shown on every rebuild of the same state).
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final error = state.errorMessage;
      final saved = state.savedMessage;
      if (error != null && error != _lastShownMessage) {
        _lastShownMessage = error;
        _showSnack(error, isError: true);
      } else if (saved != null && saved != _lastShownMessage) {
        _lastShownMessage = saved;
        _showSnack(saved, isError: false);
      }
    });

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text(
          'Business branding shown when the app starts, before sign in.',
          style: theme.textTheme.bodyMedium
              ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
        ),
        const SizedBox(height: 16),
        _AssetCard(
          title: 'Logo',
          subtitle: 'PNG, JPEG, or WebP · up to 10 MB',
          asset: state.configuration?.logo,
          busy: state.isSaving,
          canManage: widget.canManage,
          onPick: () => _pickAndUpload(BrandingAssetKind.logo),
          onRemove: () => ref
              .read(adminBrandingControllerProvider.notifier)
              .removeLogo(),
        ),
        const SizedBox(height: 16),
        _AssetCard(
          title: 'Startup animation',
          subtitle: 'Animated GIF · up to 5 MB',
          asset: state.configuration?.startupAnimation,
          busy: state.isSaving,
          canManage: widget.canManage,
          onPick: () => _pickAndUpload(BrandingAssetKind.startupAnimation),
          onRemove: () => ref
              .read(adminBrandingControllerProvider.notifier)
              .removeStartupAnimation(),
        ),
        if (!widget.canManage) ...[
          const SizedBox(height: 16),
          Text(
            'You can view branding, but changing it requires the Manage '
            'application branding permission.',
            style: theme.textTheme.bodySmall
                ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
          ),
        ],
      ],
    );
  }
}

class _AssetCard extends StatelessWidget {
  const _AssetCard({
    required this.title,
    required this.subtitle,
    required this.asset,
    required this.busy,
    required this.canManage,
    required this.onPick,
    required this.onRemove,
  });

  final String title;
  final String subtitle;
  final BrandingAssetInfo? asset;
  final bool busy;
  final bool canManage;
  final VoidCallback onPick;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      clipBehavior: Clip.antiAlias,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: theme.textTheme.titleMedium),
                      const SizedBox(height: 4),
                      Text(
                        subtitle,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
                if (asset != null)
                  Chip(
                    label: const Text('Active'),
                    visualDensity: VisualDensity.compact,
                  ),
              ],
            ),
            const SizedBox(height: 12),
            SizedBox(
              height: 120,
              width: double.infinity,
              child: DecoratedBox(
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerHighest
                      .withValues(alpha: 0.4),
                  borderRadius: BorderRadius.circular(12),
                ),
                child: asset == null
                    ? Center(
                        child: Text(
                          'Not configured — the app shows its bundled branding.',
                          textAlign: TextAlign.center,
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant,
                          ),
                        ),
                      )
                    : Padding(
                        padding: const EdgeInsets.all(8),
                        child: Image.network(
                          resolveMediaUrl(asset!.url) ?? asset!.url,
                          fit: BoxFit.contain,
                          errorBuilder: (_, _, _) => Center(
                            child: Text(
                              asset!.fileName,
                              style: theme.textTheme.bodySmall,
                            ),
                          ),
                        ),
                      ),
              ),
            ),
            if (asset != null) ...[
              const SizedBox(height: 8),
              Text(
                '${asset!.fileName} · updated ${_shortDateTime(asset!.updatedAt)}',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ],
            if (canManage) ...[
              const SizedBox(height: 12),
              Row(
                children: [
                  Expanded(
                    child: FilledButton.icon(
                      onPressed: busy ? null : onPick,
                      icon: Icon(
                        asset == null
                            ? Icons.upload_rounded
                            : Icons.autorenew_rounded,
                      ),
                      label: Text(asset == null ? 'Upload' : 'Replace'),
                    ),
                  ),
                  if (asset != null) ...[
                    const SizedBox(width: 12),
                    OutlinedButton(
                      onPressed: busy ? null : onRemove,
                      child: const Text('Remove'),
                    ),
                  ],
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }

  String _shortDateTime(DateTime value) =>
      '${value.year}-${value.month.toString().padLeft(2, '0')}-'
      '${value.day.toString().padLeft(2, '0')} '
      '${value.hour.toString().padLeft(2, '0')}:'
      '${value.minute.toString().padLeft(2, '0')}';
}

/// Default picker: mobile gallery/file selection. On Flutter Web the
/// image_picker plugin reads the file bytes directly, so the same
/// `Uint8List` pipeline works on every supported platform.
Future<Uint8List?> _pickImageBytes({required String acceptHint}) async {
  final picker = ImagePicker();
  final file = await picker.pickImage(source: ImageSource.gallery);
  if (file == null) return null;
  return file.readAsBytes();
}

/// Permission-visible export used by the Admin Centre gating helpers.
bool brandingCanRead(List<String> permissions) =>
    permissions.contains(kBrandingReadPermission) ||
    permissions.contains(kBrandingManagePermission);
