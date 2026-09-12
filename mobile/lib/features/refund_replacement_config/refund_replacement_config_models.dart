/// Refund/replacement request window bounds enforced by the backend
/// (`RefundReplacementConfigurationService`). The window is expressed in whole
/// hours and the backend rejects values outside this range with a
/// `windowHours` field error.
const int kRefundReplacementMinimumWindowHours = 1;
const int kRefundReplacementMaximumWindowHours = 24 * 365; // 8760

/// Backend default applied when no `RefundReplacement.WindowHours` row exists.
const int kRefundReplacementDefaultWindowHours = 48;

/// Refund/replacement request window configuration visible to
/// Setup → Refund / Replacement.
///
/// Mirrors the backend `RefundReplacementConfigurationResult`
/// (`{ windowHours, status }`). The window is server-authoritative: the
/// deadline for a normal request is computed by the backend as
/// `Delivery.CompletedAt + WindowHours` (India-local wall clock).
class RefundReplacementConfiguration {
  const RefundReplacementConfiguration({
    required this.windowHours,
    required this.status,
  });

  factory RefundReplacementConfiguration.fromJson(Map<String, dynamic> json) =>
      RefundReplacementConfiguration(
        windowHours:
            _intFromJson(json['windowHours']) ??
            kRefundReplacementDefaultWindowHours,
        status: json['status'] as String? ?? 'Not Configured',
      );

  final int windowHours;
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

  static int? _intFromJson(Object? value) {
    if (value is int) return value;
    if (value is num) return value.toInt();
    if (value is String) return int.tryParse(value);
    return null;
  }
}

/// Payload for updating the refund/replacement request window. An omitted
/// (null) [windowHours] leaves the stored value unchanged on the backend.
class UpdateRefundReplacementConfigurationRequest {
  const UpdateRefundReplacementConfigurationRequest({this.windowHours});

  final int? windowHours;

  Map<String, dynamic> toJson() => {
    if (windowHours != null) 'windowHours': windowHours,
  };
}
