import 'package:flutter/material.dart';

import 'vacation_refund_models.dart';

/// Consolidated view of one end-of-subscription vacation-refund request.
///
/// The status banner keeps the three customer-facing states distinct:
/// requested and approved requests are explicitly *not* issued refunds —
/// only a completed request carries a wallet credit.
class VacationRefundCard extends StatelessWidget {
  const VacationRefundCard({super.key, required this.request});

  final VacationRefundRequest request;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    request.requestNumber,
                    style: theme.textTheme.titleMedium,
                  ),
                ),
                _StatusChip(status: request.status),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              _statusMessage,
              style: theme.textTheme.bodyMedium,
            ),
            const SizedBox(height: 12),
            _FactRow(
              label: 'Reason',
              value: request.reason,
            ),
            _FactRow(
              label: 'Skipped deliveries',
              value: '${request.occurrenceCount}',
            ),
            _FactRow(
              label: 'Refund amount',
              value:
                  '₹${request.totalAmount.toStringAsFixed(2)} ${request.currency}',
            ),
            _FactRow(
              label: 'Deduction',
              value:
                  '${request.deductionPercentage.toStringAsFixed(2)}% (vacation)',
            ),
            _FactRow(
              label: 'Per-delivery share (frozen)',
              value: '₹${request.grossPerOccurrence.toStringAsFixed(2)}',
            ),
            const SizedBox(height: 8),
            Text(
              'Included deliveries',
              style: theme.textTheme.titleSmall,
            ),
            const SizedBox(height: 4),
            ...request.occurrences.map(
              (occurrence) => Padding(
                padding: const EdgeInsets.symmetric(vertical: 2),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(
                        _formatDate(occurrence.scheduledDate),
                        style: theme.textTheme.bodyMedium,
                      ),
                    ),
                    Text(
                      '₹${occurrence.allocationAmount.toStringAsFixed(2)}',
                      style: theme.textTheme.bodyMedium,
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  String get _statusMessage => switch (request.status) {
    VacationRefundStatus.pending =>
      'Refund requested — under support review. No refund issued yet.',
    VacationRefundStatus.approved =>
      'Refund approved — pending completion. No refund issued yet.',
    VacationRefundStatus.completed =>
      'Refund completed — ₹${request.totalAmount.toStringAsFixed(2)} credited to your wallet.',
    VacationRefundStatus.rejected => 'Refund request rejected by support.',
    VacationRefundStatus.unknown => 'Refund request status unavailable.',
  };

  static String _formatDate(DateTime date) =>
      '${date.day.toString().padLeft(2, '0')} '
      '${_monthName(date.month)} ${date.year}';

  static String _monthName(int month) => const [
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ][month - 1];
}

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status});

  final VacationRefundStatus status;

  @override
  Widget build(BuildContext context) {
    final (background, foreground) = switch (status) {
      VacationRefundStatus.completed => (
        Colors.green.shade100,
        Colors.green.shade900,
      ),
      VacationRefundStatus.rejected => (
        Colors.red.shade100,
        Colors.red.shade900,
      ),
      VacationRefundStatus.approved => (
        Colors.blue.shade100,
        Colors.blue.shade900,
      ),
      _ => (Colors.amber.shade100, Colors.amber.shade900),
    };
    return Chip(
      label: Text(status.label),
      backgroundColor: background,
      labelStyle: TextStyle(color: foreground),
    );
  }
}

class _FactRow extends StatelessWidget {
  const _FactRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 180,
            child: Text(
              label,
              style: theme.textTheme.bodySmall,
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodyMedium,
            ),
          ),
        ],
      ),
    );
  }
}
