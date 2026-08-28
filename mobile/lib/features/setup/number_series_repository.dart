import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'number_series_models.dart';

/// HTTP client for the Setup → Number Series module.
class NumberSeriesRepository {
  NumberSeriesRepository({required this._api});

  final ApiClient _api;

  static const String _basePath = '/api/v1/admin/setup/number-series';

  Future<List<NumberSeries>> list(String accessToken) async {
    final response = await _api.get(_basePath, accessToken: accessToken);
    final items = response['data'] as List<dynamic>;
    return items
        .map((item) => NumberSeries.fromJson(item as Map<String, dynamic>))
        .toList(growable: false);
  }

  Future<NumberSeries> get(
    String accessToken,
    String code, {
    String? scope,
  }) async {
    final response = await _api.get(
      _path('/${Uri.encodeComponent(code)}', scope: scope),
      accessToken: accessToken,
    );
    return NumberSeries.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<NumberSeriesPreview> preview(
    String accessToken,
    NumberSeriesPreviewRequest request,
  ) async {
    final response = await _api.post(
      _path('/preview', scope: request.scope),
      accessToken: accessToken,
      body: request.toJson(),
    );
    return NumberSeriesPreview.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<NumberSeries> create(
    String accessToken,
    CreateNumberSeriesRequest request,
  ) async {
    final response = await _api.post(
      _basePath,
      accessToken: accessToken,
      body: request.toJson(),
    );
    return NumberSeries.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<NumberSeries> update(
    String accessToken,
    String code,
    UpdateNumberSeriesRequest request, {
    String? scope,
  }) async {
    final response = await _api.put(
      _path('/${Uri.encodeComponent(code)}', scope: scope),
      accessToken: accessToken,
      body: request.toJson(),
    );
    return NumberSeries.fromJson(response['data'] as Map<String, dynamic>);
  }

  Future<NumberSeries> setActive(
    String accessToken,
    String code,
    bool isActive, {
    String? scope,
  }) async {
    final response = await _api.post(
      _path(
        '/${Uri.encodeComponent(code)}/${isActive ? 'activate' : 'deactivate'}',
        scope: scope,
      ),
      accessToken: accessToken,
    );
    return NumberSeries.fromJson(response['data'] as Map<String, dynamic>);
  }

  /// Builds a path under `_basePath`, appending a `scope` query parameter
  /// when present so scoped operations target the right series instance.
  String _path(String segment, {String? scope}) {
    if (scope == null || scope.isEmpty) {
      return '$_basePath$segment';
    }
    final uri = Uri(
      path: '$_basePath$segment',
      queryParameters: <String, String>{'scope': scope},
    );
    return uri.toString();
  }
}
