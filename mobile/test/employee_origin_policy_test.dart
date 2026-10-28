import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('employee-origin policy flag', () {
    test('detail parses a staff-owned ticket as employee-originated', () {
      final request = RefundReplacementRequest.fromJson({
        ..._ticketJson(),
        'isEmployeeOriginated': true,
      });

      expect(request.isEmployeeOriginated, isTrue);
    });

    test('detail defaults to customer-originated for older payloads', () {
      final request = RefundReplacementRequest.fromJson(_ticketJson());

      expect(request.isEmployeeOriginated, isFalse);
    });

    test('list row parses the flag and defaults it for older payloads', () {
      final flagged = RefundReplacementListItem.fromJson({
        ..._listRowJson(),
        'isEmployeeOriginated': true,
      });
      final legacy = RefundReplacementListItem.fromJson(_listRowJson());

      expect(flagged.isEmployeeOriginated, isTrue);
      expect(legacy.isEmployeeOriginated, isFalse);
    });
  });
}

Map<String, dynamic> _ticketJson() => {
      'requestId': 'req-1',
      'requestNumber': 'RR/000001',
      'deliveryId': 'del-1',
      'customerId': 7,
      'customerName': 'Staff Shopper',
      'customerMobile': '9888888888',
      'branchId': 1,
      'branchCode': 'MAIN',
      'branchName': 'Main Branch',
      'type': 'Refund',
      'source': 'DeliveryFailed',
      'status': 'Pending',
      'reason': 'The delivered items were unsatisfactory',
      'submittedAt': '2026-08-17T09:30:00.000',
      'proofImageRequired': false,
      'images': [],
      'settlementMethod': 'Unspecified',
    };

Map<String, dynamic> _listRowJson() => {
      'requestId': 'req-1',
      'requestNumber': 'RR/000001',
      'deliveryId': 'del-1',
      'customerId': 7,
      'customerName': 'Staff Shopper',
      'customerMobile': '9888888888',
      'branchId': 1,
      'branchCode': 'MAIN',
      'branchName': 'Main Branch',
      'type': 'Refund',
      'source': 'DeliveryFailed',
      'status': 'Pending',
      'reason': 'The delivered items were unsatisfactory',
      'submittedAt': '2026-08-17T09:30:00.000',
      'proofImageRequired': false,
      'imageCount': 0,
    };
