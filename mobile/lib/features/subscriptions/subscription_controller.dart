import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'subscription_models.dart';
import 'subscription_repository.dart';

final subscriptionRepositoryProvider = Provider<SubscriptionRepository>(
  (ref) => SubscriptionRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final subscriptionControllerProvider =
    NotifierProvider<SubscriptionController, SubscriptionState>(
      SubscriptionController.new,
    );

class SubscriptionState {
  const SubscriptionState({
    this.subscriptions = const <SubscriptionDetails>[],
    this.selectedSubscription,
    this.calendar = const <SubscriptionDelivery>[],
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.errorMessage,
  });

  final List<SubscriptionDetails> subscriptions;
  final SubscriptionDetails? selectedSubscription;
  final List<SubscriptionDelivery> calendar;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final String? errorMessage;

  SubscriptionState copyWith({
    List<SubscriptionDetails>? subscriptions,
    SubscriptionDetails? selectedSubscription,
    bool clearSelectedSubscription = false,
    List<SubscriptionDelivery>? calendar,
    bool clearCalendar = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    String? errorMessage,
    bool clearError = false,
  }) => SubscriptionState(
    subscriptions: subscriptions ?? this.subscriptions,
    selectedSubscription: clearSelectedSubscription
        ? null
        : selectedSubscription ?? this.selectedSubscription,
    calendar: clearCalendar ? const [] : calendar ?? this.calendar,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class SubscriptionController extends Notifier<SubscriptionState> {
  SubscriptionRepository get _repository =>
      ref.read(subscriptionRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  SubscriptionState build() => const SubscriptionState();

  Future<CreatedSubscription?> create(CreateSubscriptionRequest request) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final created = await _repository.create(
        token: token,
        request: request,
        idempotencyKey:
            'mobile-subscription-${DateTime.now().microsecondsSinceEpoch}',
      );
      ref.read(paymentControllerProvider.notifier).adopt(created.payment);
      state = state.copyWith(
        subscriptions: _upsert(created.subscription),
        selectedSubscription: created.subscription,
        isSaving: false,
      );
      return created;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return null;
    }
  }

  Future<CreatedSubscription?> retryPayment(
    String subscriptionId,
    PaymentMethod paymentMethod,
  ) async {
    final token = _token;
    if (token == null) return null;

    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final created = await _repository.retryPayment(
        token: token,
        subscriptionId: subscriptionId,
        paymentMethod: paymentMethod,
        idempotencyKey:
            'mobile-subscription-retry-${DateTime.now().microsecondsSinceEpoch}',
      );
      ref.read(paymentControllerProvider.notifier).adopt(created.payment);
      state = state.copyWith(
        subscriptions: _upsert(created.subscription),
        selectedSubscription: created.subscription,
        isSaving: false,
      );
      return created;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return null;
    }
  }

  Future<void> loadSubscriptions() async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, isOffline: false, clearError: true);
    try {
      final subscriptions = await _repository.getMine(token);
      state = state.copyWith(subscriptions: subscriptions, isLoading: false);
    } on Object catch (error) {
      _setFailure(error);
    }
  }

  Future<void> loadSubscription(String subscriptionId) async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(isLoading: true, isOffline: false, clearError: true);
    try {
      final subscription = await _repository.get(token, subscriptionId);
      state = state.copyWith(
        subscriptions: _upsert(subscription),
        selectedSubscription: subscription,
        isLoading: false,
      );
    } on Object catch (error) {
      _setFailure(error);
    }
  }

  Future<void> loadCalendar(String subscriptionId) async {
    final token = _token;
    if (token == null) return;

    state = state.copyWith(
      isLoading: true,
      isOffline: false,
      clearError: true,
      clearCalendar: true,
    );
    try {
      final calendar = await _repository.getCalendar(token, subscriptionId);
      state = state.copyWith(calendar: calendar, isLoading: false);
    } on Object catch (error) {
      _setFailure(error);
    }
  }

  Future<bool> update(
    String subscriptionId,
    UpdateSubscriptionRequest request,
  ) => _saveSubscription(
    () => _repository.update(
      token: _token!,
      subscriptionId: subscriptionId,
      request: request,
    ),
  );

  Future<bool> pause(String subscriptionId) =>
      _saveSubscription(() => _repository.pause(_token!, subscriptionId));

  Future<bool> resume(String subscriptionId) =>
      _saveSubscription(() => _repository.resume(_token!, subscriptionId));

  Future<bool> cancel(String subscriptionId) =>
      _saveSubscription(() => _repository.cancel(_token!, subscriptionId));

  Future<bool> skip(String subscriptionId, String deliveryId) async {
    final token = _token;
    if (token == null) return false;

    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final delivery = await _repository.skip(
        token: token,
        subscriptionId: subscriptionId,
        deliveryId: deliveryId,
      );
      state = state.copyWith(
        calendar: state.calendar
            .map((item) => item.publicId == delivery.publicId ? delivery : item)
            .toList(growable: false),
        isSaving: false,
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<bool> _saveSubscription(
    Future<SubscriptionDetails> Function() operation,
  ) async {
    if (_token == null) return false;

    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final subscription = await operation();
      state = state.copyWith(
        subscriptions: _upsert(subscription),
        selectedSubscription: subscription,
        isSaving: false,
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  List<SubscriptionDetails> _upsert(SubscriptionDetails subscription) {
    final subscriptions = [...state.subscriptions];
    final index = subscriptions.indexWhere(
      (item) => item.publicId == subscription.publicId,
    );
    if (index == -1) {
      subscriptions.insert(0, subscription);
    } else {
      subscriptions[index] = subscription;
    }
    return subscriptions;
  }

  void _setFailure(Object error, {bool saving = false}) {
    final isApiError = error is ApiException;
    state = state.copyWith(
      isLoading: saving ? state.isLoading : false,
      isSaving: saving ? false : state.isSaving,
      isOffline: !isApiError,
      errorMessage: isApiError ? error.message : _offlineMessage,
    );
  }
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
