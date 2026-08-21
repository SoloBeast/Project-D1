import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/time/india_time.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'delivery_models.dart';
import 'delivery_repository.dart';

final deliveryRepositoryProvider = Provider<DeliveryRepository>(
  (ref) => DeliveryRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final deliveryControllerProvider =
    NotifierProvider<DeliveryController, DeliveryState>(DeliveryController.new);

class DeliveryState {
  const DeliveryState({
    this.customerDeliveries = const [],
    this.staffDeliveries = const [],
    this.managedDeliveries = const [],
    this.employees = const [],
    this.selectedCustomerDelivery,
    this.selectedDelivery,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.errorMessage,
  });

  final List<CustomerDelivery> customerDeliveries;
  final List<DeliveryDetails> staffDeliveries;
  final List<DeliveryDetails> managedDeliveries;
  final List<DeliveryEmployee> employees;
  final CustomerDelivery? selectedCustomerDelivery;
  final DeliveryDetails? selectedDelivery;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final String? errorMessage;

  DeliveryState copyWith({
    List<CustomerDelivery>? customerDeliveries,
    List<DeliveryDetails>? staffDeliveries,
    List<DeliveryDetails>? managedDeliveries,
    List<DeliveryEmployee>? employees,
    CustomerDelivery? selectedCustomerDelivery,
    bool clearSelectedCustomerDelivery = false,
    DeliveryDetails? selectedDelivery,
    bool clearSelectedDelivery = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    String? errorMessage,
    bool clearError = false,
  }) => DeliveryState(
    customerDeliveries: customerDeliveries ?? this.customerDeliveries,
    staffDeliveries: staffDeliveries ?? this.staffDeliveries,
    managedDeliveries: managedDeliveries ?? this.managedDeliveries,
    employees: employees ?? this.employees,
    selectedCustomerDelivery: clearSelectedCustomerDelivery
        ? null
        : selectedCustomerDelivery ?? this.selectedCustomerDelivery,
    selectedDelivery: clearSelectedDelivery
        ? null
        : selectedDelivery ?? this.selectedDelivery,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class DeliveryController extends Notifier<DeliveryState> {
  DeliveryRepository get _repository => ref.read(deliveryRepositoryProvider);
  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  DeliveryState build() => const DeliveryState();

  Future<void> loadCustomerDeliveries() async => _load(() async {
    final deliveries = await _repository.getMine(_token!);
    state = state.copyWith(customerDeliveries: deliveries);
  });

  Future<void> loadCustomerDelivery(String id) async => _load(() async {
    final delivery = await _repository.getMineById(_token!, id);
    state = state.copyWith(
      customerDeliveries: _upsertCustomer(delivery),
      selectedCustomerDelivery: delivery,
    );
  });

  Future<void> loadToday({DateTime? date}) async => _load(() async {
    final deliveries = await _repository.getToday(_token!, date ?? indiaNow());
    state = state.copyWith(staffDeliveries: deliveries);
  });

  Future<void> loadStaffDelivery(String id) async => _load(() async {
    final delivery = await _repository.getStaff(_token!, id);
    state = state.copyWith(
      staffDeliveries: _upsertDetails(state.staffDeliveries, delivery),
      selectedDelivery: delivery,
    );
  });

  Future<void> loadBranch(
    int branchId, {
    DateTime? date,
    DeliveryStatus? status,
  }) async => _load(() async {
    final deliveries = await _repository.getBranch(
      _token!,
      branchId,
      date: date,
      status: status,
    );
    final employees = await _repository.getEmployees(_token!, branchId);
    state = state.copyWith(managedDeliveries: deliveries, employees: employees);
  });

  Future<void> loadManagedDelivery(String id) async => _load(() async {
    final delivery = await _repository.getManaged(_token!, id);
    state = state.copyWith(
      managedDeliveries: _upsertDetails(state.managedDeliveries, delivery),
      selectedDelivery: delivery,
    );
  });

  Future<DeliveryMaterialization?> materialize(DateTime throughDate) async {
    final token = _token;
    if (token == null) return null;
    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final result = await _repository.materialize(token, throughDate);
      state = state.copyWith(isSaving: false);
      return result;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return null;
    }
  }

  Future<bool> assign(String id, String employeeId, {String? reason}) => _save(
    () => _repository.assign(
      token: _token!,
      deliveryId: id,
      employeeId: employeeId,
      reason: reason,
    ),
  );

  Future<bool> pickup(String id, {String? remarks}) =>
      _save(() => _repository.pickup(_token!, id, remarks: remarks));
  Future<bool> start(String id) => _save(() => _repository.start(_token!, id));
  Future<bool> arrive(String id) =>
      _save(() => _repository.arrive(_token!, id));
  Future<bool> verifyOtp(String id, String code) =>
      _save(() => _repository.verifyOtp(_token!, id, code));
  Future<bool> complete(String id, {String? remarks}) =>
      _save(() => _repository.complete(_token!, id, remarks: remarks));
  Future<bool> fail(String id, {required String reason, String? remarks}) =>
      _save(
        () => _repository.fail(_token!, id, reason: reason, remarks: remarks),
      );

  Future<bool> issueOtp(String id) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      await _repository.issueOtp(token, id);
      state = state.copyWith(isSaving: false);
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<bool> _save(Future<DeliveryDetails> Function() operation) async {
    if (_token == null) return false;
    state = state.copyWith(isSaving: true, isOffline: false, clearError: true);
    try {
      final delivery = await operation();
      state = state.copyWith(
        staffDeliveries: _upsertDetails(state.staffDeliveries, delivery),
        managedDeliveries: _upsertDetails(state.managedDeliveries, delivery),
        selectedDelivery: delivery,
        isSaving: false,
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<void> _load(Future<void> Function() operation) async {
    if (_token == null) return;
    state = state.copyWith(isLoading: true, isOffline: false, clearError: true);
    try {
      await operation();
      state = state.copyWith(isLoading: false);
    } on Object catch (error) {
      _setFailure(error);
    }
  }

  List<CustomerDelivery> _upsertCustomer(CustomerDelivery delivery) {
    final items = [...state.customerDeliveries];
    final index = items.indexWhere(
      (item) => item.deliveryId == delivery.deliveryId,
    );
    if (index == -1) {
      items.insert(0, delivery);
    } else {
      items[index] = delivery;
    }
    return items;
  }

  List<DeliveryDetails> _upsertDetails(
    List<DeliveryDetails> current,
    DeliveryDetails delivery,
  ) {
    final items = [...current];
    final index = items.indexWhere(
      (item) => item.deliveryId == delivery.deliveryId,
    );
    if (index == -1) {
      items.insert(0, delivery);
    } else {
      items[index] = delivery;
    }
    return items;
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
