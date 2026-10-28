import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/bank_exports/bank_export_controller.dart';
import 'package:doodh_direct_mobile/features/bank_exports/bank_export_models.dart';
import 'package:doodh_direct_mobile/features/bank_exports/bank_export_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('bank export controller', () {
    test('refresh loads candidates and batches', () async {
      final repository = _FakeBankExportRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);

      await container.read(bankExportControllerProvider.notifier).refresh();

      final state = container.read(bankExportControllerProvider);
      expect(state.candidates, hasLength(1));
      expect(state.candidates.single.requestNumber, 'RR-1');
      expect(state.batches, hasLength(1));
      expect(state.batches.single.batchNumber, 'RBE/000001');
      expect(state.isLoading, isFalse);
    });

    test('createBatch claims tickets and reloads', () async {
      final repository = _FakeBankExportRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(bankExportControllerProvider.notifier);

      expect(await controller.createBatch(['ticket-1']), isTrue);
      expect(repository.createdTicketIds, ['ticket-1']);
      expect(
        container.read(bankExportControllerProvider).selectedBatch?.batchId,
        'batch-9',
      );
    });

    test('confirmItem delegates the UTR and reloads the batch', () async {
      final repository = _FakeBankExportRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(bankExportControllerProvider.notifier);
      await controller.refresh();
      await controller.selectBatch('batch-1');

      expect(await controller.confirmItem('item-1', 'UTR123456', null), isTrue);
      expect(repository.confirmedUtr, 'UTR123456');
      expect(
        container
            .read(bankExportControllerProvider)
            .selectedBatch
            ?.items
            .single
            .status,
        'Paid',
      );
    });

    test('screens never receive full account numbers', () async {
      final repository = _FakeBankExportRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      await container.read(bankExportControllerProvider.notifier).refresh();

      final batch = container.read(bankExportControllerProvider).batches.single;
      expect(batch.sourceMaskedAccountNumber, contains('9012'));
      expect(
        batch.items.single.beneficiaryMaskedAccountNumber,
        contains('1111'),
      );
    });
  });
}

final _candidate = BankExportCandidate(
  ticketId: 'ticket-1',
  requestNumber: 'RR-1',
  orderNumber: 'ORD-1',
  customerDisplayName: 'Asha Customer',
  reservedAmount: 200,
  reservedCurrency: 'INR',
  reservationCount: 1,
  companyBankConfigured: true,
);

BankExportItem _item(String status) => BankExportItem(
  itemId: 'item-1',
  ticketId: 'ticket-1',
  requestNumber: 'RR-1',
  orderNumber: 'ORD-1',
  customerDisplayName: 'Asha Customer',
  amount: 200,
  currency: 'INR',
  status: status,
  beneficiaryAccountHolderName: 'Asha Customer',
  beneficiaryBankName: 'Example National Bank',
  beneficiaryMaskedAccountNumber: 'Example National Bank ···· 1111',
  beneficiaryIfsc: 'EXMP0001234',
  claimedAt: '2026-10-21T10:00:00.000',
);

BankExportBatch _batch(String status) => BankExportBatch(
  batchId: 'batch-1',
  batchNumber: 'RBE/000001',
  status: status,
  createdAt: '2026-10-21T10:00:00.000',
  sourceAccountHolderName: 'DoodhDirect Foods Pvt Ltd',
  sourceBankName: 'State Bank of India',
  sourceMaskedAccountNumber: 'State Bank of India ···· 9012',
  sourceIfsc: 'SBIN0001234',
  itemCount: 1,
  totalAmount: 200,
  currency: 'INR',
  items: [_item(status == 'Claimed' ? 'Claimed' : 'Generated')],
);

class _FakeBankExportRepository extends BankExportRepository {
  _FakeBankExportRepository()
      : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  List<String>? createdTicketIds;
  String? confirmedUtr;
  BankExportBatch current = _batch('Generated');

  @override
  Future<List<BankExportCandidate>> eligible(String token) async =>
      [_candidate];

  @override
  Future<List<BankExportBatch>> batches(String token) async => [current];

  @override
  Future<BankExportBatch> createBatch(
    String token,
    List<String> ticketIds,
  ) async {
    createdTicketIds = ticketIds;
    current = BankExportBatch(
      batchId: 'batch-9',
      batchNumber: 'RBE/000009',
      status: 'Claimed',
      createdAt: '2026-10-21T10:00:00.000',
      sourceAccountHolderName: 'DoodhDirect Foods Pvt Ltd',
      sourceBankName: 'State Bank of India',
      sourceMaskedAccountNumber: 'State Bank of India ···· 9012',
      sourceIfsc: 'SBIN0001234',
      itemCount: 1,
      totalAmount: 200,
      currency: 'INR',
      items: [_item('Claimed')],
    );
    return current;
  }

  @override
  Future<BankExportBatch> getBatch(String token, String batchId) async =>
      current;

  @override
  Future<BankExportItem> confirmItem(
    String token,
    String itemId,
    String transferReference,
    String? remarks,
  ) async {
    confirmedUtr = transferReference;
    current = _batch('Submitted').copyPaid();
    return current.items.single;
  }
}

extension on BankExportBatch {
  BankExportBatch copyPaid() => BankExportBatch(
    batchId: batchId,
    batchNumber: batchNumber,
    status: 'Submitted',
    createdAt: createdAt,
    sourceAccountHolderName: sourceAccountHolderName,
    sourceBankName: sourceBankName,
    sourceMaskedAccountNumber: sourceMaskedAccountNumber,
    sourceIfsc: sourceIfsc,
    itemCount: itemCount,
    totalAmount: totalAmount,
    currency: currency,
    items: [
      BankExportItem(
        itemId: 'item-1',
        ticketId: 'ticket-1',
        requestNumber: 'RR-1',
        orderNumber: 'ORD-1',
        customerDisplayName: 'Asha Customer',
        amount: 200,
        currency: 'INR',
        status: 'Paid',
        beneficiaryAccountHolderName: 'Asha Customer',
        beneficiaryBankName: 'Example National Bank',
        beneficiaryMaskedAccountNumber: 'Example National Bank ···· 1111',
        beneficiaryIfsc: 'EXMP0001234',
        claimedAt: '2026-10-21T10:00:00.000',
        transferReference: 'UTR123456',
      ),
    ],
  );
}

Future<ProviderContainer> _authenticatedContainer(
  BankExportRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      bankExportRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'acct-1',
    displayName: 'Finance',
    email: null,
    mobile: null,
    roles: ['ACCOUNTANT'],
    permissions: ['REFUNDS.BANK_EXPORT'],
    branchIds: [1],
  ),
  accessToken: 'bank-export-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}
