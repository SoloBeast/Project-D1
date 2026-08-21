import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_controller.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_models.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('live dairy camera list', () {
    testWidgets('shows loading, empty, and unavailable camera states', (
      tester,
    ) async {
      await _pump(
        tester,
        const LiveDairyCameraListScreen(),
        _SeededCameraController(const CameraState(isLoading: true)),
      );
      expect(find.bySemanticsLabel('Loading dairy cameras...'), findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsOneWidget);

      await _pump(
        tester,
        const LiveDairyCameraListScreen(),
        _SeededCameraController(const CameraState()),
      );
      expect(find.text('No live cameras available'), findsOneWidget);
      expect(find.text('Refresh'), findsOneWidget);

      await _pump(
        tester,
        const LiveDairyCameraListScreen(),
        _SeededCameraController(
          const CameraState(
            publicCameras: [
              PublicCamera(
                cameraId: 'camera-1',
                displayName: 'Milking Hall',
                displayOrder: 1,
                isAvailable: false,
              ),
            ],
          ),
        ),
      );
      expect(find.text('Milking Hall'), findsOneWidget);
      expect(find.text('Temporarily offline'), findsOneWidget);
      expect(tester.widget<ListTile>(find.byType(ListTile)).enabled, isFalse);
    });

    testWidgets('shows offline retry and invokes public reload', (
      tester,
    ) async {
      final controller = _SeededCameraController(
        const CameraState(
          isOffline: true,
          errorMessage: 'Unable to reach DoodhDirect. Check your connection and try again.',
        ),
      );
      await _pump(tester, const LiveDairyCameraListScreen(), controller);

      expect(find.text('You are offline'), findsOneWidget);
      await tester.tap(find.text('Retry'));
      await tester.pump();

      expect(controller.publicLoadCount, 2);
    });
  });

  group('live dairy camera viewer', () {
    testWidgets('shows unavailable state and retries stream request', (
      tester,
    ) async {
      final controller = _SeededCameraController(
        const CameraState(
          isUnavailable: true,
          errorMessage: 'The camera stream is temporarily unavailable.',
        ),
      );
      await _pump(
        tester,
        const LiveDairyCameraViewerScreen(cameraId: 'camera-1'),
        controller,
      );

      expect(find.text('Camera temporarily unavailable'), findsOneWidget);
      expect(
        find.text('The camera stream is temporarily unavailable.'),
        findsOneWidget,
      );
      await tester.tap(find.text('Retry'));
      await tester.pump();
      expect(controller.streamLoadCount, 2);
      expect(controller.lastCameraId, 'camera-1');
    });

    testWidgets('rejects expired and unsupported stream descriptors', (
      tester,
    ) async {
      await _pump(
        tester,
        const LiveDairyCameraViewerScreen(cameraId: 'expired-camera'),
        _SeededCameraController(
          CameraState(stream: _stream(expiresAtUtc: DateTime.utc(2000))),
        ),
      );
      expect(find.text('Stream session expired'), findsOneWidget);
      expect(find.text('Refresh stream'), findsOneWidget);

      await _pump(
        tester,
        const LiveDairyCameraViewerScreen(cameraId: 'camera-1'),
        _SeededCameraController(
          CameraState(stream: _stream(protocol: CameraStreamProtocol.webRtc)),
        ),
      );
      expect(find.text('Stream format unavailable'), findsOneWidget);
    });

    testWidgets('marks development playback and handles player failure', (
      tester,
    ) async {
      late VoidCallback failPlayback;
      await _pump(
        tester,
        LiveDairyCameraViewerScreen(
          cameraId: 'camera-1',
          playerBuilder: (stream, onFailure) {
            failPlayback = onFailure;
            return const Text('Injected HLS player');
          },
        ),
        _SeededCameraController(
          CameraState(stream: _stream(isDevelopmentStream: true)),
        ),
      );

      expect(
        find.text('Development stream - not a production camera'),
        findsOneWidget,
      );
      expect(find.text('Injected HLS player'), findsOneWidget);

      failPlayback();
      await tester.pump();
      expect(find.text('Stream playback failed'), findsOneWidget);
      expect(find.text('Retry stream'), findsOneWidget);
    });
  });

  group('camera management screen', () {
    testWidgets('keeps management controls hidden for read-only users', (
      tester,
    ) async {
      await _pump(
        tester,
        const AdminCameraListScreen(),
        _SeededCameraController(
          CameraState(managedCameras: [_managedCamera()]),
        ),
        permissions: const ['CAMERAS.READ'],
      );

      expect(find.text('Milking Hall'), findsOneWidget);
      expect(find.byType(FloatingActionButton), findsNothing);
      expect(find.byIcon(Icons.edit_outlined), findsNothing);
    });

    testWidgets('shows scoped branch selector to authorized managers', (
      tester,
    ) async {
      await _pump(
        tester,
        const AdminCameraListScreen(),
        _SeededCameraController(const CameraState()),
        permissions: const ['CAMERAS.READ', 'CAMERAS.MANAGE'],
        branchIds: const [7, 9],
      );

      expect(find.byType(FloatingActionButton), findsOneWidget);
      await tester.tap(find.byType(FloatingActionButton));
      await tester.pumpAndSettle();

      expect(find.text('Add camera'), findsWidgets);
      expect(find.text('Branch 7'), findsOneWidget);
      expect(find.text('Branch ID'), findsNothing);
      expect(find.text('Active'), findsNothing);
    });

    testWidgets('shows numeric branch field to global managers', (
      tester,
    ) async {
      await _pump(
        tester,
        const AdminCameraListScreen(),
        _SeededCameraController(const CameraState()),
        permissions: const ['CAMERAS.READ', 'CAMERAS.MANAGE', 'ACCESS.GLOBAL'],
      );

      await tester.tap(find.byType(FloatingActionButton));
      await tester.pumpAndSettle();

      expect(find.text('Branch ID'), findsOneWidget);
      expect(find.text('Branch 7'), findsNothing);
    });
  });
}

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededCameraController controller, {
  List<String> permissions = const ['CAMERAS.VIEW_PUBLIC'],
  List<int> branchIds = const [7],
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        cameraControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(
            permissions: permissions,
            branchIds: branchIds,
          ),
        ),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pump();
  await tester.pump();
}

class _SeededCameraController extends CameraController {
  _SeededCameraController(this.initialState);

  final CameraState initialState;
  int publicLoadCount = 0;
  int streamLoadCount = 0;
  int managedLoadCount = 0;
  String? lastCameraId;

  @override
  CameraState build() => initialState;

  @override
  Future<void> loadPublic() async {
    publicLoadCount++;
  }

  @override
  Future<void> loadStream(String cameraId) async {
    streamLoadCount++;
    lastCameraId = cameraId;
  }

  @override
  Future<void> loadManaged({int? branchId}) async {
    managedLoadCount++;
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({
    required this.permissions,
    required this.branchIds,
  });

  final List<String> permissions;
  final List<int> branchIds;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'camera-user-1',
        displayName: 'Camera User',
        email: null,
        mobile: '9999999999',
        roles: const ['OWNER'],
        permissions: permissions,
        branchIds: branchIds,
      ),
      accessToken: 'camera-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

PublicCameraStream _stream({
  CameraStreamProtocol protocol = CameraStreamProtocol.hls,
  DateTime? expiresAtUtc,
  bool isDevelopmentStream = false,
}) => PublicCameraStream(
  cameraId: protocol == CameraStreamProtocol.hls && expiresAtUtc?.year == 2000
      ? 'expired-camera'
      : 'camera-1',
  displayName: 'Milking Hall',
  stream: CameraStreamDescriptor(
    protocol: protocol,
    playbackUri: Uri.parse('https://stream.example.test/session/index.m3u8'),
    expiresAtUtc: expiresAtUtc ?? DateTime.utc(2099),
    isDevelopmentStream: isDevelopmentStream,
  ),
);

ManagedCamera _managedCamera() => ManagedCamera(
  cameraId: 'managed-camera-1',
  branchId: 7,
  branchName: 'Main Dairy',
  internalIdentifier: 'MILKING-HALL',
  displayName: 'Milking Hall',
  isPublic: true,
  isActive: true,
  displayOrder: 1,
  protocol: CameraStreamProtocol.hls,
  providerCode: 'gateway',
  providerStreamReference: 'opaque-stream-1',
  createdAt: DateTime(2026, 8, 17, 9),
  updatedAt: DateTime(2026, 8, 17, 10),
);
