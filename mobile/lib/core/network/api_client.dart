import 'dart:convert';
import 'dart:typed_data';

import 'package:http/http.dart' as http;
import 'package:http_parser/http_parser.dart';

typedef AccessTokenRefresh = Future<String?> Function();

class ApiException implements Exception {
  const ApiException(this.statusCode, this.code, this.message, {this.field});

  final int statusCode;
  final String code;
  final String message;
  final String? field;

  @override
  String toString() => '$statusCode $code: $message';
}

class ApiByteResponse {
  const ApiByteResponse({
    required this.bytes,
    required this.contentType,
    this.fileName,
  });

  final Uint8List bytes;
  final String contentType;
  final String? fileName;
}

class ApiClient {
  ApiClient({
    http.Client? client,
    required this.baseUrl,
    this.refreshAccessToken,
  }) : _client = client ?? http.Client();

  final http.Client _client;
  final String baseUrl;
  final AccessTokenRefresh? refreshAccessToken;

  Future<Map<String, dynamic>> get(String path, {String? accessToken}) async =>
      _sendJson(method: 'GET', path: path, accessToken: accessToken);

  Future<Map<String, dynamic>> post(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
    Map<String, String> extraHeaders = const <String, String>{},
  }) async => _sendJson(
    method: 'POST',
    path: path,
    body: jsonEncode(body),
    accessToken: accessToken,
    extraHeaders: extraHeaders,
  );

  Future<Map<String, dynamic>> _sendJson({
    required String method,
    required String path,
    String? body,
    String? accessToken,
    Map<String, String> extraHeaders = const <String, String>{},
  }) async {
    var token = accessToken;
    var response = await _sendJsonRequest(
      method: method,
      path: path,
      body: body,
      accessToken: token,
      extraHeaders: extraHeaders,
    );

    if (_shouldRefresh(response, path, token)) {
      final refreshedToken = await refreshAccessToken!();
      if (refreshedToken != null) {
        token = refreshedToken;
        response = await _sendJsonRequest(
          method: method,
          path: path,
          body: body,
          accessToken: token,
          extraHeaders: extraHeaders,
        );
      }
    }

    return _decode(response);
  }

  Future<http.Response> _sendJsonRequest({
    required String method,
    required String path,
    required String? body,
    required String? accessToken,
    required Map<String, String> extraHeaders,
  }) async {
    final request = http.Request(method, Uri.parse('$baseUrl$path'))
      ..headers.addAll({..._headers(accessToken), ...extraHeaders});
    if (body != null) request.body = body;
    return http.Response.fromStream(await _client.send(request));
  }

  Future<ApiByteResponse> postBytes(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
  }) async {
    final encodedBody = jsonEncode(body);
    var token = accessToken;
    var response = await _client.post(
      Uri.parse('$baseUrl$path'),
      headers: _headers(token),
      body: encodedBody,
    );
    if (_shouldRefresh(response, path, token)) {
      token = await refreshAccessToken!();
      if (token != null) {
        response = await _client.post(
          Uri.parse('$baseUrl$path'),
          headers: _headers(token),
          body: encodedBody,
        );
      }
    }
    if (response.statusCode < 200 || response.statusCode >= 300) {
      _decode(response);
    }
    return ApiByteResponse(
      bytes: response.bodyBytes,
      contentType:
          response.headers['content-type'] ?? 'application/octet-stream',
      fileName: _fileName(response.headers['content-disposition']),
    );
  }

  Future<Map<String, dynamic>> postMultipart(
    String path, {
    required String fieldName,
    required Uint8List bytes,
    required String fileName,
    required String contentType,
    String? accessToken,
  }) async {
    Future<http.Response> send(String? token) async {
      final request = http.MultipartRequest('POST', Uri.parse('$baseUrl$path'))
        ..headers.addAll(_headers(token, includeContentType: false))
        ..files.add(
          http.MultipartFile.fromBytes(
            fieldName,
            bytes,
            filename: fileName,
            contentType: MediaType.parse(contentType),
          ),
        );
      return http.Response.fromStream(await _client.send(request));
    }

    var token = accessToken;
    var response = await send(token);
    if (_shouldRefresh(response, path, token)) {
      token = await refreshAccessToken!();
      if (token != null) response = await send(token);
    }
    return _decode(response);
  }

  Future<Map<String, dynamic>> patch(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
  }) => _sendJson(
    method: 'PATCH',
    path: path,
    body: jsonEncode(body),
    accessToken: accessToken,
  );

  Future<Map<String, dynamic>> put(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
  }) => _sendJson(
    method: 'PUT',
    path: path,
    body: jsonEncode(body),
    accessToken: accessToken,
  );

  Future<Map<String, dynamic>> delete(String path, {String? accessToken}) =>
      _sendJson(method: 'DELETE', path: path, accessToken: accessToken);

  bool _shouldRefresh(http.Response response, String path, String? token) =>
      response.statusCode == 401 &&
      token != null &&
      refreshAccessToken != null &&
      path != '/api/v1/auth/refresh';

  Map<String, String> _headers(
    String? accessToken, {
    bool includeContentType = true,
  }) => {
    'Accept': 'application/json',
    if (includeContentType) 'Content-Type': 'application/json',
    if (accessToken != null) 'Authorization': 'Bearer $accessToken',
  };

  String? _fileName(String? contentDisposition) {
    if (contentDisposition == null) return null;
    final encoded = RegExp(
      r"filename\*=UTF-8''([^;]+)",
      caseSensitive: false,
    ).firstMatch(contentDisposition)?.group(1);
    if (encoded != null) return Uri.decodeComponent(encoded);
    return RegExp(
      r'filename="?([^";]+)"?',
      caseSensitive: false,
    ).firstMatch(contentDisposition)?.group(1);
  }

  Map<String, dynamic> _decode(http.Response response) {
    final body = response.body.isEmpty
        ? <String, dynamic>{}
        : jsonDecode(response.body) as Map<String, dynamic>;
    if (response.statusCode < 200 || response.statusCode >= 300) {
      final errors = body['errors'] as List<dynamic>?;
      final firstError = errors?.whereType<Map<String, dynamic>>().firstOrNull;
      throw ApiException(
        response.statusCode,
        firstError?['code'] as String? ?? 'HTTP_ERROR',
        firstError?['message'] as String? ??
            body['message'] as String? ??
            'The request could not be completed.',
        field: firstError?['field'] as String?,
      );
    }
    return body;
  }
}
