import 'package:flutter/material.dart';

/// Brand + semantic colour tokens.
///
/// The brand palette (`ink`, `muted`, `teal`, `tealDark`, `mint`, `cream`,
/// `amber`, `coral`, `line`) is the existing DoodhDirect identity and MUST NOT
/// be replaced arbitrarily. The `*Surface` / `*Foreground` pairs below are the
/// semantic layer used by shared components so status colours stay consistent
/// (and contrast-safe) across every screen.
abstract final class DoodhColors {
  // Brand palette. The teal/cream/tan values are tuned to the supplied
  // customer reference images: warm ivory background, deep forest primary,
  // warm tan delivery surfaces. Names stay stable so every screen picks the
  // retuned values up through the shared tokens.
  static const ink = Color(0xFF172321);
  static const muted = Color(0xFF667572);

  /// Primary action / surface: primary deep teal (reference hero + CTAs,
  /// including the product-card "Add to cart" fills).
  static const teal = Color(0xFF0B6B70);

  /// Deep forest for headline emphasis and selected calendar treatment.
  static const tealDark = Color(0xFF0C2E2A);

  /// Soft pale-green supporting surface (reference cards / search wells).
  static const mint = Color(0xFFEAF0DC);

  /// Warm off-white app background.
  static const cream = Color(0xFFFFF9EE);
  static const amber = Color(0xFFF4B942);
  static const coral = Color(0xFFD8664D);

  /// Warm hairline for card borders on the ivory background.
  static const line = Color(0xFFE8E0CD);

  /// Warm tan surface for the delivery/subscription band on the customer
  /// entry surface (reference language: warm golden supporting surface).
  static const tanSurface = Color(0xFFEFDFB2);

  /// Deep golden accent for chips and highlighted actions on tan surfaces.
  static const goldAccent = Color(0xFF8A5B1F);

  /// Soft warm highlight surface for emphasized supporting rows and
  /// highlighted quick actions (reference guarantee/assurance strips).
  static const highlightSurface = Color(0xFFFBF3D9);

  /// Vivid delivered-green used by calendar status dots and the legend so the
  /// "Delivered" state reads at a glance like the reference.
  static const deliveredGreen = Color(0xFF17B34A);

  /// Light blue for the calendar "Upcoming" indicator (legend dots, calendar
  /// day-cell dots and week-strip scheduled dots) so it reads apart from the
  /// teal brand primary and the delivered green.
  static const upcomingBlue = Color(0xFF64B5F6);

  // Neutral surfaces.
  static const neutralSurface = Color(0xFFEFF2F0);
  static const neutralForeground = muted;

  // Success (healthy / active / completed).
  static const successSurface = mint;
  static const successForeground = tealDark;

  // Warning (attention / pending / at risk).
  static const warningSurface = Color(0xFFFFF2D2);
  static const warningForeground = Color(0xFF855D00);

  // Error (failed / blocked / destructive).
  static const errorSurface = Color(0xFFFCE5E0);
  static const errorForeground = coral;

  // Info (informational highlight).
  static const infoSurface = Color(0xFFE4F0F5);
  static const infoForeground = tealDark;

  /// Backwards-compatible alias for the container error surface.
  static const errorContainer = errorSurface;
}

/// Spacing scale. `xs/sm/md/lg/xl` are the canonical tokens; the aliases keep
/// the scale expressive for higher-level layout without inventing new numbers.
abstract final class DoodhSpacing {
  static const xs = 4.0;
  static const sm = 8.0;
  static const md = 16.0;
  static const lg = 24.0;
  static const xl = 32.0;
  static const xxl = 40.0;
  static const xxxl = 48.0;

  // Semantic aliases.
  static const gapXs = xs;
  static const gapSm = sm;
  static const gapMd = md;
  static const gapLg = lg;
  static const gapXl = xl;
  static const gutter = md;

  /// The standard screen/list edge inset (`EdgeInsets.all(md)`).
  ///
  /// Replaces the repeated `const EdgeInsets.all(16)` literal on scrollable
  /// screen bodies. Only adopt where it matches exactly to avoid visual churn.
  static const pagePadding = EdgeInsets.all(md);

  /// The standard card inner padding (`EdgeInsets.all(md)`).
  static const cardPadding = EdgeInsets.all(md);

  /// The standard gap between stacked cards/sections (`SizedBox` height).
  static const sectionGap = md;
}

/// Corner radius tokens.
///
/// `xs/sm/md/lg/pill` remain [`BorderRadius`] values (unchanged from the
/// original design system) so existing call sites keep compiling. The
/// `*Value` constants expose the raw numeric radius for widgets that need a
/// `double` (e.g. `BorderRadius.circular(...)`) and `*Radius` are readability
/// aliases used by the new shared primitives.
abstract final class DoodhRadii {
  static const xsValue = 4.0;
  static const smValue = 8.0;
  static const mdValue = 18.0;
  static const lgValue = 20.0;
  static const pillValue = 999.0;

  static const xs = BorderRadius.all(Radius.circular(xsValue));
  static const sm = BorderRadius.all(Radius.circular(smValue));
  static const md = BorderRadius.all(Radius.circular(mdValue));
  static const lg = BorderRadius.all(Radius.circular(lgValue));
  static const pill = BorderRadius.all(Radius.circular(pillValue));

  static const smRadius = sm;
  static const mdRadius = md;
  static const lgRadius = lg;
  static const pillRadius = pill;
}

/// Typography scale in logical pixels, aligned with the theme `TextTheme`.
abstract final class DoodhType {
  static const display = 32.0;
  static const headline = 24.0;
  static const title = 18.0;
  static const subtitle = 16.0;
  static const body = 14.0;
  static const label = 13.0;
  static const caption = 12.0;
  static const micro = 11.0;
}

/// Elevation tokens (used as Material `elevation`, not shadows).
abstract final class DoodhElevation {
  static const none = 0.0;
  static const low = 1.0;
  static const medium = 3.0;
  static const high = 6.0;
}

/// Motion tokens. Durations stay short and consistent across the app.
abstract final class DoodhMotion {
  static const fast = Duration(milliseconds: 120);
  static const normal = Duration(milliseconds: 200);
  static const slow = Duration(milliseconds: 320);

  static const standardCurve = Curves.easeInOut;
}

/// Responsive breakpoints (logical pixels, screen width).
abstract final class DoodhBreakpoints {
  static const compact = 600.0;
  static const medium = 900.0;
  static const expanded = 1200.0;

  /// Classifies a width into a coarse size bucket.
  static DoodhWindowSize of(double width) {
    if (width < compact) return DoodhWindowSize.compact;
    if (width < medium) return DoodhWindowSize.medium;
    if (width < expanded) return DoodhWindowSize.expanded;
    return DoodhWindowSize.large;
  }
}

enum DoodhWindowSize { compact, medium, expanded, large }

/// Maximum content widths for centred, readable layouts.
abstract final class DoodhContentMax {
  /// Wide app shell (customer pages, dashboards).
  static const wide = 1180.0;

  /// Form / reading width.
  static const form = 720.0;

  /// Narrow state panels.
  static const narrow = 420.0;
}

ThemeData buildDoodhTheme() {
  final scheme =
      ColorScheme.fromSeed(
        seedColor: DoodhColors.teal,
        brightness: Brightness.light,
      ).copyWith(
        primary: DoodhColors.teal,
        onPrimary: Colors.white,
        secondary: DoodhColors.amber,
        surface: Colors.white,
        onSurface: DoodhColors.ink,
        error: DoodhColors.errorForeground,
        errorContainer: DoodhColors.errorSurface,
        onErrorContainer: DoodhColors.errorForeground,
      );

  return ThemeData(
    colorScheme: scheme,
    scaffoldBackgroundColor: DoodhColors.cream,
    useMaterial3: true,
    appBarTheme: const AppBarTheme(
      backgroundColor: DoodhColors.cream,
      foregroundColor: DoodhColors.ink,
      elevation: 0,
      centerTitle: false,
      titleTextStyle: TextStyle(
        color: DoodhColors.ink,
        fontSize: 20,
        fontWeight: FontWeight.w700,
      ),
    ),
    textTheme: const TextTheme(
      displaySmall: TextStyle(
        color: DoodhColors.ink,
        fontWeight: FontWeight.w800,
      ),
      headlineSmall: TextStyle(
        color: DoodhColors.ink,
        fontWeight: FontWeight.w800,
      ),
      titleLarge: TextStyle(
        color: DoodhColors.ink,
        fontWeight: FontWeight.w700,
      ),
      titleMedium: TextStyle(
        color: DoodhColors.ink,
        fontWeight: FontWeight.w700,
      ),
      bodyLarge: TextStyle(color: DoodhColors.ink, height: 1.4),
      bodyMedium: TextStyle(color: DoodhColors.muted, height: 1.4),
      labelLarge: TextStyle(fontWeight: FontWeight.w700),
    ),
    cardTheme: const CardThemeData(
      color: Colors.white,
      elevation: DoodhElevation.none,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: DoodhRadii.mdRadius,
        side: BorderSide(color: DoodhColors.line),
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: Colors.white,
      border: const OutlineInputBorder(
        borderRadius: DoodhRadii.smRadius,
        borderSide: BorderSide(color: DoodhColors.line),
      ),
      enabledBorder: const OutlineInputBorder(
        borderRadius: DoodhRadii.smRadius,
        borderSide: BorderSide(color: DoodhColors.line),
      ),
      focusedBorder: const OutlineInputBorder(
        borderRadius: DoodhRadii.smRadius,
        borderSide: BorderSide(color: DoodhColors.teal, width: 2),
      ),
      contentPadding: const EdgeInsets.symmetric(
        horizontal: DoodhSpacing.md,
        vertical: 14,
      ),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: const Size(0, 48),
        padding: const EdgeInsets.symmetric(
          horizontal: 20,
          vertical: DoodhSpacing.md - 2,
        ),
        // Reference language: primary CTAs are pill-shaped while keeping the
        // accessible 48px height.
        shape: const StadiumBorder(),
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        minimumSize: const Size(0, 48),
        // Secondary actions mirror the primary pill silhouette.
        shape: const StadiumBorder(),
      ),
    ),
    navigationBarTheme: NavigationBarThemeData(
      backgroundColor: Colors.white,
      // Reference language: the selected destination carries a warm gold pill
      // rather than the default Material mint indicator.
      indicatorColor: DoodhColors.tanSurface,
      height: 68,
      iconTheme: WidgetStateProperty.resolveWith((states) {
        final selected = states.contains(WidgetState.selected);
        return IconThemeData(
          size: 24,
          color: selected ? DoodhColors.goldAccent : DoodhColors.muted,
        );
      }),
      labelTextStyle: WidgetStateProperty.resolveWith((states) {
        final selected = states.contains(WidgetState.selected);
        // "Subscription" (12 chars) wrapped to two lines on phones at 12px
        // because a destination is only ~1/5 of the screen width and the
        // label is a plain wrapping Text. 10.5px keeps every destination on
        // a single line down to small phone widths.
        return TextStyle(
          fontSize: 10.5,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.2,
          color: selected ? DoodhColors.goldAccent : DoodhColors.muted,
        );
      }),
    ),
    dividerTheme: const DividerThemeData(color: DoodhColors.line, space: 1),
  );
}
