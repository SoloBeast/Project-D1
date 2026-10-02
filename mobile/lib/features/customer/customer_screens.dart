import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/customer/client_configuration_repository.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
import 'package:doodh_direct_mobile/features/customer/maps_script_loader.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:google_maps_flutter/google_maps_flutter.dart';
import 'package:go_router/go_router.dart';

class CustomerOverviewScreen extends ConsumerStatefulWidget {
  const CustomerOverviewScreen({super.key});

  @override
  ConsumerState<CustomerOverviewScreen> createState() =>
      _CustomerOverviewScreenState();
}

class _CustomerOverviewScreenState
    extends ConsumerState<CustomerOverviewScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(customerControllerProvider.notifier).load(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(customerControllerProvider);
    final session = ref.watch(sessionControllerProvider);
    final user = session.session?.user;
    return CustomerShell(
      currentPath: '/customer/account',
      title: 'My account',
      child: state.profile == null
          ? state.errorMessage == null
                ? const LoadingStatePanel(message: 'Loading your account...')
                : ErrorStatePanel(
                    message: state.errorMessage!,
                    onRetry: () =>
                        ref.read(customerControllerProvider.notifier).load(),
                  )
          : RefreshIndicator(
              onRefresh: () =>
                  ref.read(customerControllerProvider.notifier).load(),
              child: DoodhPage(
                child: DoodhResponsive(
                  builder: (context, size) {
                    final identity = _IdentityColumn(
                      profile: state.profile!,
                      user: user,
                      errorMessage: state.errorMessage,
                    );                    final addresses = _AddressSection(
                      addresses: state.addresses,
                      isSaving: state.isSaving,
                    );
                    final security = _SecuritySection(user: user);

                    // Compact screens stack identity above account management
                    // so every section keeps its full card width.
                    if (size == DoodhWindowSize.compact) {
                      return ListView(
                        physics: const AlwaysScrollableScrollPhysics(),
                        padding: const EdgeInsets.only(bottom: DoodhSpacing.lg),
                        children: [
                          identity,
                          const SizedBox(height: DoodhSpacing.lg),
                          addresses,
                          const SizedBox(height: DoodhSpacing.lg),
                          security,
                        ],
                      );
                    }

                    // Wide layouts keep a readable two-column composition:
                    // identity on the left, addresses on the right, with the
                    // overall width constrained instead of stretching cards.
                    return Align(
                      alignment: Alignment.topCenter,
                      child: ConstrainedBox(
                        constraints: BoxConstraints(
                          maxWidth:
                              DoodhContentMax.narrow * 2 + DoodhSpacing.lg,
                        ),
                        child: ListView(
                          physics: const AlwaysScrollableScrollPhysics(),
                          padding: const EdgeInsets.only(
                            bottom: DoodhSpacing.lg,
                          ),
                          children: [
                            Row(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Expanded(child: identity),
                                const SizedBox(width: DoodhSpacing.lg),
                                Expanded(
                                  child: Column(
                                    crossAxisAlignment:
                                        CrossAxisAlignment.start,
                                    children: [
                                      addresses,
                                      const SizedBox(height: DoodhSpacing.lg),
                                      security,
                                    ],
                                  ),
                                ),
                              ],
                            ),
                          ],
                        ),
                      ),
                    );
                  },
                ),
              ),
            ),
    );
  }
}

/// Left column of My Account: the profile identity hero followed by the
/// personal-information card (and any reload error surfaced from the shared
/// customer controller).
class _IdentityColumn extends StatelessWidget {
  const _IdentityColumn({
    required this.profile,
    required this.user,
    this.errorMessage,
  });

  final CustomerProfile profile;
  final AuthUser? user;
  final String? errorMessage;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _ProfileHero(profile: profile, user: user),
      const SizedBox(height: DoodhSpacing.lg),
      _PersonalInfoSection(profile: profile, user: user),
      if (errorMessage != null) ...[
        const SizedBox(height: DoodhSpacing.md),
        DoodhErrorBanner(message: errorMessage!),
      ],
    ],
  );
}

/// The profile identity hero. Uses only server-provided data: name from the
/// customer profile (falling back to the session display name), mobile and
/// email from the authenticated session. With no name on record a tasteful
/// branded droplet fallback is shown — no profile photo is invented.
class _ProfileHero extends StatelessWidget {
  const _ProfileHero({required this.profile, required this.user});

  final CustomerProfile profile;
  final AuthUser? user;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final profileName = profile.fullName.trim();
    final displayName = user?.displayName?.trim() ?? '';
    final name = profileName.isNotEmpty
        ? profileName
        : displayName.isNotEmpty
        ? displayName
        : '';
    final mobile = _displayMobile(user?.mobile);
    final email = user?.email?.trim() ?? '';
    final identityFacts = [
      ?mobile,
      if (email.isNotEmpty) email,
    ].join('  ·  ');
    final avatarLetter = name.isEmpty        ? null
        : name.substring(0, 1).toUpperCase();

    return Semantics(
      container: true,
      label: 'Profile: ${name.isEmpty ? 'Welcome' : name}'
          '${mobile == null ? '' : ', mobile $mobile'}'
          '${email.isEmpty ? '' : ', email $email'}',
      child: Card(
        color: DoodhColors.tealDark,
        clipBehavior: Clip.antiAlias,
        child: Padding(
          padding: const EdgeInsets.all(DoodhSpacing.lg),
          child: Row(
            children: [
              ExcludeSemantics(
                child: Container(
                  width: 56,
                  height: 56,
                  decoration: BoxDecoration(
                    color: Colors.white.withValues(alpha: .14),
                    shape: BoxShape.circle,
                  ),
                  alignment: Alignment.center,
                  child: avatarLetter != null
                      ? Text(
                          avatarLetter,
                          style: theme.textTheme.titleLarge?.copyWith(
                            color: Colors.white,
                            fontWeight: FontWeight.w800,
                          ),
                        )
                      : const Icon(
                          Icons.water_drop_rounded,
                          color: Colors.white,
                          size: 26,
                        ),
                ),
              ),
              const SizedBox(width: DoodhSpacing.md),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Profile',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: Colors.white70,
                      ),
                    ),
                    const SizedBox(height: DoodhSpacing.xs),
                    Text(
                      name.isEmpty ? 'Welcome' : name,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: theme.textTheme.headlineSmall?.copyWith(
                        color: Colors.white,
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    if (identityFacts.isNotEmpty) ...[
                      const SizedBox(height: DoodhSpacing.xs),
                      Text(
                        identityFacts,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.bodyMedium?.copyWith(
                          color: Colors.white70,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
              const SizedBox(width: DoodhSpacing.sm),
              ExcludeSemantics(
                child: IconButton.outlined(
                  tooltip: 'Edit profile',
                  onPressed: () => context.push('/customer/profile/edit'),
                  style: IconButton.styleFrom(
                    foregroundColor: Colors.white,
                    side: BorderSide(color: Colors.white.withValues(alpha: .45)),
                  ),
                  icon: const Icon(Icons.edit_outlined, size: 20),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _PersonalInfoSection extends StatelessWidget {
  const _PersonalInfoSection({required this.profile, required this.user});

  final CustomerProfile profile;
  final AuthUser? user;

  @override
  Widget build(BuildContext context) {
    final email = user?.email?.trim() ?? '';
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        DoodhSectionHeader(
          title: 'Personal information',
          action: IconButton(
            tooltip: 'Edit profile',
            onPressed: () => context.push('/customer/profile/edit'),
            icon: const Icon(Icons.edit_outlined),
          ),
        ),
        const SizedBox(height: DoodhSpacing.sm),
        DoodhCard(
          color: DoodhColors.mint,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _ProfileRow(label: 'Name', value: profile.firstName),
              _ProfileRow(label: 'Last Name', value: profile.lastName),
              _ProfileRow(
                label: 'Alternate Number',
                value: profile.alternateMobile,
              ),
              _ProfileRow(label: 'Gender', value: profile.gender),
              _ProfileRow(
                label: 'Date of Birth',
                value: profile.dateOfBirth == null
                    ? null
                    : _date(profile.dateOfBirth!),
              ),
              // Mobile/email rows open the preserved auth change flows (OTP
              // verification lives there) so no editable duplicate exists here.
              _ProfileRow(
                label: 'Mobile Number',
                value: _displayMobile(user?.mobile) ?? user?.mobile,
                onTap: () => context.push('/security/mobile'),
              ),
              _ProfileRow(
                label: 'Email',
                value: email.isEmpty ? null : email,
                onTap: () => context.push('/security/email'),
                // Only the session-provided emailVerified flag is shown; no
                // verification state is invented for accounts without email.
                trailing: user?.emailVerified == true && email.isNotEmpty
                    ? const DoodhStatusPill(
                        label: 'Verified',
                        tone: DoodhStatusTone.success,
                      )
                    : null,
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// A single label/value row. Uses the shared [DoodhKeyValueRow] so values stack
/// under their labels on narrow screens instead of being squeezed horizontally.
/// Rows that open a change flow render a chevron affordance and stay tappable.
class _ProfileRow extends StatelessWidget {
  const _ProfileRow({
    required this.label,
    required this.value,
    this.onTap,
    this.trailing,
  });

  final String label;
  final String? value;
  final VoidCallback? onTap;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final hasValue = value != null && value!.isNotEmpty;
    final row = DoodhKeyValueRow(
      label: label,
      value: hasValue ? value! : '—',
    );
    if (onTap == null && trailing == null) return row;
    return InkWell(
      borderRadius: DoodhRadii.smRadius,
      onTap: onTap,
      child: Row(
        children: [
          Expanded(child: row),
          ?trailing,
          const ExcludeSemantics(
            child: Icon(
              Icons.chevron_right,
              size: 18,
              color: DoodhColors.tealDark,
            ),
          ),
        ],
      ),
    );
  }
}

class _AddressSection extends StatelessWidget {
  const _AddressSection({required this.addresses, required this.isSaving});

  final List<CustomerAddress> addresses;
  final bool isSaving;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        DoodhSectionHeader(
          title: 'Delivery addresses',
          action: FilledButton.tonalIcon(
            onPressed: isSaving
                ? null
                : () => context.push('/customer/addresses/new'),
            icon: const Icon(Icons.add_location_alt_outlined, size: 18),
            label: const Text('Add address'),
          ),
        ),
        const SizedBox(height: DoodhSpacing.sm),
        if (addresses.isEmpty)
          EmptyStatePanel(
            title: 'No delivery addresses',
            message:
                'Add an address with a map pin before placing deliveries.',
            action: DoodhButton(
              label: 'Add address',
              icon: Icons.add,
              onPressed: isSaving
                  ? null
                  : () => context.push('/customer/addresses/new'),
            ),
          )
        else
          ...addresses.map(
            (address) => Padding(
              padding: const EdgeInsets.only(bottom: DoodhSpacing.sm),
              child: _AddressCard(
                address: address,
                isSaving: isSaving,
              ),
            ),
          ),
      ],
    );
  }
}

enum _AddressAction { edit, setDefault, deactivate }

/// A single saved delivery address. The default address is immediately
/// obvious through a tinted surface, a star icon, a status pill and an
/// explicit semantics label — never colour alone.
class _AddressCard extends StatelessWidget {
  const _AddressCard({required this.address, required this.isSaving});

  final CustomerAddress address;  final bool isSaving;

  String get _detailText {
    final line2 = address.addressLine2?.trim();
    final lines = [
      [
        address.addressLine1,
        if (line2 != null && line2.isNotEmpty) line2,
        address.locality,
        address.city,
      ].where((part) => part.trim().isNotEmpty).join(', '),
      '${address.state} - ${address.pinCode}',
    ].where((part) => part.trim().isNotEmpty).join('\n');
    return lines;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return DoodhCard(
      color: address.isDefault ? DoodhColors.mint : null,
      onTap: isSaving
          ? null
          : () => context.push('/customer/addresses/${address.publicId}/edit'),
      semanticLabel: address.isDefault
          ? '${address.label}, default delivery address'
          : address.label,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ExcludeSemantics(
            child: Icon(
              address.isDefault ? Icons.star : Icons.location_on_outlined,
              size: 22,
              color: address.isDefault
                  ? DoodhColors.tealDark
                  : DoodhColors.muted,
            ),
          ),
          const SizedBox(width: DoodhSpacing.md),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Wrap(
                  crossAxisAlignment: WrapCrossAlignment.center,
                  spacing: DoodhSpacing.sm,
                  runSpacing: DoodhSpacing.xs,
                  children: [
                    Text(
                      address.label,
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                    if (address.isDefault)
                      const DoodhStatusPill(
                        label: 'Default',
                        tone: DoodhStatusTone.success,
                      ),
                  ],
                ),
                const SizedBox(height: DoodhSpacing.xs),
                // The full address is merged into one semantic node so screen
                // readers announce it as a single readable block.
                Semantics(
                  container: true,
                  label: _detailText,
                  child: Text(_detailText),
                ),
              ],
            ),
          ),
          const SizedBox(width: DoodhSpacing.xs),
          PopupMenuButton<_AddressAction>(
            key: ValueKey('address-menu-${address.publicId}'),
            tooltip: 'Address actions',
            enabled: !isSaving,
            onSelected: (action) async {
              switch (action) {
                case _AddressAction.edit:
                  await context.push(
                    '/customer/addresses/${address.publicId}/edit',
                  );
                case _AddressAction.setDefault:
                  final container = ProviderScope.containerOf(
                    context,
                    listen: false,
                  );
                  await container
                      .read(customerControllerProvider.notifier)
                      .saveAddress(
                        address.toDraft(isDefault: true),
                        addressId: address.publicId,
                      );
                case _AddressAction.deactivate:
                  await _confirmDeactivate(context, address);
              }
            },
            itemBuilder: (context) => [
              const PopupMenuItem(
                value: _AddressAction.edit,
                child: ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(Icons.edit_outlined),
                  title: Text('Edit'),
                ),
              ),
              if (!address.isDefault)
                const PopupMenuItem(
                  value: _AddressAction.setDefault,
                  child: ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: Icon(Icons.star_outline),
                    title: Text('Set as default'),
                  ),
                ),
              const PopupMenuItem(
                value: _AddressAction.deactivate,
                child: ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(Icons.delete_outline),
                  title: Text('Deactivate'),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  /// Deactivation keeps the existing confirm-then-deactivate flow, with the
  /// destructive action clearly labelled. A bottom sheet is used on compact
  /// screens so the actions sit within comfortable thumb reach; the confirm
  /// dialog text is unchanged.
  Future<void> _confirmDeactivate(
    BuildContext context,
    CustomerAddress address,
  ) async {
    final confirmed = await showModalBottomSheet<bool>(
      context: context,
      showDragHandle: true,
      builder: (sheetContext) => SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            DoodhSpacing.lg,
            DoodhSpacing.sm,
            DoodhSpacing.lg,
            DoodhSpacing.lg,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Deactivate address?',
                style: Theme.of(sheetContext).textTheme.titleMedium,
              ),
              const SizedBox(height: DoodhSpacing.xs),
              Text(
                'Remove ${address.label} from active delivery addresses?',
              ),
              const SizedBox(height: DoodhSpacing.md),
              DoodhButton(
                label: 'Deactivate',
                icon: Icons.delete_outline,
                expand: true,
                onPressed: () => Navigator.pop(sheetContext, true),
              ),
              const SizedBox(height: DoodhSpacing.sm),
              DoodhButton(
                label: 'Cancel',
                variant: DoodhButtonVariant.secondary,
                expand: true,
                onPressed: () => Navigator.pop(sheetContext, false),
              ),
            ],
          ),
        ),
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final container = ProviderScope.containerOf(context, listen: false);
    final saved = await container
        .read(customerControllerProvider.notifier)
        .deactivateAddress(address);
    if (!saved && context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Unable to deactivate the address.')),
      );
    }
  }
}

/// Shared security entry points: the Login & security hub (password, mobile
/// and email change flows with their OTP verification) and the direct
/// password action. Routing and auth behaviour are unchanged.
class _SecuritySection extends StatelessWidget {
  const _SecuritySection({required this.user});

  final AuthUser? user;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      DoodhSectionHeader(title: 'Security'),
      const SizedBox(height: DoodhSpacing.sm),
      DoodhActionTile(
        key: const ValueKey('account-security-entry'),
        icon: Icons.shield_outlined,
        title: 'Login & security',
        subtitle: 'Manage your password, mobile number and email.',
        onTap: () => context.push('/security'),
      ),
      const SizedBox(height: DoodhSpacing.sm),
      DoodhActionTile(
        key: const ValueKey('account-change-password-entry'),
        icon: Icons.password_outlined,
        title: 'Change Password',
        subtitle: user?.hasPassword == true
            ? 'Update the password you sign in with.'
            : 'Set a password to sign in without an OTP.',
        onTap: () => context.push(
          user?.hasPassword == true ? '/change-password' : '/create-password',
        ),
      ),
    ],
  );
}

/// Formats the canonical session mobile (`+91XXXXXXXXXX`) for display while
/// leaving other formats untouched. Display-only: storage and validation are
/// unaffected.
String? _displayMobile(String? mobile) {
  final canonical = mobile?.trim();
  if (canonical == null || canonical.isEmpty) return null;
  if (canonical.startsWith('+91') && canonical.length == 13) {
    return '+91 ${canonical.substring(3)}';
  }
  return canonical;
}

class CustomerProfileEditScreen extends ConsumerStatefulWidget {
  const CustomerProfileEditScreen({super.key});

  @override
  ConsumerState<CustomerProfileEditScreen> createState() =>
      _CustomerProfileEditScreenState();
}

class _CustomerProfileEditScreenState
    extends ConsumerState<CustomerProfileEditScreen> {
  late final TextEditingController _firstName;
  late final TextEditingController _lastName;
  late final TextEditingController _mobile;
  late final TextEditingController _gender;
  final _formKey = GlobalKey<FormState>();
  final _countryKey = GlobalKey<CountryCodeMobileFieldState>();
  late CountryCode _initialCountry;
  DateTime? _dateOfBirth;

  @override
  void initState() {
    super.initState();
    final profile = ref.read(customerControllerProvider).profile;
    _firstName = TextEditingController(text: profile?.firstName ?? '');
    _lastName = TextEditingController(text: profile?.lastName ?? '');
    _gender = TextEditingController(text: profile?.gender ?? '');
    _dateOfBirth = profile?.dateOfBirth;

    // Resolve the country from the stored alternate mobile so a canonical
    // `+91...`/`+<other>` value is split back into its national part instead of
    // being typed literally. Plain 10-digit values keep the India default.
    _initialCountry = CountryCodes.india;
    final stored = (profile?.alternateMobile ?? '').trim();
    if (stored.isEmpty) {
      _mobile = TextEditingController();
    } else if (stored.startsWith('+') || stored.startsWith('00')) {
      final parsed = parseMobileCountry(stored);
      _initialCountry = parsed?.$1 ?? CountryCodes.india;
      _mobile = TextEditingController(text: parsed?.$2 ?? stored);
    } else {
      final canonical = canonicalizeIndianMobile(stored);
      _mobile = TextEditingController(
        text: canonical != null && canonical.length == 13
            ? canonical.substring(3)
            : stored,
      );
    }
  }

  @override
  void dispose() {
    _firstName.dispose();
    _lastName.dispose();
    _mobile.dispose();
    _gender.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final saving = ref.watch(customerControllerProvider).isSaving;
    return Scaffold(
      appBar: AppBar(title: const Text('Edit profile')),
      body: DoodhPage(
        child: Form(
          key: _formKey,
          child: ListView(
            children: [
              DoodhSectionHeader(
                title: 'Personal details',
                action: const Icon(Icons.person_outline),
              ),
              const SizedBox(height: DoodhSpacing.md),
              DoodhField(label: 'First name', controller: _firstName),
              const SizedBox(height: DoodhSpacing.md),
              DoodhField(label: 'Last name', controller: _lastName),
              const SizedBox(height: DoodhSpacing.md),
              CountryCodeMobileField(
                key: _countryKey,
                controller: _mobile,
                initialCountry: _initialCountry,
                optional: true,
                label: 'Alternate mobile',
                errorText: _fieldError('alternateMobile'),
              ),
              const SizedBox(height: DoodhSpacing.md),
              DropdownButtonFormField<String>(
                initialValue: _gender.text.isEmpty ? null : _gender.text,
                decoration: InputDecoration(
                  labelText: 'Gender',
                  errorText: _fieldError('gender'),
                ),
                items: const [
                  DropdownMenuItem(value: 'Male', child: Text('Male')),
                  DropdownMenuItem(value: 'Female', child: Text('Female')),
                  DropdownMenuItem(value: 'Other', child: Text('Other')),
                ],
                onChanged: (value) => _gender.text = value ?? '',
              ),
              const SizedBox(height: DoodhSpacing.md),
              ListTile(
                contentPadding: EdgeInsets.zero,
                title: Text(
                  _dateOfBirth == null ? 'Date of birth' : _date(_dateOfBirth!),
                ),
                trailing: const Icon(Icons.calendar_month_outlined),
                onTap: () async {
                  final today = indiaNow();
                  final selected = await showDatePicker(
                    context: context,
                    firstDate: DateTime(1900),
                    lastDate: DateTime(today.year, today.month, today.day),
                    initialDate: _dateOfBirth ?? DateTime(1990),
                  );
                  if (selected != null) setState(() => _dateOfBirth = selected);
                },
              ),
              const SizedBox(height: DoodhSpacing.lg),
              DoodhButton(
                label: 'Save profile',
                icon: Icons.save_outlined,
                expand: true,
                busy: saving,
                onPressed: saving ? null : _save,
              ),
            ],
          ),
        ),
      ),
    );
  }

  String? _fieldError(String field) =>
      ref.read(customerControllerProvider).fieldErrors[field];

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;
    final saved = await ref
        .read(customerControllerProvider.notifier)
        .saveProfile(
          UpdateCustomerProfile(
            firstName: _firstName.text,
            lastName: _lastName.text,
            alternateMobile: _countryKey.currentState?.canonicalValue,
            gender: _gender.text,
            dateOfBirth: _dateOfBirth,
          ),
        );
    if (saved && mounted) context.pop();
  }
}

class CustomerAddressEditScreen extends ConsumerStatefulWidget {
  const CustomerAddressEditScreen({
    super.key,
    this.addressId,
    this.checkoutMode = false,
  });

  final String? addressId;
  final bool checkoutMode;

  @override
  ConsumerState<CustomerAddressEditScreen> createState() =>
      _CustomerAddressEditScreenState();
}

class _CustomerAddressEditScreenState
    extends ConsumerState<CustomerAddressEditScreen> {
  final _formKey = GlobalKey<FormState>();
  final _fields = <String, TextEditingController>{};
  final _contactMobileKey = GlobalKey<CountryCodeMobileFieldState>();
  late CountryCode _initialContactCountry;
  bool _isDefault = false;

  @override
  void initState() {
    super.initState();
    final address = widget.addressId == null
        ? null
        : ref
              .read(customerControllerProvider)
              .addresses
              .where((item) => item.publicId == widget.addressId)
              .firstOrNull;
    for (final field in [
      'label',
      'addressLine1',
      'addressLine2',
      'locality',
      'city',
      'state',
      'pinCode',
      'landmark',
      'deliveryInstructions',
      'contactName',
      'contactMobile',
      // Coordinates remain internal because the API and domain require them;
      // customers select them from the map instead of typing them.
      'latitude',
      'longitude',
    ]) {
      _fields[field] = TextEditingController(text: _value(address, field));
    }
    // Resolve the country from the stored contact mobile (canonical `+91...`
    // or plain 10-digit) and split a canonical value back into its national
    // part so the shared field shows a clean number.
    _initialContactCountry = CountryCodes.india;
    final storedMobile = _fields['contactMobile']!.text.trim();
    if (storedMobile.startsWith('+') || storedMobile.startsWith('00')) {
      final parsed = parseMobileCountry(storedMobile);
      if (parsed != null) {
        _initialContactCountry = parsed.$1;
        _fields['contactMobile']!.text = parsed.$2;
      }
    } else {
      final canonical = canonicalizeIndianMobile(storedMobile);
      if (canonical != null && canonical.length == 13) {
        _fields['contactMobile']!.text = canonical.substring(3);
      }
    }
    _isDefault =
        address?.isDefault ??
        ref.read(customerControllerProvider).addresses.isEmpty;
  }

  @override
  void dispose() {
    for (final controller in _fields.values) {
      controller.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(customerControllerProvider);
    return Scaffold(
      appBar: AppBar(
        title: Text(
          widget.checkoutMode
              ? 'Enter delivery address'
              : widget.addressId == null
              ? 'Add address'
              : 'Edit address',
        ),
      ),
      body: DoodhPage(
        child: Form(
          key: _formKey,
          child: ListView(
            children: [
              DoodhSectionHeader(
                title: 'Address details',
                action: const Icon(Icons.home_work_outlined),
              ),
              const SizedBox(height: DoodhSpacing.md),
              if (!widget.checkoutMode) _text('label', 'Label', required: true),
              _text('addressLine1', 'Address line 1', required: true),
              _text('addressLine2', 'Address line 2'),
              _text('locality', 'Locality', required: true),
              _text('city', 'City', required: true),
              _text('state', 'State', required: true),
              _text(
                'pinCode',
                'PIN code',
                required: true,
                keyboardType: TextInputType.number,
              ),
              _text('landmark', 'Landmark'),
              _text('deliveryInstructions', 'Delivery instructions'),
              _text('contactName', 'Contact name', required: true),
              CountryCodeMobileField(
                key: _contactMobileKey,
                controller: _fields['contactMobile']!,
                initialCountry: _initialContactCountry,
                optional: true,
                label: 'Contact mobile',
                errorText: state.fieldErrors['contactMobile'],
                validator: (canonical) => canonical == null &&
                        _fields['contactMobile']!.text.trim().isEmpty
                    ? 'Contact mobile is required'
                    : null,
              ),
              const SizedBox(height: DoodhSpacing.md),
              Text('Location', style: Theme.of(context).textTheme.titleMedium),
              const Text(
                'Tap the map to adjust your delivery location.',
              ),
              const SizedBox(height: DoodhSpacing.md),
              GoogleMapCoordinatePicker(
                initialLocation: _initialMapLocation(),
                onLocationSelected: _setMapLocation,
                mapsLoader: _loadGoogleMaps,
              ),
              const SizedBox(height: DoodhSpacing.sm),
              DoodhButton(
                label: 'Retry address lookup',
                icon: Icons.pin_drop_outlined,
                variant: DoodhButtonVariant.secondary,
                expand: true,
                onPressed: state.isSaving || !_hasValidCoordinates
                    ? null
                    : _lookup,
              ),
              if (!widget.checkoutMode)
                CheckboxListTile(
                  contentPadding: EdgeInsets.zero,
                  value: _isDefault,
                  onChanged: (value) =>
                      setState(() => _isDefault = value ?? false),
                  title: const Text('Use as default address'),
                  controlAffinity: ListTileControlAffinity.leading,
                ),
              const SizedBox(height: DoodhSpacing.lg),
              DoodhButton(
                label: widget.checkoutMode
                    ? 'Use this address'
                    : 'Save address',
                icon: Icons.save_outlined,
                expand: true,
                busy: state.isSaving,
                onPressed: state.isSaving ? null : _save,
              ),
              if (state.errorMessage != null) ...[
                const SizedBox(height: DoodhSpacing.md),
                DoodhErrorBanner(message: state.errorMessage!),
              ],
            ],
          ),
        ),
      ),
    );
  }

  /// Loads the Google Maps JavaScript API using the Web Client Key fetched at
  /// runtime from the backend's client-safe configuration endpoint.
  ///
  /// No compile-time `--dart-define`, `index.html` tag or hard-coded key is
  /// used: the key is resolved from the server when this screen opens and a
  /// browser refresh reloads the latest value. When the session has no token,
  /// the fetch fails, or the operator has not configured a key, an empty key
  /// is passed so [loadGoogleMapsScript] surfaces its canonical
  /// "not configured" error inside the picker.
  Future<void> _loadGoogleMaps() async {
    final token = ref.read(sessionControllerProvider).session?.accessToken;
    String? key;
    if (token != null) {
      try {
        key =
            (await ref.read(clientConfigurationRepositoryProvider).get(token))
                .googleMapsWebClientKey;
      } on Object {
        key = null;
      }
    }
    return loadGoogleMapsScript(key ?? '');
  }

  LatLng? _initialMapLocation() {
    final latitude = double.tryParse(_fields['latitude']!.text);
    final longitude = double.tryParse(_fields['longitude']!.text);
    if (latitude == null || longitude == null) return null;
    if (!latitude.isFinite || !longitude.isFinite) return null;
    if (latitude < -90 ||
        latitude > 90 ||
        longitude < -180 ||
        longitude > 180) {
      return null;
    }
    return LatLng(latitude, longitude);
  }

  bool get _hasValidCoordinates {
    final latitude = double.tryParse(_fields['latitude']!.text);
    final longitude = double.tryParse(_fields['longitude']!.text);
    return latitude != null &&
        longitude != null &&
        latitude.isFinite &&
        longitude.isFinite &&
        latitude >= -90 &&
        latitude <= 90 &&
        longitude >= -180 &&
        longitude <= 180;
  }

  void _setMapLocation(LatLng location) {
    _fields['latitude']!.text = location.latitude.toStringAsFixed(6);
    _fields['longitude']!.text = location.longitude.toStringAsFixed(6);
    setState(() {});
    // The selected coordinates are retained even when the optional lookup is
    // unavailable or offline. This keeps saving safe and preserves the pin.
    _lookup();
  }

  Widget _text(
    String key,
    String label, {
    bool required = false,
    TextInputType? keyboardType,
    String? Function(String?)? validator,
  }) => Padding(
    padding: const EdgeInsets.only(bottom: DoodhSpacing.md),
    child: DoodhField(
      label: label,
      controller: _fields[key],
      keyboardType: keyboardType,
      errorText: ref.watch(customerControllerProvider).fieldErrors[key],
      validator: validator ??
          (required
              ? (value) => value == null || value.trim().isEmpty
                    ? '$label is required'
                    : null
              : null),
    ),
  );

  Future<void> _lookup() async {
    final latitude = double.tryParse(_fields['latitude']!.text);
    final longitude = double.tryParse(_fields['longitude']!.text);
    if (latitude == null || longitude == null || !_hasValidCoordinates) return;
    final lookup = await ref
        .read(customerControllerProvider.notifier)
        .reverseLookup(latitude, longitude);
    if (!mounted) return;
    if (lookup == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'Address lookup is unavailable right now. Your coordinates and address fields were preserved.',
          ),
        ),
      );
      return;
    }
    for (final entry in {
      'addressLine1': lookup.addressLine1,
      'locality': lookup.locality,
      'city': lookup.city,
      'state': lookup.state,
      'pinCode': lookup.pinCode,
      'landmark': lookup.landmark,
    }.entries) {
      final value = entry.value?.trim();
      if (value != null && value.isNotEmpty) {
        _fields[entry.key]!.text = value;
      }
    }
  }

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;

    final latitude = double.tryParse(_fields['latitude']!.text);
    final longitude = double.tryParse(_fields['longitude']!.text);
    if (latitude == null || longitude == null || !_hasValidCoordinates) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Select a valid location on the map.')),
      );
      return;
    }

    final draft = AddressDraft(
      label: widget.checkoutMode ? '' : _fields['label']!.text,
      addressLine1: _fields['addressLine1']!.text,
      addressLine2: _fields['addressLine2']!.text,
      locality: _fields['locality']!.text,
      city: _fields['city']!.text,
      state: _fields['state']!.text,
      pinCode: _fields['pinCode']!.text,
      landmark: _fields['landmark']!.text,
      deliveryInstructions: _fields['deliveryInstructions']!.text,
      contactName: _fields['contactName']!.text,
      contactMobile: _contactMobileKey.currentState?.canonicalValue ?? '',
      latitude: latitude,
      longitude: longitude,
      isDefault: widget.checkoutMode ? false : _isDefault,
    );
    if (widget.checkoutMode) {
      if (mounted) context.pop(draft);
      return;
    }
    final saved = await ref
        .read(customerControllerProvider.notifier)
        .saveAddress(draft, addressId: widget.addressId);
    if (saved && mounted) context.pop();
  }

  String _value(CustomerAddress? address, String field) => switch (field) {
    'label' => address?.label ?? '',
    'addressLine1' => address?.addressLine1 ?? '',
    'addressLine2' => address?.addressLine2 ?? '',
    'locality' => address?.locality ?? '',
    'city' => address?.city ?? '',
    'state' => address?.state ?? '',
    'pinCode' => address?.pinCode ?? '',
    'landmark' => address?.landmark ?? '',
    'deliveryInstructions' => address?.deliveryInstructions ?? '',
    'contactName' => address?.contactName ?? '',
    'contactMobile' => address?.contactMobile ?? '',
    'latitude' => address?.latitude.toString() ?? '',
    'longitude' => address?.longitude.toString() ?? '',
    _ => '',
  };
}

String _date(DateTime value) =>
    '${value.day.toString().padLeft(2, '0')}/${value.month.toString().padLeft(2, '0')}/${value.year}';
