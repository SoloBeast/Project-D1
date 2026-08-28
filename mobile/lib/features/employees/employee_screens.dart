import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
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

/// Invitation deep-link route. The token is the single-use value returned by
/// the backend exactly once (on create/resend) and consumed by
/// `/api/v1/employee-invitations/{token}/verify`.
const String kInvitationRoute = '/invite';

/// Employees → list. Requires `EMPLOYEES.READ`; management actions require
/// `EMPLOYEES.MANAGE`.
class EmployeeListScreen extends ConsumerStatefulWidget {
  const EmployeeListScreen({super.key});

  @override
  ConsumerState<EmployeeListScreen> createState() =>
      _EmployeeListScreenState();
}

class _EmployeeListScreenState extends ConsumerState<EmployeeListScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(employeeControllerProvider.notifier).load(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(employeeControllerProvider);
    final canManage =
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kEmployeesManagePermission,
            ) ??
            false;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Employees'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () => ref
                      .read(employeeControllerProvider.notifier)
                      .load(),
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
        onRetry: () =>
            ref.read(employeeControllerProvider.notifier).load(),
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

    final items = [...state.employees]..sort(
      (a, b) => a.displayName.toLowerCase().compareTo(
        b.displayName.toLowerCase(),
      ),
    );

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        if (state.savedMessage != null) ...[
          _SavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          _ErrorBanner(message: state.errorMessage!),
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

class _SavedBanner extends StatelessWidget {
  const _SavedBanner({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Card(
    color: DoodhColors.mint,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Row(
        children: [
          const Icon(Icons.check_circle_outline, color: DoodhColors.tealDark),
          const SizedBox(width: 8),
          Expanded(child: Text(message)),
        ],
      ),
    ),
  );
}

class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Card(
    color: Theme.of(context).colorScheme.errorContainer,
    child: Padding(
      padding: const EdgeInsets.all(12),
      child: Row(
        children: [
          Icon(
            Icons.error_outline,
            color: Theme.of(context).colorScheme.error,
          ),
          const SizedBox(width: 8),
          Expanded(child: Text(message)),
        ],
      ),
    ),
  );
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

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
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
                _ActiveBadge(isActive: employee.isActive),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              role?.label ?? employee.roleName ?? employee.roleCode,
              style: TextStyle(fontWeight: FontWeight.w600),
            ),
            if (employee.branchName != null && employee.branchName!.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 2),
                child: Text(
                  'Branch: ${employee.branchName}',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
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
            if (canManage) ...[
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
                      onPressed: isBusy ? null : () => _setActive(context, ref, false),
                      icon: const Icon(Icons.block_outlined, size: 18),
                      label: const Text('Deactivate'),
                    )
                  else
                    FilledButton.tonalIcon(
                      onPressed: isBusy ? null : () => _setActive(context, ref, true),
                      icon: const Icon(Icons.play_arrow_outlined, size: 18),
                      label: const Text('Activate'),
                    ),
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

  Future<void> _setActive(BuildContext context, WidgetRef ref, bool active) async {
    await ref.read(employeeControllerProvider.notifier).update(
      employee.id,
      UpdateEmployeeRequest(
        displayName: employee.displayName,
        email: employee.email,
        roleCode: employee.roleCode,
        branchId: employee.branchId,
        isActive: active,
      ),
    );
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
      EmployeeInvitationStatus.invited => (
        'Invitation sent',
        scheme.primary,
      ),
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

class _ActiveBadge extends StatelessWidget {
  const _ActiveBadge({required this.isActive});

  final bool isActive;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: isActive ? DoodhColors.mint : scheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        isActive ? 'Active' : 'Inactive',
        style: TextStyle(
          color: isActive ? DoodhColors.tealDark : scheme.onSurfaceVariant,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
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
  late final TextEditingController _displayName;
  late final TextEditingController _mobile;
  late final TextEditingController _email;
  EmployeeRole? _role;
  EmployeeBranchOption? _branch;
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
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kEmployeesManagePermission,
            ) ??
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
        padding: const EdgeInsets.all(16),
        children: [
          if (state.errorMessage != null) ...[
            _ErrorBanner(message: state.errorMessage!),
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
                  validator: (value) =>
                      value == null || value.trim().isEmpty
                      ? 'Enter the employee name.'
                      : null,
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _mobile,
                  enabled: !state.isSaving,
                  keyboardType: TextInputType.phone,
                  decoration: InputDecoration(
                    labelText: 'Mobile number *',
                    hintText: '10-digit mobile number',
                    errorText: fieldErrors['mobile'],
                  ),
                  validator: (value) {
                    final mobile = value?.trim() ?? '';
                    if (mobile.isEmpty) return 'Enter the mobile number.';
                    if (!RegExp(r'^[0-9]{10}$').hasMatch(mobile)) {
                      return 'Enter a valid 10-digit mobile number.';
                    }
                    return null;
                  },
                ),
                const SizedBox(height: 12),
                TextFormField(
                  controller: _email,
                  enabled: !state.isSaving,
                  keyboardType: TextInputType.emailAddress,
                  decoration: InputDecoration(
                    labelText: 'Email (optional)',
                    hintText: 'e.g. ramesh@example.com',
                    errorText: fieldErrors['email'],
                  ),
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
                      : (value) => setState(() => _role = value),
                  validator: (value) => value == null
                      ? 'Select a role for this employee.'
                      : null,
                ),
                const SizedBox(height: 12),
                DropdownButtonFormField<EmployeeBranchOption>(
                  initialValue: _branch,
                  decoration: InputDecoration(
                    labelText: 'Branch',
                    helperText: _role == EmployeeRole.systemAdmin
                        ? 'System Administrators are not bound to a branch.'
                        : 'Assign the branch this employee works in.',
                    errorText: fieldErrors['branchId'],
                  ),
                  items: (state.branchOptions.isEmpty
                          ? const <EmployeeBranchOption>[]
                          : state.branchOptions)
                      .map(
                        (branch) => DropdownMenuItem(
                          value: branch,
                          child: Text(branch.displayName),
                        ),
                      )
                      .toList(),
                  onChanged: state.isSaving ||
                          _role == EmployeeRole.systemAdmin
                      ? null
                      : (value) => setState(() => _branch = value),
                  validator: (value) =>
                      _role != EmployeeRole.systemAdmin && value == null
                      ? 'Select a branch.'
                      : null,
                ),
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

    final branchId = role == EmployeeRole.systemAdmin
        ? null
        : _branch?.id;

    final employee = await ref.read(employeeControllerProvider.notifier).create(
      CreateEmployeeRequest(
        displayName: _displayName.text.trim(),
        mobile: _mobile.text.trim(),
        email: _email.text.trim().isEmpty ? null : _email.text.trim(),
        roleCode: role.apiCode,
        branchId: branchId,
        sendInvitation: _sendInvitation,
      ),
    );
    if (!mounted) return;
    if (employee == null) return;

    // Surface the freshly generated single-use invitation link (UAT flow:
    // "Verify invitation is generated/sent") before returning to the list.
    final invitation = ref.read(employeeControllerProvider).lastInvitation;
    if (invitation != null) {
      await _showInvitationLink(context, invitation);
      if (!mounted) return;
    }
    context.pop();
  }
}

/// Edit → displayName, email, role, branch, active state. Role and branch
/// changes are audited on the backend. Requires `EMPLOYEES.MANAGE`.
class EmployeeEditScreen extends ConsumerStatefulWidget {
  const EmployeeEditScreen({super.key, required this.employeeId, this.employee});

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

  @override
  void initState() {
    super.initState();
    _employee = widget.employee;
    _displayName = TextEditingController(text: _employee?.displayName ?? '');
    _email = TextEditingController(text: _employee?.email ?? '');
    _role = _employee?.assignableRole;
    Future.microtask(() async {
      final controller = ref.read(employeeControllerProvider.notifier);
      if (ref.read(employeeControllerProvider).branchOptions.isEmpty) {
        await controller.loadBranchOptions();
      }
      final options = ref.read(employeeControllerProvider).branchOptions;
      if (mounted && _branch == null && _employee?.branchId != null) {
        final matches = options.where(
          (b) => b.id == _employee!.branchId,
        );
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
        ref.watch(sessionControllerProvider).session?.user.permissions.contains(
              kEmployeesManagePermission,
            ) ??
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
        padding: const EdgeInsets.all(16),
        children: [
          if (state.errorMessage != null) ...[
            _ErrorBanner(message: state.errorMessage!),
            const SizedBox(height: 12),
          ],
          _InvitationRow(
            status: employee.invitationStatus ??
                EmployeeInvitationStatus.invited,
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
                  validator: (value) =>
                      value == null || value.trim().isEmpty
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

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    final employee = _employee!;
    final role = _role;
    if (role == null) return;
    final branchId = role == EmployeeRole.systemAdmin ? null : _branch?.id;

    final updated = await ref.read(employeeControllerProvider.notifier).update(
      employee.id,
      UpdateEmployeeRequest(
        displayName: _displayName.text.trim(),
        email: _email.text.trim().isEmpty ? null : _email.text.trim(),
        roleCode: role.apiCode,
        branchId: branchId,
        isActive: employee.isActive,
      ),
    );
    if (!mounted) return;
    if (updated != null) {
      setState(() => _employee = updated);
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
  late final TextEditingController _displayName;
  late final TextEditingController _mobile;
  late final TextEditingController _password;
  late final TextEditingController _otp;

  @override
  void initState() {
    super.initState();
    _displayName = TextEditingController();
    _mobile = TextEditingController();
    _password = TextEditingController();
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
    _mobile.dispose();
    _password.dispose();
    _otp.dispose();
    super.dispose();
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
        message: state.errorMessage ??
            'This invitation could not be verified.',
        onRetry: () => ref
            .read(employeeControllerProvider.notifier)
            .verifyInvitation(widget.token),
      );
    }
    if (!verification.isValid) {
      return StatePanel(
        icon: Icons.link_off_outlined,
        title: 'Invitation unavailable',
        message: verification.reason ??
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
    final branchName = _branchName(verification.branchId, state);
    final fieldErrors = state.fieldErrors;

    // Prefill invitee details from the invitation (editable name/mobile; role
    // and branch are fixed by the administrator and never editable).
    if (_mobile.text.isEmpty && verification.mobile != null) {
      _mobile.text = verification.mobile!;
    }
    if (_displayName.text.isEmpty &&
        verification.displayName != null &&
        verification.displayName!.isNotEmpty) {
      _displayName.text = verification.displayName!;
    }

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        if (state.savedMessage != null) ...[
          _SavedBanner(message: state.savedMessage!),
          const SizedBox(height: 12),
        ],
        if (state.errorMessage != null) ...[
          _ErrorBanner(message: state.errorMessage!),
          const SizedBox(height: 12),
        ],
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
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
                  'Branch: ${branchName ?? '—'}',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
                const SizedBox(height: 8),
                Text(
                  'Your role and branch were assigned by your administrator. '
                  'They cannot be changed here.',
                  style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
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
                validator: (value) =>
                    value == null || value.trim().isEmpty
                    ? 'Enter your name.'
                    : null,
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _mobile,
                enabled: !state.isSaving && !state.isSendingOtp,
                keyboardType: TextInputType.phone,
                decoration: InputDecoration(
                  labelText: 'Mobile number',
                  helperText: 'We send a one-time code to verify this number.',
                  errorText: fieldErrors['mobile'],
                ),
                validator: (value) {
                  final mobile = value?.trim() ?? '';
                  if (mobile.isEmpty) return 'Enter your mobile number.';
                  if (!RegExp(r'^[0-9]{10}$').hasMatch(mobile)) {
                    return 'Enter a valid 10-digit mobile number.';
                  }
                  return null;
                },
              ),
              const SizedBox(height: 12),
              TextFormField(
                controller: _password,
                enabled: !state.isSaving && !state.isCompleting,
                obscureText: true,
                decoration: InputDecoration(
                  labelText: 'Create password',
                  helperText: 'Minimum 8 characters.',
                  errorText: fieldErrors['password'],
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
    if (!_formKey.currentState!.validate()) return;
    await ref
        .read(employeeControllerProvider.notifier)
        .sendInvitationOtp(_mobile.text.trim());
  }

  Future<void> _complete() async {
    if (!_formKey.currentState!.validate()) return;
    final success = await ref
        .read(employeeControllerProvider.notifier)
        .completeRegistration(
          token: widget.token,
          displayName: _displayName.text.trim(),
          mobile: _mobile.text.trim(),
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
  EmployeeInvitationResult invitation,
) async {
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
