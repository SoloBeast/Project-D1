import 'dart:async';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_controller.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_models.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('camera controller', () {
    test('loads public, stream, and branch-filtered managed cameras', () async {
      final repository = _FakeCameraRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(cameraControllerProvider.notifier);

      await controller.loadPublic();
      expect(
        container.read(cameraControllerProvider).publicCameras.single.cameraId,
        'public-camera-1',
      );

      await controller.loadStream('public-camera-1');
      expect(
        container.read(cameraControllerProvider).stream?.cameraId,
        'public-camera-1',
      );

      await controller.loadManaged(branchId: 7);
      final state = container.read(cameraControllerProvider);
      expect(state.managedCameras.single.branchId, 7);
      expect(state.isLoading, isFalse);
      expect(repository.lastToken, 'camera-token');
      expect(repository.lastBranchId, 7);
      expect(repository.lastStreamCameraId, 'public-camera-1');
    });

    test(
      'sets loading immediately and clears stale stream before refresh',
      () async {
        final repository = _DelayedCameraRepository();
        final container = await _authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(cameraControllerProvider.notifier);

        final firstLoad = controller.loadStream('public-camera-1');
        repository.complete(_publicStream('public-camera-1'));
        await firstLoad;
        expect(container.read(cameraControllerProvider).stream, isNotNull);

        final secondLoad = controller.loadStream('public-camera-2');
        final loading = container.read(cameraControllerProvider);
        expect(loading.isLoading, isTrue);
        expect(loading.stream, isNull);

        repository.complete(_publicStream('public-camera-2'));
        await secondLoad;
        expect(container.read(cameraControllerProvider).isLoading, isFalse);
      },
    );

    test('appends created camera and selects it', () async {
      final repository = _FakeCameraRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(cameraControllerProvider.notifier);

      expect(await controller.create(_request), isTrue);
      final state = container.read(cameraControllerProvider);

      expect(state.managedCameras.single.cameraId, 'created-camera');
      expect(state.selectedCamera?.cameraId, 'created-camera');
      expect(state.isSaving, isFalse);
    });

    test(
      'replaces an existing camera on update and upserts an absent one',
      () async {
        final repository = _FakeCameraRepository();
        final container = await _authenticatedContainer(repository);
        addTearDown(container.dispose);
        final controller = container.read(cameraControllerProvider.notifier);

        await controller.loadManaged();
        expect(await controller.update('managed-camera-1', _request), isTrue);
        var state = container.read(cameraControllerProvider);
        expect(state.managedCameras, hasLength(1));
        expect(state.managedCameras.single.displayName, 'Updated camera');

        expect(await controller.update('missing-camera', _request), isTrue);
        state = container.read(cameraControllerProvider);
        expect(state.managedCameras, hasLength(2));
        expect(state.selectedCamera?.cameraId, 'missing-camera');
      },
    );

    for (final statusCode in [401, 403]) {
      test('maps $statusCode failures to unauthorized state', () async {
        final container = await _authenticatedContainer(
          _FailingCameraRepository(
            ApiException(statusCode, 'CAMERA_ACCESS_DENIED', 'Camera denied.'),
          ),
        );
        addTearDown(container.dispose);

        await container.read(cameraControllerProvider.notifier).loadManaged();
        final state = container.read(cameraControllerProvider);

        expect(state.isUnauthorized, isTrue);
        expect(state.isOffline, isFalse);
        expect(state.isUnavailable, isFalse);
        expect(state.errorMessage, 'Camera denied.');
      });
    }

    test('maps 503 stream failures to unavailable state', () async {
      final container = await _authenticatedContainer(
        _FailingCameraRepository(
          const ApiException(
            503,
            'CAMERA_STREAM_UNAVAILABLE',
            'The camera stream is temporarily unavailable.',
          ),
        ),
      );
      addTearDown(container.dispose);

      await container
          .read(cameraControllerProvider.notifier)
          .loadStream('camera-1');
      final state = container.read(cameraControllerProvider);

      expect(state.isUnavailable, isTrue);
      expect(state.isOffline, isFalse);
      expect(state.isUnauthorized, isFalse);
      expect(state.stream, isNull);
    });

    test('maps transport failures to offline state', () async {
      final container = await _authenticatedContainer(
        _FailingCameraRepository(Exception('socket closed')),
      );
      addTearDown(container.dispose);

      await container.read(cameraControllerProvider.notifier).loadPublic();
      final state = container.read(cameraControllerProvider);

      expect(state.isOffline, isTrue);
      expect(state.errorMessage, contains('Check your connection'));
    });

    test('does not call repository without an authenticated session', () async {
      final repository = _FakeCameraRepository();
      final container = ProviderContainer(
        overrides: [cameraRepositoryProvider.overrideWithValue(repository)],
      );
      addTearDown(container.dispose);
      container.read(cameraControllerProvider);

      await container.read(cameraControllerProvider.notifier).loadPublic();
      expect(
        await container
            .read(cameraControllerProvider.notifier)
            .create(_request),
        isFalse,
      );

      expect(repository.callCount, 0);
      expect(container.read(cameraControllerProvider), isA<CameraState>());
    });
  });
}

Future<ProviderContainer> _authenticatedContainer(
  CameraRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      cameraRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}

class _FakeCameraRepository extends CameraRepository {
  _FakeCameraRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;
  String? lastStreamCameraId;
  int? lastBranchId;
  int callCount = 0;

  @override
  Future<List<PublicCamera>> getPublic(String token) async {
    _record(token);
    return const [
      PublicCamera(
        cameraId: 'public-camera-1',
        displayName: 'Milking Hall',
        displayOrder: 1,
        isAvailable: true,
      ),
    ];
  }

  @override
  Future<PublicCameraStream> getPublicStream(
    String token,
    String cameraId,
  ) async {
    _record(token);
    lastStreamCameraId = cameraId;
    return _publicStream(cameraId);
  }

  @override
  Future<List<ManagedCamera>> getManaged(String token, {int? branchId}) async {
    _record(token);
    lastBranchId = branchId;
    return [_managedCamera()];
  }

  @override
  Future<ManagedCamera> create(String token, SaveCameraRequest request) async {
    _record(token);
    return _managedCamera(cameraId: 'created-camera');
  }

  @override
  Future<ManagedCamera> update(
    String token,
    String cameraId,
    SaveCameraRequest request,
  ) async {
    _record(token);
    return _managedCamera(cameraId: cameraId, displayName: 'Updated camera');
  }

  void _record(String token) {
    callCount++;
    lastToken = token;
  }
}

class _DelayedCameraRepository extends CameraRepository {
  _DelayedCameraRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  Completer<PublicCameraStream> _completer = Completer<PublicCameraStream>();

  @override
  Future<PublicCameraStream> getPublicStream(String token, String cameraId) =>
      _completer.future;

  void complete(PublicCameraStream stream) {
    _completer.complete(stream);
    _completer = Completer<PublicCameraStream>();
  }
}

class _FailingCameraRepository extends CameraRepository {
  _FailingCameraRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<List<PublicCamera>> getPublic(String token) async => throw failure;

  @override
  Future<PublicCameraStream> getPublicStream(
    String token,
    String cameraId,
  ) async => throw failure;

  @override
  Future<List<ManagedCamera>> getManaged(String token, {int? branchId}) async =>
      throw failure;
}

PublicCameraStream _publicStream(String cameraId) => PublicCameraStream(
  cameraId: cameraId,
  displayName: 'Milking Hall',
  stream: CameraStreamDescriptor(
    protocol: CameraStreamProtocol.hls,
    playbackUri: Uri.parse('https://stream.example.test/session/index.m3u8'),
    expiresAtUtc: DateTime.utc(2099),
    isDevelopmentStream: false,
  ),
);

ManagedCamera _managedCamera({
  String cameraId = 'managed-camera-1',
  String displayName = 'Milking Hall',
}) => ManagedCamera(
  cameraId: cameraId,
  branchId: 7,
  branchName: 'Main Dairy',
  internalIdentifier: 'MILKING-HALL',
  displayName: displayName,
  isPublic: true,
  isActive: true,
  displayOrder: 1,
  protocol: CameraStreamProtocol.hls,
  providerCode: 'gateway',
  providerStreamReference: 'opaque-stream-1',
  createdAtUtc: DateTime.utc(2026, 8, 17, 9),
  updatedAtUtc: DateTime.utc(2026, 8, 17, 10),
);

const _request = SaveCameraRequest(
  branchId: 7,
  internalIdentifier: 'MILKING-HALL',
  displayName: 'Milking Hall',
  isPublic: true,
  isActive: true,
  displayOrder: 1,
  protocol: CameraStreamProtocol.hls,
  providerCode: 'gateway',
  providerStreamReference: 'opaque-stream-1',
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'owner-1',
    displayName: 'Owner',
    email: 'owner@example.test',
    mobile: null,
    roles: ['OWNER'],
    permissions: ['CAMERAS.READ', 'CAMERAS.MANAGE', 'ACCESS.GLOBAL'],
    branchIds: [7],
  ),
  accessToken: 'camera-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
