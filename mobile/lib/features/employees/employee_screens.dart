import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'employee_controller.dart';
import 'employee_models.dart';

const String kEmployeesReadPermission = 'EMPLOYEES.READ';
const String kEmployeesManagePermission = 'EMPLOYEES.MANAGE';

/// Maximum height of the branch multi-select list. Keeps the form usable when
/// an administrator can assign many branches without letting the checkbox
/// list push the submit action out of reach.
const double kBranchMultiSelectMaxHeight = 272;

/// Human-readable branch list for an employee, using the authoritative
/// assignment collection. Assignments that are no longer offered for new
/// work (archived branches are absent from [options], inactive ones carry
/// `isActive: false`) are labelled `(inactive)` instead of being dropped, so
/// retained history stays visible.
String describeEmployeeBranches(
  Employee employee,
  List<EmployeeBranchOption> options,
) {
  if (employee.branchAssignments.isEmpty) {
    final legacy = employee.branchName;
    return legacy == null || legacy.isEmpty ? '' : legacy;
  }
  final byId = {for (final option in options) option.id: option};
  return employee.branchAssignments.map((assignment) {
    final option = byId[assignment.branchId];
    final name = (assignment.branchName?.isNotEmpty ?? false)
        ? assignment.branchName!
        : (option?.name ?? 'Branch ${assignment.branchId}');
    final inactive = option == null || !option.isActive;
    return inactive ? '$name (inactive)' : name;
  }).join(', ');
}

/// Multi-select checkbox list for Dairy Manager branch assignments. Only
/// [options] marked active are selectable — archived/inactive branches can
/// never be newly assigned. Branches in [retainedInactiveIds] (assignments
/// that still exist server-side but are no longer eligible) render as
/// disabled, checked rows labelled `(inactive)`: they stay in the submitted
/// set unless the administrator explicitly removes them elsewhere, so saving
/// never silently drops them.
class BranchMultiSelect extends StatelessWidget {
  const BranchMultiSelect({
    super.key,
    required this.options,
    required this.selectedIds,
    required this.onChanged,
    required this.enabled,
    this.retainedInactiveIds = const <int>{},
    this.validationError,
  });

  final List<EmployeeBranchOption> options;
  final Set<int> selectedIds;
  final ValueChanged<Set<int>> onChanged;
  final bool enabled;
  final Set<int> retainedInactiveIds;
  final String? validationError;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final selectable = options.where((option) => option.isActive).toList();
    final retained = options
        .where(
          (option) =>
              !option.isActive && retainedInactiveIds.contains(option.id),
        )
        .toList();
    final rows = [...selectable, ...retained];

    return Semantics(
      container: true,
      label: 'Assigned branches, ${selectedIds.length} selected',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (rows.isEmpty)
            Text(
              'No active branches are available for assignment.',
              style: TextStyle(color: scheme.onSurfaceVariant),
            )
          else
            ConstrainedBox(
              constraints: const BoxConstraints(
                maxHeight: kBranchMultiSelectMaxHeight,
              ),
              child: ListView.builder(
                shrinkWrap: true,
                itemCount: rows.length,
                itemBuilder: (context, index) {
                  final option = rows[index];
                  final retainedInactive =
                      !option.isActive &&
                      retainedInactiveIds.contains(option.id);
                  final checked =
                      retainedInactive || selectedIds.contains(option.id);
                  return CheckboxListTile(
                    contentPadding: EdgeInsets.zero,
                    controlAffinity: ListTileControlAffinity.leading,
                    dense: true,
                    enabled: enabled && !retainedInactive,
                    value: checked,
                    onChanged: enabled && !retainedInactive
                        ? (value) {
                            final next = Set<int>.of(selectedIds);
                            if (value ?? false) {
                              next.add(option.id);
                            } else {
                              next.remove(option.id);
                            }
                            onChanged(next);
                          }
                        : null,
                    title: Text(
                      retainedInactive
                          ? '${option.displayName} (inactive)'
                          : option.displayName,
                    ),
                    subtitle: retainedInactive
                        ? const Text(
                            'Retained from an earlier assignment. Remove it '
                            'by saving a new selection without it.',
                          )
                        : null,
                  );
                },
              ),
            ),
          if (validationError != null) ...[
            const SizedBox(height: 4),
            Text(
              validationError!,
              style: TextStyle(color: scheme.error, fontSize: 12),
            ),
          ],
        ],
      ),
    );
  }
}

/// Invitation deep-link route. The token is the single-use value returned by
/// the backend exactly once (on create/resend) and consumed by
/// `/api/v1/employee-invitations/{token}/verify`.
const String kInvitationRoute = '/invite';

/// Employees → list. Requires `EMPLOYEES.READ`; management actions require
/// `EMPLOYEES.MANAGE`.
class EmployeeListScreen extends ConsumerStatefulWidget {
  const EmployeeListScreen({super.key});

  @override
  ConsumerState<EmployeeListScreen> createState() => _EmployeeListScreenState();
}

class _EmployeeListScreenState extends ConsumerState<EmployeeListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() {
      final controller = ref.read(employeeControllerProvider.notifier);
      controller.load();
      // Branch options resolve assignment names and mark retained
      // inactive assignments; the list itself stays authoritative from
      // the server-filtered employee rows.
      controller.loadBranchOptions();
    });
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(employeeControllerProvider);
    final canManage =
        ref
            .watch(sessionControllerProvider)
            .session
            ?.user
            .permissions
            .contains(kEmployeesManagePermission) ??
        false;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Employees'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () => ref.read(employeeControllerProvider.notifier).load(),
            icon: const Icon(Icons.refresh),
          ),
          if (canManage)
            IconButton(
              tooltip: 'Create employee',
              onPressed: state.isSaving
                  ? null
                  : () => context.push('/admin/employees/new'),
              icon: const Icon(Icons.person_add_alt_1_outlined),
            ),
        ],
      ),
      body: _body(context, state, canManage),
    );
  }

  Widget _body(BuildContext context, EmployeeState state, bool canManage) {
    if (state.isLoading && state.employees.isEmpty) {
      return const LoadingStatePanel(message: 'Loading employees');
    }
    if (state.errorMessage != null && state.employees.isEmpty) {
      return ErrorStatePanel(
        message: state.errorMessage!,
        onRetry: () => ref.read(employeeControllerProvider.notifier).load(),
      );
    }
    if (state.employees.isEmpty) {
      return EmptyStatePanel(
        title: 'No employees',
        message: canManage
            ? 'Create an employee to start onboarding them with a secure '
                  'invitation.'
            : 'Employees managed for this account are not available.',
        action: canManage
            ? FilledButton.icon(
                onPressed: () => context.push('/admin/employees/new'),
                icon: const Icon(Icons.person_add_alt_1_outlined),
                label: const Text('Create employee'),
              )
            : null,
      );
    }

    // Defensive de-duplication by account: the backend returns one row per
    // employee, but the visible list must never show the same employee
    // twice even if rows ever repeat across branches.
    final seenIds = <int>{};
    final items = [
      for (final employee in state.employees)
        if (seenIds.add(employee.id)) employee,
    ]..sort(
      (a, b) =>
          a.displayName.toLowerCase().compareTo(b.displayName.toLowerCase()),
    );

    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        if (state.savedMessage != null) ...[
          DoodhSavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          DoodhErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        ...items.map(
          (employee) => _EmployeeCard(
            employee: employee,
            canManage: canManage,
            isBusy: state.isSaving,
          ),
        ),
      ],
    );
  }
}

class _EmployeeCard extends ConsumerWidget {
  const _EmployeeCard({
    required this.employee,
    required this.canManage,
    required this.isBusy,
  });

  final Employee employee;
  final bool canManage;
  final bool isBusy;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final scheme = Theme.of(context).colorScheme;
    final role = employee.assignableRole;
    final status = employee.invitationStatus;
    final isSelf =
        ref.watch(sessionControllerProvider).session?.user.publicUserId ==
        employee.publicId;

    return Card(
      child: Padding(
        padding: DoodhSpacing.cardPadding,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    employee.displayName,
                    style: Theme.of(context).textTheme.titleMedium,
                  ),
                ),
                DoodhChip(
                  label: employee.isArchived
                      ? 'Archived'
                      : (employee.isActive ? 'Active' : 'Inactive'),
                  tone: employee.isArchived || !employee.isActive
                      ? DoodhTone.neutral
                      : DoodhTone.success,
                ),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              role?.label ?? employee.roleName ?? employee.roleCode,
              style: TextStyle(fontWeight: FontWeight.w600),
            ),
            Builder(
              builder: (context) {
                final options = ref
                    .watch(employeeControllerProvider)
                    .branchOptions;
                final branches = employee.branchAssignments.isEmpty
                    ? (employee.branchName != null &&
                            employee.branchName!.isNotEmpty
                        ? 'Branch: ${employee.branchName}'
                        : '')
                    : 'Branches: ${describeEmployeeBranches(employee, options)}';
                if (branches.isEmpty) return const SizedBox.shrink();
                return Padding(
                  padding: const EdgeInsets.only(top: 2),
                  child: Text(
                    branches,
                    style: TextStyle(color: scheme.onSurfaceVariant),
                  ),
                );
              },
            ),
            const SizedBox(height: 8),
            if (employee.mobile != null && employee.mobile!.isNotEmpty)
              Text('Mobile: ${employee.mobile}'),
            if (employee.email != null && employee.email!.isNotEmpty)
              Text('Email: ${employee.email}'),
            if (status != null) ...[
              const SizedBox(height: 8),
              _InvitationRow(
                status: status,
                expiresAt: employee.invitationExpiresAt,
              ),
            ],
            if (employee.registeredAt != null)
              Padding(
                padding: const EdgeInsets.only(top: 2),
                child: Text(
                  'Registered: ${_formatDate(employee.registeredAt)}',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
              ),
            if (canManage && !employee.isArchived) ...[
              const SizedBox(height: 8),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  if (status == EmployeeInvitationStatus.invited) ...[
                    OutlinedButton.icon(
                      onPressed: isBusy
                          ? null
                          : () => _resendInvitation(context, ref),
                      icon: const Icon(Icons.refresh, size: 18),
                      label: const Text('Resend'),
                    ),
                    const SizedBox(width: 8),
                    OutlinedButton.icon(
                      onPressed: isBusy
                          ? null
                          : () => _cancelInvitation(context, ref),
                      icon: const Icon(Icons.cancel_outlined, size: 18),
                      label: const Text('Cancel'),
                    ),
                    const SizedBox(width: 8),
                  ],
                  if (employee.isActive)
                    TextButton.icon(
                      onPressed: isBusy
                          ? null
                          : () => _setActive(context, ref, false),
                      icon: const Icon(Icons.block_outlined, size: 18),
                      label: const Text('Deactivate'),
                    )
                  else ...[
                    FilledButton.tonalIcon(
                      onPressed: isBusy
                          ? null
                          : () => _setActive(context, ref, true),
                      icon: const Icon(Icons.play_arrow_outlined, size: 18),
                      label: const Text('Activate'),
                    ),
                    if (!isSelf) ...[
                      const SizedBox(width: 8),
                      TextButton.icon(
                        onPressed: isBusy ? null : () => _delete(context, ref),
                        icon: const Icon(Icons.delete_outline, size: 18),
                        label: const Text('Delete'),
                        style: TextButton.styleFrom(
                          foregroundColor: scheme.error,
                        ),
                      ),
                    ],
                  ],
                  const SizedBox(width: 8),
                  IconButton(
                    tooltip: 'Edit ${employee.displayName}',
                    onPressed: isBusy
                        ? null
                        : () => context.push(
                            '/admin/employees/${employee.id}',
                            extra: employee,
                          ),
                    icon: const Icon(Icons.edit_outlined),
                  ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }

  Future<void> _resendInvitation(BuildContext context, WidgetRef ref) async {
    final invitationId = _invitationId;
    if (invitationId == null) return;
    final invitation = await ref
        .read(employeeControllerProvider.notifier)
        .resendInvitation(employee.id, invitationId);
    if (!context.mounted || invitation == null) return;
    await _showInvitationLink(context, invitation);
  }

  Future<void> _cancelInvitation(BuildContext context, WidgetRef ref) async {
    final invitationId = _invitationId;
    if (invitationId == null) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cancel invitation?'),
        content: Text(
          'The invitation for ${employee.displayName} will be invalidated. '
          'This cannot be undone.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Keep'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Cancel invitation'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    await ref
        .read(employeeControllerProvider.notifier)
        .cancelInvitation(employee.id, invitationId);
  }

  Future<void> _setActive(
    BuildContext context,
    WidgetRef ref,
    bool active,
  ) async {
    // Activation toggles must preserve the complete assignment set: sending
    // only the legacy single branch would silently drop every other branch
    // on multi-branch employees.
    final assignmentIds = employee.allBranchIds;
    await ref
        .read(employeeControllerProvider.notifier)
        .update(
          employee.id,
          UpdateEmployeeRequest(
            displayName: employee.displayName,
            email: employee.email,
            roleCode: employee.roleCode,
            branchId: assignmentIds.length == 1 &&
                    employee.branchAssignments.isEmpty
                ? assignmentIds.single
                : null,
            branchIds: employee.branchAssignments.isEmpty
                ? null
                : assignmentIds,
            isActive: active,
          ),
        );
  }

  Future<void> _delete(BuildContext context, WidgetRef ref) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) {
        final scheme = Theme.of(context).colorScheme;
        return AlertDialog(
          title: const Text('Delete employee?'),
          content: Text(
            '${employee.displayName} is inactive. If no orders, deliveries '
            'or other records reference this account it will be permanently '
            'deleted; otherwise it will be archived and kept for history. A '
            'permanent delete cannot be undone.',
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: const Text('Keep'),
            ),
            FilledButton(
              style: FilledButton.styleFrom(
                backgroundColor: scheme.error,
                foregroundColor: scheme.onError,
              ),
              onPressed: () => Navigator.of(context).pop(true),
              child: const Text('Delete'),
            ),
          ],
        );
      },
    );
    if (confirmed != true) return;
    await ref
        .read(employeeControllerProvider.notifier)
        .deleteEmployee(employee.id);
  }

  int? get _invitationId => employee.invitationId;
}

class _InvitationRow extends StatelessWidget {
  const _InvitationRow({required this.status, this.expiresAt});

  final EmployeeInvitationStatus status;
  final DateTime? expiresAt;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final (label, color) = switch (status) {
      EmployeeInvitationStatus.invited => ('Invitation sent', scheme.primary),
      EmployeeInvitationStatus.registered => (
        'Registered',
        DoodhColors.tealDark,
      ),
      EmployeeInvitationStatus.cancelled => (
        'Invitation cancelled',
        scheme.error,
      ),
      EmployeeInvitationStatus.expired => (
        'Invitation expired',
        scheme.onSurfaceVariant,
      ),
    };
    return Row(
      children: [
        Icon(Icons.mail_outline, size: 16, color: color),
        const SizedBox(width: 6),
        Expanded(
          child: Text(
            expiresAt == null
                ? label
                : '$label · expires ${_formatDate(expiresAt)}',
            style: TextStyle(color: color, fontSize: 12),
          ),
        ),
      ],
    );
  }
}

/// Create Employee → Name, Mobile, Email, Role, Branch. OWNER is intentionally
/// absent from the role selector. Requires `EMPLOYEES.MANAGE`.
class CreateEmployeeScreen extends ConsumerStatefulWidget {
  const CreateEmployeeScreen({super.key});

  @override
  ConsumerState<CreateEmployeeScreen> createState() =>
      _CreateEmployeeScreenState();
}

class _CreateEmployeeScreenState extends ConsumerState<CreateEmployeeScreen> {
  final _formKey = GlobalKey<FormState>();
  final _countryKey = GlobalKey<CountryCodeMobileFieldState>();
  late final TextEditingController _displayName;
  late final TextEditingController _mobile;
  late final TextEditingController _email;
  EmployeeRole? _role;
  EmployeeBranchOption? _branch;

  /// Dairy Manager multi-branch selection (branch ids). Only offered when
  /// [_role] is Dairy Manager; submitted as the authoritative `branchIds` set.
  Set<int> _selectedBranchIds = <int>{};
  String? _branchSelectionError;
  bool _sendInvitation = true;

  @override
  void initState() {
    super.initState();
    _displayName = TextEditingController();
    _mobile = TextEditingController();
    _email = TextEditingController();
    Future.microtask(
      () => ref.read(employeeControllerProvider.notifier).loadBranchOptions(),
    );
  }

  @override
  void dispose() {
    _displayName.dispose();
    _mobile.dispose();
    _email.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(employeeControllerProvider);
    final canManage =
        ref
            .watch(sessionControllerProvider)
            .session
            ?.user
            .permissions
            .contains(kEmployeesManagePermission) ??
        false;
    if (!canManage) {
      return Scaffold(
        appBar: AppBar(title: const Text('Create employee')),
        body: const UnauthorizedStatePanel(),
      );
    }

    final fieldErrors = state.fieldErrors;

    return Scaffold(
      appBar: AppBar(title: const Text('Create employee')),
      body: ListView(
        padding: DoodhSpacing.pagePadding,
        children: [
          if (state.errorMessage != null) ...[
            DoodhErrorBanner(message: state.errorMessage!),
            const SizedBox(height: 12),
          ],
          Form(
            key: _formKey,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                TextFormField(
                  controller: _displayName,
                  enabled: !state.isSaving,
                  textCapitalization: TextCapitalization.words,
                  decoration: InputDecoration(
                    labelText: 'Full name *',
                    hintText: 'e.g. Ramesh Kumar',
                    errorText: fieldErrors['displayName'],
                  ),
                  validator: (value) => value == null || value.trim().isEmpty
                      ? 'Enter the employee name.'
                      : null,
                ),
                const SizedBox(height: 12),
                CountryCodeMobileField(
                  key: _countryKey,
                  controller: _mobile,
                  enabled: !state.isSaving,
                  label: 'Mobile number *',
                  hintText: '10-digit mobile number',
                  errorText: fieldErrors['mobile'],
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _email,
                  enabled: !state.isSaving,
                  keyboardType: TextInputType.emailAddress,
                  decoration: InputDecoration(
                    labelText: 'Email *',
                    hintText: 'e.g. ramesh@example.com',
                    helperText:
                        'The secure invitation link is sent to this email.',
                    errorText: fieldErrors['email'],
                  ),
                  validator: (value) {
                    final email = value?.trim() ?? '';
                    if (email.isEmpty) return 'Enter the employee email.';
                    if (!RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$')
                        .hasMatch(email)) {
                      return 'Enter a valid email address.';
                    }
                    return null;
                  },
                ),
                const SizedBox(height: 12),
                DropdownButtonFormField<EmployeeRole>(
                  initialValue: _role,
                  decoration: InputDecoration(
                    labelText: 'Role *',
                    helperText:
                        'Assigned by the administrator and carried by the '
                        'invitation — the employee cannot change it.',
                    errorText: fieldErrors['roleCode'],
                  ),
                  items: EmployeeRole.values
                      .map(
                        (role) => DropdownMenuItem(
                          value: role,
                          child: Text(role.label),
                        ),
                      )
                      .toList(),
                  onChanged: state.isSaving
                      ? null
                      : (value) => setState(() {
                          _role = value;
                          _branchSelectionError = null;
                          // Preserve the expressed single-branch intent when
                          // switching into the multi-select, so the choice
                          // stays visible instead of being silently dropped.
                          if (value == EmployeeRole.dairyManager &&
                              _branch != null) {
                            _selectedBranchIds = {_branch!.id};
                          }
                        }),
                  validator: (value) =>
                      value == null ? 'Select a role for this employee.' : null,
                ),
                const SizedBox(height: 12),
                if (_role == EmployeeRole.dairyManager) ...[
                  Text(
                    'Branches *',
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    'A Dairy Manager can work in several branches. Only '
                    'active branches can be assigned.',
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                      fontSize: 12,
                    ),
                  ),
                  const SizedBox(height: 8),
                  BranchMultiSelect(
                    options: state.branchOptions,
                    selectedIds: _selectedBranchIds,
                    enabled: !state.isSaving,
                    validationError:
                        _branchSelectionError ?? fieldErrors['branchIds'],
                    onChanged: (next) => setState(() {
                      _selectedBranchIds = next;
                      _branchSelectionError = null;
                    }),
                  ),
                ] else ...[
                  DropdownButtonFormField<EmployeeBranchOption>(
                    initialValue: _branch,
                    decoration: InputDecoration(
                      labelText: 'Branch',
                      helperText: _role == EmployeeRole.systemAdmin
                          ? 'System Administrators are not bound to a branch.'
                          : 'Assign the branch this employee works in.',
                      errorText: fieldErrors['branchId'],
                    ),
                    items:
                        (state.branchOptions.isEmpty
                                ? const <EmployeeBranchOption>[]
                                : state.branchOptions)
                            .map(
                              (branch) => DropdownMenuItem(
                                value: branch,
                                child: Text(branch.displayName),
                              ),
                            )
                            .toList(),
                    onChanged:
                        state.isSaving || _role == EmployeeRole.systemAdmin
                        ? null
                        : (value) => setState(() => _branch = value),
                    validator: (value) =>
                        _role != EmployeeRole.systemAdmin && value == null
                        ? 'Select a branch.'
                        : null,
                  ),
                ],
                const SizedBox(height: 12),
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Send secure invitation'),
                  subtitle: const Text(
                    'Generate a single-use invitation link for onboarding.',
                  ),
                  value: _sendInvitation,
                  onChanged: state.isSaving
                      ? null
                      : (value) => setState(() => _sendInvitation = value),
                ),
                const SizedBox(height: 20),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    onPressed: state.isSaving ? null : _submit,
                    icon: const Icon(Icons.person_add_alt_1_outlined),
                    label: Text(state.isSaving ? 'Creating...' : 'Create'),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    final role = _role;
    if (role == null) return;

    // Dairy Managers submit the complete selected set; every other role keeps
    // the legacy single-branch field so their workflows are byte-identical.
    List<int>? branchIds;
    int? branchId;
    if (role == EmployeeRole.systemAdmin) {
      branchId = null;
    } else if (role == EmployeeRole.dairyManager) {
      if (_selectedBranchIds.isEmpty) {
        setState(
          () => _branchSelectionError = 'Select at least one branch.',
        );
        return;
      }
      branchIds = _selectedBranchIds.toList()..sort();
    } else {
      branchId = _branch?.id;
    }

    final employee = await ref
        .read(employeeControllerProvider.notifier)
        .create(
          CreateEmployeeRequest(
            displayName: _displayName.text.trim(),
            mobile: _countryKey.currentState?.canonicalValue ?? '',
            email: _email.text.trim(),
            roleCode: role.apiCode,
            branchId: branchId,
            sendInvitation: _sendInvitation,
            branchIds: branchIds,
          ),
        );
    if (!mounted) return;
    if (employee == null) return;

    // Surface the freshly generated single-use invitation link (UAT flow:
    // "Verify invitation is generated/sent") before returning to the list.
    final invitation = ref.read(employeeControllerProvider).lastInvitation;
    if (invitation != null) {
      await _showInvitationLink(
        context,
        invitation,
        branchNames: role == EmployeeRole.dairyManager
            ? _selectedBranchNames()
            : null,
      );
      if (!mounted) return;
    }
    context.pop();
  }

  /// Display names for the Dairy Manager branches selected on this form,
  /// shown during invitation review.
  List<String> _selectedBranchNames() {
    final options = ref.read(employeeControllerProvider).branchOptions;
    final byId = {for (final option in options) option.id: option};
    return [
      for (final id in _selectedBranchIds.toList()..sort())
        byId[id]?.displayName ?? 'Branch $id',
    ];
  }
}

/// Edit → displayName, email, role, branch, active state. Role and branch
/// changes are audited on the backend. Requires `EMPLOYEES.MANAGE`.
class EmployeeEditScreen extends ConsumerStatefulWidget {
  const EmployeeEditScreen({
    super.key,
    required this.employeeId,
    this.employee,
  });

  final int employeeId;
  final Employee? employee;

  @override
  ConsumerState<EmployeeEditScreen> createState() => _EmployeeEditScreenState();
}

class _EmployeeEditScreenState extends ConsumerState<EmployeeEditScreen> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _displayName;
  late final TextEditingController _email;
  Employee? _employee;
  EmployeeRole? _role;
  EmployeeBranchOption? _branch;

  /// Complete branch selection for Dairy Managers, initialized from the
  /// employee's full assignment set so unrelated saves never drop branches.
  Set<int> _selectedBranchIds = <int>{};
  Set<int> _initialBranchIds = <int>{};
  String? _branchSelectionError;

  @override
  void initState() {
    super.initState();
    _employee = widget.employee;
    _displayName = TextEditingController(text: _employee?.displayName ?? '');
    _email = TextEditingController(text: _employee?.email ?? '');
    _role = _employee?.assignableRole;
    _selectedBranchIds = Set<int>.of(_employee?.allBranchIds ?? const []);
    _initialBranchIds = Set<int>.of(_selectedBranchIds);
    Future.microtask(() async {
      final controller = ref.read(employeeControllerProvider.notifier);
      if (ref.read(employeeControllerProvider).branchOptions.isEmpty) {
        await controller.loadBranchOptions();
      }
      final options = ref.read(employeeControllerProvider).branchOptions;
      if (mounted && _branch == null && _employee?.branchId != null) {
        final matches = options.where((b) => b.id == _employee!.branchId);
        if (matches.isNotEmpty) {
          setState(() => _branch = matches.first);
        }
      }
    });
  }

  @override
  void dispose() {
    _displayName.dispose();
    _email.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(employeeControllerProvider);
    final canManage =
        ref
            .watch(sessionControllerProvider)
            .session
            ?.user
            .permissions
            .contains(kEmployeesManagePermission) ??
        false;
    final employee = _employee;
    if (!canManage) {
      return Scaffold(
        appBar: AppBar(title: const Text('Edit employee')),
        body: const UnauthorizedStatePanel(),
      );
    }
    if (employee == null) {
      return Scaffold(
        appBar: AppBar(title: const Text('Edit employee')),
        body: ErrorStatePanel(
          message: 'Employee details could not be loaded.',
          onRetry: () => context.pop(),
        ),
      );
    }

    final fieldErrors = state.fieldErrors;

    return Scaffold(
      appBar: AppBar(title: Text('Edit ${employee.displayName}')),
      body: ListView(
        padding: DoodhSpacing.pagePadding,
        children: [
          if (state.errorMessage != null) ...[
            DoodhErrorBanner(message: state.errorMessage!),
            const SizedBox(height: 12),
          ],
          _InvitationRow(
            status:
                employee.invitationStatus ?? EmployeeInvitationStatus.invited,
            expiresAt: employee.invitationExpiresAt,
          ),
          const SizedBox(height: 16),
          Form(
            key: _formKey,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                TextFormField(
                  controller: _displayName,
                  enabled: !state.isSaving,
                  textCapitalization: TextCapitalization.words,
                  decoration: InputDecoration(
                    labelText: 'Full name',
                    errorText: fieldErrors['displayName'],
                  ),
                  validator: (value) => value == null || value.trim().isEmpty
                      ? 'Enter the employee name.'
                      : null,
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _email,
                  enabled: !state.isSaving,
                  keyboardType: TextInputType.emailAddress,
                  decoration: InputDecoration(
                    labelText: 'Email (optional)',
                    errorText: fieldErrors['email'],
                  ),
                ),
                const SizedBox(height: 12),
                DropdownButtonFormField<EmployeeRole>(
                  initialValue: _role,
                  decoration: const InputDecoration(
                    labelText: 'Role',
                    helperText:
                        'Changing the role reassigns access immediately and '
                        'is recorded in the audit trail.',
                  ),
                  items: EmployeeRole.values
                      .map(
                        (role) => DropdownMenuItem(
                          value: role,
                          child: Text(role.label),
                        ),
                      )
                      .toList(),
                  onChanged: state.isSaving
                      ? null
                      : (value) => setState(() => _role = value),
                ),
                const SizedBox(height: 12),
                if (_role == EmployeeRole.dairyManager) ...[
                  Text(
                    'Branches',
                    style: Theme.of(context).textTheme.titleSmall,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    'Add or remove branch assignments. The complete '
                    'selection is saved and recorded in the audit trail.',
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                      fontSize: 12,
                    ),
                  ),
                  const SizedBox(height: 8),
                  BranchMultiSelect(
                    options: state.branchOptions,
                    selectedIds: _selectedBranchIds,
                    enabled: !state.isSaving,
                    retainedInactiveIds: _retainedInactiveIds(
                      state.branchOptions,
                    ),
                    validationError:
                        _branchSelectionError ?? fieldErrors['branchIds'],
                    onChanged: (next) => setState(() {
                      _selectedBranchIds = next;
                      _branchSelectionError = null;
                    }),
                  ),
                ] else ...[
                  DropdownButtonFormField<EmployeeBranchOption>(
                    initialValue: _branch,
                    decoration: const InputDecoration(
                      labelText: 'Branch',
                      helperText:
                          'Changing the branch updates the employee’s working '
                          'branch and is recorded in the audit trail.',
                    ),
                    items: state.branchOptions
                        .map(
                          (branch) => DropdownMenuItem(
                            value: branch,
                            child: Text(branch.displayName),
                          ),
                        )
                        .toList(),
                    onChanged: state.isSaving
                        ? null
                        : (value) => setState(() => _branch = value),
                  ),
                ],
                const SizedBox(height: 20),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton.icon(
                    onPressed: state.isSaving ? null : _submit,
                    icon: const Icon(Icons.save_outlined),
                    label: Text(state.isSaving ? 'Saving...' : 'Save changes'),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  /// Assigned branches that are no longer offered for new work, kept visible
  /// so retained history is labelled instead of dropped.
  Set<int> _retainedInactiveIds(List<EmployeeBranchOption> options) {
    final activeIds = {
      for (final option in options)
        if (option.isActive) option.id,
    };
    return _selectedBranchIds.difference(activeIds);
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    final employee = _employee!;
    final role = _role;
    if (role == null) return;

    // Branch fields are sent only when the assignment actually changed:
    // omitting them keeps the persisted set, so saving an unrelated field
    // can never silently remove branches.
    int? branchId;
    List<int>? branchIds;
    if (role == EmployeeRole.systemAdmin) {
      branchId = null;
    } else if (role == EmployeeRole.dairyManager) {
      final changed =
          _selectedBranchIds.length != _initialBranchIds.length ||
          !_selectedBranchIds.containsAll(_initialBranchIds);
      if (changed) {
        if (_selectedBranchIds.isEmpty) {
          setState(
            () => _branchSelectionError = 'Select at least one branch.',
          );
          return;
        }
        branchIds = _selectedBranchIds.toList()..sort();
      }
    } else {
      final selectedId = _branch?.id;
      if (selectedId != employee.branchId) branchId = selectedId;
    }

    final updated = await ref
        .read(employeeControllerProvider.notifier)
        .update(
          employee.id,
          UpdateEmployeeRequest(
            displayName: _displayName.text.trim(),
            email: _email.text.trim().isEmpty ? null : _email.text.trim(),
            roleCode: role.apiCode,
            branchId: branchId,
            branchIds: branchIds,
            isActive: employee.isActive,
          ),
        );
    if (!mounted) return;
    if (updated != null) {
      setState(() {
        _employee = updated;
        _selectedBranchIds = Set<int>.of(updated.allBranchIds);
        _initialBranchIds = Set<int>.of(_selectedBranchIds);
      });
    }
  }
}

/// Invitee-facing onboarding screen. This route is intentionally
/// unauthenticated — the employee opens `/invite/{token}` from the invitation
/// link, verifies their mobile with an OTP, and completes registration. Role
/// and branch come from the invitation and are shown read-only.
class EmployeeInvitationScreen extends ConsumerStatefulWidget {
  const EmployeeInvitationScreen({super.key, required this.token});

  final String token;

  @override
  ConsumerState<EmployeeInvitationScreen> createState() =>
      _EmployeeInvitationScreenState();
}

class _EmployeeInvitationScreenState
    extends ConsumerState<EmployeeInvitationScreen> {
  final _formKey = GlobalKey<FormState>();
  final _countryKey = GlobalKey<CountryCodeMobileFieldState>();
  late final TextEditingController _displayName;
  late final TextEditingController _mobile;
  late final TextEditingController _password;
  late final TextEditingController _confirmPassword;
  late final TextEditingController _otp;
  bool _obscurePassword = true;
  bool _obscureConfirmPassword = true;
  String? _otpChallengeMobile;
  String? _mobileValidationError;

  /// Country resolved from the invitation's stored mobile number before the
  /// field is first built. The backend is India-only today, but this keeps the
  /// prefill consistent with the shared parser for any `+`/`00` values.
  CountryCode? _prefillCountry;

  @override
  void initState() {
    super.initState();
    _displayName = TextEditingController();
    _mobile = TextEditingController()..addListener(_handleMobileChanged);
    _password = TextEditingController();
    _confirmPassword = TextEditingController();
    _otp = TextEditingController();
    Future.microtask(
      () => ref
          .read(employeeControllerProvider.notifier)
          .verifyInvitation(widget.token),
    );
  }

  @override
  void dispose() {
    _displayName.dispose();
    _mobile
      ..removeListener(_handleMobileChanged)
      ..dispose();
    _password.dispose();
    _confirmPassword.dispose();
    _otp.dispose();
    super.dispose();
  }

  void _handleMobileChanged() {
    final canonical = _countryKey.currentState?.canonicalValue;
    if (_otpChallengeMobile != null && canonical != _otpChallengeMobile) {
      _otpChallengeMobile = null;
      _otp.clear();
    }
    if (_mobileValidationError != null && mounted) {
      setState(() => _mobileValidationError = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(employeeControllerProvider);
    final verification = state.invitationVerification;

    return Scaffold(
      appBar: AppBar(title: const Text('Join DoodhDirect')),
      body: _body(context, state, verification),
    );
  }

  Widget _body(
    BuildContext context,
    EmployeeState state,
    EmployeeInvitationVerification? verification,
  ) {
    if (state.isLoading && verification == null) {
      return const LoadingStatePanel(message: 'Checking invitation');
    }
    if (verification == null) {
      return ErrorStatePanel(
        message: state.errorMessage ?? 'This invitation could not be verified.',
        onRetry: () => ref
            .read(employeeControllerProvider.notifier)
            .verifyInvitation(widget.token),
      );
    }
    if (!verification.isValid) {
      return StatePanel(
        icon: Icons.link_off_outlined,
        title: 'Invitation unavailable',
        message:
            verification.reason ??
            'This invitation is no longer valid. Contact your administrator '
                'to request a new one.',
        action: FilledButton(
          onPressed: () => context.go('/login'),
          child: const Text('Back to login'),
        ),
      );
    }

    final scheme = Theme.of(context).colorScheme;
    final role = EmployeeRole.fromApiCode(verification.roleCode);
    // The branches shown to the invitee come from the server, resolved from
    // the invitation's persisted branch set (branchAssignments). The invitee
    // is anonymous so state.branchOptions is empty and cannot resolve names;
    // it is only used as a last-resort fallback for legacy verification
    // payloads that predate the enriched metadata.
    final invitedNames = verification.allBranchNames;
    final branchName = invitedNames.isNotEmpty
        ? invitedNames.join(', ')
        : (verification.branchName ??
              verification.branchCode ??
              _branchName(verification.branchId, state));
    final branchLabel = invitedNames.length > 1 ? 'Branches' : 'Branch';
    final fieldErrors = state.fieldErrors;

    // Prefill invitee details from the invitation (editable name/mobile; role
    // and branch are fixed by the administrator and never editable).
    if (_mobile.text.isEmpty && verification.mobile != null) {
      final stored = verification.mobile!;
      final trimmed = stored.trim();
      if (trimmed.isEmpty) {
        _mobile.text = stored;
      } else if (trimmed.startsWith('+') || trimmed.startsWith('00')) {
        final parsed = parseMobileCountry(trimmed);
        _prefillCountry = parsed?.$1;
        _mobile.text = parsed?.$2 ?? stored;
      } else {
        final canonical = canonicalizeIndianMobile(trimmed);
        if (canonical != null && canonical.length == 13) {
          _prefillCountry = CountryCodes.india;
          _mobile.text = canonical.substring(3);
        } else {
          _prefillCountry = CountryCodes.india;
          _mobile.text = stored;
        }
      }
    }
    if (_displayName.text.isEmpty &&
        verification.displayName != null &&
        verification.displayName!.isNotEmpty) {
      _displayName.text = verification.displayName!;
    }

    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        if (state.savedMessage != null) ...[
          DoodhSavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          DoodhErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        Card(
          child: Padding(
            padding: DoodhSpacing.cardPadding,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Your assigned profile',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 8),
                Text(
                  'Role: ${role?.label ?? verification.roleCode}',
                  style: TextStyle(
                    color: scheme.primary,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  '$branchLabel: ${branchName ?? '—'}',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
                const SizedBox(height: 8),
                Text(
                  invitedNames.length > 1
                      ? 'Your role and branches were assigned by your '
                            'administrator. They cannot be changed here.'
                      : 'Your role and branch were assigned by your '
                            'administrator. They cannot be changed here.',
                  style: TextStyle(
                    color: scheme.onSurfaceVariant,
                    fontSize: 12,
                  ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              TextFormField(
                controller: _displayName,
                enabled: !state.isSaving && !state.isCompleting,
                textCapitalization: TextCapitalization.words,
                decoration: InputDecoration(
                  labelText: 'Full name',
                  errorText: fieldErrors['displayName'],
                ),
                validator: (value) => value == null || value.trim().isEmpty
                    ? 'Enter your name.'
                    : null,
              ),
              const SizedBox(height: 12),
              CountryCodeMobileField(
                key: _countryKey,
                controller: _mobile,
                enabled: !state.isSaving && !state.isSendingOtp,
                initialCountry: _prefillCountry ?? CountryCodes.india,
                helperText:
                    'We send a one-time code to verify this number.',
                errorText: _mobileValidationError ?? fieldErrors['mobile'],
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _password,
                enabled: !state.isSaving && !state.isCompleting,
                obscureText: _obscurePassword,
                decoration: InputDecoration(
                  labelText: 'Create password',
                  helperText: 'Minimum 8 characters.',
                  errorText: fieldErrors['password'],
                  suffixIcon: IconButton(
                    tooltip: _obscurePassword
                        ? 'Show password'
                        : 'Hide password',
                    icon: Icon(
                      _obscurePassword
                          ? Icons.visibility_outlined
                          : Icons.visibility_off_outlined,
                    ),
                    onPressed: () =>
                        setState(() => _obscurePassword = !_obscurePassword),
                  ),
                ),
                validator: (value) {
                  final password = value ?? '';
                  if (password.length < 8) {
                    return 'Password must be at least 8 characters.';
                  }
                  return null;
                },
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _confirmPassword,
                enabled: !state.isSaving && !state.isCompleting,
                obscureText: _obscureConfirmPassword,
                decoration: InputDecoration(
                  labelText: 'Confirm password',
                  errorText: fieldErrors['confirmPassword'],
                  suffixIcon: IconButton(
                    tooltip: _obscureConfirmPassword
                        ? 'Show password'
                        : 'Hide password',
                    icon: Icon(
                      _obscureConfirmPassword
                          ? Icons.visibility_outlined
                          : Icons.visibility_off_outlined,
                    ),
                    onPressed: () => setState(
                      () => _obscureConfirmPassword = !_obscureConfirmPassword,
                    ),
                  ),
                ),
                validator: (value) {
                  if ((value ?? '').isEmpty) return 'Confirm your password.';
                  if (value != _password.text) return 'Passwords do not match.';
                  return null;
                },
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _otp,
                enabled: !state.isSendingOtp && !state.isCompleting,
                keyboardType: TextInputType.number,
                decoration: InputDecoration(
                  labelText: 'One-time code',
                  helperText: 'Enter the code sent to your mobile number.',
                  errorText: fieldErrors['otpCode'],
                ),
                validator: (value) {
                  final otp = value?.trim() ?? '';
                  if (otp.isEmpty) return 'Enter the one-time code.';
                  if (!RegExp(r'^[0-9]{4,8}$').hasMatch(otp)) {
                    return 'Enter a valid code.';
                  }
                  return null;
                },
              ),
              const SizedBox(height: 20),
              SizedBox(
                width: double.infinity,
                child: OutlinedButton.icon(
                  onPressed: state.isSendingOtp || state.isCompleting
                      ? null
                      : _sendOtp,
                  icon: const Icon(Icons.sms_outlined),
                  label: Text(
                    state.isSendingOtp ? 'Sending...' : 'Send one-time code',
                  ),
                ),
              ),
              const SizedBox(height: 12),
              SizedBox(
                width: double.infinity,
                child: FilledButton.icon(
                  onPressed: state.isCompleting || state.isSendingOtp
                      ? null
                      : _complete,
                  icon: const Icon(Icons.check_circle_outline),
                  label: Text(
                    state.isCompleting
                        ? 'Completing...'
                        : 'Complete registration',
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  String? _branchName(int? branchId, EmployeeState state) {
    if (branchId == null) return null;
    final matches = state.branchOptions.where((b) => b.id == branchId);
    return matches.isEmpty ? null : matches.first.displayName;
  }

  Future<void> _sendOtp() async {
    final verification = ref
        .read(employeeControllerProvider)
        .invitationVerification;
    final raw = _mobile.text.trim();
    final canonical = _countryKey.currentState?.canonicalValue;
    final country =
        _countryKey.currentState?.currentCountry ?? CountryCodes.india;
    String? error;
    if (verification == null || !verification.isValid) {
      error = 'This invitation is no longer valid.';
    } else if (raw.isEmpty) {
      error = 'Enter your mobile number.';
    } else if (canonical == null) {
      error = mobileNumberErrorMessage(country);
    }
    if (error != null) {
      setState(() => _mobileValidationError = error);
      return;
    }

    final sent = await ref
        .read(employeeControllerProvider.notifier)
        .sendInvitationOtp(canonical!);
    if (sent && mounted) {
      setState(() => _otpChallengeMobile = canonical);
    }
  }

  Future<void> _complete() async {
    if (!_formKey.currentState!.validate()) return;
    final success = await ref
        .read(employeeControllerProvider.notifier)
        .completeRegistration(
          token: widget.token,
          displayName: _displayName.text.trim(),
          mobile: _countryKey.currentState?.canonicalValue ?? '',
          password: _password.text,
          otpCode: _otp.text.trim(),
        );
    if (success && mounted) {
      context.go('/home');
    }
  }
}

Future<void> _showInvitationLink(
  BuildContext context,
  EmployeeInvitationResult invitation, {
  List<String>? branchNames,
}) async {
  final link = '$kInvitationRoute/${Uri.encodeComponent(invitation.token)}';
  await showDialog<void>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Invitation link'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text(
            'Share this single-use link with the employee. It expires on '
            'the date shown. The previous link is no longer valid.',
          ),
          if (branchNames != null && branchNames.isNotEmpty) ...[
            const SizedBox(height: 8),
            Text(
              'Invited branches: ${branchNames.join(', ')}',
              style: TextStyle(
                color: Theme.of(context).colorScheme.onSurfaceVariant,
                fontSize: 12,
              ),
            ),
          ],
          const SizedBox(height: 12),
          SelectableText(link),
          const SizedBox(height: 4),
          Text(
            'Expires: ${_formatDate(invitation.expiresAt)}',
            style: TextStyle(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
              fontSize: 12,
            ),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
        FilledButton.icon(
          onPressed: () {
            Clipboard.setData(ClipboardData(text: link));
            ScaffoldMessenger.of(context).showSnackBar(
              const SnackBar(content: Text('Invitation link copied')),
            );
            Navigator.of(context).pop();
          },
          icon: const Icon(Icons.copy_outlined, size: 18),
          label: const Text('Copy'),
        ),
      ],
    ),
  );
}

String _formatDate(DateTime? value) {
  if (value == null) return '—';
  final local = value.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${two(local.day)}-${two(local.month)}-${local.year}';
}
