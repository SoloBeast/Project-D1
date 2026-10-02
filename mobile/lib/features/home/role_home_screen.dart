import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/core/widgets/customer_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_product_card.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_controller.dart';
import 'package:doodh_direct_mobile/features/deliveries/delivery_models.dart';
import 'package:doodh_direct_mobile/features/orders/order_controller.dart';
import 'package:doodh_direct_mobile/features/orders/order_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/wallet/wallet_controller.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/branding/doodh_brand_mark.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/notifications/notification_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Display label for a branch id: the server-resolved branch name (e.g.
/// "Dabua"/"NIT3") when the authenticated session carries it, otherwise the
/// numeric fallback used by legacy cached sessions that predate the enriched
/// branchDetails metadata.
String branchDisplayName(List<AuthUserBranchInfo> details, int id) {
  for (final branch in details) {
    if (branch.id == id) return branch.name;
  }
  return 'Branch $id';
}

class RoleHomeScreen extends ConsumerWidget {
  const RoleHomeScreen({super.key, required this.role});

  final UserRole role;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (role == UserRole.customer) return const _CustomerHomeActions();
    final user = ref.watch(sessionControllerProvider).session?.user;
    return Scaffold(
      appBar: AppBar(
        title: Text('${role.label} workspace'),
        actions: [
          IconButton(
            tooltip: 'Login & security',
            onPressed: () => context.push('/security'),
            icon: const Icon(Icons.account_circle_outlined),
          ),
          const _NotificationButton(),
          const _SignOutButton(),
        ],
      ),
      body: switch (role) {
        UserRole.customer => const _CustomerHomeActions(),
        UserRole.delivery => _DeliveryHomeActions(
          roles: user?.roles ?? const [],
          permissions: user?.permissions ?? const [],
          branchIds: user?.branchIds ?? const [],
          branchDetails: user?.branchDetails ?? const [],
        ),
        UserRole.dairy => const _DairyHomeActions(),
        UserRole.owner || UserRole.admin => _AdminHomeActions(
          permissions: user?.permissions ?? const [],
          branchIds: user?.branchIds ?? const [],
          branchDetails: user?.branchDetails ?? const [],
        ),
        UserRole.support => const _SupportHomeActions(),
        UserRole.accountant => const StatePanel(
          icon: Icons.account_balance_outlined,
          title: 'Accounting workspace ready',
          message: 'Accounting workflows remain outside the Identity and RBAC phase.',
        ),
      },
    );
  }
}

class _SignOutButton extends ConsumerWidget {
  const _SignOutButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) => IconButton(
    tooltip: 'Sign out',
    onPressed: () => ref.read(sessionControllerProvider.notifier).signOut(),
    icon: const Icon(Icons.logout),
  );
}

class _NotificationButton extends ConsumerWidget {
  const _NotificationButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unreadCount = ref.watch(
      notificationControllerProvider.select((state) => state.unreadCount),
    );
    return SizedBox.square(
      dimension: 48,
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          Positioned.fill(
            child: IconButton(
              tooltip: 'Notifications',
              onPressed: () => context.push('/notifications'),
              icon: const Icon(Icons.notifications_outlined),
            ),
          ),
          if (unreadCount > 0)
            Positioned(
              right: 2,
              top: 3,
              child: IgnorePointer(
                child: Container(
                  constraints: const BoxConstraints(
                    minWidth: 18,
                    minHeight: 18,
                  ),
                  padding: const EdgeInsets.symmetric(horizontal: 4),
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: Theme.of(context).colorScheme.error,
                    borderRadius: BorderRadius.circular(9),
                    border: Border.all(
                      color: Theme.of(context).colorScheme.surface,
                      width: 1.5,
                    ),
                  ),
                  child: Text(
                    key: const ValueKey('home-notification-count'),
                    unreadCount > 99 ? '99+' : '$unreadCount',
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.onError,
                      fontSize: 10,
                      fontWeight: FontWeight.w700,
                      height: 1,
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _CartButton extends ConsumerWidget {
  const _CartButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final cartCount = ref.watch(
      orderControllerProvider.select((state) => state.cart.length),
    );
    return IconButton(
      key: const ValueKey('customer-cart-action'),
      tooltip: 'Cart',
      onPressed: () {
        ScaffoldMessenger.of(context).hideCurrentSnackBar();
        context.go('/checkout');
      },
      icon: Badge(
        isLabelVisible: cartCount > 0,
        label: Text(cartCount > 99 ? '99+' : '$cartCount'),
        child: const Icon(Icons.shopping_cart_outlined),
      ),
    );
  }
}

class _CustomerHomeActions extends ConsumerStatefulWidget {
  const _CustomerHomeActions();

  @override
  ConsumerState<_CustomerHomeActions> createState() =>
      _CustomerHomeActionsState();
}

class _CustomerHomeActionsState extends ConsumerState<_CustomerHomeActions> {
  @override
  void initState() {
    super.initState();
    Future.microtask(_loadHomeData);
  }

  Future<void> _loadHomeData() async {
    await Future.wait([
      ref.read(customerControllerProvider.notifier).load(),
      ref.read(catalogueControllerProvider.notifier).load(),
      ref.read(orderControllerProvider.notifier).loadOrders(),
      ref.read(walletControllerProvider.notifier).load(),
      ref.read(deliveryControllerProvider.notifier).loadCustomerDeliveries(),
      ref.read(subscriptionControllerProvider.notifier).loadSubscriptions(),
      ref.read(notificationControllerProvider.notifier).loadInitial(),
    ]);
    if (!mounted) return;
    // Customer-wide calendar: occurrence calendars for EVERY active
    // subscription, so the week strip, Upcoming Deliveries and the date
    // sheet show all scheduled orders — never just the first subscription's.
    await ref.read(subscriptionControllerProvider.notifier).loadAllCalendars();
  }

  void _openCatalogue() => context.push('/catalogue');

  @override
  Widget build(BuildContext context) => CustomerShell(
    currentPath: '/home',
    // Brand row + tagline on the left of the app bar's title row, with the
    // header trio — Wallet → Cart → Notification (notification last) —
    // grouped on the right (business ask).
    header: const _CustomerHomeHeader(),
    child: DoodhPage(
      child: RefreshIndicator(
        onRefresh: _loadHomeData,
        child: ListView(
          key: const ValueKey('customer-home-scroll'),
          physics: const AlwaysScrollableScrollPhysics(),
          children: [
            DoodhSearchBar(
              key: const ValueKey('home-search'),
              hint: 'Search milk, paneer, ghee…',
              onChanged: (value) => ref
                  .read(catalogueControllerProvider.notifier)
                  .setSearchQuery(value),
              onSubmitted: (_) => _openCatalogue(),
            ),
            const SizedBox(height: DoodhSpacing.lg),
            const _MyDeliveries(),
            // Business ask: "Upcoming Deliveries" is its own section card —
            // sharing the tan My-Deliveries card read as one crowded block.
            const SizedBox(height: DoodhSpacing.lg),
            const UpcomingDeliveriesSection(),
            _HomeCategoryDiscovery(onBrowseCatalogue: _openCatalogue),
            _HomeProductDiscovery(onBrowseCatalogue: _openCatalogue),
            const SizedBox(height: DoodhSpacing.xl),
          ],
        ),
      ),
    ),
  );
}

class _CustomerHomeHeader extends StatelessWidget {
  const _CustomerHomeHeader();

  @override
  Widget build(BuildContext context) {
    // Business ask: the DoodhDirect name was being truncated on phones
    // because the wallet balance pill shared the title row. The header is
    // now two rows — brand block + cart/notification icons on top, the
    // wallet chip on its own row BELOW the icons — so the name gets the
    // full title-row width back.
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  // Business ask: the logo sits BESIDE the DoodhDirect name (the
                  // business-uploaded logo when branding is configured, the
                  // bundled drop icon otherwise).
                  const Row(
                    children: [
                      DoodhBrandMark(showWordmark: false, height: 30),
                      SizedBox(width: DoodhSpacing.sm),
                      Flexible(
                        child: Text(
                          'DoodhDirect',
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            fontSize: 20,
                            fontWeight: FontWeight.w800,
                            color: DoodhColors.ink,
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 2),
                  Text(
                    'Fresh dairy, delivered with care.',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
            ),
            const _CartButton(),
            const _NotificationButton(),
          ],
        ),
        const SizedBox(height: DoodhSpacing.xs),
        const Align(alignment: Alignment.centerRight, child: _WalletChip()),
      ],
    );
  }
}

/// Visible balance chip — the first of the header trio (Wallet → Cart →
/// Notification, per the business ask). Same controller state and destination
/// as before — the balance is only the server-authoritative value.
class _WalletChip extends StatelessWidget {
  const _WalletChip();

  @override
  Widget build(BuildContext context) {
    return Consumer(
      builder: (context, ref, _) {
        final wallet = ref.watch(
          walletControllerProvider.select((state) => state.wallet),
        );
        // No Flexible wrapper: the chip now lives on its own header row
        // (below the cart/notification icons), not inside the flexed title
        // row, so it can size to its content directly.
        return Tooltip(
          message: wallet == null
              ? 'Wallet'
              : 'Wallet ${wallet.formattedBalance}',
          child: InkWell(
            borderRadius: DoodhRadii.pillRadius,
            onTap: () => context.go('/wallet'),
            child: Container(
              key: const ValueKey('home-wallet-action'),
              padding: const EdgeInsets.symmetric(
                horizontal: DoodhSpacing.sm + 4,
                vertical: DoodhSpacing.xs + 2,
              ),
              decoration: BoxDecoration(
                color: DoodhColors.tanSurface,
                borderRadius: DoodhRadii.pillRadius,
                border: Border.all(color: DoodhColors.goldAccent),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Icon(
                    Icons.account_balance_wallet_outlined,
                    size: 18,
                    color: DoodhColors.goldAccent,
                  ),
                  const SizedBox(width: DoodhSpacing.xs + 2),
                  Text(
                    wallet?.formattedBalance ?? '',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.labelLarge
                        ?.copyWith(color: DoodhColors.goldAccent),
                  ),
                ],
              ),
            ),
          ),
        );
      },
    );
  }
}

/// "My Calendar" opens the dedicated customer-wide calendar screen (all
/// delivery sources, all dates, all active subscriptions). It never depends
/// on having an active subscription and never routes to the per-subscription
/// Delivery Calendar, which keeps serving subscription detail use cases.
void _pushMyCalendar(BuildContext context, WidgetRef ref) {
  context.push('/my-calendar');
}

/// Selected-date delivery details sheet. Reads ONLY the controllers the Home
/// already loaded — no new providers or API calls. Date selection itself is
/// presentation state and lives in [_MyDeliveriesState].
void _showDateDeliveries(BuildContext context, DateTime date) {
  HapticFeedback.selectionClick();
  showModalBottomSheet<void>(
    context: context,
    showDragHandle: true,
    isScrollControlled: true,
    builder: (sheetContext) => _DateDeliveriesSheet(date: date),
  );
}

class _MyDeliveries extends ConsumerStatefulWidget {
  const _MyDeliveries();

  @override
  ConsumerState<_MyDeliveries> createState() => _MyDeliveriesState();
}

class _MyDeliveriesState extends ConsumerState<_MyDeliveries> {
  /// The Home delivery calendar always shows one full week of dates. Single
  /// presentation constant so the window size is never a scattered magic
  /// number; the dates themselves stay dynamically generated.
  static const int visibleDays = 7;

  @override
  Widget build(BuildContext context) {
    final delivery = ref.watch(deliveryControllerProvider);
    final subscription = ref.watch(subscriptionControllerProvider);
    // Reference layout: one week of larger date tiles. Tiles shrink on
    // compact phones so all seven stay visible without overflow.
    final windowSize = DoodhBreakpoints.of(MediaQuery.sizeOf(context).width);
    final compact = windowSize == DoodhWindowSize.compact;
    final dates = List.generate(
      _MyDeliveriesState.visibleDays,
      (index) => _dateOnly(DateTime.now().add(Duration(days: index))),
    );
    final loading = delivery.isLoading || subscription.isLoading;
    final unavailable =
        delivery.isOffline ||
        subscription.isOffline ||
        delivery.errorMessage != null ||
        subscription.errorMessage != null;
    return DoodhCard(
      key: const ValueKey('my-deliveries-card'),
      color: DoodhColors.tanSurface,
      padding: const EdgeInsets.all(DoodhSpacing.lg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'My Deliveries',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.titleLarge
                      ?.copyWith(fontSize: 18, fontWeight: FontWeight.w800),
                ),
              ),
              TextButton(
                onPressed: () => context.push('/deliveries'),
                child: const Text('View all'),
              ),
            ],
          ),
          // Reference language: quick access to the vacation planner next to
          // the delivery calendar. Routes to the dedicated Add Vacation screen;
          // eligibility is always decided server-side.
          Row(
            children: [
              Expanded(
                // Single-line labels: at 14px the text wrapped to two lines
                // in each half-width button on phones.
                child: FilledButton.tonalIcon(
                  key: const ValueKey('home-set-vacation-action'),
                  onPressed: () => context.push('/subscriptions/vacation'),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size(0, 44),
                    padding: const EdgeInsets.symmetric(horizontal: 12),
                    textStyle: const TextStyle(fontSize: 13),
                  ),
                  icon: const Icon(Icons.beach_access_outlined, size: 18),
                  label: const Text(
                    'Set Vacation',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ),
              const SizedBox(width: DoodhSpacing.sm),
              Expanded(
                child: OutlinedButton.icon(
                  key: const ValueKey('home-my-calendar-action'),
                  onPressed: () => _pushMyCalendar(context, ref),
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size(0, 44),
                    padding: const EdgeInsets.symmetric(horizontal: 12),
                    textStyle: const TextStyle(fontSize: 13),
                  ),
                  icon: const Icon(Icons.calendar_month_outlined, size: 18),
                  label: const Text(
                    'My Calendar',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.sm),
          const SizedBox(height: 4),
          Text(
            loading
                ? 'Checking your delivery calendar...'
                : unavailable
                ? 'Delivery information is temporarily unavailable.'
                : 'Deliveries and subscriptions for the next seven days.',
          ),
          const SizedBox(height: DoodhSpacing.md),
          // Reference layout: SQUARE date tiles, sized so the week no longer
          // squeezes into one screen width — the strip scrolls to the right.
          SizedBox(
            height: compact ? 72 : 84,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              itemCount: dates.length,
              separatorBuilder: (_, _) => const SizedBox(width: 10),
              itemBuilder: (context, index) => _DeliveryDateTile(
                date: dates[index],
                deliveries: delivery.customerDeliveries,
                calendar: subscription.calendar,
                enabled: !loading && !unavailable,
                compact: compact,
                onSelected: (date) => _showDateDeliveries(context, date),
              ),
            ),
          ),
          const SizedBox(height: DoodhSpacing.md),
          if (loading)
            const LinearProgressIndicator(minHeight: 3)
          else if (unavailable)
            const DoodhStatusPill(
              label: 'Unable to refresh delivery status',
              tone: DoodhStatusTone.warning,
            )
          else
            const Wrap(
              spacing: 12,
              runSpacing: 6,
              children: [
                _LegendItem(
                  icon: Icons.remove_circle_outline,
                  label: 'Vacation / Skipped',
                  color: DoodhColors.coral,
                ),
                _LegendItem(
                  icon: Icons.check_circle,
                  label: 'Delivered',
                  color: DoodhColors.deliveredGreen,
                ),
                _LegendItem(
                  icon: Icons.event_available_outlined,
                  label: 'Upcoming',
                  color: DoodhColors.upcomingBlue,
                ),
                _LegendItem(
                  icon: Icons.circle_outlined,
                  label: 'No Delivery',
                  color: DoodhColors.muted,
                ),
              ],
            ),
        ],
      ),
    );
  }
}

/// A concrete upcoming delivery line resolved to its source record so the
/// card can act on it (modify → source detail, cancel → order/subscription
/// action, item name/quantity from the authoritative data).
class _UpcomingDeliveryLine {
  const _UpcomingDeliveryLine({
    required this.delivery,
    this.order,
    this.subscription,
    this.occurrence,
  });

  final CustomerDelivery delivery;

  /// Set for one-time-order deliveries (matched by the order's delivery
  /// reference number).
  final OrderSummary? order;

  /// Set for subscription occurrences (matched by the deterministic
  /// `SUB-{subscriptionPublicId:N}-{yyyyMMdd}` reference).
  final SubscriptionDetails? subscription;

  /// Set when the line came from the subscription calendar (pre-materialized
  /// occurrence) instead of a created Delivery row.
  final SubscriptionDelivery? occurrence;

  String get itemTitle {
    final orderItems = order?.items ?? const <OrderItem>[];
    if (orderItems.isNotEmpty) {
      return orderItems.map((item) => item.productName).join(', ');
    }
    final subscription = this.subscription;
    if (subscription != null) {
      return subscription.productName;
    }
    return delivery.sourceType.label;
  }

  String? get quantityLabel {
    final subscription = this.subscription;
    if (subscription != null) {
      return subscription.formattedQuantity;
    }
    final orderItems = order?.items ?? const <OrderItem>[];
    if (orderItems.length == 1) {
      final item = orderItems.single;
      return '${_formatQuantity(item.quantity)} ${item.unitOfMeasure}';
    }
    return null;
  }

  /// Only one-time orders offer Modify (opens the order detail).
  /// Subscriptions are managed (hold/cancel) from the subscription detail
  /// screen: schedule updates are disabled, so subscription cards never
  /// show Modify.
  bool get canModify => order != null;

  bool get canCancel =>
      occurrence == null &&
      ((order != null && order!.cancelledAt == null) ||
          (subscription?.status.canCancel ?? false));

  /// Pre-materialized calendar occurrences are skipped per-date (same
  /// semantics as the selected-date sheet), never by cancelling the whole
  /// subscription.
  bool get canSkip =>
      occurrence != null &&
      occurrence!.status.canSkip &&
      subscription?.status.canUpdate == true;

  static String _formatQuantity(double value) => value == value.roundToDouble()
      ? value.round().toString()
      : value.toStringAsFixed(2);
}

/// "Upcoming Deliveries" — reference-layout card strip of the next scheduled
/// deliveries with per-card Modify / Cancel and an Add Item affordance.
///
/// Composed entirely from state Home already loaded (customer deliveries +
/// orders + subscriptions): no new providers and no extra API calls. Cards
/// without a resolvable source still show tracking (tapping opens the
/// delivery detail screen) but never surface actions the client cannot
/// honour.
class UpcomingDeliveriesSection extends ConsumerWidget {
  const UpcomingDeliveriesSection({super.key});

  static const int _maxCards = 6;

  /// Today in India-local calendar time — the business date every customer
  /// (and the backend) schedules against. Device-timezone independent.
  static DateTime _todayDate() {
    final now = indiaNow();
    return DateTime(now.year, now.month, now.day);
  }

  /// India-local calendar date of an API timestamp. The backend serializes
  /// timestamps as India-local wall-clock without a 'Z' suffix, but cached or
  /// third-party data may carry UTC markers — normalize both to the India
  /// calendar so "today" never shifts with the device timezone.
  static DateTime _indiaDateOf(DateTime value) {
    final local = value.isUtc ? toIndiaTime(value) : value;
    return DateTime(local.year, local.month, local.day);
  }

  List<_UpcomingDeliveryLine> _upcomingLines(
    DeliveryState delivery,
    OrderState order,
    SubscriptionState subscription,
  ) {
    final todayDate = _todayDate();
    final ordersByDeliveryReference = {
      for (final order in order.orders)
        if (order.deliveryReferenceNumber != null)
          order.deliveryReferenceNumber!: order,
    };
    final subscriptionsByPublicId = {
      for (final item in subscription.subscriptions) item.publicId: item,
    };

    SubscriptionDetails? ownerSubscription;
    for (final item in subscription.subscriptions) {
      if (item.status == SubscriptionStatus.active) {
        ownerSubscription = item;
        break;
      }
    }

    // Owner provenance for calendar occurrences. loadAllCalendars keys each
    // subscription's calendar by its publicId; state seeded without the map
    // attributes the flat calendar to the first active subscription.
    final calendarsByOwner = subscription.calendarsBySubscription.isNotEmpty
        ? subscription.calendarsBySubscription
        : <String, List<SubscriptionDelivery>>{
            if (ownerSubscription != null)
              ownerSubscription.publicId: subscription.calendar,
          };

    final lines = <_UpcomingDeliveryLine>[];
    final seenReferences = <String>{};

    // Source 1 — materialized Delivery rows (created by the dairy manager's
    // "Materialize" run). These carry tracking and lifecycle actions.
    for (final item in delivery.customerDeliveries) {
      final date = _indiaDateOf(item.scheduledDate);
      // Upcoming = today onward, before any terminal state.
      if (date.isBefore(todayDate)) continue;
      if (item.status == DeliveryStatus.delivered ||
          item.status == DeliveryStatus.failed) {
        continue;
      }

      final order = ordersByDeliveryReference[item.referenceNumber];
      SubscriptionDetails? subscription;
      if (item.sourceType == DeliverySourceType.subscriptionOccurrence &&
          item.referenceNumber.length > 'SUB-'.length) {
        // SUB-{publicId:N}-{yyyyMMdd} — the GUID is the first 32 chars.
        final raw = item.referenceNumber
            .substring('SUB-'.length)
            .split('-')
            .first;
        final dashed = <String>[
          raw.substring(0, 8),
          raw.substring(8, 12),
          raw.substring(12, 16),
          raw.substring(16, 20),
          raw.substring(20, 32),
        ].join('-');
        subscription = subscriptionsByPublicId[dashed];
      }
      seenReferences.add(item.referenceNumber);
      lines.add(
        _UpcomingDeliveryLine(
          delivery: item,
          order: order,
          subscription: subscription,
        ),
      );
    }

    // Source 2 — subscription calendar occurrences not yet materialized into
    // Delivery rows (the manager materializes on their own cadence, so a
    // scheduled TODAY occurrence may have no Delivery yet). Without this the
    // section showed "Nothing scheduled" while the calendar band above it
    // correctly showed the day as Upcoming. Every active subscription's
    // occurrences are included — never only the first subscription's.
    for (final ownerEntry in calendarsByOwner.entries) {
      final owner =
          subscriptionsByPublicId[ownerEntry.key] ?? ownerSubscription;
      for (final item in ownerEntry.value) {
        if (item.status != SubscriptionDeliveryStatus.scheduled) continue;
        final date = _indiaDateOf(item.scheduledDate);
        if (date.isBefore(todayDate)) continue;
        // C# Guid "N" format (DeliveryService) is lowercase hex — keep the
        // publicId exactly as serialized so pre-materialized Delivery rows
        // dedupe against these occurrences instead of doubling up.
        final reference =
            'SUB-${ownerEntry.key.replaceAll('-', '')}-'
            '${date.year.toString().padLeft(4, '0')}'
            '${date.month.toString().padLeft(2, '0')}'
            '${date.day.toString().padLeft(2, '0')}';
        if (seenReferences.contains(reference)) continue;
        lines.add(
          _UpcomingDeliveryLine(
            delivery: CustomerDelivery(
              deliveryId: 'occurrence-${item.publicId}',
              sourceType: DeliverySourceType.subscriptionOccurrence,
              referenceNumber: reference,
              status: DeliveryStatus.readyForAssignment,
              scheduledDate: item.scheduledDate,
              destinationAddress: item.address,
              assignedEmployeeId: null,
              assignedEmployeeName: null,
              isTrackingActive: false,
              latestLocation: null,
              completedAt: null,
              failedAt: null,
              failureReason: null,
              activeOtp: null,
            ),
            order: null,
            subscription: owner,
            occurrence: item,
          ),
        );
      }
    }

    lines.sort(
      (a, b) => a.delivery.scheduledDate.compareTo(b.delivery.scheduledDate),
    );
    return lines;
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final delivery = ref.watch(deliveryControllerProvider);
    final order = ref.watch(orderControllerProvider);
    final subscription = ref.watch(subscriptionControllerProvider);
    final loading = delivery.isLoading || order.isLoading;
    final lines = loading
        ? const <_UpcomingDeliveryLine>[]
        : _upcomingLines(delivery, order, subscription);

    // Business ask: "Upcoming Deliveries" is its own white section card,
    // visually separate from the tan My-Deliveries card above it.
    return DoodhCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  'Upcoming Deliveries',
                  key: const ValueKey('home-upcoming-deliveries-title'),
                  style: Theme.of(context).textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.w800),
                ),
              ),
              TextButton.icon(
                key: const ValueKey('home-add-item-action'),
                onPressed: () => context.push('/catalogue'),
                icon: const Icon(Icons.add, size: 18),
                label: const Text('Add Item'),
              ),
            ],
          ),
          const SizedBox(height: DoodhSpacing.sm),
          if (loading)
            const LinearProgressIndicator(minHeight: 3)
          else if (lines.isEmpty)
            Text(
              'Nothing scheduled right now. Add an item to start a delivery.',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: Theme.of(context).colorScheme.onSurfaceVariant,
              ),
            )
          else
            SizedBox(
              // Tall enough for date + item + quantity + status + actions at
              // the default text scale without vertical overflow.
              height: 196,
              child: ListView.separated(
                key: const ValueKey('home-upcoming-deliveries-list'),
                scrollDirection: Axis.horizontal,
                itemCount: lines.length.clamp(0, _maxCards),
                separatorBuilder: (_, _) =>
                    const SizedBox(width: DoodhSpacing.sm),
                itemBuilder: (context, index) =>
                    _UpcomingDeliveryCard(line: lines[index]),
              ),
            ),
        ],
      ),
    );
  }
}

class _UpcomingDeliveryCard extends ConsumerWidget {
  const _UpcomingDeliveryCard({required this.line});

  final _UpcomingDeliveryLine line;

  Future<void> _skip(BuildContext context, WidgetRef ref) async {
    final subscription = line.subscription;
    final occurrence = line.occurrence;
    if (subscription == null || occurrence == null) return;
    final messenger = ScaffoldMessenger.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Skip this delivery?'),
        content: Text(
          'The ${line.itemTitle} delivery on '
          '${occurrence.scheduledDate.day}/'
          '${occurrence.scheduledDate.month}/'
          '${occurrence.scheduledDate.year} will be skipped.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Keep delivery'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Skip'),
          ),
        ],
      ),
    );
    if (confirmed != true || !context.mounted) return;
    final ok = await ref
        .read(subscriptionControllerProvider.notifier)
        .skip(subscription.publicId, occurrence.publicId);
    messenger.showSnackBar(
      SnackBar(
        content: Text(
          ok
              ? 'Delivery skipped.'
              : 'This delivery can no longer be skipped. The cutoff may have '
                    'passed or it is already being prepared.',
        ),
      ),
    );
  }

  Future<void> _cancel(BuildContext context, WidgetRef ref) async {
    final order = line.order;
    final subscription = line.subscription;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Cancel this delivery?'),
        content: Text(
          order != null
              ? 'The whole order ${order.orderNumber} will be cancelled.'
              : 'The subscription ${subscription?.productName ?? ''} will be '
                    'cancelled — not just this date.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Keep'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Cancel delivery'),
          ),
        ],
      ),
    );
    if (confirmed != true || !context.mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    if (order != null) {
      final ok = await ref
          .read(orderControllerProvider.notifier)
          .cancel(order.publicId);
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            ok ? 'Order cancelled.' : 'Could not cancel the order.',
          ),
        ),
      );
    } else if (subscription != null) {
      final ok = await ref
          .read(subscriptionControllerProvider.notifier)
          .cancel(subscription.publicId);
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            ok
                ? 'Subscription cancelled.'
                : 'Could not cancel the subscription.',
          ),
        ),
      );
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final delivery = line.delivery;
    final isSubscription =
        delivery.sourceType == DeliverySourceType.subscriptionOccurrence;

    return SizedBox(
      width: 264,
      child: Card(
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          // Pre-materialized occurrences have no Delivery row to open — tap
          // routes to the owning subscription instead.
          onTap: line.occurrence == null
              ? () => context.push('/deliveries/${delivery.deliveryId}')
              : () => context.push(
                  '/subscriptions/${line.subscription?.publicId ?? ''}',
                ),
          child: Padding(
            padding: const EdgeInsets.symmetric(
              horizontal: DoodhSpacing.md,
              vertical: DoodhSpacing.sm,
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // Full-width date header, matching the reference layout.
                Text(
                  _upcomingDateLabel(delivery.scheduledDate),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: FontWeight.w800,
                  ),
                ),
                const SizedBox(height: DoodhSpacing.xs),
                Text(
                  line.itemTitle,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
                if (line.quantityLabel != null)
                  Text(
                    line.quantityLabel!,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),
                const SizedBox(height: DoodhSpacing.xs),
                // Compact status: short label, no long delivery jargon.
                DoodhStatusPill(
                  label: isSubscription ? 'Confirmed' : delivery.status.label,
                  tone: DoodhStatusTone.success,
                ),
                const SizedBox(height: DoodhSpacing.xs),
                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    if (line.canModify)
                      TextButton(
                        key: ValueKey(
                          'home-upcoming-modify-${delivery.deliveryId}',
                        ),
                        onPressed: () => context.push(
                          '/orders/${line.order!.publicId}',
                        ),
                        child: const Text('Modify'),
                      ),
                    if (line.canSkip)
                      TextButton(
                        key: ValueKey(
                          'home-upcoming-skip-${delivery.deliveryId}',
                        ),
                        onPressed: () => _skip(context, ref),
                        child: const Text('Skip'),
                      ),
                    if (line.canCancel)
                      TextButton(
                        key: ValueKey(
                          'home-upcoming-cancel-${delivery.deliveryId}',
                        ),
                        onPressed: () => _cancel(context, ref),
                        child: const Text('Cancel'),
                      ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  String _upcomingDateLabel(DateTime value) {
    const weekdays = [
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ];
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
    return '${weekdays[value.weekday - 1]}, '
        '${value.day} ${months[value.month - 1]}';
  }
}

class _LegendItem extends StatelessWidget {
  const _LegendItem({
    required this.icon,
    required this.label,
    required this.color,
  });

  final IconData icon;
  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Container(
        width: 12,
        height: 12,
        decoration: BoxDecoration(color: color, shape: BoxShape.circle),
        child: Icon(icon, size: 8, color: Colors.white),
      ),
      const SizedBox(width: 5),
      // Flexible so long labels can never push the row past narrow phones
      // (320px): the wrap wraps items, but a single item row must also fit.
      Flexible(
        child: Text(
          label,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: Theme.of(context).textTheme.bodySmall,
        ),
      ),
    ],
  );
}

class _DeliveryDateTile extends StatelessWidget {
  const _DeliveryDateTile({
    required this.date,
    required this.deliveries,
    required this.calendar,
    required this.enabled,
    required this.onSelected,
    this.compact = false,
  });

  final DateTime date;
  final List<CustomerDelivery> deliveries;
  final List<SubscriptionDelivery> calendar;
  final bool enabled;

  /// Invoked when the customer taps the tile; opens the selected-date
  /// delivery details surface. Only wired while [enabled].
  final ValueChanged<DateTime> onSelected;

  /// Compact phones shrink the tile so all seven days fit without overflow.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final today = _dateOnly(DateTime.now()) == date;
    CustomerDelivery? customerDelivery;
    for (final item in deliveries) {
      if (_dateOnly(item.scheduledDate) == date) {
        customerDelivery = item;
        break;
      }
    }
    SubscriptionDelivery? subscriptionDelivery;
    for (final item in calendar) {
      if (_dateOnly(item.scheduledDate) == date) {
        subscriptionDelivery = item;
        break;
      }
    }
    final status =
        customerDelivery?.status.label ?? subscriptionDelivery?.status.label;
    final label = status ?? (enabled ? 'No Delivery' : 'Status unavailable');
    // Reference language: the tile carries a colored status dot (paired with
    // the text label for semantics — never colour alone).
    final dotColor = customerDelivery != null
        ? _deliveryDotColor(customerDelivery.status)
        : subscriptionDelivery != null
        ? _subscriptionDotColor(subscriptionDelivery.status)
        : enabled
        ? DoodhColors.muted
        : DoodhColors.muted.withValues(alpha: 0.4);
    final dotIcon = customerDelivery != null
        ? _deliveryStatusIcon(customerDelivery.status)
        : subscriptionDelivery != null
        ? _subscriptionStatusIcon(subscriptionDelivery.status)
        : Icons.circle_outlined;
    return Semantics(
      label: '${_weekday(date)}, ${date.day}, $label${today ? ', today' : ''}',
      button: true,
      selected: today,
      enabled: enabled,
      child: InkWell(
        key: ValueKey('home-date-tile-${_isoDate(date)}'),
        borderRadius: DoodhRadii.mdRadius,
        onTap: enabled ? () => onSelected(date) : null,
        child: Container(
          // Reference layout: SQUARE tiles — the day number leads, the
          // weekday follows, the status dot anchors the bottom.
          width: compact ? 72.0 : 84.0,
          height: compact ? 72.0 : 84.0,
          decoration: BoxDecoration(
            color: today ? DoodhColors.tealDark : Colors.white,
            borderRadius: DoodhRadii.mdRadius,
            border: Border.all(
              color: today ? DoodhColors.tealDark : DoodhColors.line,
              width: today ? 2 : 1,
            ),
          ),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Text(
                '${date.day}',
                style: TextStyle(
                  fontSize: compact ? 18 : 22,
                  height: 1.1,
                  fontWeight: FontWeight.w800,
                  color: today ? Colors.white : DoodhColors.ink,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                _weekday(date),
                style: TextStyle(
                  fontSize: compact ? 10 : 11,
                  letterSpacing: .3,
                  color: today ? Colors.white70 : DoodhColors.muted,
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 5),
              // Colored status dot: a plain filled dot for "no record" days and
              // a tiny status glyph inside the dot for real records.
              Container(
                width: 14,
                height: 14,
                decoration: BoxDecoration(
                  color: dotColor,
                  shape: BoxShape.circle,
                ),
                child: customerDelivery == null && subscriptionDelivery == null
                    ? null
                    : Icon(dotIcon, size: 9, color: Colors.white),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

String _isoDate(DateTime value) =>
    '${value.year.toString().padLeft(4, '0')}-'
    '${value.month.toString().padLeft(2, '0')}-'
    '${value.day.toString().padLeft(2, '0')}';

/// Modal bottom sheet listing the deliveries for one selected calendar date.
///
/// Data comes exclusively from the controllers Home already loaded:
/// [DeliveryController.customerDeliveries] (order-driven deliveries) and
/// [SubscriptionController.calendar] (subscription occurrences, whose product
/// names resolve through the loaded subscription details). Loading renders a
/// spinner — never a false "No delivery" — and offline/error reuses the same
/// unavailable language as the calendar band.
class _DateDeliveriesSheet extends ConsumerWidget {
  const _DateDeliveriesSheet({required this.date});

  final DateTime date;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final delivery = ref.watch(deliveryControllerProvider);
    final subscription = ref.watch(subscriptionControllerProvider);
    final catalogue = ref.watch(catalogueControllerProvider);
    final target = _dateOnly(date);

    final dayDeliveries = delivery.customerDeliveries
        .where((item) => _dateOnly(item.scheduledDate) == target)
        .toList(growable: false);
    final dayOccurrences = subscription.calendar
        .where((item) => _dateOnly(item.scheduledDate) == target)
        .toList(growable: false);

    // Product presentation (names, images) resolves against the catalogue
    // Home already loaded; nothing is fetched here.
    final imageUrlByProductId = <String, String?>{
      for (final product in catalogue.products)
        product.publicId: product.imageUrl,
    };
    // Occurrence ownership: loadAllCalendars keys each subscription's
    // occurrences by its publicId, so every row resolves its REAL owner —
    // two subscriptions on one date keep their own product identity. State
    // seeded without the provenance map falls back to attributing the flat
    // calendar to the first active subscription.
    final subscriptionsByPublicId = {
      for (final item in subscription.subscriptions) item.publicId: item,
    };
    final calendarOwner = subscription.subscriptions
        .where((item) => item.status == SubscriptionStatus.active)
        .firstOrNull;
    SubscriptionDetails? ownerOf(SubscriptionDelivery occurrence) {
      for (final entry in subscription.calendarsBySubscription.entries) {
        if (entry.value.any((item) => item.publicId == occurrence.publicId)) {
          return subscriptionsByPublicId[entry.key] ?? calendarOwner;
        }
      }
      return calendarOwner;
    }

    Widget occurrenceRow(SubscriptionDelivery occurrence) {
      final owner = ownerOf(occurrence);
      return Padding(
        padding: const EdgeInsets.only(bottom: DoodhSpacing.sm),
        child: _DateSubscriptionRow(
          delivery: occurrence,
          owner: owner,
          imageUrl: owner == null ? null : imageUrlByProductId[owner.productId],
        ),
      );
    }

    final loading = delivery.isLoading || subscription.isLoading;
    final unavailable =
        delivery.isOffline ||
        subscription.isOffline ||
        delivery.errorMessage != null ||
        subscription.errorMessage != null;

    return SafeArea(
      child: ConstrainedBox(
        constraints: BoxConstraints(
          maxHeight: MediaQuery.sizeOf(context).height * 0.75,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(
                DoodhSpacing.lg,
                DoodhSpacing.xs,
                DoodhSpacing.lg,
                DoodhSpacing.xs,
              ),
              child: Text(
                'Deliveries for ${date.day}/${date.month}/${date.year}',
                key: const ValueKey('date-deliveries-heading'),
                style: Theme.of(context).textTheme.titleMedium,
              ),
            ),
            const Divider(color: DoodhColors.line, height: 1),
            if (loading)
              const Padding(
                key: ValueKey('date-deliveries-loading'),
                padding: EdgeInsets.all(DoodhSpacing.xl),
                child: Column(
                  children: [
                    CircularProgressIndicator(),
                    SizedBox(height: DoodhSpacing.md),
                    Text('Checking deliveries for this day...'),
                  ],
                ),
              )
            else if (unavailable)
              const Padding(
                key: ValueKey('date-deliveries-unavailable'),
                padding: EdgeInsets.all(DoodhSpacing.lg),
                child: Column(
                  children: [
                    DoodhStatusPill(
                      label: 'Delivery details unavailable',
                      tone: DoodhStatusTone.warning,
                    ),
                    SizedBox(height: DoodhSpacing.sm),
                    Text(
                      'We could not load delivery information right now. '
                      'Pull to refresh on Home and try again.',
                      textAlign: TextAlign.center,
                    ),
                  ],
                ),
              )
            else if (dayDeliveries.isEmpty && dayOccurrences.isEmpty)
              const Padding(
                key: ValueKey('date-deliveries-empty'),
                padding: EdgeInsets.all(DoodhSpacing.xl),
                child: Column(
                  children: [
                    Icon(
                      Icons.event_busy_outlined,
                      size: 34,
                      color: DoodhColors.muted,
                    ),
                    SizedBox(height: DoodhSpacing.sm),
                    Text('No delivery scheduled for this day.'),
                  ],
                ),
              )
            else
              Flexible(
                child: ListView(
                  shrinkWrap: true,
                  padding: const EdgeInsets.fromLTRB(
                    DoodhSpacing.lg,
                    DoodhSpacing.md,
                    DoodhSpacing.lg,
                    DoodhSpacing.lg,
                  ),
                  children: [
                    for (final occurrence in dayOccurrences)
                      occurrenceRow(occurrence),
                    for (final item in dayDeliveries)
                      Padding(
                        padding: const EdgeInsets.only(bottom: DoodhSpacing.sm),
                        child: _DateDeliveryRow(delivery: item),
                      ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// One subscription occurrence on the selected date. Every occurrence is
/// listed separately — occurrences are never merged into fabricated single
/// deliveries.
class _DateSubscriptionRow extends ConsumerWidget {
  const _DateSubscriptionRow({
    required this.delivery,
    required this.owner,
    required this.imageUrl,
  });

  final SubscriptionDelivery delivery;

  /// The active subscription whose calendar Home loaded — the owner of this
  /// occurrence. Null (e.g. no active subscription) disables Skip and degrades
  /// the product identity to a generic label rather than inventing one.
  final SubscriptionDetails? owner;

  /// Product image URL resolved by the sheet from already-loaded catalogue
  /// data. Null keeps the branded fallback.
  final String? imageUrl;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final subscriptionState = ref.watch(subscriptionControllerProvider);
    // Skip stays available only for Scheduled occurrences (status.canSkip)
    // whose owning subscription is active — mirroring the subscriptions
    // screen; backend remains authoritative.
    final canSkip =
        delivery.status.canSkip &&
        owner != null &&
        owner!.status == SubscriptionStatus.active;
    return DoodhCard(
      color: DoodhColors.mint,
      semanticLabel:
          'Subscription delivery, ${formatQuantity(delivery.quantity)}, '
          '${delivery.slot.apiValue}, ${delivery.status.label}',
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Bounded width: the shared image component stretches to its parent,
          // so it must sit in a sized box inside this Row.
          SizedBox(
            width: 56,
            child: DoodhProductImage(
              imageUrl: imageUrl,
              height: 56,
              borderRadius: DoodhRadii.smRadius,
              icon: Icons.event_repeat_outlined,
              fallbackLabel: '',
            ),
          ),
          const SizedBox(width: DoodhSpacing.md),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  // Product identity comes only from the owning subscription
                  // record loaded on Home; nothing is invented.
                  owner?.productName ?? 'Subscription delivery',
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                const SizedBox(height: 2),
                Text(
                  '${delivery.slot.apiValue} · '
                  '${formatQuantity(delivery.quantity)} quantity',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
                Text(
                  delivery.address,
                  style: Theme.of(context).textTheme.bodySmall
                      ?.copyWith(color: DoodhColors.muted),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                const SizedBox(height: DoodhSpacing.xs),
                DoodhStatusPill(
                  label: delivery.status.label,
                  tone: _subscriptionStatusTone(delivery.status),
                ),
              ],
            ),
          ),
          if (canSkip)
            IconButton(
              key: ValueKey('date-delivery-skip-${delivery.publicId}'),
              tooltip: 'Skip delivery',
              onPressed: subscriptionState.isSaving
                  ? null
                  : () => _confirmSkip(context, ref),
              icon: const Icon(Icons.event_busy_outlined),
            ),
        ],
      ),
    );
  }

  Future<void> _confirmSkip(BuildContext context, WidgetRef ref) async {
    // Captured before the dialog's async gap so no BuildContext is used after
    // awaiting (use_build_context_synchronously).
    final messenger = ScaffoldMessenger.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Skip this delivery?'),
        content: Text(
          'The delivery scheduled for '
          '${delivery.scheduledDate.day}/${delivery.scheduledDate.month}/'
          '${delivery.scheduledDate.year} will be skipped.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('Keep delivery'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('Skip'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    final updated = await ref
        .read(subscriptionControllerProvider.notifier)
        .skip(owner!.publicId, delivery.publicId);
    messenger.hideCurrentSnackBar();
    messenger.showSnackBar(
      SnackBar(
        content: Text(
          updated
              ? 'Delivery skipped.'
              : 'This delivery can no longer be skipped. The cutoff may have '
                    'passed or it is already being prepared.',
        ),
      ),
    );
  }
}

/// One order-driven delivery on the selected date.
class _DateDeliveryRow extends StatelessWidget {
  const _DateDeliveryRow({required this.delivery});

  final CustomerDelivery delivery;

  @override
  Widget build(BuildContext context) => DoodhCard(
    color: Colors.white,
    semanticLabel:
        '${delivery.sourceType.label} delivery ${delivery.referenceNumber}, '
        '${delivery.status.label}',
    onTap: () => context.push('/deliveries/${delivery.deliveryId}'),
    child: Row(
      children: [
        CircleAvatar(
          radius: 24,
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
              Text(
                '${delivery.sourceType.label} · ${delivery.referenceNumber}',
                style: Theme.of(context).textTheme.titleSmall,
              ),
              const SizedBox(height: 2),
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

DoodhStatusTone _subscriptionStatusTone(SubscriptionDeliveryStatus status) =>
    switch (status) {
      SubscriptionDeliveryStatus.delivered => DoodhStatusTone.success,
      SubscriptionDeliveryStatus.failed => DoodhStatusTone.error,
      SubscriptionDeliveryStatus.skipped ||
      SubscriptionDeliveryStatus.cancelled => DoodhStatusTone.warning,
      _ => DoodhStatusTone.neutral,
    };

IconData _deliveryStatusIcon(DeliveryStatus status) => switch (status) {
  DeliveryStatus.delivered => Icons.check_circle,
  DeliveryStatus.failed => Icons.error_outline,
  DeliveryStatus.readyForAssignment ||
  DeliveryStatus.assigned ||
  DeliveryStatus.pickedUp ||
  DeliveryStatus.outForDelivery ||
  DeliveryStatus.arrived => Icons.local_shipping_outlined,
  DeliveryStatus.unknown => Icons.help_outline,
};

IconData _subscriptionStatusIcon(SubscriptionDeliveryStatus status) =>
    switch (status) {
      SubscriptionDeliveryStatus.delivered => Icons.check_circle,
      SubscriptionDeliveryStatus.failed => Icons.error_outline,
      SubscriptionDeliveryStatus.skipped ||
      SubscriptionDeliveryStatus.cancelled => Icons.remove_circle_outline,
      SubscriptionDeliveryStatus.scheduled => Icons.event_available_outlined,
      SubscriptionDeliveryStatus.unknown => Icons.help_outline,
    };

/// Status dot colors for the home date strip. They reuse the shared semantic
/// palette so the strip matches the app's status language.
Color _deliveryDotColor(DeliveryStatus status) => switch (status) {
  DeliveryStatus.delivered => DoodhColors.deliveredGreen,
  DeliveryStatus.failed => DoodhColors.coral,
  DeliveryStatus.readyForAssignment ||
  DeliveryStatus.assigned ||
  DeliveryStatus.pickedUp ||
  DeliveryStatus.outForDelivery ||
  DeliveryStatus.arrived => DoodhColors.upcomingBlue,
  DeliveryStatus.unknown => DoodhColors.muted,
};

Color _subscriptionDotColor(SubscriptionDeliveryStatus status) =>
    switch (status) {
      SubscriptionDeliveryStatus.delivered => DoodhColors.deliveredGreen,
      SubscriptionDeliveryStatus.failed => DoodhColors.coral,
      SubscriptionDeliveryStatus.skipped ||
      SubscriptionDeliveryStatus.cancelled => DoodhColors.coral,
      SubscriptionDeliveryStatus.scheduled => DoodhColors.upcomingBlue,
      SubscriptionDeliveryStatus.unknown => DoodhColors.muted,
    };

DateTime _dateOnly(DateTime value) =>
    DateTime(value.year, value.month, value.day);

String _weekday(DateTime value) =>
    const ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'][value.weekday - 1];

class _QuickAction extends StatelessWidget {
  const _QuickAction({
    required this.icon,
    required this.label,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Card(
    child: InkWell(
      borderRadius: DoodhRadii.md,
      onTap: onTap,
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(icon, color: DoodhColors.teal, size: 28),
            const SizedBox(height: 10),
            Flexible(
              child: Text(
                label,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.titleMedium,
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

/// Trust signals shown on the customer and guest entry surfaces.
///
/// These restate existing product promises only — the app never invents
/// promotions, prices or availability claims here.
const List<DoodhTrustItem> _homeTrustItems = [
  DoodhTrustItem(icon: Icons.verified_outlined, label: 'Farm fresh'),
  DoodhTrustItem(icon: Icons.home_work_outlined, label: 'Doorstep delivery'),
  DoodhTrustItem(icon: Icons.lock_outline, label: 'Secure payments'),
];

/// Adds a product to the cart without a confirmation snackbar — the header
/// cart badge reflects the change immediately.
void _addProductToCart(WidgetRef ref, CatalogueProduct product) {
  ref.read(orderControllerProvider.notifier).setCartItem(product, 1);
}

/// Compact category discovery for the customer entry surface.
///
/// Category filtering stays server-side (unchanged API): selecting a category
/// reuses [CatalogueController.selectCategory] and then opens the catalogue, so
/// the entry surface never duplicates catalogue state.
class _HomeCategoryDiscovery extends ConsumerWidget {
  const _HomeCategoryDiscovery({required this.onBrowseCatalogue});

  final VoidCallback onBrowseCatalogue;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(catalogueControllerProvider);
    if (state.categories.isEmpty) return const SizedBox.shrink();
    final options = [
      for (final category in state.categories)
        DoodhCategoryOption(id: category.publicId, label: category.name),
    ];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: DoodhSpacing.xl),
        DoodhSectionHeader(
          title: 'Shop by category',
          action: TextButton(
            onPressed: onBrowseCatalogue,
            child: const Text('Browse all'),
          ),
        ),
        const SizedBox(height: DoodhSpacing.md),
        DoodhCategoryRail(
          key: const ValueKey('home-category-rail'),
          options: options,
          selectedId: state.selectedCategoryId,
          onSelected: (categoryId) {
            ref
                .read(catalogueControllerProvider.notifier)
                .selectCategory(categoryId);
            context.push('/catalogue');
          },
        ),
      ],
    );
  }
}

/// Data-driven product discovery for the customer entry surface.
///
/// Uses only products already loaded by [CatalogueController] — no extra API
/// calls and no invented prices, discounts or availability. When the catalogue
/// has nothing to show yet (loading, offline or genuinely empty) the section is
/// omitted rather than rendering fabricated content.
class _HomeProductDiscovery extends ConsumerWidget {
  const _HomeProductDiscovery({required this.onBrowseCatalogue});

  final VoidCallback onBrowseCatalogue;

  static const int _maxItems = 8;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(catalogueControllerProvider);
    if (state.products.isEmpty) return const SizedBox.shrink();

    final searching = state.searchQuery.trim().isNotEmpty;
    final visible = state.visibleProducts;

    if (searching && visible.isEmpty) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const SizedBox(height: DoodhSpacing.xl),
          DoodhSectionHeader(
            title: 'Search results',
            action: TextButton(
              onPressed: onBrowseCatalogue,
              child: const Text('Open catalogue'),
            ),
          ),
          const SizedBox(height: DoodhSpacing.md),
          EmptyStatePanel(
            title: 'No matching products',
            message:
                'We could not find "${state.searchQuery.trim()}" in the '
                'catalogue yet.',
            action: DoodhButton(
              label: 'Browse catalogue',
              icon: Icons.storefront_outlined,
              onPressed: onBrowseCatalogue,
            ),
          ),
        ],
      );
    }

    final products = searching ? visible : state.products;
    final featured = products.take(_maxItems).toList(growable: false);
    final cart = ref.watch(
      orderControllerProvider.select((orderState) => orderState.cart),
    );
    final cartQuantities = <String, double>{
      for (final item in cart) item.product.publicId: item.quantity,
    };

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SizedBox(height: DoodhSpacing.xl),
        DoodhSectionHeader(
          title: searching ? 'Search results' : 'Fresh picks',
          action: TextButton(
            onPressed: onBrowseCatalogue,
            child: const Text('Browse all'),
          ),
        ),
        const SizedBox(height: DoodhSpacing.md),
        DoodhGrid(
          // Matches the catalogue grid so home "Fresh picks" also stays
          // 2-up on phones; minColumns holds it on narrow devices where
          // the width estimate alone would floor to 1.
          minItemWidth: 150,
          maxColumns: 4,
          minColumns: 2,
          children: [
            for (final product in featured)
              CatalogueProductCard(
                key: ValueKey('home-product-${product.publicId}'),
                product: product,
                cartQuantity: cartQuantities[product.publicId],
                onOpen: () =>
                    context.push('/catalogue/products/${product.publicId}'),
                onSubscribe: () => context.push('/subscriptions/new'),
                onAddToCart: () => _addProductToCart(ref, product),
              ),
          ],
        ),
      ],
    );
  }
}

class _DeliveryHomeActions extends StatelessWidget {
  const _DeliveryHomeActions({
    required this.roles,
    required this.permissions,
    required this.branchIds,
    required this.branchDetails,
  });

  final List<String> roles;
  final List<String> permissions;
  final List<int> branchIds;
  final List<AuthUserBranchInfo> branchDetails;

  bool get _canManage =>
      roles.contains('DELIVERY_MANAGER') ||
      permissions.contains('DELIVERIES.READ_BRANCH');

  @override
  Widget build(BuildContext context) {
    final branchId = branchIds.isEmpty ? null : branchIds.first;
    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        Text(
          _canManage ? 'Delivery management' : 'Delivery route',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
        const SizedBox(height: 16),
        if (!_canManage)
          Card(
            child: ListTile(
              leading: const Icon(Icons.route_outlined),
              title: const Text("Today's deliveries"),
              subtitle: const Text('Operate deliveries assigned to you'),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/delivery'),
            ),
          ),
        if (_canManage && branchId != null)
          Card(
            child: ListTile(
              leading: const Icon(Icons.local_shipping_outlined),
              title: Text(
                '${branchDisplayName(branchDetails, branchId)} deliveries',
              ),
              subtitle: const Text(
                'Assign staff and monitor delivery progress',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () =>
                  context.push('/delivery-management/branch/$branchId'),
            ),
          ),
        if (_canManage && branchId == null)
          const StatePanel(
            icon: Icons.location_off_outlined,
            title: 'No branch assigned',
            message: 'A branch assignment is required to manage deliveries.',
          ),
      ],
    );
  }
}

class _SupportHomeActions extends StatelessWidget {
  const _SupportHomeActions();

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        Text(
          'Customer support',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
        const SizedBox(height: 8),
        const Text(
          'Review customer care workflows and branch operations requests.',
        ),
        const SizedBox(height: 16),
        Card(
          child: ListTile(
            leading: const Icon(Icons.assignment_return_outlined),
            title: const Text('Refund / replacement requests'),
            subtitle: const Text(
              'Review customer requests, approve, reject, or complete them',
            ),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => context.push('/staff/refund-replacements'),
          ),
        ),
      ],
    );
  }
}

class _DairyHomeActions extends ConsumerWidget {
  const _DairyHomeActions();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final user = ref.watch(sessionControllerProvider).session?.user;
    final branchIds = user?.branchIds ?? const <int>[];
    final branchId = branchIds.isEmpty ? null : branchIds.first;
    final branchName = branchId == null ? null : user?.branchName(branchId);
    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        Text(
          'Dairy operations',
          style: Theme.of(context).textTheme.headlineSmall,
        ),
        const SizedBox(height: 8),
        const Text(
          'Record production and manage operational milk availability.',
        ),
        const SizedBox(height: 16),
        if (branchId == null)
          const StatePanel(
            icon: Icons.location_off_outlined,
            title: 'No branch assigned',
            message: 'A branch assignment is required to manage dairy operations and deliveries.',
          )
        else ...[
          Card(
            child: ListTile(
              leading: const Icon(Icons.agriculture_outlined),
              title: Text(
                '${branchName ?? 'Branch $branchId'} dairy dashboard',
              ),
              subtitle: const Text(
                'Production, batches, availability, and usage',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => context.push('/dairy/dashboard'),
            ),
          ),
          const SizedBox(height: 12),
          Card(
            child: ListTile(
              leading: const Icon(Icons.local_shipping_outlined),
              title: const Text('Delivery Management'),
              subtitle: const Text(
                'Manage deliveries, generate subscription deliveries, and assign deliveries to delivery staff.',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () =>
                  context.push('/delivery-management/branch/$branchId'),
            ),
          ),
          const SizedBox(height: 12),
          Card(
            child: ListTile(
              leading: const Icon(Icons.assignment_return_outlined),
              title: const Text('Refund / replacement requests'),
              subtitle: const Text(
                'Review customer requests, approve, reject, or complete them',
              ),
              trailing: const Icon(Icons.chevron_right),
              onTap: () =>
                  context.push('/staff/refund-replacements?branchId=$branchId'),
            ),
          ),
        ],
      ],
    );
  }
}

class _AdminHomeActions extends ConsumerWidget {
  const _AdminHomeActions({
    required this.permissions,
    required this.branchIds,
    required this.branchDetails,
  });

  final List<String> permissions;
  final List<int> branchIds;
  final List<AuthUserBranchInfo> branchDetails;

  bool get _canReadCameras =>
      permissions.contains('CAMERAS.READ') ||
      permissions.contains('CAMERAS.MANAGE');

  bool get _canReadNumberSeries =>
      permissions.contains('SETUP.NUMBER_SERIES.READ') ||
      permissions.contains('SETUP.NUMBER_SERIES.MANAGE');

  bool get _canReadTaxCharges =>
      permissions.contains('SETUP.TAX_CHARGES.READ') ||
      permissions.contains('SETUP.TAX_CHARGES.MANAGE');

  bool get _canReadOtpProvider =>
      permissions.contains('SETUP.OTP_PROVIDER.READ') ||
      permissions.contains('SETUP.OTP_PROVIDER.MANAGE');

  bool get _canReadIntegrations =>
      permissions.contains('SETUP.INTEGRATIONS.READ') ||
      permissions.contains('SETUP.INTEGRATIONS.MANAGE');

  bool get _canReadRefundReplacementConfig =>
      permissions.contains('SETUP.REFUND_REPLACEMENT.READ') ||
      permissions.contains('SETUP.REFUND_REPLACEMENT.MANAGE');

  bool get _canReadBranding =>
      permissions.contains('SETUP.BRANDING.READ') ||
      permissions.contains('SETUP.BRANDING.MANAGE');

  bool get _canManageEmployees =>
      permissions.contains('EMPLOYEES.READ') ||
      permissions.contains('EMPLOYEES.MANAGE');

  bool get _canReadBranches =>
      permissions.contains('BRANCHES.READ') ||
      permissions.contains('BRANCHES.MANAGE');

  bool get _canReadRefundReplacements =>
      permissions.contains('REFUND_REPLACEMENT.READ_BRANCH') ||
      permissions.contains('REFUND_REPLACEMENT.MANAGE_BRANCH');

  bool get _canReadReports => permissions.any(
    const {
      'REPORTS.ADMINISTRATION.READ',
      'REPORTS.FINANCIAL.READ',
      'REPORTS.OPERATIONS.READ',
      'REPORTS.MILK_TESTS.READ',
      'REPORTS.AUDIT.READ',
    }.contains,
  );

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return ListView(
      padding: DoodhSpacing.pagePadding,
      children: [
        if (_canReadReports) ...[
          Text(
            'Administration',
            style: Theme.of(context).textTheme.headlineSmall,
          ),
          const SizedBox(height: 8),
          const Text('Review operational metrics and authorized reports.'),
          const SizedBox(height: 8),
        ],
        if (_canManageEmployees) ...[
          const _AdminSectionHeader('User & Access'),
          _AdminTileGrid(
            items: [
              _AdminTileData(
                icon: Icons.group_outlined,
                label: 'Employees',
                subtitle: 'Staff accounts & roles',
                onTap: () => context.push('/admin/employees'),
              ),
            ],
          ),
        ],
        if (_canReadBranches) ...[
          const _AdminSectionHeader('Master Data'),
          _AdminTileGrid(
            items: [
              _AdminTileData(
                icon: Icons.storefront_outlined,
                label: 'Branches',
                subtitle: 'Add, edit, activate, deactivate',
                onTap: () => context.push('/admin/branches'),
              ),
              _AdminTileData(
                icon: Icons.inventory_2_outlined,
                label: 'Catalogue',
                subtitle: 'Products & availability',
                onTap: () => context.push('/admin/catalogue'),
              ),
              _AdminTileData(
                icon: Icons.local_drink_outlined,
                label: 'Preview catalogue',
                subtitle: 'Customer view',
                onTap: () => context.push('/catalogue'),
              ),
            ],
          ),
        ],
        if (_canReadNumberSeries ||
            _canReadTaxCharges ||
            _canReadOtpProvider ||
            _canReadIntegrations ||
            _canReadRefundReplacementConfig ||
            _canReadBranding) ...[
          const _AdminSectionHeader('System Setup'),
          _AdminTileGrid(
            items: [
              if (_canReadNumberSeries)
                _AdminTileData(
                  icon: Icons.numbers_outlined,
                  label: 'Number Series',
                  subtitle: 'Templates & reset policies',
                  onTap: () => context.push('/admin/setup/number-series'),
                ),
              if (_canReadTaxCharges)
                _AdminTileData(
                  icon: Icons.receipt_long_outlined,
                  label: 'Tax & Charges',
                  subtitle: 'Checkout taxes & fees',
                  onTap: () => context.push('/admin/setup/tax-charges'),
                ),
              if (_canReadOtpProvider)
                _AdminTileData(
                  icon: Icons.sms_outlined,
                  label: 'OTP Provider',
                  subtitle: 'MSG91 widget & auth key',
                  onTap: () => context.push('/admin/setup/otp-provider'),
                ),
              if (_canReadIntegrations)
                _AdminTileData(
                  icon: Icons.plumbing_outlined,
                  label: 'Integrations',
                  subtitle: 'SMTP, Razorpay & Maps keys',
                  onTap: () => context.push('/admin/setup/integrations'),
                ),
              if (_canReadRefundReplacementConfig)
                _AdminTileData(
                  icon: Icons.timer_outlined,
                  label: 'Refund / Replacement Window',
                  subtitle: 'Request window in hours',
                  onTap: () => context.push('/admin/setup/refund-replacement'),
                ),
              if (_canReadBranding)
                _AdminTileData(
                  icon: Icons.branding_watermark_outlined,
                  label: 'Branding',
                  subtitle: 'Logo & startup animation',
                  onTap: () => context.push('/admin/setup/branding'),
                ),
            ],
          ),
        ],
        if (_canReadCameras ||
            _canReadReports ||
            _canReadRefundReplacements ||
            branchIds.isNotEmpty) ...[
          const _AdminSectionHeader('Monitoring & Operations'),
          _AdminTileGrid(
            items: [
              if (_canReadReports)
                _AdminTileData(
                  icon: Icons.dashboard_outlined,
                  label: 'Dashboard & Reports',
                  subtitle: 'Metrics, filters, exports',
                  onTap: () => context.push('/admin'),
                ),
              if (_canReadCameras)
                _AdminTileData(
                  icon: Icons.video_settings_outlined,
                  label: 'Cameras',
                  subtitle: 'Visibility & stream status',
                  onTap: () => context.push('/admin/cameras'),
                ),
              if (_canReadRefundReplacements)
                _AdminTileData(
                  icon: Icons.assignment_return_outlined,
                  label: 'Refund / replacement requests',
                  subtitle: branchIds.isEmpty
                      ? 'Review customer requests'
                      : 'Review requests for ${branchDisplayName(branchDetails, branchIds.first)}',
                  onTap: () => context.push(
                    branchIds.isEmpty
                        ? '/staff/refund-replacements'
                        : '/staff/refund-replacements?branchId=${branchIds.first}',
                  ),
                ),
              if (branchIds.isNotEmpty)
                _AdminTileData(
                  icon: Icons.agriculture_outlined,
                  label: 'Dairy operations',
                  subtitle:
                      '${branchDisplayName(branchDetails, branchIds.first)} activity',
                  onTap: () => context.push('/dairy/dashboard'),
                ),
              if (branchIds.isNotEmpty)
                _AdminTileData(
                  icon: Icons.local_shipping_outlined,
                  label: 'Deliveries',
                  subtitle:
                      'Assign & monitor ${branchDisplayName(branchDetails, branchIds.first)}',
                  onTap: () => context.push(
                    '/delivery-management/branch/${branchIds.first}',
                  ),
                ),
            ],
          ),
        ],
      ],
    );
  }
}

/// Compact section heading with a small accent bar.
class _AdminSectionHeader extends StatelessWidget {
  const _AdminSectionHeader(this.title);

  final String title;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(top: 22, bottom: 12),
    child: Row(
      children: [
        Container(
          width: 4,
          height: 18,
          decoration: BoxDecoration(
            color: DoodhColors.teal,
            borderRadius: BorderRadius.circular(2),
          ),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            title,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: Theme.of(context).textTheme.titleLarge,
          ),
        ),
      ],
    ),
  );
}

/// Responsive icon-tile grid: 2 columns on phones, 3 on tablets, 4 on wide
/// screens. Tiles use the shared DoodhDirect Card theme and icon wells.
class _AdminTileGrid extends StatelessWidget {
  const _AdminTileGrid({required this.items});

  final List<_AdminTileData> items;

  @override
  Widget build(BuildContext context) {
    if (items.isEmpty) return const SizedBox.shrink();
    return LayoutBuilder(
      builder: (context, constraints) {
        final width = constraints.maxWidth;
        final columns = width >= 900 ? 4 : (width >= 600 ? 3 : 2);
        final tileHeight = width >= 600 ? 140.0 : 136.0;
        return GridView(
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
            crossAxisCount: columns,
            crossAxisSpacing: 12,
            mainAxisSpacing: 12,
            mainAxisExtent: tileHeight,
          ),
          children: [for (final item in items) _AdminTile(data: item)],
        );
      },
    );
  }
}

/// Compact tappable tile: icon well, short title, optional short subtitle.
class _AdminTile extends StatelessWidget {
  const _AdminTile({required this.data});

  final _AdminTileData data;

  @override
  Widget build(BuildContext context) => Card(
    child: InkWell(
      borderRadius: DoodhRadii.md,
      onTap: data.onTap,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 10),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: DoodhColors.mint.withValues(alpha: .8),
                borderRadius: DoodhRadii.sm,
              ),
              child: Icon(data.icon, color: DoodhColors.tealDark, size: 22),
            ),
            const SizedBox(height: 6),
            Flexible(
              child: Text(
                data.label,
                maxLines: 2,
                textAlign: TextAlign.center,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.titleMedium
                    ?.copyWith(fontSize: 13, height: 1.2),
              ),
            ),
            if (data.subtitle != null) ...[
              const SizedBox(height: 2),
              Flexible(
                child: Text(
                  data.subtitle!,
                  maxLines: 1,
                  textAlign: TextAlign.center,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall
                      ?.copyWith(fontSize: 11, height: 1.2),
                ),
              ),
            ],
          ],
        ),
      ),
    ),
  );
}

class _AdminTileData {
  const _AdminTileData({
    required this.icon,
    required this.label,
    required this.onTap,
    this.subtitle,
  });

  final IconData icon;
  final String label;
  final String? subtitle;
  final VoidCallback onTap;
}

/// The browsing-only home shown to guests. It exposes only the public
/// storefront (home, catalogue, product details, cart, checkout review) and a
/// contextual sign-in prompt. No customer-specific data is fetched or shown —
/// protected quick actions simply route to the sign-in flow with a
/// return-intent, so the guest is never shown fabricated account state.
class GuestHomeScreen extends ConsumerWidget {
  const GuestHomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('DoodhDirect'),
        actions: const [_GuestSignInButton(), _CartButton()],
      ),
      body: SafeArea(
        child: ListView(
          padding: DoodhSpacing.pagePadding,
          children: [
            const _GuestGreetingHeader(),
            const SizedBox(height: DoodhSpacing.lg),
            // Guests can start a search straight from the entry surface. The
            // query remains in shared catalogue state after navigation.
            DoodhSearchBar(
              key: const ValueKey('guest-home-search'),
              hint: 'Search milk, paneer, ghee…',
              onChanged: (value) => ref
                  .read(catalogueControllerProvider.notifier)
                  .setSearchQuery(value),
              onSubmitted: (_) => context.push('/catalogue'),
            ),
            const SizedBox(height: DoodhSpacing.md),
            const _GuestLoginPromptCard(),
            const SizedBox(height: DoodhSpacing.md),
            const DoodhSectionHeader(title: 'Explore'),
            const SizedBox(height: DoodhSpacing.md),
            LayoutBuilder(
              builder: (context, constraints) {
                final columns = constraints.maxWidth >= 720 ? 4 : 2;
                return GridView.count(
                  crossAxisCount: columns,
                  shrinkWrap: true,
                  physics: const NeverScrollableScrollPhysics(),
                  mainAxisSpacing: 12,
                  crossAxisSpacing: 12,
                  childAspectRatio: constraints.maxWidth >= 720 ? 1.7 : 1.35,
                  children: [
                    _QuickAction(
                      icon: Icons.shopping_bag_outlined,
                      label: 'Shop',
                      onTap: () => context.push('/catalogue'),
                    ),
                    _QuickAction(
                      icon: Icons.shopping_cart_outlined,
                      label: 'My cart',
                      onTap: () => context.go('/checkout'),
                    ),
                    _QuickAction(
                      icon: Icons.lock_outline,
                      label: 'Orders',
                      onTap: () => context.go('/orders'),
                    ),
                    _QuickAction(
                      icon: Icons.account_balance_wallet_outlined,
                      label: 'Wallet',
                      onTap: () => context.go('/wallet'),
                    ),
                  ],
                );
              },
            ),
            const SizedBox(height: DoodhSpacing.md),
            const DoodhTrustStrip(items: _homeTrustItems),
            const SizedBox(height: DoodhSpacing.lg),
            DoodhHeroCard(onBuy: () => context.push('/catalogue')),
            const SizedBox(height: 24),
          ],
        ),
      ),
    );
  }
}

class _GuestSignInButton extends ConsumerWidget {
  const _GuestSignInButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) => TextButton.icon(
    onPressed: () => context.go('/login'),
    icon: const Icon(Icons.login),
    label: const Text('Sign in'),
  );
}

class _GuestGreetingHeader extends StatelessWidget {
  const _GuestGreetingHeader();

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        'Welcome to DoodhDirect',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: 4),
      const Text(
        'Fresh dairy, delivered with care. Browse now — sign in when you '
        'are ready to order.',
      ),
    ],
  );
}

class _GuestLoginPromptCard extends StatelessWidget {
  const _GuestLoginPromptCard();

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: DoodhSpacing.cardPadding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Sign in for orders, payments and more',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 8),
          const Text(
            'Your cart is saved on this device and will be there after you '
            'sign in.',
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              FilledButton(
                onPressed: () => context.go('/login'),
                child: const Text('Sign in'),
              ),
            ],
          ),
        ],
      ),
    ),
  );
}
