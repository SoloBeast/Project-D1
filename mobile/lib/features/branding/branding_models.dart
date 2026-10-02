/// Models for the globally-managed DoodhDirect branding assets (logo and
/// startup animation). Mirrors the backend `BrandingResult` contract: only
/// safe public metadata — never storage keys, uploader identity, or audit
/// data.
library;

enum BrandingAssetKind { logo, startupAnimation }

class BrandingAssetInfo {
  const BrandingAssetInfo({
    required this.kind,
    required this.fileName,
    required this.contentType,
    required this.fileSize,
    required this.updatedAt,
    required this.url,
  });

  final BrandingAssetKind kind;
  final String fileName;
  final String contentType;
  final int fileSize;
  final DateTime updatedAt;

  /// Relative API URL (e.g. `/api/v1/branding/logo`). Resolve through
  /// [resolveMediaUrl] before handing it to `Image.network`.
  final String url;

  bool get isAnimation => contentType == 'image/gif';

  factory BrandingAssetInfo.fromJson(Map<String, dynamic> json) =>
      BrandingAssetInfo(
        kind: json['kind'] == 'StartupAnimation'
            ? BrandingAssetKind.startupAnimation
            : BrandingAssetKind.logo,
        fileName: json['fileName'] as String? ?? '',
        contentType: json['contentType'] as String? ?? '',
        fileSize: (json['fileSize'] as num?)?.toInt() ?? 0,
        updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0),
        url: json['url'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => <String, dynamic>{
    'kind': kind == BrandingAssetKind.startupAnimation
        ? 'StartupAnimation'
        : 'Logo',
    'fileName': fileName,
    'contentType': contentType,
    'fileSize': fileSize,
    'updatedAt': updatedAt.toIso8601String(),
    'url': url,
  };
}

class BrandingConfiguration {
  const BrandingConfiguration({this.logo, this.startupAnimation});

  final BrandingAssetInfo? logo;
  final BrandingAssetInfo? startupAnimation;

  factory BrandingConfiguration.fromJson(Map<String, dynamic> json) =>
      BrandingConfiguration(
        logo: json['logo'] is Map<String, dynamic>
            ? BrandingAssetInfo.fromJson(json['logo'] as Map<String, dynamic>)
            : null,
        startupAnimation:
            json['startupAnimation'] is Map<String, dynamic>
                ? BrandingAssetInfo.fromJson(
                    json['startupAnimation'] as Map<String, dynamic>)
                : null,
      );

  Map<String, dynamic> toJson() => <String, dynamic>{
    if (logo != null) 'logo': logo!.toJson(),
    if (startupAnimation != null) 'startupAnimation': startupAnimation!.toJson(),
  };
}
