import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/india_mobile.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:doodh_direct_mobile/core/widgets/country_code_mobile_field.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
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
                padding: false,
                child: ListView(
                  physics: const AlwaysScrollableScrollPhysics(),
                  padding: const EdgeInsets.fromLTRB(16, 20, 16, 28),
                  children: [
                    _ProfileSection(profile: state.profile!, user: user),
                    const SizedBox(height: 16),
                    Card(
                      child: ListTile(
                        leading: Icon(
                          Icons.password_outlined,
                          color: Theme.of(context).colorScheme.primary,
                        ),
                        title: const Text('Change Password'),
                        subtitle: Text(
                          user?.hasPassword == true
                              ? 'Update the password you sign in with.'
                              : 'Set a password to sign in without an OTP.',
                        ),
                        trailing: const Icon(Icons.chevron_right),
                        onTap: () => context.push(
                          user?.hasPassword == true
                              ? '/change-password'
                              : '/create-password',
                        ),
                      ),
                    ),
                    const SizedBox(height: 24),
                    _AddressSection(
                      addresses: state.addresses,
                      isSaving: state.isSaving,
                    ),
                    if (state.errorMessage != null) ...[
                      const SizedBox(height: 16),
                      Text(
                        state.errorMessage!,
                        style: TextStyle(
                          color: Theme.of(context).colorScheme.error,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
    );
  }
}

class _ProfileSection extends StatelessWidget {
  const _ProfileSection({required this.profile, required this.user});

  final CustomerProfile profile;
  final AuthUser? user;

  @override
  Widget build(BuildContext context) => Card(
    color: DoodhColors.mint,
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text('Profile', style: Theme.of(context).textTheme.titleLarge),
              IconButton(
                tooltip: 'Edit profile',
                onPressed: () => context.push('/customer/profile/edit'),
                icon: const Icon(Icons.edit_outlined),
              ),
            ],
          ),
          if (profile.customerNumber?.isNotEmpty == true)
            Padding(
              padding: const EdgeInsets.only(top: 4, bottom: 12),
              child: Text(
                'Customer number: ${profile.customerNumber}',
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: DoodhColors.muted),
              ),
            ),
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
            value: user?.mobile,
            onTap: () => context.push('/security/mobile'),
          ),
          _ProfileRow(
            label: 'Email',
            value: user?.email,
            onTap: () => context.push('/security/email'),
          ),
        ],
      ),
    ),
  );
}

class _ProfileRow extends StatelessWidget {
  const _ProfileRow({required this.label, required this.value, this.onTap});

  final String label;
  final String? value;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final hasValue = value != null && value!.isNotEmpty;
    final tappable = onTap != null;
    final row = Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SizedBox(
          width: 140,
          child: Text(
            label,
            style: theme.textTheme.bodyMedium
                ?.copyWith(color: DoodhColors.muted),
          ),
        ),
        Expanded(
          child: Text(
            hasValue ? value! : '—',
            style: theme.textTheme.bodyMedium?.copyWith(
              color: hasValue ? DoodhColors.ink : DoodhColors.muted,
            ),
          ),
        ),
        if (tappable)
          Padding(
            padding: const EdgeInsets.only(left: 8),
            child: Icon(
              Icons.chevron_right,
              size: 18,
              color: theme.colorScheme.primary,
            ),
          ),
      ],
    );
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: tappable
          ? InkWell(
              borderRadius: BorderRadius.circular(8),
              onTap: onTap,
              child: row,
            )
          : row,
    );
  }
}

class _AddressSection extends StatelessWidget {
  const _AddressSection({required this.addresses, required this.isSaving});

  final List<CustomerAddress> addresses;
  final bool isSaving;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      DoodhSectionHeader(
        title: 'Delivery addresses',
        action: IconButton(
          tooltip: 'Add address',
          onPressed: isSaving
              ? null
              : () => context.push('/customer/addresses/new'),
          icon: const Icon(Icons.add_location_alt_outlined),
        ),
      ),
      if (addresses.isEmpty)
        EmptyStatePanel(
          title: 'No delivery addresses',
          message: 'Add an address with a map pin before placing deliveries.',
          action: FilledButton.icon(
            onPressed: isSaving
                ? null
                : () => context.push('/customer/addresses/new'),
            icon: const Icon(Icons.add),
            label: const Text('Add address'),
          ),
        )
      else
        ...addresses.map(
          (address) => Card(
            child: ListTile(
              onTap: isSaving
                  ? null
                  : () => context.push(
                      '/customer/addresses/${address.publicId}/edit',
                    ),
              leading: Icon(
                address.isDefault ? Icons.star : Icons.location_on_outlined,
                color: address.isDefault
                    ? Theme.of(context).colorScheme.primary
                    : null,
              ),
              title: Text(
                '${address.label}${address.isDefault ? '  (default)' : ''}',
              ),
              subtitle: Text(
                '${address.addressLine1}, ${address.locality}, ${address.city}\n'
                '${address.state} - ${address.pinCode}',
              ),
              isThreeLine: true,
              trailing: PopupMenuButton<_AddressAction>(
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
            ),
          ),
        ),
    ],
  );

  Future<void> _confirmDeactivate(
    BuildContext context,
    CustomerAddress address,
  ) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Deactivate address?'),
        content: Text(
          'Remove ${address.label} from active delivery addresses?',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Deactivate'),
          ),
        ],
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

enum _AddressAction { edit, setDefault, deactivate }

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
              const SizedBox(height: 12),
              TextFormField(
                controller: _firstName,
                decoration: const InputDecoration(labelText: 'First name'),
              ),
              TextFormField(
                controller: _lastName,
                decoration: const InputDecoration(labelText: 'Last name'),
              ),
              CountryCodeMobileField(
                key: _countryKey,
                controller: _mobile,
                initialCountry: _initialCountry,
                optional: true,
                label: 'Alternate mobile',
                errorText: _fieldError('alternateMobile'),
              ),
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
              const SizedBox(height: 20),
              FilledButton.icon(
                onPressed: saving ? null : _save,
                icon: saving
                    ? const SizedBox.square(
                        dimension: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.save_outlined),
                label: const Text('Save profile'),
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
              const SizedBox(height: 12),
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
              const SizedBox(height: 8),
              Text('Location', style: Theme.of(context).textTheme.titleMedium),
              const Text(
                'Tap the map to adjust your delivery location.',
              ),
              const SizedBox(height: 12),
              GoogleMapCoordinatePicker(
                initialLocation: _initialMapLocation(),
                onLocationSelected: _setMapLocation,
                mapsLoader: _loadGoogleMaps,
              ),
              const SizedBox(height: 8),
              OutlinedButton.icon(
                onPressed: state.isSaving || !_hasValidCoordinates
                    ? null
                    : _lookup,
                icon: const Icon(Icons.pin_drop_outlined),
                label: const Text('Retry address lookup'),
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
              const SizedBox(height: 12),
              FilledButton.icon(
                onPressed: state.isSaving ? null : _save,
                icon: state.isSaving
                    ? const SizedBox.square(
                        dimension: 18,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.save_outlined),
                label: Text(widget.checkoutMode ? 'Use this address' : 'Save address'),
              ),
              if (state.errorMessage != null) ...[
                const SizedBox(height: 12),
                Text(
                  state.errorMessage!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
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
  }) => TextFormField(
    controller: _fields[key],
    keyboardType: keyboardType,
    decoration: InputDecoration(
      labelText: label,
      errorText: ref.watch(customerControllerProvider).fieldErrors[key],
    ),
    validator: validator ??
        (required
            ? (value) => value == null || value.trim().isEmpty
                  ? '$label is required'
                  : null
            : null),
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
