import 'dart:async';

import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
import 'package:doodh_direct_mobile/features/customer/maps_script_loader.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'branch_controller.dart';
import 'branch_models.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';

const String kBranchesReadPermission = 'BRANCHES.READ';
const String kBranchesManagePermission = 'BRANCHES.MANAGE';

class BranchListScreen extends ConsumerStatefulWidget {
  const BranchListScreen({super.key});

  @override
  ConsumerState<BranchListScreen> createState() => _BranchListScreenState();
}

class _BranchListScreenState extends ConsumerState<BranchListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(branchControllerProvider.notifier).load(),
    );
  }

  Future<void> _reload() =>
      ref.read(branchControllerProvider.notifier).load();

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(branchControllerProvider);
    final canManage =
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kBranchesManagePermission,
            ) ??
            false;
    return Scaffold(
      appBar: AppBar(
        title: const Text('Branches'),
        actions: [
          if (canManage)
            IconButton(
              tooltip: 'Add branch',
              onPressed: () => context.push('/admin/branches/new'),
              icon: const Icon(Icons.add),
            ),
        ],
      ),
      body: _BranchListBody(state: state, canManage: canManage, onRetry: _reload),
    );
  }
}

class _BranchListBody extends StatelessWidget {
  const _BranchListBody({
    required this.state,
    required this.canManage,
    required this.onRetry,
  });

  final BranchState state;
  final bool canManage;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) {
    if (state.isLoading && state.branches.isEmpty) {
      return const LoadingStatePanel(message: 'Loading branches...');
    }
    if (state.isUnauthorized) return const UnauthorizedStatePanel();
    if (state.isOffline && state.branches.isEmpty) {
      return OfflineStatePanel(onRetry: onRetry);
    }
    if (state.errorMessage != null && state.branches.isEmpty) {
      return ErrorStatePanel(message: state.errorMessage!, onRetry: onRetry);
    }
    if (state.branches.isEmpty) {
      return EmptyStatePanel(
        title: 'No branches yet',
        message:
            'Create your first branch to start allocating orders and '
            'scoping numbering series.',
        action: canManage
            ? FilledButton.icon(
                onPressed: () => context.push('/admin/branches/new'),
                icon: const Icon(Icons.add),
                label: const Text('Add branch'),
              )
            : null,
      );
    }

    final branches = [...state.branches]
      ..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    return RefreshIndicator(
      onRefresh: onRetry,
      child: ListView.separated(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 20, 16, 28),
        itemCount: branches.length,
        separatorBuilder: (_, index) => const SizedBox(height: 10),
        itemBuilder: (context, index) {
          final branch = branches[index];
          return _BranchCard(
            branch: branch,
            canManage: canManage,
            onTap: () => context.push('/admin/branches/${branch.publicId}'),
          );
        },
      ),
    );
  }
}

class _BranchCard extends StatelessWidget {
  const _BranchCard({
    required this.branch,
    required this.canManage,
    required this.onTap,
  });

  final Branch branch;
  final bool canManage;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: ListTile(
        contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
        leading: CircleAvatar(
          backgroundColor: DoodhColors.mint,
          child: Icon(
            Icons.storefront_outlined,
            color: DoodhColors.tealDark,
          ),
        ),
        title: Row(
          children: [
            Flexible(
              child: Text(
                branch.name,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.titleMedium,
              ),
            ),
            const SizedBox(width: 8),
            _StatusChip(
              isActive: branch.isActive,
              isArchived: branch.isArchived,
            ),
          ],
        ),
        subtitle: Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Text(
            _subtitle(branch),
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
          ),
        ),
        trailing: canManage ? const Icon(Icons.chevron_right) : null,
        onTap: onTap,
      ),
    );
  }

  String _subtitle(Branch branch) {
    final parts = <String>[
      'Code ${branch.code}',
      branch.city.trim(),
    ];
    return parts.join(' · ');
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.isActive, this.isArchived = false});

  final bool isActive;
  final bool isArchived;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final Color background;
    final Color foreground;
    final String label;
    if (isArchived) {
      background = theme.colorScheme.surfaceContainerHighest;
      foreground = theme.colorScheme.onSurfaceVariant;
      label = 'Archived';
    } else if (isActive) {
      background = DoodhColors.mint;
      foreground = DoodhColors.tealDark;
      label = 'Active';
    } else {
      background = theme.colorScheme.surfaceContainerHighest;
      foreground = theme.colorScheme.onSurfaceVariant;
      label = 'Inactive';
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(
        label,
        style: theme.textTheme.labelSmall?.copyWith(
          color: foreground,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

class BranchFormScreen extends ConsumerStatefulWidget {
  const BranchFormScreen({super.key, this.branch});

  final Branch? branch;

  bool get isEditing => branch != null;

  @override
  ConsumerState<BranchFormScreen> createState() => _BranchFormScreenState();
}

class _BranchFormScreenState extends ConsumerState<BranchFormScreen> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _code;
  late final TextEditingController _name;
  late final TextEditingController _addressLine1;
  late final TextEditingController _addressLine2;
  late final TextEditingController _locality;
  late final TextEditingController _city;
  late final TextEditingController _state;
  late final TextEditingController _pinCode;
  late final TextEditingController _serviceRadius;
  double? _latitude;
  double? _longitude;
  late bool _hasExplicitLocationSelection;
  bool _isLookingUpAddress = false;
  int _addressLookupSequence = 0;

  @override
  void initState() {
    super.initState();
    final branch = widget.branch;
    _code = TextEditingController(text: branch?.code ?? '');
    _name = TextEditingController(text: branch?.name ?? '');
    _addressLine1 = TextEditingController(text: branch?.addressLine1 ?? '');
    _addressLine2 = TextEditingController(text: branch?.addressLine2 ?? '');
    _locality = TextEditingController(text: branch?.locality ?? '');
    _city = TextEditingController(text: branch?.city ?? '');
    _state = TextEditingController(text: branch?.state ?? '');
    _pinCode = TextEditingController(text: branch?.pinCode ?? '');
    _latitude = branch?.latitude;
    _longitude = branch?.longitude;
    _hasExplicitLocationSelection = branch?.latitude != null &&
        branch?.longitude != null;
    _serviceRadius = TextEditingController(
      text: branch == null || branch.serviceRadiusKm == null
          ? ''
          : branch.serviceRadiusKm!.toString(),
    );
  }

  @override
  void dispose() {
    _code.dispose();
    _name.dispose();
    _addressLine1.dispose();
    _addressLine2.dispose();
    _locality.dispose();
    _city.dispose();
    _state.dispose();
    _pinCode.dispose();
    _serviceRadius.dispose();
    super.dispose();
  }

  Future<void> _loadGoogleMaps() async {
    final token = ref.read(sessionControllerProvider).session?.accessToken;
    String? key;
    if (token != null) {
      try {
        key = (await ref.read(clientConfigurationRepositoryProvider).get(token))
            .googleMapsWebClientKey;
      } on Object {
        key = null;
      }
    }
    return loadGoogleMapsScript(key ?? '');
  }

  LatLng _initialMapLocation() => LatLng(
        _latitude ?? 12.9716,
        _longitude ?? 77.5946,
      );

  void _setMapLocation(LatLng location) {
    setState(() {
      _latitude = location.latitude;
      _longitude = location.longitude;
      _hasExplicitLocationSelection = true;
      _isLookingUpAddress = true;
    });
    unawaited(_reverseGeocodeAddress(location.latitude, location.longitude));
  }

  Future<void> _reverseGeocodeAddress(double latitude, double longitude) async {
    final sequence = ++_addressLookupSequence;
    AddressLookup? lookup;
    try {
      lookup = await ref
          .read(customerControllerProvider.notifier)
          .reverseLookup(latitude, longitude);
    } on Object {
      lookup = null;
    }
    if (!mounted) return;
    // Ignore stale responses that belong to an earlier map selection.
    if (sequence != _addressLookupSequence) return;
    setState(() {
      _isLookingUpAddress = false;
    });
    if (lookup == null) {
      // Coordinates are kept. Never silently clear an address the user already
      // entered (e.g. when the geocoding provider is unavailable).
      final hasExistingAddress =
          _addressLine1.text.trim().isNotEmpty ||
          _locality.text.trim().isNotEmpty;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(
            content: Text(
              hasExistingAddress
                  ? 'Address lookup is unavailable right now. The map location '
                      'was updated and your existing address was preserved.'
                  : 'The selected location could not be converted to an address. '
                      'The map location is saved - enter the address manually.',
            ),
          ),
        );
      return;
    }
    _applyAddressLookup(lookup);
  }

  void _applyAddressLookup(AddressLookup lookup) {
    void fill(TextEditingController controller, String? value) {
      final normalized = value?.trim();
      if (normalized != null && normalized.isNotEmpty) {
        controller.text = normalized;
      }
    }

    fill(_addressLine1, lookup.addressLine1);
    fill(_locality, lookup.locality);
    fill(_city, lookup.city);
    fill(_state, lookup.state);
    fill(_pinCode, lookup.pinCode);
  }

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;
    final latitude = _latitude;
    final longitude = _longitude;
    if (!_hasExplicitLocationSelection || latitude == null || longitude == null) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Select a valid location on the map.')),
        );
      return;
    }
    final request = UpsertBranchRequest(
      code: _code.text.trim(),
      name: _name.text.trim(),
      addressLine1: _addressLine1.text.trim().isEmpty
          ? null
          : _addressLine1.text.trim(),
      addressLine2: _addressLine2.text.trim().isEmpty
          ? null
          : _addressLine2.text.trim(),
      locality: _locality.text.trim().isEmpty ? null : _locality.text.trim(),
      city: _city.text.trim(),
      state: _state.text.trim(),
      pinCode: _pinCode.text.trim().isEmpty ? null : _pinCode.text.trim(),
      latitude: latitude,
      longitude: longitude,
      serviceRadiusKm: _serviceRadius.text.trim().isEmpty
          ? null
          : double.tryParse(_serviceRadius.text.trim()),
    );
    final notifier = ref.read(branchControllerProvider.notifier);
    final success = widget.isEditing
        ? await notifier.update(widget.branch!.publicId, request)
        : await notifier.create(request);
    if (!mounted) return;
    final state = ref.read(branchControllerProvider);
    if (success && state.savedMessage != null) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(state.savedMessage!)));
      context.pop();
    } else if (!success) {
      final error = state.errorMessage;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(content: Text(error ?? 'Unable to save the branch.')),
        );
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(branchControllerProvider);
    final canManage =
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kBranchesManagePermission,
            ) ??
            false;
    if (!canManage) {
      return Scaffold(
        appBar: AppBar(title: const Text('Branch')),
        body: const UnauthorizedStatePanel(),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: Text(widget.isEditing ? 'Edit branch' : 'Add branch'),
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Form(
            key: _formKey,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      flex: 2,
                      child: TextFormField(
                        controller: _code,
                        enabled: !state.isSaving,
                        onChanged: (_) => setState(() {}),
                        textCapitalization: TextCapitalization.characters,
                        maxLength: 50,
                        decoration: const InputDecoration(
                          labelText: 'Branch code *',
                          hintText: 'e.g. MAIN',
                          helperText:
                              'Stable business key used for order allocation and '
                              'scoped numbering series.',
                        ),
                        validator: (value) {
                          final trimmed = value?.trim() ?? '';
                          if (trimmed.isEmpty) return 'Enter a branch code.';
                          if (trimmed.length > 50) {
                            return 'Keep the code under 50 characters.';
                          }
                          return null;
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      flex: 3,
                      child: TextFormField(
                        controller: _name,
                        enabled: !state.isSaving,
                        maxLength: 200,
                        decoration: const InputDecoration(labelText: 'Branch name *'),
                        validator: (value) {
                          final trimmed = value?.trim() ?? '';
                          if (trimmed.isEmpty) return 'Enter a branch name.';
                          return null;
                        },
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                TextFormField(
                  controller: _addressLine1,
                  enabled: !state.isSaving,
                  maxLength: 300,
                  decoration: const InputDecoration(labelText: 'Address line 1'),
                ),
                const SizedBox(height: 8),
                TextFormField(
                  controller: _addressLine2,
                  enabled: !state.isSaving,
                  maxLength: 300,
                  decoration: const InputDecoration(labelText: 'Address line 2'),
                ),
                const SizedBox(height: 8),
                TextFormField(
                  controller: _locality,
                  enabled: !state.isSaving,
                  maxLength: 150,
                  decoration: const InputDecoration(labelText: 'Locality'),
                ),
                const SizedBox(height: 8),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      flex: 3,
                      child: TextFormField(
                        controller: _city,
                        enabled: !state.isSaving,
                        maxLength: 100,
                        decoration: const InputDecoration(labelText: 'City *'),
                        validator: (value) {
                          final trimmed = value?.trim() ?? '';
                          if (trimmed.isEmpty) return 'Enter a city.';
                          return null;
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      flex: 3,
                      child: TextFormField(
                        controller: _state,
                        enabled: !state.isSaving,
                        maxLength: 100,
                        decoration: const InputDecoration(labelText: 'State *'),
                        validator: (value) {
                          final trimmed = value?.trim() ?? '';
                          if (trimmed.isEmpty) return 'Enter a state.';
                          return null;
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      flex: 2,
                      child: TextFormField(
                        controller: _pinCode,
                        enabled: !state.isSaving,
                        maxLength: 10,
                        keyboardType: TextInputType.number,
                        decoration: const InputDecoration(labelText: 'PIN code'),
                        validator: (value) {
                          final trimmed = value?.trim() ?? '';
                          if (trimmed.isEmpty) return null;
                          if (trimmed.length > 10) {
                            return 'Keep the PIN code under 10 characters.';
                          }
                          return null;
                        },
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                if (!_hasExplicitLocationSelection)
                  const Padding(
                    padding: EdgeInsets.only(bottom: 8),
                    child: Text(
                      'Select a branch location on the map before saving.',
                    ),
                  ),
                GoogleMapCoordinatePicker(
                  initialLocation: _initialMapLocation(),
                  onLocationSelected: _setMapLocation,
                  mapsLoader: _loadGoogleMaps,
                ),
                if (_isLookingUpAddress)
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: Row(
                      children: [
                        const SizedBox(
                          width: 14,
                          height: 14,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        ),
                        const SizedBox(width: 10),
                        Expanded(
                          child: Text(
                            'Determining the address for the selected location...',
                            style: Theme.of(context).textTheme.bodyMedium,
                          ),
                        ),
                      ],
                    ),
                  ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _serviceRadius,
                  enabled: !state.isSaving,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  decoration: const InputDecoration(
                    labelText: 'Service radius (km)',
                    helperText: 'Optional',
                  ),
                  validator: (value) {
                    final trimmed = value?.trim() ?? '';
                    if (trimmed.isEmpty) return null;
                    final parsed = double.tryParse(trimmed);
                    if (parsed == null || parsed < 0) return 'Enter a valid distance';
                    return null;
                  },
                ),
                if (state.fieldErrors.isNotEmpty)
                  ...state.fieldErrors.entries.map(
                    (entry) => Padding(
                      padding: const EdgeInsets.only(top: 8),
                      child: Text(
                        '${entry.key}: ${entry.value}',
                        style: TextStyle(color: Theme.of(context).colorScheme.error),
                      ),
                    ),
                  ),
                const SizedBox(height: 20),
                FilledButton.icon(
                  onPressed: state.isSaving ? null : _save,
                  icon: state.isSaving
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.save_outlined),
                  label: Text(widget.isEditing ? 'Save changes' : 'Create branch'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class BranchDetailScreen extends ConsumerStatefulWidget {
  const BranchDetailScreen({super.key, required this.branchId, this.branch});

  final String branchId;
  final Branch? branch;

  @override
  ConsumerState<BranchDetailScreen> createState() => _BranchDetailScreenState();
}

class _BranchDetailScreenState extends ConsumerState<BranchDetailScreen> {
  bool _deactivateConfirming = false;
  bool _deleteConfirming = false;

  @override
  void initState() {
    super.initState();
    if (widget.branch == null) {
      Future.microtask(
        () => ref.read(branchControllerProvider.notifier).loadById(widget.branchId),
      );
    }
  }

  Branch? _effectiveBranch(BranchState state) {
    if (state.selectedBranch?.publicId == widget.branchId) {
      return state.selectedBranch;
    }
    for (final branch in state.branches) {
      if (branch.publicId == widget.branchId) return branch;
    }
    return widget.branch;
  }

  Future<void> _toggleActive(Branch branch) async {
    final isDeactivating = branch.isActive;
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(isDeactivating ? 'Deactivate branch?' : 'Activate branch?'),
        content: Text(
          isDeactivating
              ? 'Deactivating "${branch.name}" prevents it from receiving new '
                    'allocations. Existing orders and history are preserved.'
              : 'Activating "${branch.name}" makes it available for new '
                    'allocations again.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(isDeactivating ? 'Deactivate' : 'Activate'),
          ),
        ],
      ),
    );
    if (confirm != true || !mounted) return;
    setState(() => _deactivateConfirming = true);
    final success = await ref
        .read(branchControllerProvider.notifier)
        .setActive(branch.publicId, !isDeactivating);
    if (!mounted) return;
    setState(() => _deactivateConfirming = false);
    if (success) {
      final saved = ref.read(branchControllerProvider).savedMessage;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(saved ?? 'Branch updated.')));
    } else {
      final error = ref.read(branchControllerProvider).errorMessage;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(content: Text(error ?? 'Unable to update the branch.')),
        );
    }
  }

  Future<void> _delete(Branch branch) async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete branch?'),
        content: Text(
          '"${branch.name}" is inactive. If no orders or other records '
          'reference it, the branch will be permanently deleted. If it still '
          'carries historical records, it will be archived instead and kept '
          'for history. This cannot be undone for a permanent delete.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Delete'),
          ),
        ],
      ),
    );
    if (confirm != true || !mounted) return;
    setState(() => _deleteConfirming = true);
    final success = await ref
        .read(branchControllerProvider.notifier)
        .delete(branch.publicId);
    if (!mounted) return;
    setState(() => _deleteConfirming = false);
    if (success) {
      final message =
          ref.read(branchControllerProvider).savedMessage ?? 'Branch updated.';
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(message)));
      // A permanent delete removes the branch from state, so leave the detail
      // screen; an archive keeps the branch open in its view-only state.
      final stillSelected =
          ref.read(branchControllerProvider).selectedBranch?.publicId ==
              branch.publicId;
      if (!stillSelected && mounted) {
        context.pop();
      }
    } else {
      final error = ref.read(branchControllerProvider).errorMessage;
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          SnackBar(content: Text(error ?? 'Unable to delete the branch.')),
        );
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(branchControllerProvider);
    final canManage =
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kBranchesManagePermission,
            ) ??
            false;
    final branch = _effectiveBranch(state);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Branch details'),
        actions: [
          if (branch != null && canManage && !branch.isArchived)
            IconButton(
              tooltip: 'Edit branch',
              onPressed: state.isSaving
                  ? null
                  : () => context.push(
                        '/admin/branches/${branch.publicId}/edit',
                        extra: branch,
                      ),
              icon: const Icon(Icons.edit_outlined),
            ),
        ],
      ),
      body: _BranchDetailBody(
        state: state,
        branch: branch,
        canManage: canManage,
        busy: _deactivateConfirming || _deleteConfirming || state.isSaving,
        onRetry: () =>
            ref.read(branchControllerProvider.notifier).loadById(widget.branchId),
        onToggleActive: branch == null ? null : () => _toggleActive(branch),
        onDelete: branch == null ? null : () => _delete(branch),
      ),
    );
  }
}

class _BranchDetailBody extends StatelessWidget {
  const _BranchDetailBody({
    required this.state,
    required this.branch,
    required this.canManage,
    required this.busy,
    required this.onRetry,
    required this.onToggleActive,
    required this.onDelete,
  });

  final BranchState state;
  final Branch? branch;
  final bool canManage;
  final bool busy;
  final Future<void> Function() onRetry;
  final VoidCallback? onToggleActive;
  final VoidCallback? onDelete;

  @override
  Widget build(BuildContext context) {
    final branch = this.branch;
    if (state.isLoading && branch == null) {
      return const LoadingStatePanel(message: 'Loading branch...');
    }
    if (branch == null) {
      if (state.isUnauthorized) return const UnauthorizedStatePanel();
      if (state.isOffline) return OfflineStatePanel(onRetry: onRetry);
      return ErrorStatePanel(
        message: state.errorMessage ?? 'Branch not found.',
        onRetry: onRetry,
      );
    }

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        branch.name,
                        style: Theme.of(context).textTheme.headlineSmall,
                      ),
                    ),
                    _StatusChip(
                      isActive: branch.isActive,
                      isArchived: branch.isArchived,
                    ),
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  'Code ${branch.code}',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                ),
                const SizedBox(height: 12),
                Text(
                  branch.addressSummary,
                  style: Theme.of(context).textTheme.bodyMedium,
                ),
                const SizedBox(height: 8),
                Text(
                  '${branch.latitude}, ${branch.longitude}'
                  '${branch.serviceRadiusKm == null ? '' : ' · ${branch.serviceRadiusKm!.toStringAsFixed(1)} km service radius'}',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        if (canManage && !branch.isArchived)
          Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              FilledButton.tonalIcon(
                onPressed: busy ? null : onToggleActive,
                icon: Icon(
                  branch.isActive
                      ? Icons.pause_circle_outline
                      : Icons.play_circle_outline,
                ),
                label: Text(
                  branch.isActive ? 'Deactivate branch' : 'Activate branch',
                ),
              ),
              if (!branch.isActive) ...[
                const SizedBox(height: 12),
                OutlinedButton.icon(
                  onPressed: busy ? null : onDelete,
                  icon: const Icon(Icons.delete_outline),
                  style: OutlinedButton.styleFrom(
                    foregroundColor: Theme.of(context).colorScheme.error,
                  ),
                  label: const Text('Delete branch'),
                ),
              ],
            ],
          ),
      ],
    );
  }
}
