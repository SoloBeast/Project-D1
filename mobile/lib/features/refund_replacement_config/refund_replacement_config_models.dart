/// Refund/replacement request window bounds enforced by the backend
/// (`RefundReplacementConfigurationService`). The window is expressed in whole
/// hours and the backend rejects values outside this range with a
/// `windowHours` field error.
const int kRefundReplacementMinimumWindowHours = 1;
const int kRefundReplacementMaximumWindowHours = 24 * 365; // 8760

/// Backend default applied when no `RefundReplacement.WindowHours` row exists.
const int kRefundReplacementDefaultWindowHours = 48;

/// Delivery-fail customer-choice window bounds enforced by the backend
/// (`RefundReplacementConfigurationService`). Whole hours; the backend rejects
/// values outside this range with a `deliveryFailWindowHours` field error.
const int kDeliveryFailMinimumWindowHours = 1;
const int kDeliveryFailMaximumWindowHours = 24 * 365; // 8760

/// Delivery-fail automatic-refund deduction bounds enforced by the backend.
/// Percentage with up to two decimal places; the backend rounds AwayFromZero
/// and rejects values outside this range with a
/// `deliveryFailDeductionPercentage` field error.
const double kDeliveryFailMinimumDeductionPercentage = 0;
const double kDeliveryFailMaximumDeductionPercentage = 100;

/// Backend defaults applied when the delivery-fail rows do not exist yet.
/// Display fallbacks only: the backend response is the source of truth.
const int kDeliveryFailDefaultWindowHours = 24;
const double kDeliveryFailDefaultDeductionPercentage = 10;

/// Refund/replacement request window configuration visible to
/// Setup → Refund / Replacement.
///
/// Mirrors the backend `RefundReplacementConfigurationResult`
/// (`{ windowHours, deliveryFailWindowHours, deliveryFailDeductionPercentage,
/// status }`). The windows are server-authoritative: the deadline for a
/// normal request is computed by the backend as
/// `Delivery.CompletedAt + WindowHours` (India-local wall clock), while the
/// delivery-fail choice deadline is `FailedAt + DeliveryFailWindowHours`.
class RefundReplacementConfiguration {
  const RefundReplacementConfiguration({
    required this.windowHours,
    required this.deliveryFailWindowHours,
    required this.deliveryFailDeductionPercentage,
    required this.status,
  });

  factory RefundReplacementConfiguration.fromJson(Map<String, dynamic> json) =>
      RefundReplacementConfiguration(
        windowHours:
            _intFromJson(json['windowHours']) ??
            kRefundReplacementDefaultWindowHours,
        deliveryFailWindowHours:
            _intFromJson(json['deliveryFailWindowHours']) ??
            kDeliveryFailDefaultWindowHours,
        deliveryFailDeductionPercentage:
            _doubleFromJson(json['deliveryFailDeductionPercentage']) ??
            kDeliveryFailDefaultDeductionPercentage,
        status: json['status'] as String? ?? 'Not Configured',
      );

  final int windowHours;
  final int deliveryFailWindowHours;
  final double deliveryFailDeductionPercentage;
  final String status;

  /// Mirrors the backend `ToResult`: a positive window is configured.
  bool get configured => windowHours > 0;

  /// Returns a human-readable validation error when [hours] is outside the
  /// backend-accepted range, or `null` when it is valid.
  static String? validateWindowHours(int hours) {
    if (hours < kRefundReplacementMinimumWindowHours ||
        hours > kRefundReplacementMaximumWindowHours) {
      return 'Enter a window between $kRefundReplacementMinimumWindowHours '
          'and $kRefundReplacementMaximumWindowHours hours.';
    }
    return null;
  }

  /// Returns a human-readable validation error when [hours] is outside the
  /// backend-accepted delivery-fail window range, or `null` when valid.
  static String? validateDeliveryFailWindowHours(int hours) {
    if (hours < kDeliveryFailMinimumWindowHours ||
        hours > kDeliveryFailMaximumWindowHours) {
      return 'Enter a window between $kDeliveryFailMinimumWindowHours '
          'and $kDeliveryFailMaximumWindowHours hours.';
    }
    return null;
  }

  /// Returns a human-readable validation error when [percentage] is outside
  /// the backend-accepted 0..100 deduction range, or `null` when valid.
  static String? validateDeliveryFailDeductionPercentage(double percentage) {
    if (percentage.isNaN ||
        percentage < kDeliveryFailMinimumDeductionPercentage ||
        percentage > kDeliveryFailMaximumDeductionPercentage) {
      return 'Enter a deduction between '
          '${kDeliveryFailMinimumDeductionPercentage.toStringAsFixed(0)} '
          'and ${kDeliveryFailMaximumDeductionPercentage.toStringAsFixed(0)}%.';
    }
    return null;
  }

  static int? _intFromJson(Object? value) {
    if (value is int) return value;
    if (value is num) return value.toInt();
    if (value is String) return int.tryParse(value);
    return null;
  }

  static double? _doubleFromJson(Object? value) {
    if (value is double) return value;
    if (value is num) return value.toDouble();
    if (value is String) return double.tryParse(value);
    return null;
  }
}

/// Payload for updating the refund/replacement configuration. An omitted
/// (null) field leaves the stored value unchanged on the backend.
class UpdateRefundReplacementConfigurationRequest {
  const UpdateRefundReplacementConfigurationRequest({
    this.windowHours,
    this.deliveryFailWindowHours,
    this.deliveryFailDeductionPercentage,
  });

  final int? windowHours;
  final int? deliveryFailWindowHours;
  final double? deliveryFailDeductionPercentage;

  Map<String, dynamic> toJson() => {
    if (windowHours != null) 'windowHours': windowHours,
    if (deliveryFailWindowHours != null)
      'deliveryFailWindowHours': deliveryFailWindowHours,
    if (deliveryFailDeductionPercentage != null)
      'deliveryFailDeductionPercentage': deliveryFailDeductionPercentage,
  };
}
