import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

/// One contiguous vacation block derived from skipped/cancelled occurrences.
///
/// The backend persists no vacation entity — a saved 16–18 range resolves to
/// per-occurrence skips, and dates without a scheduled occurrence (or past
/// the skip cutoff / already materialized) stay non-vacation. Grouping
/// consecutive skipped days into ranges is what lets the customer see their
/// real vacation history (past / present / upcoming) instead of guessing
/// from calendar dots.
class VacationRange {
  const VacationRange({
    required this.from,
    required this.to,
    required this.dates,
    required this.occurrences,
    required this.productNames,
  });

  final DateTime from;
  final DateTime to;
  final List<DateTime> dates;
  final List<SubscriptionDelivery> occurrences;
  final Set<String> productNames;

  int get dayCount => dates.length;
  int get skippedCount => occurrences.length;
}

/// Groups consecutive skipped/cancelled occurrence days into ranges.
///
/// [occurrencesBySubscription] maps owning subscription publicId to its
/// calendar; [productNameBySubscription] resolves the display name per owner.
/// Only `skipped`/`cancelled` occurrences are vacation days — scheduled,
/// failed, delivered and unknown occurrences never form a range.
/// Groups consecutive skipped/cancelled occurrence days into ranges.
/// [productNameOf] resolves the owning subscription's display name per
/// occurrence (matched by identity by the caller). Only `skipped`/`cancelled`
/// occurrences are vacation days — scheduled, failed, delivered and unknown
/// occurrences never form a range.
List<VacationRange> groupVacationRanges(
  List<SubscriptionDelivery> occurrences,
  String Function(SubscriptionDelivery occurrence) productNameOf,
) {
  DateTime day(DateTime value) =>
      DateTime(value.year, value.month, value.day);

  final byDay = <DateTime, List<SubscriptionDelivery>>{};
  for (final occurrence in occurrences) {
    if (occurrence.status != SubscriptionDeliveryStatus.skipped &&
        occurrence.status != SubscriptionDeliveryStatus.cancelled) {
      continue;
    }
    byDay.putIfAbsent(day(occurrence.scheduledDate), () => []).add(occurrence);
  }
  if (byDay.isEmpty) return const [];

  final sortedDays = byDay.keys.toList()..sort();
  final ranges = <VacationRange>[];
  var run = <DateTime>[sortedDays.first];
  for (var index = 1; index < sortedDays.length; index++) {
    final previous = sortedDays[index - 1];
    final current = sortedDays[index];
    if (current.difference(previous).inDays == 1) {
      run.add(current);
    } else {
      ranges.add(_toRange(run, byDay, productNameOf));
      run = <DateTime>[current];
    }
  }
  ranges.add(_toRange(run, byDay, productNameOf));
  // Newest range first — upcoming/current vacations surface without scrolling.
  ranges.sort((a, b) => b.from.compareTo(a.from));
  return ranges;
}

VacationRange _toRange(
  List<DateTime> days,
  Map<DateTime, List<SubscriptionDelivery>> byDay,
  String Function(SubscriptionDelivery occurrence) productNameOf,
) {
  final occurrences = <SubscriptionDelivery>[
    for (final date in days) ...byDay[date]!,
  ];
  return VacationRange(
    from: days.first,
    to: days.last,
    dates: List.unmodifiable(days),
    occurrences: List.unmodifiable(occurrences),
    productNames: {for (final occurrence in occurrences) productNameOf(occurrence)},
  );
}

enum _VacationTab { upcoming, present, past }

/// Customer vacation history: past, present and upcoming ranges derived from
/// server-authoritative skipped occurrences across every subscription.
class VacationListScreen extends ConsumerStatefulWidget {
  const VacationListScreen({super.key});

  @override
  ConsumerState<VacationListScreen> createState() =>
      _VacationListScreenState();
}

class _VacationListScreenState extends ConsumerState<VacationListScreen> {
  Map<String, List<SubscriptionDelivery>> _calendars = {};
  Map<String, String> _productNames = {};
  bool _initialLoad = true;
  bool _failed = false;
  _VacationTab _tab = _VacationTab.upcoming;

  @override
  void initState() {
    super.initState();
    Future.microtask(_refresh);
  }

  DateTime _today() {
    final now = DateTime.now();
    return DateTime(now.year, now.month, now.day);
  }

  Future<void> _refresh() async {
    final controller = ref.read(subscriptionControllerProvider.notifier);
    if (ref.read(subscriptionControllerProvider).subscriptions.isEmpty) {
      await controller.loadSubscriptions();
    }
    if (!mounted) return;
    final subscriptions = ref
        .read(subscriptionControllerProvider)
        .subscriptions;
    if (subscriptions.isEmpty) {
      setState(() {
        _calendars = {};
        _productNames = {};
        _initialLoad = false;
        _failed = false;
      });
      return;
    }
    final token =
        ref.read(sessionControllerProvider).session?.accessToken ?? '';
    final repository = ref.read(subscriptionRepositoryProvider);
    try {
      final results = await Future.wait([
        for (final subscription in subscriptions)
          repository.getCalendar(token, subscription.publicId),
      ]);
      if (!mounted) return;
      setState(() {
        _calendars = {
          for (var index = 0; index < subscriptions.length; index++)
            subscriptions[index].publicId: results[index],
        };
        _productNames = {
          for (final subscription in subscriptions)
            subscription.publicId: subscription.productName,
        };
        _initialLoad = false;
        _failed = false;
      });
    } on Object catch (_) {
      if (!mounted) return;
      setState(() {
        _initialLoad = false;
        _failed = true;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    // Resolve owner names per occurrence by identity: match each skipped
    // occurrence back to its owning calendar so multi-subscription vacations
    // show every product, not just the first subscription's.
    final namesByOccurrence = <SubscriptionDelivery, String>{};
    final controllerNames = _productNames;
    for (final entry in _calendars.entries) {
      final ownerName = controllerNames[entry.key] ?? 'Subscription';
      for (final occurrence in entry.value) {
        namesByOccurrence[occurrence] = ownerName;
      }
    }
    final grouped = groupVacationRanges(
      namesByOccurrence.keys.toList(growable: false),
      (occurrence) => namesByOccurrence[occurrence] ?? 'Subscription',
    );
    final today = _today();
    final upcoming = grouped
        .where((range) => range.from.isAfter(today))
        .toList(growable: false);
    final past = grouped
        .where((range) => range.to.isBefore(today))
        .toList(growable: false);
    final present = grouped
        .where(
          (range) => !range.from.isAfter(today) && !range.to.isBefore(today),
        )
        .toList(growable: false);
    final visible = switch (_tab) {
      _VacationTab.upcoming => upcoming,
      _VacationTab.present => present,
      _VacationTab.past => past,
    };

    return Scaffold(
      backgroundColor: DoodhColors.cream,
      appBar: AppBar(
        title: const Text('My Vacations'),
        actions: [
          IconButton(
            key: const ValueKey('vacation-list-add-action'),
            tooltip: 'Set vacation',
            onPressed: () async {
              await context.push('/subscriptions/vacation');
              if (!mounted) return;
              await _refresh();
            },
            icon: const Icon(Icons.add),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: _initialLoad
            ? const LoadingStatePanel(message: 'Loading your vacations')
            : _failed
            ? ListView(
                physics: const AlwaysScrollableScrollPhysics(),
                padding: DoodhSpacing.pagePadding,
                children: [
                  ErrorStatePanel(
                    message:
                        'We could not load your vacations. Please try again.',
                    onRetry: _refresh,
                  ),
                ],
              )
            : ListView(
                key: const ValueKey('vacation-list-scroll'),
                physics: const AlwaysScrollableScrollPhysics(),
                padding: DoodhSpacing.pagePadding,
                children: [
                  SegmentedButton<_VacationTab>(
                    segments: [
                      ButtonSegment(
                        value: _VacationTab.upcoming,
                        label: Text('Upcoming (${upcoming.length})'),
                        icon: const Icon(Icons.upcoming_outlined, size: 18),
                      ),
                      ButtonSegment(
                        value: _VacationTab.present,
                        label: Text('Present (${present.length})'),
                        icon: const Icon(Icons.today_outlined, size: 18),
                      ),
                      ButtonSegment(
                        value: _VacationTab.past,
                        label: Text('Past (${past.length})'),
                        icon: const Icon(Icons.history_outlined, size: 18),
                      ),
                    ],
                    selected: {_tab},
                    onSelectionChanged: (selection) =>
                        setState(() => _tab = selection.single),
                  ),
                  const SizedBox(height: DoodhSpacing.md),
                  if (visible.isEmpty)
                    EmptyStatePanel(
                      key: ValueKey('vacation-empty-${_tab.name}'),
                      title: switch (_tab) {
                        _VacationTab.upcoming => 'No upcoming vacations',
                        _VacationTab.present => 'No ongoing vacation',
                        _VacationTab.past => 'No past vacations',
                      },
                      message: switch (_tab) {
                        _VacationTab.upcoming =>
                          'Future vacation ranges you save will appear here.',
                        _VacationTab.present =>
                          'A vacation covering today will appear here.',
                        _VacationTab.past =>
                          'Completed vacation ranges will appear here.',
                      },
                      action: DoodhButton(
                        label: 'Set vacation',
                        icon: Icons.beach_access_outlined,
                        onPressed: () async {
                          await context.push('/subscriptions/vacation');
                          if (!mounted) return;
                          await _refresh();
                        },
                      ),
                    )
                  else
                    for (final range in visible)
                      _VacationRangeCard(range: range, tab: _tab),
                  const SizedBox(height: DoodhSpacing.xl),
                ],
              ),
      ),
    );
  }
}

class _VacationRangeCard extends StatelessWidget {
  const _VacationRangeCard({required this.range, required this.tab});

  final VacationRange range;
  final _VacationTab tab;

  @override
  Widget build(BuildContext context) {
    final iso =
        '${range.from.year.toString().padLeft(4, '0')}-'
        '${range.from.month.toString().padLeft(2, '0')}-'
        '${range.from.day.toString().padLeft(2, '0')}';
    final title = _sameDay(range.from, range.to)
        ? _formatDate(range.from)
        : '${_formatDate(range.from)} → ${_formatDate(range.to)}';
    return DoodhCard(
      key: ValueKey('vacation-range-card-$iso'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  title,
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeight.w800,
                  ),
                ),
              ),
              DoodhStatusPill(
                label: switch (tab) {
                  _VacationTab.upcoming => 'Upcoming',
                  _VacationTab.present => 'Ongoing',
                  _VacationTab.past => 'Past',
                },
                tone: switch (tab) {
                  _VacationTab.upcoming => DoodhStatusTone.neutral,
                  _VacationTab.present => DoodhStatusTone.success,
                  _VacationTab.past => DoodhStatusTone.neutral,
                },
              ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.xs),
          Text(
            '${range.dayCount} ${range.dayCount == 1 ? 'day' : 'days'} · '
            '${range.skippedCount} '
            '${range.skippedCount == 1 ? 'delivery' : 'deliveries'} skipped',
            style: Theme.of(context).textTheme.bodyMedium,
          ),
          if (range.productNames.isNotEmpty) ...[
            const SizedBox(height: DoodhSpacing.xs),
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                for (final name in range.productNames)
                  Chip(
                    label: Text(name),
                    visualDensity: VisualDensity.compact,
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

bool _sameDay(DateTime a, DateTime b) =>
    a.year == b.year && a.month == b.month && a.day == b.day;

String _formatDate(DateTime value) {
  const months = [
    'Jan',
    'Feb',
    'Mar',
    'Apr',
    'May',
    'Jun',
    'Jul',
    'Aug',
    'Sep',
    'Oct',
    'Nov',
    'Dec',
  ];
  return '${value.day.toString().padLeft(2, '0')} '
      '${months[value.month - 1]} ${value.year}';
}
