import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/authenticated_api_client.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'branch_models.dart';
import 'branch_repository.dart';

final branchRepositoryProvider = Provider<BranchRepository>(
  (ref) => BranchRepository(api: authenticatedApiClient(ref)),
);

final branchControllerProvider =
    NotifierProvider<BranchController, BranchState>(BranchController.new);

class BranchState {
  const BranchState({
    this.branches = const [],
    this.selectedBranch,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.isUnavailable = false,
    this.errorMessage,
    this.fieldErrors = const <String, String>{},
    this.savedMessage,
  });

  final List<Branch> branches;
  final Branch? selectedBranch;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final bool isUnauthorized;
  final bool isUnavailable;
  final String? errorMessage;
  final Map<String, String> fieldErrors;
  final String? savedMessage;

  BranchState copyWith({
    List<Branch>? branches,
    Branch? selectedBranch,
    bool clearSelectedBranch = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    bool? isUnauthorized,
    bool? isUnavailable,
    String? errorMessage,
    bool clearError = false,
    Map<String, String>? fieldErrors,
    String? savedMessage,
    bool clearSaved = false,
  }) => BranchState(
    branches: branches ?? this.branches,
    selectedBranch: clearSelectedBranch
        ? null
        : selectedBranch ?? this.selectedBranch,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    isUnauthorized: isUnauthorized ?? this.isUnauthorized,
    isUnavailable: isUnavailable ?? this.isUnavailable,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
    fieldErrors: clearError
        ? const <String, String>{}
        : fieldErrors ?? this.fieldErrors,
    savedMessage: clearSaved ? null : savedMessage ?? this.savedMessage,
  );
}

class BranchController extends Notifier<BranchState> {
  BranchRepository get _repository => ref.read(branchRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  BranchState build() => const BranchState();

  Future<void> load() async {
    final token = _token;
    if (token == null) return;
    state = state.copyWith(
      isLoading: true,
      isOffline: false,
      isUnauthorized: false,
      isUnavailable: false,
      clearError: true,
    );
    try {
      final branches = await _repository.getBranches(token);
      state = state.copyWith(branches: branches, isLoading: false);
    } on Object catch (error) {
      _setFailure(error, saving: false);
    }
  }

  Future<void> loadById(String branchId) async {
    final token = _token;
    if (token == null) return;
    state = state.copyWith(
      isLoading: true,
      isOffline: false,
      isUnauthorized: false,
      isUnavailable: false,
      clearError: true,
    );
    try {
      final branch = await _repository.getBranch(token, branchId);
      state = state.copyWith(
        selectedBranch: branch,
        isLoading: false,
        clearError: true,
      );
    } on Object catch (error) {
      _setFailure(error, saving: false);
    }
  }

  Future<bool> create(UpsertBranchRequest request) =>
      _save((token) => _repository.create(token, request));

  Future<bool> update(String branchId, UpsertBranchRequest request) =>
      _save((token) => _repository.update(token, branchId, request));

  Future<bool> setActive(String branchId, bool isActive) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(
      isSaving: true,
      isOffline: false,
      isUnauthorized: false,
      isUnavailable: false,
      clearError: true,
      clearSaved: true,
    );
    try {
      final updated = isActive
          ? await _repository.activate(token, branchId)
          : await _repository.deactivate(token, branchId);
      final branches = [...state.branches];
      final index = branches.indexWhere((item) => item.publicId == branchId);
      if (index == -1) {
        branches.add(updated);
      } else {
        branches[index] = updated;
      }
      state = state.copyWith(
        branches: branches,
        selectedBranch: state.selectedBranch?.publicId == branchId
            ? updated
            : state.selectedBranch,
        isSaving: false,
        clearError: true,
        savedMessage: isActive
            ? 'Branch activated'
            : 'Branch deactivated',
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<bool> _save(
    Future<Branch> Function(String token) operation,
  ) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(
      isSaving: true,
      isOffline: false,
      isUnauthorized: false,
      isUnavailable: false,
      clearError: true,
      clearSaved: true,
    );
    try {
      final branch = await operation(token);
      final branches = [...state.branches];
      final index = branches.indexWhere((item) => item.publicId == branch.publicId);
      if (index == -1) {
        branches.add(branch);
      } else {
        branches[index] = branch;
      }
      state = state.copyWith(
        branches: branches,
        selectedBranch: branch,
        isSaving: false,
        clearError: true,
        savedMessage: 'Branch saved',
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  void _setFailure(Object error, {required bool saving}) {
    final isOffline = error is ApiNetworkException;
    final isUnauthorized = error is ApiException && error.statusCode == 403;
    final isUnavailable = error is ApiException &&
        (error.statusCode == 404 || error.statusCode == 409);
    state = state.copyWith(
      isSaving: saving ? false : state.isSaving,
      isLoading: saving ? state.isLoading : false,
      isOffline: isOffline,
      isUnauthorized: isUnauthorized,
      isUnavailable: isUnavailable,
      errorMessage: _messageOf(error),
    );
  }

  String? _messageOf(Object error) {
    if (error is ApiNetworkException) return error.message;
    if (error is ApiException) return error.message;
    return 'Something went wrong while saving the branch.';
  }
}
