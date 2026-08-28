/// Reset policy controlling when a numbering series restarts its counter.
enum NumberSeriesResetPolicy {
  never,
  daily,
  monthly,
  calendarYear,
  financialYear;

  static NumberSeriesResetPolicy fromJson(Object? value) {
    switch (value) {
      case 'Daily':
      case 'daily':
        return NumberSeriesResetPolicy.daily;
      case 'Monthly':
      case 'monthly':
        return NumberSeriesResetPolicy.monthly;
      case 'CalendarYear':
      case 'calendarYear':
      case 'CalendarYearly':
        return NumberSeriesResetPolicy.calendarYear;
      case 'FinancialYear':
      case 'financialYear':
        return NumberSeriesResetPolicy.financialYear;
      case 'Never':
      case 'never':
      default:
        return NumberSeriesResetPolicy.never;
    }
  }

  /// Backend enum name as serialized over the wire.
  String get apiValue {
    switch (this) {
      case NumberSeriesResetPolicy.never:
        return 'Never';
      case NumberSeriesResetPolicy.daily:
        return 'Daily';
      case NumberSeriesResetPolicy.monthly:
        return 'Monthly';
      case NumberSeriesResetPolicy.calendarYear:
        return 'CalendarYear';
      case NumberSeriesResetPolicy.financialYear:
        return 'FinancialYear';
    }
  }

  String get label {
    switch (this) {
      case NumberSeriesResetPolicy.never:
        return 'Never';
      case NumberSeriesResetPolicy.daily:
        return 'Daily';
      case NumberSeriesResetPolicy.monthly:
        return 'Monthly';
      case NumberSeriesResetPolicy.calendarYear:
        return 'Calendar year';
      case NumberSeriesResetPolicy.financialYear:
        return 'Financial year';
    }
  }

  String get description {
    switch (this) {
      case NumberSeriesResetPolicy.never:
        return 'The counter never resets.';
      case NumberSeriesResetPolicy.daily:
        return 'Resets at the start of every day.';
      case NumberSeriesResetPolicy.monthly:
        return 'Resets at the start of every month.';
      case NumberSeriesResetPolicy.calendarYear:
        return 'Resets on 1 January each year.';
      case NumberSeriesResetPolicy.financialYear:
        return 'Resets at the start of the Indian financial year (1 April).';
    }
  }
}

/// A numbering series visible to Setup → Number Series.
class NumberSeries {
  const NumberSeries({
    required this.code,
    required this.description,
    required this.template,
    required this.startingNumber,
    required this.lastUsedNumber,
    required this.incrementBy,
    required this.resetPolicy,
    required this.isActive,
    this.scopeKey,
    this.nextNumber,
    this.lastUsedAt,
    this.createdByUserId,
    this.updatedByUserId,
  });

  factory NumberSeries.fromJson(Map<String, dynamic> json) => NumberSeries(
    code: json['code'] as String,
    description: json['description'] as String,
    template: json['template'] as String,
    startingNumber: _long(json['startingNumber']),
    lastUsedNumber: _long(json['lastUsedNumber']),
    incrementBy: _int(json['incrementBy']),
    resetPolicy: NumberSeriesResetPolicy.fromJson(json['resetPolicy']),
    isActive: json['isActive'] as bool,
    scopeKey: json['scopeKey'] == null
        ? null
        : (json['scopeKey'] as String).isEmpty
            ? null
            : json['scopeKey'] as String,
    nextNumber: json['nextNumber'] as String?,
    lastUsedAt: json['lastUsedAt'] == null
        ? null
        : DateTime.tryParse(json['lastUsedAt'] as String),
    createdByUserId: _nullableLong(json['createdByUserId']),
    updatedByUserId: _nullableLong(json['updatedByUserId']),
  );

  final String code;
  final String description;
  final String template;
  final int startingNumber;
  final int lastUsedNumber;
  final int incrementBy;
  final NumberSeriesResetPolicy resetPolicy;
  final bool isActive;
  final String? scopeKey;
  final String? nextNumber;
  final DateTime? lastUsedAt;
  final int? createdByUserId;
  final int? updatedByUserId;

  static int _int(Object? value) => (value as num).toInt();

  static int _long(Object? value) => (value as num).toInt();

  static int? _nullableLong(Object? value) => value == null
      ? null
      : value is num
          ? value.toInt()
          : int.tryParse(value.toString());
}

/// A template preview computed WITHOUT consuming or advancing the live sequence.
class NumberSeriesPreview {
  const NumberSeriesPreview({
    required this.code,
    required this.template,
    required this.nextNumber,
    required this.formattedNumber,
    this.scopeKey,
  });

  factory NumberSeriesPreview.fromJson(Map<String, dynamic> json) =>
      NumberSeriesPreview(
        code: json['code'] as String,
        template: json['template'] as String,
        nextNumber: (json['nextNumber'] as num).toInt(),
        formattedNumber: json['formattedNumber'] as String,
        scopeKey: json['scopeKey'] == null
            ? null
            : (json['scopeKey'] as String).isEmpty
                ? null
                : json['scopeKey'] as String,
      );

  final String code;
  final String template;
  final int nextNumber;
  final String formattedNumber;
  final String? scopeKey;
}

/// Payload for creating a new series.
class CreateNumberSeriesRequest {
  const CreateNumberSeriesRequest({
    required this.code,
    required this.description,
    required this.template,
    required this.startingNumber,
    required this.incrementBy,
    required this.resetPolicy,
    this.scopeKey,
  });

  final String code;
  final String description;
  final String template;
  final int startingNumber;
  final int incrementBy;
  final NumberSeriesResetPolicy resetPolicy;
  final String? scopeKey;

  Map<String, dynamic> toJson() => {
    'code': code,
    'description': description,
    'template': template,
    'startingNumber': startingNumber,
    'incrementBy': incrementBy,
    'resetPolicy': resetPolicy.apiValue,
    if (scopeKey != null && scopeKey!.isNotEmpty) 'scopeKey': scopeKey,
  };
}

/// Payload for updating an existing series.
class UpdateNumberSeriesRequest {
  const UpdateNumberSeriesRequest({
    required this.description,
    required this.template,
    required this.startingNumber,
    required this.incrementBy,
    required this.resetPolicy,
  });

  final String description;
  final String template;
  final int startingNumber;
  final int incrementBy;
  final NumberSeriesResetPolicy resetPolicy;

  Map<String, dynamic> toJson() => {
    'description': description,
    'template': template,
    'startingNumber': startingNumber,
    'incrementBy': incrementBy,
    'resetPolicy': resetPolicy.apiValue,
  };
}

/// Payload for previewing a template. `nextNumber` is optional; when omitted the
/// backend computes the next live value without consuming it.
class NumberSeriesPreviewRequest {
  const NumberSeriesPreviewRequest({
    required this.code,
    required this.template,
    this.nextNumber,
    this.scope,
  });

  final String code;
  final String template;
  final int? nextNumber;
  final String? scope;

  Map<String, dynamic> toJson() => {
    'code': code,
    'template': template,
    if (nextNumber != null) 'nextNumber': nextNumber,
    if (scope != null && scope!.isNotEmpty) 'scope': scope,
  };
}
