import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:flutter/material.dart';

class DashboardMetrics {
  const DashboardMetrics({
    required this.customers,
    required this.activeCustomers,
    required this.employees,
    required this.orders,
    required this.oneTimeOrderRevenue,
    required this.activeSubscriptions,
    required this.successfulPayments,
    required this.pendingPayments,
    required this.refunds,
    required this.walletBalances,
    required this.deliveries,
    required this.successfulDeliveries,
    required this.failedDeliveries,
    required this.milkProduced,
    required this.milkUsed,
    required this.pendingMilkTests,
    required this.availableCameras,
    required this.notificationFailures,
  });

  factory DashboardMetrics.fromJson(Map<String, dynamic> json) =>
      DashboardMetrics(
        customers: _int(json['customers']),
        activeCustomers: _int(json['activeCustomers']),
        employees: _int(json['employees']),
        orders: _int(json['orders']),
        oneTimeOrderRevenue: _decimal(json['oneTimeOrderRevenue']),
        activeSubscriptions: _int(json['activeSubscriptions']),
        successfulPayments: _decimal(json['successfulPayments']),
        pendingPayments: _decimal(json['pendingPayments']),
        refunds: _decimal(json['refunds']),
        walletBalances: _decimal(json['walletBalances']),
        deliveries: _int(json['deliveries']),
        successfulDeliveries: _int(json['successfulDeliveries']),
        failedDeliveries: _int(json['failedDeliveries']),
        milkProduced: _decimal(json['milkProduced']),
        milkUsed: _decimal(json['milkUsed']),
        pendingMilkTests: _int(json['pendingMilkTests']),
        availableCameras: _int(json['availableCameras']),
        notificationFailures: _int(json['notificationFailures']),
      );

  final int customers;
  final int activeCustomers;
  final int employees;
  final int orders;
  final double oneTimeOrderRevenue;
  final int activeSubscriptions;
  final double successfulPayments;
  final double pendingPayments;
  final double refunds;
  final double walletBalances;
  final int deliveries;
  final int successfulDeliveries;
  final int failedDeliveries;
  final double milkProduced;
  final double milkUsed;
  final int pendingMilkTests;
  final int availableCameras;
  final int notificationFailures;
}

class ReportFilter {
  const ReportFilter({
    this.search,
    this.statuses = const [],
    this.from,
    this.to,
    this.page = 1,
    this.pageSize = 25,
    this.sortBy,
    this.descending = true,
  });

  final String? search;
  final List<String> statuses;
  final DateTime? from;
  final DateTime? to;
  final int page;
  final int pageSize;
  final String? sortBy;
  final bool descending;

  ReportFilter copyWith({
    String? search,
    List<String>? statuses,
    DateTime? from,
    DateTime? to,
    int? page,
    int? pageSize,
    String? sortBy,
    bool? descending,
    bool clearSearch = false,
    bool clearFrom = false,
    bool clearTo = false,
    bool clearSort = false,
  }) => ReportFilter(
    search: clearSearch ? null : search ?? this.search,
    statuses: statuses ?? this.statuses,
    from: clearFrom ? null : from ?? this.from,
    to: clearTo ? null : to ?? this.to,
    page: page ?? this.page,
    pageSize: pageSize ?? this.pageSize,
    sortBy: clearSort ? null : sortBy ?? this.sortBy,
    descending: descending ?? this.descending,
  );

  Map<String, dynamic> toQuery() => {
    if (search != null && search!.trim().isNotEmpty) 'search': search!.trim(),
    if (statuses.isNotEmpty) 'statuses': statuses,
    if (from != null) 'dateRange.from': _indiaLocalIso(from!),
    if (to != null) 'dateRange.to': _indiaLocalIso(to!),
    'page': '$page',
    'pageSize': '$pageSize',
    'sortBy': ?sortBy,
    'sortDirection': descending ? 'Descending' : 'Ascending',
  };

  Map<String, dynamic> toJson() => {
    if (search != null && search!.trim().isNotEmpty) 'search': search!.trim(),
    if (statuses.isNotEmpty) 'statuses': statuses,
    if (from != null || to != null)
      'dateRange': {
        if (from != null) 'from': _indiaLocalIso(from!),
        if (to != null) 'to': _indiaLocalIso(to!),
      },
    'page': page,
    'pageSize': pageSize,
    if (sortBy != null) 'sortBy': sortBy,
    'sortDirection': descending ? 'Descending' : 'Ascending',
  };
}

String _indiaLocalIso(DateTime value) =>
    indiaWallClock(value).toIso8601String();

class ReportPageData {
  const ReportPageData({
    required this.items,
    required this.page,
    required this.pageSize,
    required this.totalCount,
    required this.hasNextPage,
  });

  factory ReportPageData.fromJson(Map<String, dynamic> json) => ReportPageData(
    items: (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .toList(growable: false),
    page: _int(json['page'], fallback: 1),
    pageSize: _int(json['pageSize'], fallback: 25),
    totalCount: _int(json['totalCount']),
    hasNextPage: json['hasNextPage'] == true,
  );

  final List<Map<String, dynamic>> items;
  final int page;
  final int pageSize;
  final int totalCount;
  final bool hasNextPage;
}

class ReportModuleDescriptor {
  const ReportModuleDescriptor({
    required this.slug,
    required this.label,
    required this.icon,
    required this.permission,
    required this.sorts,
    required this.columns,
  });

  final String slug;
  final String label;
  final IconData icon;
  final String permission;
  final List<ReportSortOption> sorts;
  final List<ReportColumn> columns;
}

class ReportSortOption {
  const ReportSortOption(this.value, this.label);
  final String value;
  final String label;
}

class ReportColumn {
  const ReportColumn(this.key, this.label);
  final String key;
  final String label;
}

const reportModules = <ReportModuleDescriptor>[
  ReportModuleDescriptor(
    slug: 'customers',
    label: 'Customers',
    icon: Icons.people_outline,
    permission: 'REPORTS.ADMINISTRATION.READ',
    sorts: [_created, _name, _active],
    columns: [_id, _displayNameColumn, _mobile, _activeColumn, _createdColumn],
  ),
  ReportModuleDescriptor(
    slug: 'employees',
    label: 'Employees',
    icon: Icons.badge_outlined,
    permission: 'REPORTS.ADMINISTRATION.READ',
    sorts: [_created, _name, _active],
    columns: [_id, _displayNameColumn, _mobile, _activeColumn, _roles],
  ),
  ReportModuleDescriptor(
    slug: 'orders',
    label: 'Orders',
    icon: Icons.receipt_long_outlined,
    permission: 'REPORTS.ADMINISTRATION.READ',
    sorts: [_created, _orderNumber, _status, _amount],
    columns: [
      _orderNumberColumn,
      _customerName,
      _branchNameColumn,
      _statusColumn,
      _amountColumn,
      _createdColumn,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'subscriptions',
    label: 'Subscriptions',
    icon: Icons.event_repeat_outlined,
    permission: 'REPORTS.ADMINISTRATION.READ',
    sorts: [_created, _startDate, _endDate, _status, _amount],
    columns: [
      _customerName,
      _productName,
      _branchNameColumn,
      _statusColumn,
      _startDateColumn,
      _endDateColumn,
      _amountColumn,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'payments',
    label: 'Payments',
    icon: Icons.payments_outlined,
    permission: 'REPORTS.FINANCIAL.READ',
    sorts: [_created, _amount, _refunded, _status],
    columns: [
      _id,
      _statusColumn,
      _method,
      ReportColumn('amount', 'Amount'),
      _refundedColumn,
      _createdColumn,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'wallets',
    label: 'Wallets',
    icon: Icons.account_balance_wallet_outlined,
    permission: 'REPORTS.FINANCIAL.READ',
    sorts: [_balance, _customerNameSort, _transactions],
    columns: [
      _customerName,
      _balanceColumn,
      _currency,
      _transactionsColumn,
      _lastActivity,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'deliveries',
    label: 'Deliveries',
    icon: Icons.local_shipping_outlined,
    permission: 'REPORTS.OPERATIONS.READ',
    sorts: [_scheduled, _status, _completed],
    columns: [
      _customerName,
      _branchNameColumn,
      _scheduledColumn,
      _statusColumn,
      _assigned,
      _failure,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'dairy',
    label: 'Dairy',
    icon: Icons.agriculture_outlined,
    permission: 'REPORTS.OPERATIONS.READ',
    sorts: [_occurred, _branchName],
    columns: [
      _branchNameColumn,
      _occurredColumn,
      _quantity,
      _unit,
      _statusText,
      _purpose,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'milk-tests',
    label: 'Milk tests',
    icon: Icons.science_outlined,
    permission: 'REPORTS.MILK_TESTS.READ',
    sorts: [_requested, _status, _completed],
    columns: [
      _branchNameColumn,
      _requestedColumn,
      _statusColumn,
      _decision,
      _parameters,
      _images,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'cameras',
    label: 'Cameras',
    icon: Icons.videocam_outlined,
    permission: 'REPORTS.OPERATIONS.READ',
    sorts: [_displayOrder, _displayName, _active],
    columns: [
      _displayNameColumn,
      _branchNameColumn,
      _activeColumn,
      _public,
      _protocol,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'notifications',
    label: 'Notifications',
    icon: Icons.notifications_outlined,
    permission: 'REPORTS.OPERATIONS.READ',
    sorts: [_occurred, _eventType, _status, _critical],
    columns: [
      _eventTypeColumn,
      _statusColumn,
      _occurredColumn,
      _criticalColumn,
      _failed,
      _attempts,
    ],
  ),
  ReportModuleDescriptor(
    slug: 'audit',
    label: 'Audit',
    icon: Icons.policy_outlined,
    permission: 'REPORTS.AUDIT.READ',
    sorts: [_created, _action, _entityType],
    columns: [
      _actionColumn,
      _entityTypeColumn,
      _entityId,
      _reason,
      _createdColumn,
    ],
  ),
];

const _created = ReportSortOption('createdAtUtc', 'Created');
const _name = ReportSortOption('displayName', 'Name');
const _active = ReportSortOption('isActive', 'Active');
const _orderNumber = ReportSortOption('orderNumber', 'Order number');
const _status = ReportSortOption('status', 'Status');
const _amount = ReportSortOption('payableAmount', 'Amount');
const _startDate = ReportSortOption('startDate', 'Start date');
const _endDate = ReportSortOption('endDate', 'End date');
const _method = ReportColumn('method', 'Method');
const _refunded = ReportSortOption('refundedAmount', 'Refunded');
const _balance = ReportSortOption('balance', 'Balance');
const _customerNameSort = ReportSortOption('customerName', 'Customer');
const _transactions = ReportSortOption('transactionCount', 'Transactions');
const _scheduled = ReportSortOption('scheduledDate', 'Scheduled');
const _completed = ReportSortOption('completedAtUtc', 'Completed');
const _occurred = ReportSortOption('occurredAtUtc', 'Occurred');
const _requested = ReportSortOption('requestedAtUtc', 'Requested');
const _branchName = ReportSortOption('branchName', 'Branch');
const _displayOrder = ReportSortOption('displayOrder', 'Display order');
const _displayName = ReportSortOption('displayName', 'Name');
const _eventType = ReportSortOption('eventType', 'Event type');
const _critical = ReportSortOption('isCritical', 'Critical');
const _action = ReportSortOption('action', 'Action');
const _entityType = ReportSortOption('entityType', 'Entity type');
const _id = ReportColumn('id', 'ID');
const _mobile = ReportColumn('mobile', 'Mobile');
const _roles = ReportColumn('roles', 'Roles');
const _customerName = ReportColumn('customerName', 'Customer');
const _branchNameColumn = ReportColumn('branchName', 'Branch');
const _productName = ReportColumn('productName', 'Product');
const _createdColumn = ReportColumn('createdAtUtc', 'Created');
const _activeColumn = ReportColumn('isActive', 'Active');
const _orderNumberColumn = ReportColumn('orderNumber', 'Order');
const _statusColumn = ReportColumn('status', 'Status');
const _amountColumn = ReportColumn('payableAmount', 'Amount');
const _startDateColumn = ReportColumn('startDate', 'Start');
const _endDateColumn = ReportColumn('endDate', 'End');
const _refundedColumn = ReportColumn('refundedAmount', 'Refunded');
const _balanceColumn = ReportColumn('balance', 'Balance');
const _currency = ReportColumn('currency', 'Currency');
const _transactionsColumn = ReportColumn('transactionCount', 'Transactions');
const _lastActivity = ReportColumn('lastActivityAtUtc', 'Last activity');
const _scheduledColumn = ReportColumn('scheduledDate', 'Scheduled');
const _assigned = ReportColumn('assignedEmployeeName', 'Assigned');
const _failure = ReportColumn('failureCode', 'Failure');
const _occurredColumn = ReportColumn('occurredAtUtc', 'Occurred');
const _quantity = ReportColumn('quantity', 'Quantity');
const _unit = ReportColumn('unit', 'Unit');
const _statusText = ReportColumn('status', 'Status');
const _purpose = ReportColumn('purpose', 'Purpose');
const _requestedColumn = ReportColumn('requestedAtUtc', 'Requested');
const _decision = ReportColumn('customerDecision', 'Decision');
const _parameters = ReportColumn('parameterCount', 'Parameters');
const _images = ReportColumn('imageCount', 'Images');
const _displayNameColumn = ReportColumn('displayName', 'Name');
const _public = ReportColumn('isPublic', 'Public');
const _protocol = ReportColumn('streamProtocol', 'Protocol');
const _eventTypeColumn = ReportColumn('eventType', 'Event type');
const _criticalColumn = ReportColumn('isCritical', 'Critical');
const _failed = ReportColumn('failedDeliveryCount', 'Failed');
const _attempts = ReportColumn('attemptCount', 'Attempts');
const _actionColumn = ReportColumn('action', 'Action');
const _entityTypeColumn = ReportColumn('entityType', 'Entity');
const _entityId = ReportColumn('entityId', 'Entity ID');
const _reason = ReportColumn('reason', 'Reason');

int _int(dynamic value, {int fallback = 0}) =>
    value is num ? value.toInt() : int.tryParse('$value') ?? fallback;
double _decimal(dynamic value) =>
    value is num ? value.toDouble() : double.tryParse('$value') ?? 0;

ReportModuleDescriptor? reportModuleBySlug(String slug) =>
    reportModules.where((module) => module.slug == slug).firstOrNull;

String displayReportValue(dynamic value) {
  if (value == null) return '-';
  if (value is List) return value.join(', ');
  if (value is bool) return value ? 'Yes' : 'No';
  if (value is String && value.contains('T')) {
    final date = DateTime.tryParse(value);
    if (date != null) {
      return indiaWallClock(date).toIso8601String().split('.').first;
    }
  }
  return '$value';
}
