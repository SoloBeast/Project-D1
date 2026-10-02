/// Central media URL resolution for the app.
///
/// The API returns relative media paths (for example a product image URL of
/// `/api/v1/products/{id}/image`). On Flutter Web a relative URL handed to
/// `NetworkImage`/`Image.network` resolves against the *web app's own origin*,
/// not the API origin, so every product image silently fell back to the
/// branded placeholder. All media rendering must go through [resolveMediaUrl]
/// so relative paths are joined onto the configured API base URL exactly once.
library;

import 'package:doodh_direct_mobile/features/auth/auth_repository.dart' show apiBaseUrl;

/// Resolves [url] into an absolute URL for direct image loading.
///
/// * `null`/blank input → `null` (callers keep their branded fallback).
/// * Already absolute (`scheme://…`) → returned unchanged.
/// * Relative → resolved against the configured API base URL using the same
///   join semantics as `ApiClient` (`'$baseUrl$path'`), so a base URL with a
///   path component (e.g. `https://host/api-root`) behaves identically for
///   media and JSON routes.
String? resolveMediaUrl(String? url) {
  final trimmed = url?.trim();
  if (trimmed == null || trimmed.isEmpty) return null;

  final uri = Uri.tryParse(trimmed);
  if (uri == null) return null;
  if (uri.hasScheme && uri.hasAuthority) return trimmed;

  final base = Uri.parse(apiBaseUrl);
  if (trimmed.startsWith('/')) {
    // Mirror ApiClient's string concat: a leading-slash media path appends to
    // the base URL's path instead of replacing it (Uri.resolve semantics).
    final root = base.toString().replaceAll(RegExp(r'/+$'), '');
    return '$root$trimmed';
  }
  return base.resolve(trimmed).toString();
}
