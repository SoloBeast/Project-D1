import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'bank_export_models.dart';

class BankExportDownload {
  const BankExportDownload({
    required this.bytes,
    required this.fileName,
    required this.disclaimer,
  });

  final Uint8List bytes;
  final String fileName;
  final String disclaimer;
}

class BankExportRepository {
  BankExportRepository({required this.api});

  final ApiClient api;

  static const _base = '/api/v1/refund-bank-exports';

  Future<List<BankExportCandidate>> eligible(String token) async =>
      ((await api.get('$_base/eligible', accessToken: token))['data']
                  as List<dynamic>? ??
              const [])
          .map((e) => BankExportCandidate.fromJson(e as Map<String, dynamic>))
          .toList();

  Future<List<BankExportBatch>> batches(String token) async =>
      ((await api.get('$_base/batches', accessToken: token))['data']
                  as List<dynamic>? ??
              const [])
          .map((e) => BankExportBatch.fromJson(e as Map<String, dynamic>))
          .toList();

  Future<BankExportBatch> createBatch(
    String token,
    List<String> ticketIds,
  ) async => BankExportBatch.fromJson(
    (await api.post(
          '$_base/batches',
          body: {
            'ticketIds': ticketIds,
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<BankExportBatch> getBatch(String token, String batchId) async =>
      BankExportBatch.fromJson(
        (await api.get('$_base/batches/$batchId', accessToken: token))['data']
            as Map<String, dynamic>,
      );

  Future<BankExportBatch> generate(String token, String batchId) async =>
      BankExportBatch.fromJson(
        (await api.post(
              '$_base/batches/$batchId/generate',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<BankExportDownload> download(String token, String batchId) async {
    final response = await api.getBytes(
      '$_base/batches/$batchId/download',
      accessToken: token,
    );
    return BankExportDownload(
      bytes: response.bytes,
      fileName: response.fileName ?? 'refund-bank-export.csv',
      disclaimer: '',
    );
  }

  Future<BankExportBatch> submit(String token, String batchId) async =>
      BankExportBatch.fromJson(
        (await api.post(
              '$_base/batches/$batchId/submit',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<BankExportBatch> voidBatch(
    String token,
    String batchId,
    String reason,
  ) async => BankExportBatch.fromJson(
    (await api.post(
          '$_base/batches/$batchId/void',
          body: {'reason': reason},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<BankExportItem> confirmItem(
    String token,
    String itemId,
    String transferReference,
    String? remarks,
  ) async => BankExportItem.fromJson(
    (await api.post(
          '$_base/items/$itemId/confirm-paid',
          body: {
            'transferReference': transferReference,
            if (remarks != null && remarks.isNotEmpty) 'remarks': remarks,
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<BankExportItem> failItem(
    String token,
    String itemId,
    String reason,
  ) async => BankExportItem.fromJson(
    (await api.post(
          '$_base/items/$itemId/fail',
          body: {'reason': reason},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<BankExportItem> voidItem(
    String token,
    String itemId,
    String reason,
  ) async => BankExportItem.fromJson(
    (await api.post(
          '$_base/items/$itemId/void',
          body: {'reason': reason},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<BankExportItem> releaseItem(
    String token,
    String itemId,
    String reason,
  ) async => BankExportItem.fromJson(
    (await api.post(
          '$_base/items/$itemId/release',
          body: {'reason': reason},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );
}
