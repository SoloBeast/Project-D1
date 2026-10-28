import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:flutter_test/flutter_test.dart';

Map<String, dynamic> orderJson({Map<String, dynamic>? cancellation}) => {
  'publicId': 'order-1',
  'orderNumber': 'ORD/NEAR/000001',
  'type': 'OneTime',
  'status': 'Cancelled',
  'createdAt': '2026-08-20T02:41:00',
  'addressLabel': 'Home',
  'city': 'Bengaluru',
  'branchName': 'Near Branch',
  'items': const <Map<String, dynamic>>[],
  'subtotal': 200,
  'discountAmount': 0,
  'charges': const <Map<String, dynamic>>[],
  'chargesTotal': 0,
  'payableAmount': 200,
  'cancelledAt': '2026-08-20T02:41:00',
  'paymentPublicId': null,
  'paymentStatus': null,
  'gatewayPaymentId': null,
  'deliveryPublicId': null,
  'deliveryReferenceNumber': null,
  'deliveryStatus': null,
  'cancellation': cancellation,
};

void main() {
  group('OrderSummary cancellation', () {
    test('parses stored reason and comment', () {
      final order = OrderSummary.fromJson(
        orderJson(
          cancellation: {
            'reasonCode': 'ChangedMind',
            'comment': 'evening works better',
            'cancelledByUserId': 7,
            'cancelledAt': '2026-08-20T02:41:00',
          },
        ),
      );

      expect(order.cancellation, isNotNull);
      expect(order.cancellation!.reasonCode, 'ChangedMind');
      expect(order.cancellation!.comment, 'evening works better');
      expect(order.cancellation!.cancelledByUserId, 7);
    });

    test('absent cancellation stays null for historical orders', () {
      final order = OrderSummary.fromJson(orderJson());
      expect(order.cancellation, isNull);
    });

    test('reason list matches backend contract', () {
      expect(orderCancellationReasons, [
        'OrderedByMistake',
        'ChangedMind',
        'DuplicateOrder',
        'DeliveryTimeNoLongerSuitable',
        'Other',
      ]);
    });
  });

  group('SubscriptionDetails cancellation refund', () {
    Map<String, dynamic> subscriptionJson({
      Map<String, dynamic>? cancellationRefund,
    }) => {
      'publicId': 'sub-1',
      'status': 'Cancelled',
      'productId': 'prod-1',
      'productSku': 'MILK-001',
      'productName': 'Fresh Milk',
      'unitOfMeasure': 'litre',
      'quantity': 1,
      'unitPrice': 80,
      'payableAmount': 320,
      'startDate': '2026-08-17',
      'endDate': '2026-08-26',
      'totalEntitlement': 4,
      'usedEntitlement': 1,
      'remainingEntitlement': 3,
      'addressId': 'addr-1',
      'address': '1 Main Road',
      'branchId': 'br-1',
      'branchCode': 'MAIN',
      'branchName': 'Main Branch',
      'schedules': const <Map<String, dynamic>>[],
      'activatedAt': null,
      'pausedAt': null,
      'cancelledAt': '2026-08-16T07:30:00',
      'completedAt': null,
      'createdAt': '2026-08-15T07:30:00',
      'cancellationRefund': cancellationRefund,
    };

    test('parses completed refund summary', () {
      final details = SubscriptionDetails.fromJson(
        subscriptionJson(
          cancellationRefund: {
            'eligibleOccurrences': 3,
            'excludedOccurrences': 1,
            'refundedAmount': 240,
            'status': 'Completed',
          },
        ),
      );

      expect(details.cancellationRefund, isNotNull);
      expect(details.cancellationRefund!.eligibleOccurrences, 3);
      expect(details.cancellationRefund!.refundedAmount, 240);
      expect(details.cancellationRefund!.status, 'Completed');
    });

    test('absent refund stays null for pre-feature responses', () {
      final details = SubscriptionDetails.fromJson(subscriptionJson());
      expect(details.cancellationRefund, isNull);
    });
  });

  group('OrderCancellation refund snapshot (O2/O3)', () {
    test('parses frozen deduction snapshot when present', () {
      final order = OrderSummary.fromJson(
        orderJson(
          cancellation: {
            'reasonCode': 'ChangedMind',
            'comment': 'left early',
            'cancelledByUserId': 7,
            'cancelledAt': '2026-08-20T02:41:00',
            'refund': {
              'grossBasis': 200,
              'deductionPercentage': 25,
              'deductionAmount': 50,
              'refundedAmount': 150,
              'status': 'Completed',
            },
          },
        ),
      );

      expect(order.cancellation!.refund, isNotNull);
      expect(order.cancellation!.refund!.deductionPercentage, 25);
      expect(order.cancellation!.refund!.deductionAmount, 50);
      expect(order.cancellation!.refund!.refundedAmount, 150);
      expect(order.cancellation!.refund!.status, 'Completed');
    });

    test('absent refund stays null for pre-settlement responses', () {
      final order = OrderSummary.fromJson(
        orderJson(
          cancellation: {
            'reasonCode': 'ChangedMind',
            'comment': null,
            'cancelledByUserId': 7,
            'cancelledAt': '2026-08-20T02:41:00',
          },
        ),
      );
      expect(order.cancellation!.refund, isNull);
    });
  });

  group('CancelOrderPreview (O2/O3)', () {
    Map<String, dynamic> previewJson({bool pickup = false, bool can = true}) => {
      'canCancel': can,
      'blockReason': can ? null : 'Order has already been delivered.',
      'deliveryStatus': pickup ? 'PickedUp' : 'Assigned',
      'pickupOccurred': pickup,
      'deductionPercentage': pickup ? 25 : 0,
      'grossBasis': 200,
      'deductionAmount': pickup ? 50 : 0,
      'estimatedRefund': pickup ? 150 : 200,
      'eligiblePaidAmount': 200,
      'requiresConfirmation': pickup,
    };

    test('pre-pickup preview shows zero deduction and no confirmation', () {
      final preview = CancelOrderPreview.fromJson(previewJson());
      expect(preview.canCancel, isTrue);
      expect(preview.pickupOccurred, isFalse);
      expect(preview.deductionPercentage, 0);
      expect(preview.deductionAmount, 0);
      expect(preview.estimatedRefund, 200);
      expect(preview.requiresConfirmation, isFalse);
    });

    test('post-pickup preview carries server deduction and estimate', () {
      final preview = CancelOrderPreview.fromJson(previewJson(pickup: true));
      expect(preview.pickupOccurred, isTrue);
      expect(preview.requiresConfirmation, isTrue);
      expect(preview.deductionPercentage, 25);
      expect(preview.deductionAmount, 50);
      expect(preview.estimatedRefund, 150);
    });

    test('blocked preview preserves the authoritative reason', () {
      final preview = CancelOrderPreview.fromJson(
        previewJson(can: false),
      );
      expect(preview.canCancel, isFalse);
      expect(preview.blockReason, isNotNull);
    });
  });

  group('OrderSummary.canCancel (O2/O3 entry points)', () {
    OrderSummary summaryWith(String status) => OrderSummary.fromJson({
      ...orderJson(),
      'status': status,
      'cancellation': null,
    });

    test('confirmed, assigned and out-for-delivery stay cancellable', () {
      expect(summaryWith('Confirmed').canCancel, isTrue);
      expect(summaryWith('Assigned').canCancel, isTrue);
      expect(summaryWith('OutForDelivery').canCancel, isTrue);
    });

    test('delivered, failed and cancelled route to help path', () {
      expect(summaryWith('Delivered').canCancel, isFalse);
      expect(summaryWith('Failed').canCancel, isFalse);
      expect(summaryWith('Cancelled').canCancel, isFalse);
      expect(summaryWith('PendingPayment').canCancel, isFalse);
    });
  });
}
