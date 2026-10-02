import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Customer vacation selection: an inclusive From/To date range that the
/// backend resolves to per-occurrence skips across all of the customer's
/// eligible subscriptions. The screen never implies every date will be
/// skipped until the server result says so.
class AddVacationScreen extends ConsumerStatefulWidget {
  const AddVacationScreen({this.initialFromDate, super.key});

  final DateTime? initialFromDate;

  @override
  ConsumerState<AddVacationScreen> createState() => _AddVacationScreenState();
}

class _AddVacationScreenState extends ConsumerState<AddVacationScreen> {
  DateTime? _fromDate;
  DateTime? _toDate;
  String? _validationMessage;
  VacationResult? _result;

  @override
  void initState() {
    super.initState();
    _fromDate = widget.initialFromDate == null
        ? null
        : _dateOnly(widget.initialFromDate!);
  }

  DateTime _dateOnly(DateTime value) => DateTime(value.year, value.month, value.day);

  DateTime get _today => _dateOnly(DateTime.now());

  bool get _rangeComplete => _fromDate != null && _toDate != null;

  String? get _clientValidationMessage {
    if (_fromDate == null || _toDate == null) return null;
    if (_toDate!.isBefore(_fromDate!)) {
      return 'The end date cannot be before the start date.';
    }
    if (_toDate!.isAfter(_fromDate!.add(const Duration(days: 365)))) {
      return 'The vacation range cannot exceed 366 days.';
    }
    return null;
  }

  Future<void> _pickDate({required bool isFrom}) async {
    final initial = (isFrom ? _fromDate : _toDate) ??
        _fromDate ??
        _today;
    final firstDate = isFrom ? _today : (_fromDate ?? _today);
    final picked = await showDatePicker(
      context: context,
      initialDate: initial.isBefore(firstDate) ? firstDate : initial,
      firstDate: firstDate,
      lastDate: _today.add(const Duration(days: 730)),
      helpText: isFrom ? 'Select start date' : 'Select end date',
    );
    if (picked == null) return;
    setState(() {
      _result = null;
      if (isFrom) {
        _fromDate = _dateOnly(picked);
        if (_toDate != null && _toDate!.isBefore(_fromDate!)) {
          _toDate = _fromDate;
        }
      } else {
        _toDate = _dateOnly(picked);
      }
      _validationMessage = _clientValidationMessage;
    });
  }

  Future<void> _save() async {
    final message = _clientValidationMessage;
    if (message != null) {
      setState(() => _validationMessage = message);
      return;
    }
    final result = await ref
        .read(subscriptionControllerProvider.notifier)
        .createVacation(
          CreateVacationRequest(fromDate: _fromDate!, toDate: _toDate!),
        );
    if (!mounted) return;
    if (result == null) {
      // The controller already surfaced the error via the shared state.
      return;
    }
    setState(() => _result = result);
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(subscriptionControllerProvider);
    final body = _result != null ? _buildResult(context, _result!) : _buildForm(context, state);
    return Scaffold(
      appBar: AppBar(
        title: const Text('Add Vacation'),
        actions: [
          IconButton(
            tooltip: 'Refresh calendar',
            onPressed: state.isLoading
                ? null
                : () async {
                    for (final subscription in state.subscriptions) {
                      await ref
                          .read(subscriptionControllerProvider.notifier)
                          .loadCalendar(subscription.publicId);
                    }
                  },
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: body,
    );
  }

  Widget _buildForm(BuildContext context, SubscriptionState state) {
    final affectedCount = state.subscriptions.isEmpty
        ? null
        : null; // Preview is informational only; server result is authoritative.
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: DoodhSpacing.pagePadding,
      children: [
        DoodhCard(
          semanticLabel: 'Set vacation period',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _DateField(
                key: const ValueKey('vacation-from-date'),
                label: 'From Date',
                value: _fromDate,
                onTap: state.isSaving ? null : () => _pickDate(isFrom: true),
              ),
              const SizedBox(height: DoodhSpacing.md),
              _DateField(
                key: const ValueKey('vacation-to-date'),
                label: 'To Date',
                value: _toDate,
                onTap: state.isSaving ? null : () => _pickDate(isFrom: false),
              ),
              if (_validationMessage != null) ...[
                const SizedBox(height: DoodhSpacing.md),
                DoodhStatusPill(
                  key: const ValueKey('vacation-validation'),
                  label: _validationMessage!,
                  tone: DoodhStatusTone.warning,
                ),
              ],
              const SizedBox(height: DoodhSpacing.lg),
              FilledButton(
                key: const ValueKey('vacation-save'),
                onPressed: (state.isSaving || !_rangeComplete)
                    ? null
                    : _save,
                child: state.isSaving
                    ? const SizedBox(
                        width: 20,
                        height: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('SAVE VACATION'),
              ),
              const SizedBox(height: DoodhSpacing.md),
              Text(
                'No delivery would be made in your vacation period.',
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: DoodhColors.muted,
                    ),
              ),
              if (affectedCount != null) const SizedBox.shrink(),
            ],
          ),
        ),
        if (state.errorMessage != null) ...[
          const SizedBox(height: DoodhSpacing.md),
          ErrorStatePanel(
            message: state.errorMessage!,
            onRetry: () => _save(),
          ),
        ],
      ],
    );
  }

  Widget _buildResult(BuildContext context, VacationResult result) {
    final ineligible = result.ineligible;
    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: DoodhSpacing.pagePadding,
      children: [
        DoodhCard(
          semanticLabel:
              'Vacation saved from ${_formatDate(result.fromDate)} to ${_formatDate(result.toDate)}',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Icon(
                result.skippedCount > 0
                    ? Icons.beach_access_outlined
                    : Icons.info_outline,
                size: 40,
                color: DoodhColors.tealDark,
              ),
              const SizedBox(height: DoodhSpacing.sm),
              Text(
                result.skippedCount > 0
                    ? '${result.skippedCount} '
                        '${result.skippedCount == 1 ? 'delivery' : 'deliveries'} skipped'
                    : 'No deliveries were scheduled in this period',
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w800,
                    ),
              ),
              const SizedBox(height: DoodhSpacing.xs),
              Text(
                '${_formatDate(result.fromDate)} → ${_formatDate(result.toDate)}',
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodyMedium,
              ),
              const SizedBox(height: DoodhSpacing.md),
              for (final item in result.skippedDates)
                Padding(
                  padding: const EdgeInsets.only(bottom: DoodhSpacing.xs),
                  child: Row(
                    children: [
                      const Icon(
                        Icons.event_busy_outlined,
                        size: 16,
                        color: DoodhColors.coral,
                      ),
                      const SizedBox(width: DoodhSpacing.xs),
                      Expanded(
                        child: Text(
                          '${_formatDate(item.date)} · ${item.productName} · ${item.slot.apiValue}',
                        ),
                      ),
                    ],
                  ),
                ),
            ],
          ),
        ),
        if (ineligible.isNotEmpty) ...[
          const SizedBox(height: DoodhSpacing.md),
          DoodhCard(
            semanticLabel: 'Dates that could not be skipped',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${ineligible.length} '
                  '${ineligible.length == 1 ? 'date could not be skipped' : 'dates could not be skipped'}',
                  style: Theme.of(context).textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                ),
                const SizedBox(height: DoodhSpacing.sm),
                for (final item in ineligible)
                  Padding(
                    padding: const EdgeInsets.only(bottom: DoodhSpacing.xs),
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Icon(
                          item.reason == VacationIneligibleReason.deliveryPrepared
                              ? Icons.local_shipping_outlined
                              : Icons.schedule_outlined,
                          size: 16,
                          color: DoodhColors.goldAccent,
                        ),
                        const SizedBox(width: DoodhSpacing.xs),
                        Expanded(
                          child: Text(
                            '${_formatDate(item.date)} · ${item.productName} · ${item.reason.label}',
                          ),
                        ),
                      ],
                    ),
                  ),
              ],
            ),
          ),
        ],
        const SizedBox(height: DoodhSpacing.lg),
        FilledButton(
          key: const ValueKey('vacation-done'),
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Done'),
        ),
      ],
    );
  }

  String _formatDate(DateTime value) =>
      '${value.day.toString().padLeft(2, '0')}/'
      '${value.month.toString().padLeft(2, '0')}/'
      '${value.year.toString().padLeft(4, '0')}';
}

class _DateField extends StatelessWidget {
  const _DateField({
    super.key,
    required this.label,
    required this.value,
    required this.onTap,
  });

  final String label;
  final DateTime? value;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => InkWell(
        borderRadius: DoodhRadii.mdRadius,
        onTap: onTap,
        child: InputDecorator(
          isEmpty: value == null,
          decoration: InputDecoration(
            labelText: label,
            border: const OutlineInputBorder(),
            suffixIcon: const Icon(Icons.calendar_today_outlined),
          ),
          child: Text(
            value == null ? '' : _format(value!),
            style: Theme.of(context).textTheme.bodyLarge,
          ),
        ),
      );

  static String _format(DateTime value) =>
      '${value.day.toString().padLeft(2, '0')}/'
      '${value.month.toString().padLeft(2, '0')}/'
      '${value.year.toString().padLeft(4, '0')}';
}
