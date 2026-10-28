import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'vacation_refund_card.dart';
import 'vacation_refund_models.dart';
import 'vacation_refund_repository.dart';

/// Customer-facing vacation-refund state for one subscription.
///
/// Renders the created request with [VacationRefundCard] when present.
/// Otherwise, once the term has ended and vacation days were recorded, it
/// renders a pending/blocked banner with counts and the server's blocked
/// reason — so an end-of-term vacation refund that has not appeared yet is
/// explained instead of silently missing. Renders nothing while loading,
/// when the section does not apply, or when the status cannot be loaded
/// (the subscription detail screen owns its own offline handling).
class VacationRefundStatusSection extends ConsumerStatefulWidget {
  const VacationRefundStatusSection({
    required this.subscriptionId,
    this.repository,
    this.token,
    super.key,
  });

  final String subscriptionId;

  /// Test seam: a fake repository serving canned statuses.
  final VacationRefundRepository? repository;

  /// Test seam: bypasses the session for the access token.
  final String? token;

  @override
  ConsumerState<VacationRefundStatusSection> createState() =>
      _VacationRefundStatusSectionState();
}

class _VacationRefundStatusSectionState
    extends ConsumerState<VacationRefundStatusSection> {
  Future<VacationRefundSubscriptionStatus?>? _future;

  @override
  void initState() {
    super.initState();
    _refresh();
  }

  @override
  void didUpdateWidget(covariant VacationRefundStatusSection oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.subscriptionId != widget.subscriptionId ||
        oldWidget.token != widget.token ||
        oldWidget.repository != widget.repository) {
      _refresh();
    }
  }

  void _refresh() {
    final token =
        widget.token ??
        ref.read(sessionControllerProvider).session?.accessToken;
    if (token == null) {
      _future = null;
      return;
    }
    final injected = widget.repository;
    final VacationRefundRepository repository =
        injected ?? ref.read(vacationRefundRepositoryProvider);
    _future = repository.getStatusForCustomer(token, widget.subscriptionId);
  }

  @override
  Widget build(BuildContext context) {
    final pending = _future;
    if (pending == null) return const SizedBox.shrink();
    return FutureBuilder<VacationRefundSubscriptionStatus?>(
      future: pending,
      builder: (context, snapshot) {
        if (snapshot.hasError) return const SizedBox.shrink();
        if (!snapshot.hasData) return const SizedBox.shrink();
        final status = snapshot.data;
        if (status == null) return const SizedBox.shrink();
        if (status.request != null) {
          return VacationRefundCard(request: status.request!);
        }
        if (!status.termEnded || status.vacationSkippedCount == 0) {
          return const SizedBox.shrink();
        }
        return _pendingBanner(status);
      },
    );
  }

  Widget _pendingBanner(VacationRefundSubscriptionStatus status) {
    final count = status.vacationSkippedCount;
    final days = count == 1 ? '1 skipped delivery' : '$count skipped deliveries';
    switch (status.blockedReason) {
      case 'subscriptionNotActive':
        // Cancellation refunds surface through their own workflow.
        return const SizedBox.shrink();
      case 'unresolvedTicketOverlap':
        return DoodhInfoBanner(
          tone: DoodhTone.warning,
          title: 'Vacation refund on hold',
          message:
              'Your vacation refund for $days is on hold until a related '
              'delivery issue is resolved. No refund has been issued yet.',
        );
      case 'noSuccessfulPayment':
        return DoodhInfoBanner(
          tone: DoodhTone.error,
          title: 'Vacation refund needs support',
          message:
              'We could not link a payment to your vacation refund for $days. '
              'Please contact support. No refund has been issued yet.',
        );
      case 'paymentFullyRefunded':
        return const DoodhInfoBanner(
          tone: DoodhTone.info,
          title: 'Vacation refund settled',
          message:
              'Your subscription payment has already been fully refunded, '
              'so there is nothing further to refund.',
        );
      default:
        return DoodhInfoBanner(
          tone: DoodhTone.info,
          title: 'Vacation refund pending',
          message:
              'Your vacation refund for $days is being prepared. Support '
              'will review it before any refund is issued.',
        );
    }
  }
}
