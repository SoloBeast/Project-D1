import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_models.dart';
import 'package:doodh_direct_mobile/features/cameras/camera_repository.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test(
    'gets public cameras with bearer token and parses safe metadata',
    () async {
      final repository = _repository((request) async {
        _expectRequest(request, method: 'GET', path: '/api/v1/cameras/public');
        return _response([
          {
            'cameraId': 'camera-public-1',
            'displayName': 'Milking Hall',
            'displayOrder': 1,
            'isAvailable': true,
          },
        ]);
      });

      final cameras = await repository.getPublic('camera-token');

      expect(cameras.single.cameraId, 'camera-public-1');
      expect(cameras.single.isAvailable, isTrue);
    },
  );

  test('returns an empty public list when response data is absent', () async {
    final repository = _repository(
      (_) async => http.Response(jsonEncode({'success': true}), 200),
    );

    expect(await repository.getPublic('camera-token'), isEmpty);
  });

  test(
    'gets a short-lived public stream from the exact camera route',
    () async {
      final repository = _repository((request) async {
        _expectRequest(
          request,
          method: 'GET',
          path: '/api/v1/cameras/public/camera-1/stream',
        );
        return _response({
          'cameraId': 'camera-1',
          'displayName': 'Milking Hall',
          'stream': {
            'protocol': 'Hls',
            'playbackUri': 'https://stream.example.test/session/index.m3u8',
            'expiresAtUtc': '2099-08-17T10:05:00.000Z',
            'isDevelopmentStream': false,
          },
        });
      });

      final result = await repository.getPublicStream(
        'camera-token',
        'camera-1',
      );

      expect(result.cameraId, 'camera-1');
      expect(result.stream.protocol, CameraStreamProtocol.hls);
    },
  );

  test('gets all managed cameras without a branch query', () async {
    final repository = _repository((request) async {
      _expectRequest(request, method: 'GET', path: '/api/v1/admin/cameras');
      expect(request.url.query, isEmpty);
      return _response([_managedJson]);
    });

    final cameras = await repository.getManaged('camera-token');

    expect(cameras.single.branchId, 7);
  });

  test('adds the authorized branch query to managed camera reads', () async {
    final repository = _repository((request) async {
      _expectRequest(request, method: 'GET', path: '/api/v1/admin/cameras');
      expect(request.url.queryParameters, {'branchId': '7'});
      return _response([_managedJson]);
    });

    await repository.getManaged('camera-token', branchId: 7);
  });

  test('creates metadata without sending isActive', () async {
    final repository = _repository((request) async {
      _expectRequest(request, method: 'POST', path: '/api/v1/admin/cameras');
      expect(jsonDecode(request.body), {
        'branchId': 7,
        'internalIdentifier': 'MILKING-HALL',
        'displayName': 'Milking Hall',
        'isPublic': true,
        'displayOrder': 2,
        'protocol': 'Hls',
        'providerCode': 'gateway',
        'providerStreamReference': 'opaque-stream-1',
      });
      expect(jsonDecode(request.body), isNot(contains('isActive')));
      return _response(_managedJson, statusCode: 201);
    });

    final created = await repository.create('camera-token', _request);

    expect(created.cameraId, 'camera-managed-1');
  });

  test('updates metadata and sends explicit active status', () async {
    final repository = _repository((request) async {
      _expectRequest(
        request,
        method: 'PATCH',
        path: '/api/v1/admin/cameras/camera-managed-1',
      );
      expect(jsonDecode(request.body)['isActive'], isFalse);
      return _response({..._managedJson, 'isActive': false});
    });

    final updated = await repository.update(
      'camera-token',
      'camera-managed-1',
      _request,
    );

    expect(updated.isActive, isFalse);
  });
}

CameraRepository _repository(
  Future<http.Response> Function(http.Request request) handler,
) => CameraRepository(
  api: ApiClient(
    client: MockClient(handler),
    baseUrl: 'https://api.example.test',
  ),
);

void _expectRequest(
  http.Request request, {
  required String method,
  required String path,
}) {
  expect(request.method, method);
  expect(request.url.path, path);
  expect(request.headers['Authorization'], 'Bearer camera-token');
  expect(request.headers['Accept'], 'application/json');
  expect(request.headers['Content-Type'], 'application/json');
}

http.Response _response(Object data, {int statusCode = 200}) => http.Response(
  jsonEncode({'success': true, 'data': data, 'errors': <Object>[]}),
  statusCode,
  headers: {'content-type': 'application/json'},
);

const _request = SaveCameraRequest(
  branchId: 7,
  internalIdentifier: ' MILKING-HALL ',
  displayName: ' Milking Hall ',
  isPublic: true,
  isActive: false,
  displayOrder: 2,
  protocol: CameraStreamProtocol.hls,
  providerCode: ' gateway ',
  providerStreamReference: ' opaque-stream-1 ',
);

final _managedJson = <String, dynamic>{
  'cameraId': 'camera-managed-1',
  'branchId': 7,
  'branchName': 'Main Dairy',
  'internalIdentifier': 'MILKING-HALL',
  'displayName': 'Milking Hall',
  'isPublic': true,
  'isActive': true,
  'displayOrder': 2,
  'protocol': 'Hls',
  'providerCode': 'gateway',
  'providerStreamReference': 'opaque-stream-1',
  'createdAt': '2026-08-17T09:00:00.000',
  'updatedAt': '2026-08-17T10:00:00.000',
};
