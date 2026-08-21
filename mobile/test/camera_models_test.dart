import 'package:doodh_direct_mobile/features/cameras/camera_models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('camera stream protocol', () {
    test('parses API values and exposes API and display values', () {
      expect(CameraStreamProtocol.fromApi(' HLS '), CameraStreamProtocol.hls);
      expect(
        CameraStreamProtocol.fromApi('WebRtc'),
        CameraStreamProtocol.webRtc,
      );
      expect(
        CameraStreamProtocol.fromApi('unsupported'),
        CameraStreamProtocol.unknown,
      );
      expect(CameraStreamProtocol.fromApi(null), CameraStreamProtocol.unknown);
      expect(CameraStreamProtocol.hls.apiValue, 'Hls');
      expect(CameraStreamProtocol.webRtc.apiValue, 'WebRtc');
      expect(CameraStreamProtocol.webRtc.label, 'WebRTC');
    });
  });

  test('parses public camera without privileged metadata', () {
    final camera = PublicCamera.fromJson({
      'cameraId': 'camera-public-1',
      'displayName': 'Milking Hall',
      'displayOrder': 2.0,
      'isAvailable': true,
    });

    expect(camera.cameraId, 'camera-public-1');
    expect(camera.displayName, 'Milking Hall');
    expect(camera.displayOrder, 2);
    expect(camera.isAvailable, isTrue);
  });

  test('parses managed camera timestamps as India-local wall-clock values', () {
    final camera = ManagedCamera.fromJson(_managedJson);

    expect(camera.cameraId, 'camera-managed-1');
    expect(camera.branchId, 7);
    expect(camera.branchName, 'Main Dairy');
    expect(camera.internalIdentifier, 'MILKING-HALL');
    expect(camera.protocol, CameraStreamProtocol.hls);
    expect(camera.providerCode, 'gateway');
    expect(camera.providerStreamReference, 'opaque-stream-1');
    expect(camera.createdAt.isUtc, isFalse);
    expect(camera.updatedAt.isUtc, isFalse);
    expect(camera.updatedAt, DateTime(2026, 8, 17, 15, 45));
  });

  test('parses nested short-lived public stream descriptor', () {
    final result = PublicCameraStream.fromJson({
      'cameraId': 'camera-public-1',
      'displayName': 'Milking Hall',
      'stream': {
        'protocol': 'Hls',
        'playbackUri': 'https://stream.example.test/session/index.m3u8',
        'expiresAtUtc': '2099-08-17T10:05:00.000Z',
        'isDevelopmentStream': true,
      },
    });

    expect(result.cameraId, 'camera-public-1');
    expect(result.stream.protocol, CameraStreamProtocol.hls);
    expect(result.stream.playbackUri.scheme, 'https');
    expect(result.stream.expiresAtUtc.isUtc, isTrue);
    expect(result.stream.isDevelopmentStream, isTrue);
    expect(result.stream.isExpired, isFalse);
  });

  test('treats a future descriptor as active', () {
    final descriptor = _descriptor(DateTime.utc(2026, 8, 17, 10, 5));

    expect(
      descriptor.isExpiredAt(DateTime.utc(2026, 8, 17, 10, 4, 59)),
      isFalse,
    );
  });

  test('treats a descriptor at its expiry instant as expired', () {
    final descriptor = _descriptor(DateTime.utc(2026, 8, 17, 10, 5));

    expect(
      descriptor.isExpiredAt(DateTime.utc(2026, 8, 17, 10, 5)),
      isTrue,
    );
  });

  test('treats a past descriptor as expired', () {
    final descriptor = _descriptor(DateTime.utc(2026, 8, 17, 10, 5));

    expect(
      descriptor.isExpiredAt(DateTime.utc(2026, 8, 17, 10, 5, 1)),
      isTrue,
    );
  });

  test('serializes create and update requests with trimmed metadata', () {
    const request = SaveCameraRequest(
      branchId: 7,
      internalIdentifier: ' MILKING-HALL ',
      displayName: ' Milking Hall ',
      isPublic: true,
      isActive: false,
      displayOrder: 3,
      protocol: CameraStreamProtocol.webRtc,
      providerCode: ' gateway ',
      providerStreamReference: ' opaque-reference ',
    );

    expect(request.toCreateJson(), {
      'branchId': 7,
      'internalIdentifier': 'MILKING-HALL',
      'displayName': 'Milking Hall',
      'isPublic': true,
      'displayOrder': 3,
      'protocol': 'WebRtc',
      'providerCode': 'gateway',
      'providerStreamReference': 'opaque-reference',
    });
    expect(request.toCreateJson(), isNot(contains('isActive')));
    expect(request.toUpdateJson()['isActive'], isFalse);
  });
}

CameraStreamDescriptor _descriptor(DateTime expiresAtUtc) =>
    CameraStreamDescriptor(
      protocol: CameraStreamProtocol.hls,
      playbackUri: Uri.parse('https://stream.example.test/session/index.m3u8'),
      expiresAtUtc: expiresAtUtc,
      isDevelopmentStream: false,
    );

final _managedJson = <String, dynamic>{
  'cameraId': 'camera-managed-1',
  'branchId': 7,
  'branchName': 'Main Dairy',
  'internalIdentifier': 'MILKING-HALL',
  'displayName': 'Milking Hall',
  'isPublic': true,
  'isActive': true,
  'displayOrder': 1,
  'protocol': 'Hls',
  'providerCode': 'gateway',
  'providerStreamReference': 'opaque-stream-1',
  'createdAt': '2026-08-17T09:00:00.000',
  'updatedAt': '2026-08-17T15:45:00.000',
};
