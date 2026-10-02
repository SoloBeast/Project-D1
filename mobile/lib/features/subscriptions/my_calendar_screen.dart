import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart'
    show formatQuantity;
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart'
    hide SubscriptionDeliverySlot;
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// Customer-wide "My Calendar".
///
/// NOT the per-subscription Delivery Calendar. This screen composes delivery
/// activity across ALL of the customer's sources:
/// * materialized deliveries — `GET /api/v1/deliveries/mine` (one-time orders
///   AND subscription deliveries, real authoritative status);
/// * occurrence calendars for EVERY active subscription —
///   `GET /api/v1/subscriptions/{id}/calendar` — so future scheduled dates
///   show Upcoming and vacation/skipped dates stay distinguishable from
///   "No delivery" dates.
///
/// No new backend endpoints, no business-rule changes; occurrences are read
/// through [SubscriptionRepository.getCalendar] per active subscription
/// (controller `loadCalendar` keeps only one subscription's calendar, so this
/// screen loads provenance-tracked copies directly, once per screen entry).
class MyCalendarScreen extends ConsumerStatefulWidget {
  const MyCalendarScreen({super.key});

  @override
  ConsumerState<MyCalendarScreen> createState() => _MyCalendarScreenState();
}

class _MyCalendarScreenState extends ConsumerState<MyCalendarScreen> {
  /// The month the grid displays (any day within the month).
  late DateTime _visibleMonth;

  /// The selected date (day precision). Starts as today.
  late DateTime _selectedDate;

  /// Occurrence calendars per subscription id (all active subscriptions).
  Map<String, List<SubscriptionDelivery>> _occurrencesBySubscription = {};

  /// First load in flight. Later refreshes keep the old content visible.
  bool _initialLoad = true;

  @override
  void initState() {
    super.initState();
    final today = _dateOnly(DateTime.now());
    _visibleMonth = DateTime(today.year, today.month);
    _selectedDate = today;
    Future.microtask(_refresh);
  }

  DateTime _dateOnly(DateTime value) =>
      DateTime(value.year, value.month, value.day);

  Future<void> _refresh() async {
    // Materialized deliveries + orders (Home controllers; deduped by
    // controllers' in-flight handling — no duplicate API surface).
    await Future.wait([
      ref.read(deliveryControllerProvider.notifier).loadCustomerDeliveries(),
      ref.read(orderControllerProvider.notifier).loadOrders(),
    ]);
    if (!mounted) return;

    // Occurrence calendars for EVERY active subscription — never only the
    // first (customer-wide composition requirement).
    final repository = ref.read(subscriptionRepositoryProvider);
    final token =
        ref.read(sessionControllerProvider).session?.accessToken ?? '';
    final actives = ref
        .read(subscriptionControllerProvider)
        .subscriptions
        .where((item) => item.status == SubscriptionStatus.active)
        .toList(growable: false);
    final results = await Future.wait([
      for (final subscription in actives)
        repository.getCalendar(token, subscription.publicId),
    ]);
    if (!mounted) return;
    setState(() {
      _occurrencesBySubscription = <String, List<SubscriptionDelivery>>{
        for (var index = 0; index < actives.length; index++)
          actives[index].publicId: results[index],
      };
      _initialLoad = false;
    });
  }

  void _goToMonth(int months) {
    setState(() {
      _visibleMonth = DateTime(
        _visibleMonth.year,
        _visibleMonth.month + months,
      );
    });
  }

  void _goToToday() {
    final today = _dateOnly(DateTime.now());
    setState(() {
      _visibleMonth = DateTime(today.year, today.month);
      _selectedDate = today;
    });
  }

  @override
  Widget build(BuildContext context) {
    final delivery = ref.watch(deliveryControllerProvider);
    final subscription = ref.watch(subscriptionControllerProvider);
    final orders = ref.watch(
      orderControllerProvider.select((state) => state.orders),
    );

    final loading =
        _initialLoad || delivery.isLoading || subscription.isLoading;
    final failed =
        delivery.isOffline ||
        subscription.isOffline ||
        delivery.errorMessage != null ||
        subscription.errorMessage != null;

    final today = _dateOnly(DateTime.now());
    final composed = _composeCalendar(
      deliveries: delivery.customerDeliveries,
      occurrencesBySubscription: _occurrencesBySubscription,
      subscriptions: subscription.subscriptions,
      orders: orders,
    );

    return Scaffold(
      backgroundColor: DoodhColors.cream,
      appBar: AppBar(
        title: const Text('My Calendar'),
        actions: [
          Padding(
            padding: const EdgeInsets.only(right: DoodhSpacing.md),
            child: FilledButton.tonalIcon(
              key: const ValueKey('my-calendar-set-vacation-action'),
              onPressed: () => context.push('/subscriptions/vacation'),
              icon: const Icon(Icons.beach_access_outlined, size: 18),
              label: const Text('Set Vacation'),
            ),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: SingleChildScrollView(
          key: const ValueKey('my-calendar-scroll'),
          physics: const AlwaysScrollableScrollPhysics(),
          child: Padding(
            padding: DoodhSpacing.pagePadding,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const Wrap(
                  key: ValueKey('my-calendar-legend'),
                  spacing: 12,
                  runSpacing: 6,
                  children: [
                    _LegendDot(color: DoodhColors.coral, label: 'Vacation'),
                    _LegendDot(
                      color: DoodhColors.deliveredGreen,
                      label: 'Delivered',
                    ),
                    _LegendDot(
                      color: DoodhColors.upcomingBlue,
                      label: 'Upcoming',
                    ),
                    _LegendDot(color: DoodhColors.muted, label: 'No Delivery'),
                  ],
                ),
                const SizedBox(height: DoodhSpacing.md),
                _MonthCard(
                  visibleMonth: _visibleMonth,
                  selectedDate: _selectedDate,
                  entries: composed,
                  loading: loading,
                  failed: failed,
                  today: today,
                  onPreviousMonth: () => _goToMonth(-1),
                  onNextMonth: () => _goToMonth(1),
                  onToday: _goToToday,
                  onDaySelected: (date) => setState(() => _selectedDate = date),
                ),
                const SizedBox(height: DoodhSpacing.md),
                _SelectedDateSection(
                  date: _selectedDate,
                  entries: composed,
                  loading: loading,
                  failed: failed,
                  onRetry: _refresh,
                ),
                const SizedBox(height: DoodhSpacing.xl),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Composition layer — view models over existing domain models only
// ---------------------------------------------------------------------------

enum _CalendarState { vacationSkipped, delivered, upcoming }

/// One composed entry for a date. Holds references to the REAL underlying
/// records — the screen never fabricates delivery data.
class _CalendarEntry {
  const _CalendarEntry._({
    required this.state,
    this.delivery,
    this.order,
    this.occurrence,
    this.owner,
  });

  /// A real Delivery record (authoritative for status). [order] carries the
  /// order enrichment (items) when the reference matches a loaded order.
  factory _CalendarEntry.fromDelivery(
    CustomerDelivery delivery,
    OrderSummary? order,
  ) => _CalendarEntry._(
    state: delivery.status == DeliveryStatus.delivered
        ? _CalendarState.delivered
        : _CalendarState.upcoming,
    delivery: delivery,
    order: order,
  );

  /// A subscription occurrence with no Delivery: scheduled → Upcoming,
  /// skipped/cancelled → Vacation/Skipped. [owner] is the subscription the
  /// calendar was loaded for (provenance), used for product identity.
  factory _CalendarEntry.fromOccurrence(
    SubscriptionDelivery occurrence,
    SubscriptionDetails? owner,
  ) => _CalendarEntry._(
    state:
        occurrence.status == SubscriptionDeliveryStatus.skipped ||
            occurrence.status == SubscriptionDeliveryStatus.cancelled
        ? _CalendarState.vacationSkipped
        : _CalendarState.upcoming,
    occurrence: occurrence,
    owner: owner,
  );

  final _CalendarState state;
  final CustomerDelivery? delivery;
  final OrderSummary? order;
  final SubscriptionDelivery? occurrence;
  final SubscriptionDetails? owner;
}

/// Derives entries per date across ALL sources. Composition rules:
/// * a real Delivery is authoritative for its date — the grid state comes
///   from it, and future scheduled occurrences on that date do not override;
/// * skipped occurrences ALWAYS surface as Vacation/Skipped (a Delivery will
///   never exist for them);
/// * de-duplication: within one date, identical (state, product, slot,
///   quantity) occurrences collapse to a single entry; a date never shows the
///   same real Delivery twice.
Map<DateTime, List<_CalendarEntry>> _composeCalendar({
  required List<CustomerDelivery> deliveries,
  required Map<String, List<SubscriptionDelivery>> occurrencesBySubscription,
  required List<SubscriptionDetails> subscriptions,
  required List<OrderSummary> orders,
}) {
  DateTime day(DateTime value) => DateTime(value.year, value.month, value.day);

  final ordersByNumber = <String, OrderSummary>{
    for (final order in orders) order.orderNumber: order,
  };

  final byDate = <DateTime, List<_CalendarEntry>>{};
  void add(DateTime date, _CalendarEntry entry) {
    byDate.putIfAbsent(day(date), () => []).add(entry);
  }

  // 1) Real deliveries first (authoritative).
  final seenDeliveryIds = <String>{};
  for (final item in deliveries) {
    if (!seenDeliveryIds.add(item.deliveryId)) continue;
    add(
      item.scheduledDate,
      _CalendarEntry.fromDelivery(item, ordersByNumber[item.referenceNumber]),
    );
  }

  // 2) Occurrence calendars for every active subscription.
  final ownerById = <String, SubscriptionDetails>{
    for (final subscription in subscriptions)
      subscription.publicId: subscription,
  };
  for (final entry in occurrencesBySubscription.entries) {
    final owner = ownerById[entry.key];
    for (final occurrence in entry.value) {
      final isSkipped =
          occurrence.status == SubscriptionDeliveryStatus.skipped ||
          occurrence.status == SubscriptionDeliveryStatus.cancelled;
      final isScheduled =
          occurrence.status == SubscriptionDeliveryStatus.scheduled;
      if (!isSkipped && !isScheduled) continue;

      final date = day(occurrence.scheduledDate);
      final dateEntries = byDate[date] ?? const <_CalendarEntry>[];
      // A scheduled occurrence is suppressed ONLY by ITS OWN materialized
      // Delivery — matched via the backend's deterministic reference
      // `SUB-{subscriptionPublicId:N}-{yyyyMMdd}` (DeliveryService, where
      // {N} is the GUID without dashes). Another subscription's Delivery on
      // the same date must never hide it.
      final materializedRefs = dateEntries
          .where((item) => item.delivery != null)
          .map((item) => item.delivery!.referenceNumber.toLowerCase())
          .toSet();
      final stamp =
          '${occurrence.scheduledDate.year.toString().padLeft(4, '0')}'
          '${occurrence.scheduledDate.month.toString().padLeft(2, '0')}'
          '${occurrence.scheduledDate.day.toString().padLeft(2, '0')}';
      final ownDeliveryReference =
          'sub-${entry.key.replaceAll('-', '')}-$stamp';
      if (isScheduled && materializedRefs.contains(ownDeliveryReference)) {
        continue;
      }

      // Every occurrence renders: two scheduled orders on the same date (even
      // identical ones) are two real deliveries the customer expects to see.
      // A scheduled occurrence is only ever suppressed by ITS OWN materialized
      // Delivery (checked above via the deterministic reference number).

      add(
        occurrence.scheduledDate,
        _CalendarEntry.fromOccurrence(occurrence, owner),
      );
    }
  }
  return byDate;
}

Color _stateDotColor(_CalendarState? state, {required bool selected}) {
  if (selected) return Colors.white70;
  return switch (state) {
    _CalendarState.vacationSkipped => DoodhColors.coral,
    _CalendarState.delivered => DoodhColors.deliveredGreen,
    _CalendarState.upcoming => DoodhColors.upcomingBlue,
    _ => DoodhColors.muted,
  };
}

String _stateLegend(_CalendarState? state) => switch (state) {
  _CalendarState.vacationSkipped => 'Vacation',
  _CalendarState.delivered => 'Delivered',
  _CalendarState.upcoming => 'Upcoming',
  _ => 'No Delivery',
};

// ---------------------------------------------------------------------------
// Calendar card (reference: white card, month + Today pill + chevrons,
// Sun-first weekday header, day cells with status dots, dark selected circle)
// ---------------------------------------------------------------------------

class _LegendDot extends StatelessWidget {
  const _LegendDot({required this.color, required this.label});

  final Color color;
  final String label;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Container(
        width: 12,
        height: 12,
        decoration: BoxDecoration(color: color, shape: BoxShape.circle),
      ),
      const SizedBox(width: 5),
      Text(label, style: Theme.of(context).textTheme.bodySmall),
    ],
  );
}

class _MonthCard extends StatelessWidget {
  const _MonthCard({
    required this.visibleMonth,
    required this.selectedDate,
    required this.entries,
    required this.loading,
    required this.failed,
    required this.today,
    required this.onPreviousMonth,
    required this.onNextMonth,
    required this.onToday,
    required this.onDaySelected,
  });

  final DateTime visibleMonth;
  final DateTime selectedDate;
  final Map<DateTime, List<_CalendarEntry>> entries;
  final bool loading;
  final bool failed;
  final DateTime today;
  final VoidCallback onPreviousMonth;
  final VoidCallback onNextMonth;
  final VoidCallback onToday;
  final ValueChanged<DateTime> onDaySelected;

  static const _weekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

  String get _monthLabel {
    const months = [
      'January',
      'February',
      'March',
      'April',
      'May',
      'June',
      'July',
      'August',
      'September',
      'October',
      'November',
      'December',
    ];
    return '${months[visibleMonth.month - 1]} ${visibleMonth.year}';
  }

  @override
  Widget build(BuildContext context) {
    final firstDay = DateTime(visibleMonth.year, visibleMonth.month, 1);
    final leadingBlanks = firstDay.weekday % 7; // Sunday-first grid.
    final daysInMonth = DateTime(
      visibleMonth.year,
      visibleMonth.month + 1,
      0,
    ).day;
    final rows = ((leadingBlanks + daysInMonth) / 7).ceil();

    return DoodhCard(
      key: const ValueKey('my-calendar-card'),
      color: Colors.white,
      padding: const EdgeInsets.all(DoodhSpacing.lg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  _monthLabel,
                  key: const ValueKey('my-calendar-month-label'),
                  style: Theme.of(context).textTheme.titleLarge
                      ?.copyWith(fontWeight: FontWeight.w800),
                ),
              ),
              FilledButton(
                key: const ValueKey('my-calendar-today-action'),
                onPressed: onToday,
                style: FilledButton.styleFrom(
                  minimumSize: const Size(0, 40),
                  padding: const EdgeInsets.symmetric(horizontal: 18),
                ),
                child: const Text('Today'),
              ),
              IconButton(
                key: const ValueKey('my-calendar-previous-month'),
                tooltip: 'Previous month',
                onPressed: onPreviousMonth,
                icon: const Icon(Icons.chevron_left),
              ),
              IconButton(
                key: const ValueKey('my-calendar-next-month'),
                tooltip: 'Next month',
                onPressed: onNextMonth,
                icon: const Icon(Icons.chevron_right),
              ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.md),
          Row(
            children: [
              for (final weekday in _weekdays)
                Expanded(
                  child: Center(
                    child: Text(
                      weekday,
                      style: Theme.of(context).textTheme.labelLarge
                          ?.copyWith(fontWeight: FontWeight.w800),
                    ),
                  ),
                ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.xs),
          if (loading)
            const Padding(
              key: ValueKey('my-calendar-loading'),
              padding: EdgeInsets.symmetric(vertical: DoodhSpacing.xl),
              child: Column(
                children: [
                  CircularProgressIndicator(),
                  SizedBox(height: DoodhSpacing.md),
                  Text('Loading your calendar…'),
                ],
              ),
            )
          else if (failed)
            Padding(
              key: const ValueKey('my-calendar-error'),
              padding: const EdgeInsets.symmetric(vertical: DoodhSpacing.lg),
              child: Column(
                children: [
                  const DoodhStatusPill(
                    label: 'Calendar unavailable',
                    tone: DoodhStatusTone.warning,
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                  const Text(
                    'We could not load your delivery calendar. Please try again.',
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: DoodhSpacing.sm),
                  DoodhButton(
                    key: const ValueKey('my-calendar-retry-action'),
                    label: 'Retry',
                    icon: Icons.refresh,
                    onPressed: onToday,
                  ),
                ],
              ),
            )
          else
            Column(
              key: const ValueKey('my-calendar-grid'),
              children: [
                for (var row = 0; row < rows; row++)
                  Row(
                    children: [
                      for (var column = 0; column < 7; column++)
                        Expanded(
                          child: _buildCell(
                            row * 7 + column + 1 - leadingBlanks,
                            daysInMonth,
                          ),
                        ),
                    ],
                  ),
              ],
            ),
        ],
      ),
    );
  }

  Widget _buildCell(int dayNumber, int daysInMonth) {
    if (dayNumber < 1 || dayNumber > daysInMonth) {
      return const SizedBox(height: 60);
    }
    final date = DateTime(visibleMonth.year, visibleMonth.month, dayNumber);
    final dayEntries = entries[date] ?? const <_CalendarEntry>[];
    // Grid state: real Delivery wins over Upcoming occurrences, but a
    // Vacation/Skipped occurrence is never hidden by anything.
    _CalendarState? state;
    if (dayEntries.any(
      (item) => item.state == _CalendarState.vacationSkipped,
    )) {
      state = _CalendarState.vacationSkipped;
    } else if (dayEntries.any(
      (item) => item.state == _CalendarState.delivered,
    )) {
      state = _CalendarState.delivered;
    } else if (dayEntries.any(
      (item) => item.state == _CalendarState.upcoming,
    )) {
      state = _CalendarState.upcoming;
    }
    final isSelected = _sameDay(date, selectedDate);
    return _CalendarDayCell(
      key: ValueKey('my-calendar-day-${_iso(date)}'),
      date: date,
      dayNumber: dayNumber,
      state: state,
      isSelected: isSelected,
      isToday: _sameDay(date, today),
      onTap: () => onDaySelected(date),
    );
  }

  static bool _sameDay(DateTime a, DateTime b) =>
      a.year == b.year && a.month == b.month && a.day == b.day;

  static String _iso(DateTime value) =>
      '${value.year.toString().padLeft(4, '0')}-'
      '${value.month.toString().padLeft(2, '0')}-'
      '${value.day.toString().padLeft(2, '0')}';
}

class _CalendarDayCell extends StatelessWidget {
  const _CalendarDayCell({
    super.key,
    required this.date,
    required this.dayNumber,
    required this.state,
    required this.isSelected,
    required this.isToday,
    required this.onTap,
  });

  final DateTime date;
  final int dayNumber;
  final _CalendarState? state;
  final bool isSelected;
  final bool isToday;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final dark = isSelected;
    return Semantics(
      label:
          '${date.day} ${date.month} ${_stateLegend(state)}${dark ? ', selected' : ''}',
      button: true,
      selected: dark,
      child: InkWell(
        borderRadius: DoodhRadii.md,
        onTap: onTap,
        child: SizedBox(
          height: 56,
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Container(
                width: 34,
                height: 34,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  // Reference: selected date is a solid dark circle.
                  color: dark ? DoodhColors.tealDark : Colors.transparent,
                  shape: BoxShape.circle,
                ),
                child: Text(
                  '$dayNumber',
                  style: Theme.of(context).textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w800,
                    color: dark ? Colors.white : DoodhColors.ink,
                  ),
                ),
              ),
              const SizedBox(height: 3),
              Container(
                width: 7,
                height: 7,
                decoration: BoxDecoration(
                  color: _stateDotColor(state, selected: dark),
                  shape: BoxShape.circle,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// Selected-date section
// ---------------------------------------------------------------------------

class _SelectedDateSection extends StatelessWidget {
  const _SelectedDateSection({
    required this.date,
    required this.entries,
    required this.loading,
    required this.failed,
    required this.onRetry,
  });

  final DateTime date;
  final Map<DateTime, List<_CalendarEntry>> entries;
  final bool loading;
  final bool failed;
  final Future<void> Function() onRetry;

  @override
  Widget build(BuildContext context) {
    final heading = 'Deliveries for ${date.day}/${date.month}/${date.year}';
    if (loading) {
      return _SectionPanel(
        heading: heading,
        child: const Padding(
          key: ValueKey('my-calendar-day-loading'),
          padding: EdgeInsets.symmetric(vertical: DoodhSpacing.lg),
          child: Center(child: CircularProgressIndicator()),
        ),
      );
    }
    if (failed) {
      return _SectionPanel(
        heading: heading,
        child: Padding(
          key: const ValueKey('my-calendar-day-error'),
          padding: const EdgeInsets.symmetric(vertical: DoodhSpacing.sm),
          child: Column(
            children: [
              const DoodhStatusPill(
                label: 'Delivery details unavailable',
                tone: DoodhStatusTone.warning,
              ),
              const SizedBox(height: DoodhSpacing.sm),
              DoodhButton(
                key: const ValueKey('my-calendar-day-retry-action'),
                label: 'Retry',
                icon: Icons.refresh,
                onPressed: () => onRetry(),
              ),
            ],
          ),
        ),
      );
    }

    final dayEntries =
        entries[DateTime(date.year, date.month, date.day)] ??
        const <_CalendarEntry>[];
    if (dayEntries.isEmpty) {
      return _SectionPanel(
        heading: heading,
        child: const Padding(
          key: ValueKey('my-calendar-day-empty'),
          padding: EdgeInsets.symmetric(vertical: DoodhSpacing.lg),
          child: Column(
            children: [
              Icon(
                Icons.event_busy_outlined,
                size: 32,
                color: DoodhColors.muted,
              ),
              SizedBox(height: DoodhSpacing.sm),
              Text('No deliveries for this date'),
            ],
          ),
        ),
      );
    }

    return _SectionPanel(
      heading: heading,
      child: Column(
        children: [
          for (final entry in dayEntries)
            Padding(
              padding: const EdgeInsets.only(bottom: DoodhSpacing.sm),
              child: entry.delivery != null
                  ? _DeliveryRow(delivery: entry.delivery!, order: entry.order)
                  : _OccurrenceRow(
                      occurrence: entry.occurrence!,
                      owner: entry.owner,
                    ),
            ),
        ],
      ),
    );
  }
}

class _SectionPanel extends StatelessWidget {
  const _SectionPanel({required this.heading, required this.child});

  final String heading;
  final Widget child;

  @override
  Widget build(BuildContext context) => DoodhCard(
    key: const ValueKey('my-calendar-day-section'),
    color: DoodhColors.tanSurface,
    padding: const EdgeInsets.all(DoodhSpacing.lg),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          heading,
          key: const ValueKey('my-calendar-day-heading'),
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: DoodhSpacing.md),
        child,
      ],
    ),
  );
}

/// A real Delivery row — actual status from `/deliveries/mine`; deep-links to
/// the existing delivery detail screen. Product identity from the matched
/// order's items when available (existing enrichment, nothing invented).
class _DeliveryRow extends StatelessWidget {
  const _DeliveryRow({required this.delivery, required this.order});

  final CustomerDelivery delivery;
  final OrderSummary? order;

  @override
  Widget build(BuildContext context) {
    final title = order == null || order!.items.isEmpty
        ? '${delivery.sourceType.label} · ${delivery.referenceNumber}'
        : order!.items
              .map(
                (item) =>
                    '${item.productName} (${formatQuantity(item.quantity)})',
              )
              .join(', ');
    return DoodhCard(
      color: Colors.white,
      semanticLabel:
          '${delivery.sourceType.label} delivery ${delivery.referenceNumber}, ${delivery.status.label}',
      onTap: () => context.push('/deliveries/${delivery.deliveryId}'),
      child: Row(
        children: [
          const CircleAvatar(
            radius: 22,
            backgroundColor: DoodhColors.mint,
            child: Icon(
              Icons.local_shipping_outlined,
              color: DoodhColors.tealDark,
            ),
          ),
          const SizedBox(width: DoodhSpacing.md),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: Theme.of(context).textTheme.titleSmall),
                const SizedBox(height: 2),
                Text(
                  delivery.destinationAddress,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall
                      ?.copyWith(color: DoodhColors.muted),
                ),
                const SizedBox(height: DoodhSpacing.xs),
                DoodhStatusPill(
                  label: delivery.status.label,
                  tone: switch (delivery.status) {
                    DeliveryStatus.delivered => DoodhStatusTone.success,
                    DeliveryStatus.failed => DoodhStatusTone.error,
                    _ => DoodhStatusTone.neutral,
                  },
                ),
              ],
            ),
          ),
          const Icon(Icons.chevron_right),
        ],
      ),
    );
  }
}

/// Subscription occurrence WITHOUT a Delivery row (future scheduled →
/// Upcoming; skipped/cancelled → Vacation/Skipped). Product identity comes
/// from the owning subscription (calendar provenance) — never invented.
class _OccurrenceRow extends StatelessWidget {
  const _OccurrenceRow({required this.occurrence, required this.owner});

  final SubscriptionDelivery occurrence;
  final SubscriptionDetails? owner;

  @override
  Widget build(BuildContext context) {
    final isSkipped =
        occurrence.status == SubscriptionDeliveryStatus.skipped ||
        occurrence.status == SubscriptionDeliveryStatus.cancelled;
    return DoodhCard(
      color: DoodhColors.mint,
      semanticLabel:
          'Subscription ${isSkipped ? 'vacation or skipped' : 'upcoming'} '
          'delivery, ${occurrence.slot.apiValue}, '
          '${formatQuantity(occurrence.quantity)}',
      child: Row(
        children: [
          CircleAvatar(
            radius: 22,
            backgroundColor: DoodhColors.highlightSurface,
            child: Icon(
              isSkipped
                  ? Icons.event_busy_outlined
                  : Icons.event_repeat_outlined,
              color: DoodhColors.tealDark,
            ),
          ),
          const SizedBox(width: DoodhSpacing.md),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  owner?.productName ?? 'Subscription delivery',
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                const SizedBox(height: 2),
                Text(
                  '${occurrence.slot.apiValue} · '
                  '${formatQuantity(occurrence.quantity)} quantity',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
                Text(
                  occurrence.address,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall
                      ?.copyWith(color: DoodhColors.muted),
                ),
                const SizedBox(height: DoodhSpacing.xs),
                DoodhStatusPill(
                  // Legend language: a scheduled occurrence without a Delivery
                  // is 'Upcoming'; skipped/cancelled is 'Vacation / Skipped'.
                  // Real Delivery rows keep their actual status labels.
                  label: isSkipped ? 'Vacation / Skipped' : 'Upcoming',
                  tone: isSkipped
                      ? DoodhStatusTone.warning
                      : DoodhStatusTone.neutral,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
