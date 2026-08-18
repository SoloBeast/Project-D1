import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/customer/google_map_coordinate_picker.dart';
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
    return Scaffold(
      appBar: AppBar(title: const Text('My account')),
      body: state.profile == null
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
              child: ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  _ProfileSection(profile: state.profile!),
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
    );
  }
}

class _ProfileSection extends StatelessWidget {
  const _ProfileSection({required this.profile});

  final CustomerProfile profile;

  @override
  Widget build(BuildContext context) => Card(
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
          Text(profile.fullName.isEmpty ? 'Add your name' : profile.fullName),
          if (profile.dateOfBirth != null)
            Text('Date of birth: ${_date(profile.dateOfBirth!)}'),
          if (profile.gender?.isNotEmpty == true) Text(profile.gender!),
          if (profile.alternateMobile?.isNotEmpty == true)
            Text(profile.alternateMobile!),
        ],
      ),
    ),
  );
}

class _AddressSection extends StatelessWidget {
  const _AddressSection({required this.addresses, required this.isSaving});

  final List<CustomerAddress> addresses;
  final bool isSaving;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text(
            'Delivery addresses',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          IconButton(
            tooltip: 'Add address',
            onPressed: isSaving
                ? null
                : () => context.push('/customer/addresses/new'),
            icon: const Icon(Icons.add_location_alt_outlined),
          ),
        ],
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
  DateTime? _dateOfBirth;

  @override
  void initState() {
    super.initState();
    final profile = ref.read(customerControllerProvider).profile;
    _firstName = TextEditingController(text: profile?.firstName ?? '');
    _lastName = TextEditingController(text: profile?.lastName ?? '');
    _mobile = TextEditingController(text: profile?.alternateMobile ?? '');
    _gender = TextEditingController(text: profile?.gender ?? '');
    _dateOfBirth = profile?.dateOfBirth;
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
      body: Form(
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            TextFormField(
              controller: _firstName,
              decoration: const InputDecoration(labelText: 'First name'),
            ),
            TextFormField(
              controller: _lastName,
              decoration: const InputDecoration(labelText: 'Last name'),
            ),
            TextFormField(
              controller: _mobile,
              decoration: const InputDecoration(labelText: 'Alternate mobile'),
              keyboardType: TextInputType.phone,
            ),
            TextFormField(
              controller: _gender,
              decoration: const InputDecoration(labelText: 'Gender'),
            ),
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: Text(
                _dateOfBirth == null ? 'Date of birth' : _date(_dateOfBirth!),
              ),
              trailing: const Icon(Icons.calendar_month_outlined),
              onTap: () async {
                final selected = await showDatePicker(
                  context: context,
                  firstDate: DateTime(1900),
                  lastDate: DateTime.now(),
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
    );
  }

  Future<void> _save() async {
    final saved = await ref
        .read(customerControllerProvider.notifier)
        .saveProfile(
          UpdateCustomerProfile(
            firstName: _firstName.text,
            lastName: _lastName.text,
            alternateMobile: _mobile.text,
            gender: _gender.text,
            dateOfBirth: _dateOfBirth,
          ),
        );
    if (saved && mounted) context.pop();
  }
}

class CustomerAddressEditScreen extends ConsumerStatefulWidget {
  const CustomerAddressEditScreen({super.key, this.addressId});

  final String? addressId;

  @override
  ConsumerState<CustomerAddressEditScreen> createState() =>
      _CustomerAddressEditScreenState();
}

class _CustomerAddressEditScreenState
    extends ConsumerState<CustomerAddressEditScreen> {
  final _formKey = GlobalKey<FormState>();
  final _fields = <String, TextEditingController>{};
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
      'latitude',
      'longitude',
    ]) {
      _fields[field] = TextEditingController(text: _value(address, field));
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
        title: Text(widget.addressId == null ? 'Add address' : 'Edit address'),
      ),
      body: Form(
        key: _formKey,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            _text('label', 'Label', required: true),
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
            _text(
              'contactMobile',
              'Contact mobile',
              required: true,
              keyboardType: TextInputType.phone,
            ),
            const SizedBox(height: 8),
            Text('Location', style: Theme.of(context).textTheme.titleMedium),
            const Text(
              'Tap the map to place the delivery pin, or enter coordinates manually.',
            ),
            const SizedBox(height: 12),
            GoogleMapCoordinatePicker(
              initialLocation: _initialMapLocation(),
              onLocationSelected: _setMapLocation,
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: _text(
                    'latitude',
                    'Latitude',
                    required: true,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _text(
                    'longitude',
                    'Longitude',
                    required: true,
                    keyboardType: const TextInputType.numberWithOptions(
                      decimal: true,
                    ),
                  ),
                ),
              ],
            ),
            OutlinedButton.icon(
              onPressed: state.isSaving ? null : _lookup,
              icon: const Icon(Icons.pin_drop_outlined),
              label: const Text('Look up address from pin'),
            ),
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              value: _isDefault,
              onChanged: (value) => setState(() => _isDefault = value ?? false),
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
              label: const Text('Save address'),
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
    );
  }

  LatLng? _initialMapLocation() {
    final latitude = double.tryParse(_fields['latitude']!.text);
    final longitude = double.tryParse(_fields['longitude']!.text);
    if (latitude == null || longitude == null) return null;
    if (!latitude.isFinite || !longitude.isFinite) return null;
    if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180) {
      return null;
    }
    return LatLng(latitude, longitude);
  }

  void _setMapLocation(LatLng location) {
    _fields['latitude']!.text = location.latitude.toStringAsFixed(6);
    _fields['longitude']!.text = location.longitude.toStringAsFixed(6);
    setState(() {});
  }

  Widget _text(
    String key,
    String label, {
    bool required = false,
    TextInputType? keyboardType,
  }) => TextFormField(
    controller: _fields[key],
    keyboardType: keyboardType,
    decoration: InputDecoration(labelText: label),
    validator: required
        ? (value) => value == null || value.trim().isEmpty
              ? '$label is required'
              : null
        : null,
  );

  Future<void> _lookup() async {
    final latitude = double.tryParse(_fields['latitude']!.text);
    final longitude = double.tryParse(_fields['longitude']!.text);
    if (latitude == null ||
        longitude == null ||
        !latitude.isFinite ||
        !longitude.isFinite ||
        latitude < -90 ||
        latitude > 90 ||
        longitude < -180 ||
        longitude > 180) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Enter valid latitude and longitude first.'),
        ),
      );
      return;
    }
    final lookup = await ref
        .read(customerControllerProvider.notifier)
        .reverseLookup(latitude, longitude);
    if (!mounted) return;
    if (lookup == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'Address lookup is not configured for this environment.',
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
    }.entries) {
      if (entry.value != null) _fields[entry.key]!.text = entry.value!;
    }
  }

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;

    final latitude = double.tryParse(_fields['latitude']!.text);
    final longitude = double.tryParse(_fields['longitude']!.text);
    if (latitude == null ||
        longitude == null ||
        !latitude.isFinite ||
        !longitude.isFinite ||
        latitude < -90 ||
        latitude > 90 ||
        longitude < -180 ||
        longitude > 180) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Enter valid latitude and longitude.')),
      );
      return;
    }

    final draft = AddressDraft(
      label: _fields['label']!.text,
      addressLine1: _fields['addressLine1']!.text,
      addressLine2: _fields['addressLine2']!.text,
      locality: _fields['locality']!.text,
      city: _fields['city']!.text,
      state: _fields['state']!.text,
      pinCode: _fields['pinCode']!.text,
      landmark: _fields['landmark']!.text,
      deliveryInstructions: _fields['deliveryInstructions']!.text,
      contactName: _fields['contactName']!.text,
      contactMobile: _fields['contactMobile']!.text,
      latitude: latitude,
      longitude: longitude,
      isDefault: _isDefault,
    );
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
