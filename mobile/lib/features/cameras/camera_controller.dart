import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'camera_models.dart';
import 'camera_repository.dart';

final cameraRepositoryProvider = Provider<CameraRepository>(
  (ref) => CameraRepository(api: ApiClient(baseUrl: apiBaseUrl)),
);

final cameraControllerProvider =
    NotifierProvider<CameraController, CameraState>(CameraController.new);

class CameraState {
  const CameraState({
    this.publicCameras = const [],
    this.managedCameras = const [],
    this.selectedCamera,
    this.stream,
    this.isLoading = false,
    this.isSaving = false,
    this.isOffline = false,
    this.isUnauthorized = false,
    this.isUnavailable = false,
    this.errorMessage,
  });

  final List<PublicCamera> publicCameras;
  final List<ManagedCamera> managedCameras;
  final ManagedCamera? selectedCamera;
  final PublicCameraStream? stream;
  final bool isLoading;
  final bool isSaving;
  final bool isOffline;
  final bool isUnauthorized;
  final bool isUnavailable;
  final String? errorMessage;

  CameraState copyWith({
    List<PublicCamera>? publicCameras,
    List<ManagedCamera>? managedCameras,
    ManagedCamera? selectedCamera,
    bool clearSelectedCamera = false,
    PublicCameraStream? stream,
    bool clearStream = false,
    bool? isLoading,
    bool? isSaving,
    bool? isOffline,
    bool? isUnauthorized,
    bool? isUnavailable,
    String? errorMessage,
    bool clearError = false,
  }) => CameraState(
    publicCameras: publicCameras ?? this.publicCameras,
    managedCameras: managedCameras ?? this.managedCameras,
    selectedCamera: clearSelectedCamera
        ? null
        : selectedCamera ?? this.selectedCamera,
    stream: clearStream ? null : stream ?? this.stream,
    isLoading: isLoading ?? this.isLoading,
    isSaving: isSaving ?? this.isSaving,
    isOffline: isOffline ?? this.isOffline,
    isUnauthorized: isUnauthorized ?? this.isUnauthorized,
    isUnavailable: isUnavailable ?? this.isUnavailable,
    errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
  );
}

class CameraController extends Notifier<CameraState> {
  CameraRepository get _repository => ref.read(cameraRepositoryProvider);

  String? get _token =>
      ref.read(sessionControllerProvider).session?.accessToken;

  @override
  CameraState build() => const CameraState();

  Future<void> loadPublic() async => _load(() async {
    final cameras = await _repository.getPublic(_token!);
    state = state.copyWith(publicCameras: cameras);
  });

  Future<void> loadStream(String cameraId) async => _load(
    () async {
      final stream = await _repository.getPublicStream(_token!, cameraId);
      state = state.copyWith(stream: stream, clearError: true);
    },
    clearStream: true,
  );

  Future<void> loadManaged({int? branchId}) async => _load(() async {
    final cameras = await _repository.getManaged(_token!, branchId: branchId);
    state = state.copyWith(managedCameras: cameras);
  });

  Future<bool> create(SaveCameraRequest request) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(
      isSaving: true,
      isOffline: false,
      isUnauthorized: false,
      isUnavailable: false,
      clearError: true,
    );
    try {
      final camera = await _repository.create(token, request);
      state = state.copyWith(
        managedCameras: [...state.managedCameras, camera],
        selectedCamera: camera,
        isSaving: false,
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<bool> update(String cameraId, SaveCameraRequest request) async {
    final token = _token;
    if (token == null) return false;
    state = state.copyWith(
      isSaving: true,
      isOffline: false,
      isUnauthorized: false,
      isUnavailable: false,
      clearError: true,
    );
    try {
      final camera = await _repository.update(token, cameraId, request);
      final cameras = [...state.managedCameras];
      final index = cameras.indexWhere((item) => item.cameraId == cameraId);
      if (index == -1) {
        cameras.add(camera);
      } else {
        cameras[index] = camera;
      }
      state = state.copyWith(
        managedCameras: cameras,
        selectedCamera: camera,
        isSaving: false,
      );
      return true;
    } on Object catch (error) {
      _setFailure(error, saving: true);
      return false;
    }
  }

  Future<void> _load(
    Future<void> Function() operation, {
    bool clearStream = false,
  }) async {
    final token = _token;
    if (token == null) return;
    state = state.copyWith(
      isLoading: true,
      isOffline: false,
      isUnauthorized: false,
      isUnavailable: false,
      clearError: true,
      clearStream: clearStream,
    );
    try {
      await operation();
      state = state.copyWith(isLoading: false);
    } on Object catch (error) {
      _setFailure(error);
    }
  }

  void _setFailure(Object error, {bool saving = false}) {
    final isApiError = error is ApiException;
    final statusCode = isApiError ? error.statusCode : null;
    state = state.copyWith(
      isLoading: saving ? state.isLoading : false,
      isSaving: saving ? false : state.isSaving,
      isOffline: !isApiError,
      isUnauthorized: statusCode == 401 || statusCode == 403,
      isUnavailable: statusCode == 503,
      errorMessage: isApiError ? error.message : _offlineMessage,
    );
  }
}

const _offlineMessage =
    'Unable to reach DoodhDirect. Check your connection and try again.';
