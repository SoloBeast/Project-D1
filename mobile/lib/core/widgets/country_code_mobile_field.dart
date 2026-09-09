import 'package:doodh_direct_mobile/core/utils/country_codes.dart';
import 'package:doodh_direct_mobile/core/utils/mobile_number.dart';
import 'package:flutter/material.dart';

/// Reusable mobile number field with a selectable country-code prefix.
///
/// The widget renders a single [TextFormField]. The country selector — flag,
/// dialing code and chevron — is a tappable prefix *inside* the field, so it
/// never adds an extra form control: screens and tests that look for exactly
/// one `TextFormField` (OTP login, forgot password, employee forms, ...) keep
/// working. Tapping the prefix opens a searchable country picker (search by
/// name, ISO code or dialing code).
///
/// The field reports the canonical E.164 value (e.g. `+919876543210`) through
/// [CountryCodeMobileFieldState.canonicalValue]. India keeps the exact existing
/// 10-digit normalization; every other country keeps its own dialing code and
/// is never coerced to `+91`. Default selection is India (+91).
class CountryCodeMobileField extends StatefulWidget {
  const CountryCodeMobileField({
    super.key,
    required this.controller,
    this.initialCountry = CountryCodes.india,
    this.enabled = true,
    this.autofocus = false,
    this.optional = false,
    this.label = 'Mobile number',
    this.hintText,
    this.helperText,
    this.errorText,
    this.validator,
    this.onSubmitted,
    this.onCountryChanged,
  });

  /// Holds the national number as typed by the user.
  final TextEditingController controller;

  /// The country preselected when the field is first built. Defaults to
  /// India (+91). Use [CountryCodes.byIso] / `parseMobileCountry` to prefill
  /// from a stored canonical value.
  final CountryCode initialCountry;

  final bool enabled;
  final bool autofocus;

  /// When true, an empty input is allowed. [validator] then receives `null`
  /// so the caller can apply cross-field rules (e.g. "email or mobile
  /// required").
  final bool optional;

  final String label;

  /// Optional hint text shown inside the field before the user types.
  final String? hintText;

  /// Optional helper text shown below the field (e.g. explaining that a
  /// one-time code will be sent to verify the number).
  final String? helperText;

  /// Optional error text shown below the field (e.g. a server-side field
  /// error for the mobile number). When set, the field is marked as errored
  /// regardless of the validator state.
  final String? errorText;

  /// Optional post-validation hook receiving the canonical E.164 value (or
  /// `null` when the input is empty and [optional] is true). Return a message
  /// to reject, or `null` to accept.
  final String? Function(String? canonical)? validator;

  final ValueChanged<String>? onSubmitted;

  /// Notified whenever the user picks a different country from the picker.
  final ValueChanged<CountryCode>? onCountryChanged;

  @override
  CountryCodeMobileFieldState createState() => CountryCodeMobileFieldState();
}

/// Public state of [CountryCodeMobileField] exposing the selected country and
/// the canonical E.164 value. Reachable through a
/// `GlobalKey<CountryCodeMobileFieldState>` when a screen needs to read them.
class CountryCodeMobileFieldState extends State<CountryCodeMobileField> {
  late CountryCode _selectedCountry;

  @override
  void initState() {
    super.initState();
    _selectedCountry = widget.initialCountry;
  }

  /// The country currently selected in the prefix selector.
  CountryCode get currentCountry => _selectedCountry;

  /// The canonical E.164 value (e.g. `+919876543210`) for the current input,
  /// or `null` when the input is not a valid mobile number for
  /// [currentCountry]. For India this reuses the strict existing
  /// normalization.
  String? get canonicalValue =>
      canonicalizeMobile(widget.controller.text, _selectedCountry);

  Future<void> _openCountryPicker() async {
    final picked = await showCountryCodePicker(context, _selectedCountry);
    if (picked == null || picked == _selectedCountry) return;
    setState(() => _selectedCountry = picked);
    widget.onCountryChanged?.call(picked);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isIndia = _selectedCountry.isoCode == 'IN';
    final prefixColor = widget.enabled
        ? theme.colorScheme.onSurfaceVariant
        : theme.disabledColor;
    return TextFormField(
      controller: widget.controller,
      enabled: widget.enabled,
      autofocus: widget.autofocus,
      keyboardType: TextInputType.phone,
      textInputAction: widget.onSubmitted == null
          ? TextInputAction.next
          : TextInputAction.done,
      maxLength: isIndia ? 10 : null,
      decoration: InputDecoration(
        labelText: widget.label,
        hintText: widget.hintText,
        helperText: widget.helperText,
        errorText: widget.errorText,
        prefix: InkWell(
          onTap: widget.enabled ? _openCountryPicker : null,
          borderRadius: BorderRadius.circular(8),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  _selectedCountry.flag,
                  style: const TextStyle(fontSize: 16),
                ),
                const SizedBox(width: 4),
                Text(
                  _selectedCountry.dialCodeWithPlus,
                  style: TextStyle(
                    color: prefixColor,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                Icon(
                  Icons.arrow_drop_down,
                  size: 20,
                  color: prefixColor,
                ),
              ],
            ),
          ),
        ),
        border: const OutlineInputBorder(),
        counterText: '',
      ),
      validator: (value) {
        final canonical = canonicalValue;
        if (canonical == null) {
          if (widget.optional && (value == null || value.trim().isEmpty)) {
            return widget.validator?.call(null);
          }
          return mobileNumberErrorMessage(_selectedCountry);
        }
        return widget.validator?.call(canonical);
      },
      onFieldSubmitted: widget.enabled && widget.onSubmitted != null
          ? (value) => widget.onSubmitted!(canonicalValue ?? '')
          : null,
    );
  }
}

/// Opens the searchable country picker bottom sheet.
///
/// Returns the selected [CountryCode], or `null` when dismissed without a
/// selection. The picker lists every bundled country; typing filters by name,
/// ISO code or dialing code via [CountryCodes.search].
Future<CountryCode?> showCountryCodePicker(
  BuildContext context,
  CountryCode initial,
) {
  return showModalBottomSheet<CountryCode>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (context) => _CountryCodePickerSheet(initial: initial),
  );
}

class _CountryCodePickerSheet extends StatefulWidget {
  const _CountryCodePickerSheet({required this.initial});

  final CountryCode initial;

  @override
  State<_CountryCodePickerSheet> createState() =>
      _CountryCodePickerSheetState();
}

class _CountryCodePickerSheetState extends State<_CountryCodePickerSheet> {
  final TextEditingController _searchController = TextEditingController();
  String _query = '';

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final results = CountryCodes.search(_query);
    return Padding(
      padding: EdgeInsets.only(
        bottom: MediaQuery.of(context).viewInsets.bottom,
      ),
      child: SizedBox(
        height: MediaQuery.of(context).size.height * 0.7,
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Select country',
                    style: theme.textTheme.titleMedium
                        ?.copyWith(fontWeight: FontWeight.w600),
                  ),
                  const SizedBox(height: 12),
                  TextField(
                    controller: _searchController,
                    autofocus: true,
                    decoration: InputDecoration(
                      hintText: 'Search country or dialing code',
                      prefixIcon: const Icon(Icons.search),
                      isDense: true,
                      border: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(12),
                      ),
                    ),
                    onChanged: (value) => setState(() => _query = value),
                  ),
                ],
              ),
            ),
            Expanded(
              child: ListView.builder(
                itemCount: results.length,
                itemBuilder: (context, index) {
                  final country = results[index];
                  final selected =
                      country.isoCode == widget.initial.isoCode;
                  return ListTile(
                    leading: Text(
                      country.flag,
                      style: const TextStyle(fontSize: 20),
                    ),
                    title: Text(country.name),
                    subtitle: Text(country.dialCodeWithPlus),
                    selected: selected,
                    trailing: selected
                        ? Icon(
                            Icons.check,
                            color: theme.colorScheme.primary,
                          )
                        : null,
                    onTap: () => Navigator.of(context).pop(country),
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }
}
