import 'package:doodh_direct_mobile/core/theme/doodh_theme.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'bank_export_controller.dart';
import 'bank_export_models.dart';

/// Stage 3: finance-only bank-transfer export workspace.
///
/// Batches claim approved manual-bank refunds and freeze beneficiary plus
/// company snapshots; money moves only when an item is confirmed paid
/// after the real bank transfer. Downloads carry full account numbers and
/// are audit-logged server-side.
class BankExportListScreen extends ConsumerStatefulWidget {
  const BankExportListScreen({super.key});

  @override
  ConsumerState<BankExportListScreen> createState() =>
      _BankExportListScreenState();
}

class _BankExportListScreenState extends ConsumerState<BankExportListScreen> {
  final Set<String> _selected = {};

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(bankExportControllerProvider.notifier).refresh(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(bankExportControllerProvider);
    final controller = ref.read(bankExportControllerProvider.notifier);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Bank transfer exports'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading ? null : () => controller.refresh(),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: _body(context, state, controller),
    );
  }

  Widget _body(
    BuildContext context,
    BankExportState state,
    BankExportController controller,
  ) {
    if (state.isUnauthorized) {
      return const StatePanel(
        icon: Icons.lock_outline,
        title: 'Not permitted',
        message:
            'Bank-transfer exports require the finance export permission.',
      );
    }
    if (state.isLoading && state.batches.isEmpty && state.candidates.isEmpty) {
      return const Center(child: CircularProgressIndicator());
    }
    if (state.errorMessage != null) {
      return StatePanel(
        icon: Icons.error_outline,
        title: 'Export failed',
        message: state.errorMessage!,
        action: FilledButton(
          onPressed: () => controller.refresh(),
          child: const Text('Retry'),
        ),
      );
    }

    final selectable =
        state.candidates.where((c) => c.companyBankConfigured).toList();

    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        const StatePanel(
          icon: Icons.info_outline,
          title: 'Generic bank file',
          message:
              'The CSV format is generic and has not been verified against any particular bank\u2019s upload specification. Confirm the layout with your bank before uploading.',
        ),
        const SizedBox(height: 12),
        Text('Eligible refunds', style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 4),
        if (selectable.isEmpty)
          const Text('No approved manual-bank refunds are awaiting export.')
        else ...[
          for (final candidate in selectable)
            CheckboxListTile(
              value: _selected.contains(candidate.ticketId),
              onChanged: (value) => setState(() {
                if (value == true) {
                  _selected.add(candidate.ticketId);
                } else {
                  _selected.remove(candidate.ticketId);
                }
              }),
              title: Text(
                '${candidate.requestNumber} · ${candidate.customerDisplayName}',
              ),
              subtitle: Text(
                '${candidate.reservedCurrency} ${candidate.reservedAmount.toStringAsFixed(2)}'
                '${candidate.orderNumber == null ? '' : ' · ${candidate.orderNumber}'}',
              ),
            ),
          const SizedBox(height: 8),
          FilledButton.icon(
            onPressed: state.isSaving || _selected.isEmpty
                ? null
                : () async {
                    final created = await controller.createBatch(
                      _selected.toList(),
                    );
                    if (created && context.mounted) {
                      setState(_selected.clear);
                    }
                  },
            icon: state.isSaving
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.create_new_folder_outlined),
            label: Text(
              'Claim ${_selected.isEmpty ? '' : '(${_selected.length}) '}into a batch',
            ),
          ),
        ],
        const SizedBox(height: 20),
        Text('Batches', style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 4),
        if (state.batches.isEmpty)
          const Text('No export batches yet.')
        else
          for (final batch in state.batches)
            Card(
              child: ListTile(
                leading: const Icon(Icons.account_balance_outlined),
                title: Text('${batch.batchNumber} · ${batch.status}'),
                subtitle: Text(
                  '${batch.itemCount} items · ${batch.currency ?? ''} ${batch.totalAmount.toStringAsFixed(2)}',
                ),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => context.push(
                  '/finance/bank-exports/${batch.batchId}',
                ),
              ),
            ),
      ],
    );
  }
}

class BankExportDetailScreen extends ConsumerStatefulWidget {
  const BankExportDetailScreen({super.key, required this.batchId});

  final String batchId;

  @override
  ConsumerState<BankExportDetailScreen> createState() =>
      _BankExportDetailScreenState();
}

class _BankExportDetailScreenState
    extends ConsumerState<BankExportDetailScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref
          .read(bankExportControllerProvider.notifier)
          .selectBatch(widget.batchId),
    );
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(bankExportControllerProvider);
    final controller = ref.read(bankExportControllerProvider.notifier);
    final batch = state.selectedBatch;

    return Scaffold(
      appBar: AppBar(
        title: Text(batch?.batchNumber ?? 'Export batch'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: state.isLoading
                ? null
                : () => controller.selectBatch(widget.batchId),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: batch == null
          ? (state.isLoading
                ? const Center(child: CircularProgressIndicator())
                : StatePanel(
                    icon: Icons.error_outline,
                    title: 'Batch unavailable',
                    message:
                        state.errorMessage ?? 'The batch could not be loaded.',
                  ))
          : _detail(context, batch, state, controller),
    );
  }

  Widget _detail(
    BuildContext context,
    BankExportBatch batch,
    BankExportState state,
    BankExportController controller,
  ) {
    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        if (state.errorMessage != null)
          StatePanel(
            icon: Icons.error_outline,
            title: 'Action failed',
            message: state.errorMessage!,
          ),
        if (state.lastSavedFile != null)
          StatePanel(
            icon: Icons.check_circle_outline,
            title: 'File saved',
            message: state.lastSavedFile!,
          ),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            if (batch.status == 'Claimed')
              FilledButton.icon(
                onPressed: state.isSaving
                    ? null
                    : () => controller.generate(batch.batchId),
                icon: const Icon(Icons.picture_as_pdf_outlined),
                label: const Text('Generate file'),
              ),
            if (batch.status == 'Generated') ...[
              FilledButton.icon(
                onPressed: state.isSaving
                    ? null
                    : () => controller.download(batch.batchId),
                icon: const Icon(Icons.download_outlined),
                label: const Text('Download CSV'),
              ),
              OutlinedButton.icon(
                onPressed: state.isSaving
                    ? null
                    : () => controller.submit(batch.batchId),
                icon: const Icon(Icons.send_outlined),
                label: const Text('Mark submitted to bank'),
              ),
            ],
            if (batch.status == 'Submitted')
              FilledButton.icon(
                onPressed: state.isSaving
                    ? null
                    : () => controller.download(batch.batchId),
                icon: const Icon(Icons.download_outlined),
                label: const Text('Re-download CSV'),
              ),
            if (batch.status == 'Claimed' || batch.status == 'Generated')
              OutlinedButton.icon(
                onPressed: state.isSaving
                    ? null
                    : () => _askReason(context, 'Void batch', (reason) {
                        controller.voidBatch(batch.batchId, reason);
                      }),
                icon: const Icon(Icons.cancel_outlined),
                label: const Text('Void batch'),
              ),
          ],
        ),
        const SizedBox(height: 12),
        Text(
          'Source: ${batch.sourceAccountHolderName} · ${batch.sourceBankName} · ${batch.sourceMaskedAccountNumber} · ${batch.sourceIfsc}',
        ),
        const SizedBox(height: 12),
        for (final item in batch.items) _itemCard(context, item, controller),
      ],
    );
  }

  Widget _itemCard(
    BuildContext context,
    BankExportItem item,
    BankExportController controller,
  ) {
    final state = ref.watch(bankExportControllerProvider);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '${item.requestNumber} · ${item.status}',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            Text(
              '${item.customerDisplayName} · ${item.currency} ${item.amount.toStringAsFixed(2)}',
            ),
            Text(
              'Pays to ${item.beneficiaryAccountHolderName} · ${item.beneficiaryBankName} · ${item.beneficiaryMaskedAccountNumber} · IFSC ${item.beneficiaryIfsc}',
            ),
            if (item.transferReference != null)
              Text('UTR: ${item.transferReference}'),
            if (item.failureReason != null)
              Text('Bank outcome: ${item.failureReason}'),
            if (item.voidReason != null) Text('Voided: ${item.voidReason}'),
            if (item.releaseReason != null)
              Text('Released: ${item.releaseReason}'),
            if (!item.isTerminal) ...[
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  if (item.canConfirm)
                    FilledButton.icon(
                      onPressed: state.isSaving
                          ? null
                          : () => _askUtr(context, (utr) {
                              controller.confirmItem(item.itemId, utr, null);
                            }),
                      icon: const Icon(Icons.check_circle_outline),
                      label: const Text('Confirm paid'),
                    ),
                  if (item.status == 'Submitted')
                    OutlinedButton(
                      onPressed: state.isSaving
                          ? null
                          : () => _askReason(
                              context,
                              'Record bank failure',
                              (reason) {
                                controller.failItem(item.itemId, reason);
                              },
                            ),
                      child: const Text('Bank failed'),
                    ),
                  if (item.canRelease)
                    OutlinedButton(
                      onPressed: state.isSaving
                          ? null
                          : () => _askReason(context, 'Release reservation', (
                            reason,
                          ) {
                            controller.releaseItem(item.itemId, reason);
                          }),
                      child: const Text('Release'),
                    ),
                  if (item.status == 'Claimed' ||
                      item.status == 'Generated')
                    TextButton(
                      onPressed: state.isSaving
                          ? null
                          : () => _askReason(context, 'Void item', (reason) {
                              controller.voidItem(item.itemId, reason);
                            }),
                      child: const Text('Void'),
                    ),
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }

  Future<void> _askReason(
    BuildContext context,
    String title,
    void Function(String reason) onConfirm,
  ) {
    final editor = TextEditingController();
    return showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(title),
        content: TextField(
          controller: editor,
          maxLines: 3,
          decoration: const InputDecoration(
            labelText: 'Reason (audited)',
            hintText: 'Why is this disposition safe?',
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              final reason = editor.text.trim();
              if (reason.isEmpty) return;
              Navigator.of(dialogContext).pop();
              onConfirm(reason);
            },
            child: const Text('Confirm'),
          ),
        ],
      ),
    );
  }

  Future<void> _askUtr(
    BuildContext context,
    void Function(String utr) onConfirm,
  ) {
    final editor = TextEditingController();
    return showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Confirm bank transfer paid'),
        content: TextField(
          controller: editor,
          decoration: const InputDecoration(
            labelText: 'UTR / transfer reference (6–100 chars)',
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              final utr = editor.text.trim();
              if (utr.length < 6 || utr.length > 100) return;
              Navigator.of(dialogContext).pop();
              onConfirm(utr);
            },
            child: const Text('Confirm paid'),
          ),
        ],
      ),
    );
  }
}
