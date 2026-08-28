import 'package:flutter/foundation.dart';

/// Global switch for development-only UI surfaces (login-as-dev-customer,
/// wallet top-up, development payment method, etc.).
///
/// Defaults to true only in debug builds, and can be explicitly enabled for
/// non-release builds (e.g. a UAT build that targets the Development backend)
/// via `--dart-define=DOOHDIRECT_ENABLE_DEV_TOOLS=true`.
const bool devToolsEnabled = bool.fromEnvironment(
  'DOOHDIRECT_ENABLE_DEV_TOOLS',
  defaultValue: kDebugMode,
);
