import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'branch_controller.dart';
import 'branch_models.dart';

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
            _StatusChip(isActive: branch.isActive),
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
      if (branch.branchNumber?.trim().isNotEmpty ?? false)
        'No. ${branch.branchNumber}',
      'Code ${branch.code}',
      branch.city.trim(),
    ];
    return parts.join(' · ');
  }
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.isActive});

  final bool isActive;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: isActive ? DoodhColors.mint : theme.colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(
        isActive ? 'Active' : 'Inactive',
        style: theme.textTheme.labelSmall?.copyWith(
          color: isActive ? DoodhColors.tealDark : theme.colorScheme.onSurfaceVariant,
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
  late final TextEditingController _latitude;
  late final TextEditingController _longitude;
  late final TextEditingController _serviceRadius;

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
    _latitude = TextEditingController(
      text: branch == null ? '' : _formatDecimal(branch.latitude),
    );
    _longitude = TextEditingController(
      text: branch == null ? '' : _formatDecimal(branch.longitude),
    );
    _serviceRadius = TextEditingController(
      text: branch == null || branch.serviceRadiusKm == null
          ? ''
          : _formatDecimal(branch.serviceRadiusKm!),
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
    _latitude.dispose();
    _longitude.dispose();
    _serviceRadius.dispose();
    super.dispose();
  }

  String _formatDecimal(double value) =>
      value == value.roundToDouble() ? value.toStringAsFixed(0) : value.toString();

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;
    final latitude = double.tryParse(_latitude.text.trim());
    final longitude = double.tryParse(_longitude.text.trim());
    if (latitude == null || longitude == null) {
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(
          const SnackBar(content: Text('Enter valid latitude and longitude.')),
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
          if (widget.isEditing && (widget.branch?.branchNumber?.trim().isNotEmpty ?? false)) ...[
            Card(
              color: DoodhColors.mint,
              child: ListTile(
                leading: const Icon(Icons.numbers_outlined, color: DoodhColors.tealDark),
                title: const Text('Branch number'),
                subtitle: Text(
                  '${widget.branch!.branchNumber} — allocated from the BRANCH '
                  'numbering series. It is assigned by the system and cannot be edited.',
                ),
              ),
            ),
            const SizedBox(height: 12),
          ],
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
                              'scoped numbering series. Cannot change once orders '
                              'are allocated.',
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
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: TextFormField(
                        controller: _latitude,
                        enabled: !state.isSaving,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                          signed: true,
                        ),
                        decoration: const InputDecoration(labelText: 'Latitude *'),
                        validator: (value) {
                          final parsed = double.tryParse(value?.trim() ?? '');
                          if (parsed == null) return 'Required';
                          if (parsed < -90 || parsed > 90) {
                            return 'Between -90 and 90';
                          }
                          return null;
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        controller: _longitude,
                        enabled: !state.isSaving,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                          signed: true,
                        ),
                        decoration: const InputDecoration(labelText: 'Longitude *'),
                        validator: (value) {
                          final parsed = double.tryParse(value?.trim() ?? '');
                          if (parsed == null) return 'Required';
                          if (parsed < -180 || parsed > 180) {
                            return 'Between -180 and 180';
                          }
                          return null;
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextFormField(
                        controller: _serviceRadius,
                        enabled: !state.isSaving,
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        decoration: const InputDecoration(
                          labelText: 'Service radius (km)',
                          helperText: 'Optional',
                        ),
                        validator: (value) {
                          final trimmed = value?.trim() ?? '';
                          if (trimmed.isEmpty) return null;
                          final parsed = double.tryParse(trimmed);
                          if (parsed == null || parsed < 0) {
                            return 'Enter a valid distance';
                          }
                          return null;
                        },
                      ),
                    ),
                  ],
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
          if (branch != null && canManage)
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
        busy: _deactivateConfirming || state.isSaving,
        onRetry: () =>
            ref.read(branchControllerProvider.notifier).loadById(widget.branchId),
        onToggleActive: branch == null ? null : () => _toggleActive(branch),
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
  });

  final BranchState state;
  final Branch? branch;
  final bool canManage;
  final bool busy;
  final Future<void> Function() onRetry;
  final VoidCallback? onToggleActive;

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
                    _StatusChip(isActive: branch.isActive),
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  'Code ${branch.code}',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                ),
                if (branch.branchNumber?.trim().isNotEmpty ?? false) ...[
                  const SizedBox(height: 12),
                  Container(
                    padding: const EdgeInsets.all(12),
                    decoration: BoxDecoration(
                      color: DoodhColors.mint,
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: Row(
                      children: [
                        const Icon(
                          Icons.numbers_outlined,
                          color: DoodhColors.tealDark,
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'Branch number',
                                style: Theme.of(context).textTheme.labelMedium,
                              ),
                              const SizedBox(height: 2),
                              Text(
                                branch.branchNumber!,
                                style: Theme.of(context).textTheme.titleMedium
                                    ?.copyWith(
                                      color: DoodhColors.tealDark,
                                      fontWeight: FontWeight.w700,
                                    ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    'Allocated from the BRANCH numbering series by the system. '
                    'It cannot be edited or generated on the client.',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: Theme.of(context).colorScheme.onSurfaceVariant,
                        ),
                  ),
                ],
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
        if (canManage)
          FilledButton.tonalIcon(
            onPressed: busy ? null : onToggleActive,
            icon: Icon(
              branch.isActive
                  ? Icons.pause_circle_outline
                  : Icons.play_circle_outline,
            ),
            label: Text(branch.isActive ? 'Deactivate branch' : 'Activate branch'),
          ),
      ],
    );
  }
}
