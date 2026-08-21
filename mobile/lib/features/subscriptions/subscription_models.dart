import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';

enum SubscriptionStatus {
  paymentPending,
  active,
  paused,
  cancelled,
  completed,
  paymentFailed,
  unknown;

  factory SubscriptionStatus.fromApi(String value) =>
      switch (value.toLowerCase()) {
        'paymentpending' => SubscriptionStatus.paymentPending,
        'active' => SubscriptionStatus.active,
        'paused' => SubscriptionStatus.paused,
        'cancelled' => SubscriptionStatus.cancelled,
        'completed' => SubscriptionStatus.completed,
        'paymentfailed' => SubscriptionStatus.paymentFailed,
        _ => SubscriptionStatus.unknown,
      };

  String get label => switch (this) {
    SubscriptionStatus.paymentPending => 'Payment Pending',
    SubscriptionStatus.active => 'Active',
    SubscriptionStatus.paused => 'Paused',
    SubscriptionStatus.cancelled => 'Cancelled',
    SubscriptionStatus.completed => 'Completed',
    SubscriptionStatus.paymentFailed => 'Payment failed',
    SubscriptionStatus.unknown => 'Unknown',
  };

  bool get canPause => this == SubscriptionStatus.active;
  bool get canResume => this == SubscriptionStatus.paused;
  bool get canCancel =>
      this == SubscriptionStatus.paymentPending ||
      this == SubscriptionStatus.active ||
      this == SubscriptionStatus.paused;
  bool get canUpdate =>
      this == SubscriptionStatus.active || this == SubscriptionStatus.paused;
}

enum SubscriptionDeliveryStatus {
  scheduled,
  skipped,
  failed,
  delivered,
  cancelled,
  unknown;

  factory SubscriptionDeliveryStatus.fromApi(String value) =>
      switch (value.toLowerCase()) {
        'scheduled' => SubscriptionDeliveryStatus.scheduled,
        'skipped' => SubscriptionDeliveryStatus.skipped,
        'failed' => SubscriptionDeliveryStatus.failed,
        'delivered' => SubscriptionDeliveryStatus.delivered,
        'cancelled' => SubscriptionDeliveryStatus.cancelled,
        _ => SubscriptionDeliveryStatus.unknown,
      };

  String get label => switch (this) {
    SubscriptionDeliveryStatus.scheduled => 'Scheduled',
    SubscriptionDeliveryStatus.skipped => 'Skipped',
    SubscriptionDeliveryStatus.failed => 'Failed',
    SubscriptionDeliveryStatus.delivered => 'Delivered',
    SubscriptionDeliveryStatus.cancelled => 'Cancelled',
    SubscriptionDeliveryStatus.unknown => 'Unknown',
  };

  bool get canSkip => this == SubscriptionDeliveryStatus.scheduled;
}

enum DeliveryWeekday {
  monday('Monday', 'Mon'),
  tuesday('Tuesday', 'Tue'),
  wednesday('Wednesday', 'Wed'),
  thursday('Thursday', 'Thu'),
  friday('Friday', 'Fri'),
  saturday('Saturday', 'Sat'),
  sunday('Sunday', 'Sun');

  const DeliveryWeekday(this.apiValue, this.shortLabel);

  final String apiValue;
  final String shortLabel;

  factory DeliveryWeekday.fromApi(String value) =>
      DeliveryWeekday.values.firstWhere(
        (day) => day.apiValue.toLowerCase() == value.toLowerCase(),
        orElse: () => DeliveryWeekday.monday,
      );
}

class CreateSubscriptionRequest {
  const CreateSubscriptionRequest({
    required this.productId,
    required this.addressId,
    required this.quantity,
    required this.startDate,
    required this.deliveryDays,
    required this.totalEntitlement,
    required this.paymentMethod,
  });

  final String productId;
  final String addressId;
  final double quantity;
  final DateTime startDate;
  final Set<DeliveryWeekday> deliveryDays;
  final int totalEntitlement;
  final PaymentMethod paymentMethod;

  Map<String, dynamic> toJson() => {
    'productId': productId,
    'addressId': addressId,
    'quantity': quantity,
    'startDate': formatApiDate(startDate),
    'deliveryDays': deliveryDays
        .map((day) => day.apiValue)
        .toList(growable: false),
    'totalEntitlement': totalEntitlement,
    'paymentMethod': paymentMethod.apiValue,
  };
}

class UpdateSubscriptionRequest {
  const UpdateSubscriptionRequest({
    this.quantity,
    this.addressId,
    this.deliveryDays,
  });

  final double? quantity;
  final String? addressId;
  final Set<DeliveryWeekday>? deliveryDays;

  Map<String, dynamic> toJson() => {
    'quantity': quantity,
    'addressId': addressId,
    'deliveryDays': deliveryDays
        ?.map((day) => day.apiValue)
        .toList(growable: false),
  };
}

class SubscriptionSchedule {
  const SubscriptionSchedule({required this.dayOfWeek});

  factory SubscriptionSchedule.fromJson(Map<String, dynamic> json) =>
      SubscriptionSchedule(
        dayOfWeek: DeliveryWeekday.fromApi(json['dayOfWeek'] as String),
      );

  final DeliveryWeekday dayOfWeek;
}

class SubscriptionDelivery {
  const SubscriptionDelivery({
    required this.publicId,
    required this.scheduledDate,
    required this.quantity,
    required this.status,
    required this.branchId,
    required this.branchCode,
    required this.branchName,
    required this.address,
    required this.statusChangedAt,
  });

  factory SubscriptionDelivery.fromJson(Map<String, dynamic> json) =>
      SubscriptionDelivery(
        publicId: json['publicId'] as String,
        scheduledDate: DateTime.parse(json['scheduledDate'] as String),
        quantity: (json['quantity'] as num).toDouble(),
        status: SubscriptionDeliveryStatus.fromApi(json['status'] as String),
        branchId: json['branchId'] as String,
        branchCode: json['branchCode'] as String,
        branchName: json['branchName'] as String,
        address: json['address'] as String,
        statusChangedAt: _optionalDateTime(json['statusChangedAt']),
      );

  final String publicId;
  final DateTime scheduledDate;
  final double quantity;
  final SubscriptionDeliveryStatus status;
  final String branchId;
  final String branchCode;
  final String branchName;
  final String address;
  final DateTime? statusChangedAt;
}

class SubscriptionDetails {
  const SubscriptionDetails({
    required this.publicId,
    required this.status,
    required this.productId,
    required this.productSku,
    required this.productName,
    required this.unitOfMeasure,
    required this.quantity,
    required this.unitPrice,
    required this.payableAmount,
    required this.startDate,
    required this.endDate,
    required this.totalEntitlement,
    required this.usedEntitlement,
    required this.remainingEntitlement,
    required this.addressId,
    required this.address,
    required this.branchId,
    required this.branchCode,
    required this.branchName,
    required this.schedules,
    required this.activatedAt,
    required this.pausedAt,
    required this.cancelledAt,
    required this.completedAt,
    required this.createdAt,
  });

  factory SubscriptionDetails.fromJson(Map<String, dynamic> json) =>
      SubscriptionDetails(
        publicId: json['publicId'] as String,
        status: SubscriptionStatus.fromApi(json['status'] as String),
        productId: json['productId'] as String,
        productSku: json['productSku'] as String,
        productName: json['productName'] as String,
        unitOfMeasure: json['unitOfMeasure'] as String,
        quantity: (json['quantity'] as num).toDouble(),
        unitPrice: (json['unitPrice'] as num).toDouble(),
        payableAmount: (json['payableAmount'] as num).toDouble(),
        startDate: DateTime.parse(json['startDate'] as String),
        endDate: DateTime.parse(json['endDate'] as String),
        totalEntitlement: json['totalEntitlement'] as int,
        usedEntitlement: json['usedEntitlement'] as int,
        remainingEntitlement: json['remainingEntitlement'] as int,
        addressId: json['addressId'] as String,
        address: json['address'] as String,
        branchId: json['branchId'] as String,
        branchCode: json['branchCode'] as String,
        branchName: json['branchName'] as String,
        schedules: (json['schedules'] as List<dynamic>)
            .cast<Map<String, dynamic>>()
            .map(SubscriptionSchedule.fromJson)
            .toList(growable: false),
        activatedAt: _optionalDateTime(json['activatedAt']),
        pausedAt: _optionalDateTime(json['pausedAt']),
        cancelledAt: _optionalDateTime(json['cancelledAt']),
        completedAt: _optionalDateTime(json['completedAt']),
        createdAt: DateTime.parse(json['createdAt'] as String),
      );

  final String publicId;
  final SubscriptionStatus status;
  final String productId;
  final String productSku;
  final String productName;
  final String unitOfMeasure;
  final double quantity;
  final double unitPrice;
  final double payableAmount;
  final DateTime startDate;
  final DateTime endDate;
  final int totalEntitlement;
  final int usedEntitlement;
  final int remainingEntitlement;
  final String addressId;
  final String address;
  final String branchId;
  final String branchCode;
  final String branchName;
  final List<SubscriptionSchedule> schedules;
  final DateTime? activatedAt;
  final DateTime? pausedAt;
  final DateTime? cancelledAt;
  final DateTime? completedAt;
  final DateTime createdAt;

  String get formattedQuantity =>
      '${formatQuantity(quantity)} $unitOfMeasure per delivery';
  String get formattedPayableAmount => '₹${payableAmount.toStringAsFixed(2)}';
  String get scheduleLabel =>
      schedules.map((schedule) => schedule.dayOfWeek.shortLabel).join(', ');
  double get entitlementProgress => totalEntitlement == 0
      ? 0
      : (usedEntitlement / totalEntitlement).clamp(0, 1).toDouble();
}

class CreatedSubscription {
  const CreatedSubscription({
    required this.subscription,
    required this.payment,
  });

  factory CreatedSubscription.fromJson(Map<String, dynamic> json) =>
      CreatedSubscription(
        subscription: SubscriptionDetails.fromJson(
          json['subscription'] as Map<String, dynamic>,
        ),
        payment: PaymentDetails.fromJson(
          json['payment'] as Map<String, dynamic>,
        ),
      );

  final SubscriptionDetails subscription;
  final PaymentDetails payment;
}

String formatApiDate(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';

DateTime? _optionalDateTime(Object? value) =>
    value == null ? null : DateTime.parse(value as String);
