import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:flutter/material.dart';

/// Customer-supplied reason for cancelling a confirmed order.
class CancelOrderInput {
  const CancelOrderInput({this.reasonCode, this.comment, this.confirmed = false});

  final String? reasonCode;
  final String? comment;

  /// True only when the customer explicitly confirmed the shown
  /// post-pickup deduction terms. Always false before pickup.
  final bool confirmed;
}

String _money(double value) => '₹${value.toStringAsFixed(2)}';

String _percent(double value) =>
    value == value.roundToDouble() ? '${value.round()}%' : '${value.toStringAsFixed(2)}%';

/// Shared O2/O3 cancellation dialog.
///
/// [preview] is the server-backed preview: authoritative pickup state, the
/// live Admin Centre deduction percentage, and the exact refund estimate.
/// The dialog never computes money itself and never confirms without a
/// valid preview. Returns the customer's input, or null when the customer
/// keeps the order (declines). Server-side errors are reported by the
/// caller, which may reopen the dialog with the preserved input.
Future<CancelOrderInput?> showCancelOrderDialog(
  BuildContext context, {
  String? initialReasonCode,
  String? initialComment,
  bool initialConfirmed = false,
  CancelOrderPreview? preview,
  String? serverError,
}) {
  String? reasonCode = initialReasonCode;
  final commentController = TextEditingController(text: initialComment ?? '');
  bool confirmed = initialConfirmed;
  String? validationError = serverError;
  return showDialog<CancelOrderInput>(
    context: context,
    builder: (dialogContext) => StatefulBuilder(
      builder: (context, setDialogState) {
        final afterPickup = preview?.pickupOccurred ?? false;
        final canCancel = preview?.canCancel ?? true;
        final blockReason = preview?.blockReason;
        return AlertDialog(
          title: const Text('Cancel order?'),
          content: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (!canCancel && blockReason != null) ...[
                  Text(blockReason),
                ] else ...[
                  if (preview != null) ...[
                    if (!afterPickup) ...[
                      Text(
                        'Eligible paid amount ${_money(preview.estimatedRefund)} '
                        'is refundable to your DoodhDirect Wallet with 0% deduction.',
                      ),
                    ] else ...[
                      Text(
                        'This item has already left for delivery. Cancelling now '
                        'retains ${_percent(preview.deductionPercentage)} '
                        '(${_money(preview.deductionAmount)}) and returns '
                        '${_money(preview.estimatedRefund)} to your Wallet.',
                        style: TextStyle(
                          color: Theme.of(context).colorScheme.error,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      const SizedBox(height: 8),
                      const Text(
                        'You can keep the order to continue delivery instead.',
                      ),
                    ],
                    const SizedBox(height: 12),
                  ] else ...[
                    const Text(
                      'This confirmed order will be cancelled and cannot be restored.',
                    ),
                    const SizedBox(height: 12),
                  ],
                  DropdownButtonFormField<String>(
                    initialValue: reasonCode,
                    decoration: const InputDecoration(
                      labelText: 'Reason',
                      hintText: 'Select a reason',
                    ),
                    items: orderCancellationReasons
                        .map(
                          (reason) => DropdownMenuItem(
                            value: reason,
                            child: Text(reason),
                          ),
                        )
                        .toList(growable: false),
                    onChanged: canCancel
                        ? (value) => setDialogState(() {
                              reasonCode = value;
                              validationError = null;
                            })
                        : null,
                  ),
                  const SizedBox(height: 12),
                  TextField(
                    controller: commentController,
                    maxLength: 500,
                    maxLines: 3,
                    enabled: canCancel,
                    decoration: const InputDecoration(
                      labelText: 'Comment',
                      hintText: 'Tell us why (required for Other)',
                    ),
                    onChanged: (_) => setDialogState(() {
                      validationError = null;
                    }),
                  ),
                  if (afterPickup && canCancel) ...[
                    const SizedBox(height: 8),
                    CheckboxListTile(
                      contentPadding: EdgeInsets.zero,
                      controlAffinity: ListTileControlAffinity.leading,
                      value: confirmed,
                      onChanged: (value) => setDialogState(() {
                        confirmed = value ?? false;
                        validationError = null;
                      }),
                      title: Text(
                        'I understand ${_percent(preview!.deductionPercentage)} '
                        'will be retained and I confirm cancellation.',
                      ),
                    ),
                  ],
                ],
                if (validationError != null) ...[
                  const SizedBox(height: 8),
                  Text(
                    validationError!,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ],
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(dialogContext),
              child: Text(afterPickup && canCancel ? 'Continue delivery' : 'Keep order'),
            ),
            if (canCancel)
              FilledButton(
                onPressed: () {
                  final comment = commentController.text.trim();
                  if ((reasonCode == null || reasonCode!.isEmpty) &&
                      comment.isEmpty) {
                    setDialogState(() {
                      validationError =
                          'Provide a reason or a comment to cancel.';
                    });
                    return;
                  }
                  if (reasonCode == 'Other' && comment.isEmpty) {
                    setDialogState(() {
                      validationError =
                          'A comment is required when the reason is Other.';
                    });
                    return;
                  }
                  if (afterPickup && !confirmed) {
                    setDialogState(() {
                      validationError =
                          'Confirm the deduction to cancel after pickup, or choose Continue delivery.';
                    });
                    return;
                  }
                  Navigator.pop(
                    dialogContext,
                    CancelOrderInput(
                      reasonCode: reasonCode,
                      comment: comment.isEmpty ? null : comment,
                      confirmed: afterPickup ? confirmed : false,
                    ),
                  );
                },
                child: Text(afterPickup ? 'Confirm cancellation' : 'Cancel order'),
              ),
          ],
        );
      },
    ),
  ).whenComplete(commentController.dispose);
}
