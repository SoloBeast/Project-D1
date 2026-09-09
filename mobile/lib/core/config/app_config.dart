import 'package:flutter/foundation.dart';

/// Global switch for development-only UI surfaces (login-as-dev-customer,
/// wallet top-up, development payment method, etc.).
///
/// Development tools are NEVER auto-enabled. They require a deliberate opt-in
/// via `--dart-define=DOOHDIRECT_ENABLE_DEV_TOOLS=true` when building a
/// non-release binary, and can never be enabled in a release build. Normal
/// debug/profile development builds do not surface these tools automatically.
const bool devToolsEnabled = !kReleaseMode && bool.fromEnvironment(
  'DOOHDIRECT_ENABLE_DEV_TOOLS',
  defaultValue: false,
);
