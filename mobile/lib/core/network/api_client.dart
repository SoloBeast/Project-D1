import 'dart:convert';
import 'dart:typed_data';

import 'package:http/http.dart' as http;
import 'package:http_parser/http_parser.dart';

class ApiException implements Exception {
  const ApiException(this.statusCode, this.code, this.message);

  final int statusCode;
  final String code;
  final String message;

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
  ApiClient({http.Client? client, required this.baseUrl})
    : _client = client ?? http.Client();

  final http.Client _client;
  final String baseUrl;

  Future<Map<String, dynamic>> get(String path, {String? accessToken}) async {
    final response = await _client.get(
      Uri.parse('$baseUrl$path'),
      headers: _headers(accessToken),
    );
    return _decode(response);
  }

  Future<Map<String, dynamic>> post(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
    Map<String, String> extraHeaders = const <String, String>{},
  }) async {
    final response = await _client.post(
      Uri.parse('$baseUrl$path'),
      headers: {..._headers(accessToken), ...extraHeaders},
      body: jsonEncode(body),
    );
    return _decode(response);
  }

  Future<ApiByteResponse> postBytes(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
  }) async {
    final response = await _client.post(
      Uri.parse('$baseUrl$path'),
      headers: _headers(accessToken),
      body: jsonEncode(body),
    );
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
    final request = http.MultipartRequest('POST', Uri.parse('$baseUrl$path'))
      ..headers.addAll(_headers(accessToken, includeContentType: false))
      ..files.add(
        http.MultipartFile.fromBytes(
          fieldName,
          bytes,
          filename: fileName,
          contentType: MediaType.parse(contentType),
        ),
      );
    final response = await http.Response.fromStream(
      await _client.send(request),
    );
    return _decode(response);
  }

  Future<Map<String, dynamic>> patch(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
  }) async {
    final response = await _client.patch(
      Uri.parse('$baseUrl$path'),
      headers: _headers(accessToken),
      body: jsonEncode(body),
    );
    return _decode(response);
  }

  Future<Map<String, dynamic>> put(
    String path, {
    Map<String, dynamic> body = const <String, dynamic>{},
    String? accessToken,
  }) async {
    final response = await _client.put(
      Uri.parse('$baseUrl$path'),
      headers: _headers(accessToken),
      body: jsonEncode(body),
    );
    return _decode(response);
  }

  Future<Map<String, dynamic>> delete(
    String path, {
    String? accessToken,
  }) async {
    final response = await _client.delete(
      Uri.parse('$baseUrl$path'),
      headers: _headers(accessToken),
    );
    return _decode(response);
  }

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
      );
    }
    return body;
  }
}
