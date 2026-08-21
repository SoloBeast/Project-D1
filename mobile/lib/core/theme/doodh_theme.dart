import 'package:flutter/material.dart';

abstract final class DoodhColors {
  static const ink = Color(0xFF172321);
  static const muted = Color(0xFF667572);
  static const teal = Color(0xFF087F8C);
  static const tealDark = Color(0xFF075E67);
  static const mint = Color(0xFFE5F3EF);
  static const cream = Color(0xFFFFFBF5);
  static const amber = Color(0xFFF4B942);
  static const coral = Color(0xFFD8664D);
  static const line = Color(0xFFDDE7E3);
}

abstract final class DoodhSpacing {
  static const xs = 4.0;
  static const sm = 8.0;
  static const md = 16.0;
  static const lg = 24.0;
  static const xl = 32.0;
}

abstract final class DoodhRadii {
  static const sm = BorderRadius.all(Radius.circular(8));
  static const md = BorderRadius.all(Radius.circular(14));
  static const lg = BorderRadius.all(Radius.circular(20));
}

ThemeData buildDoodhTheme() {
  final scheme = ColorScheme.fromSeed(
    seedColor: DoodhColors.teal,
    brightness: Brightness.light,
  ).copyWith(
    primary: DoodhColors.teal,
    onPrimary: Colors.white,
    secondary: DoodhColors.amber,
    surface: Colors.white,
    onSurface: DoodhColors.ink,
    error: DoodhColors.coral,
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
      displaySmall: TextStyle(color: DoodhColors.ink, fontWeight: FontWeight.w800),
      headlineSmall: TextStyle(color: DoodhColors.ink, fontWeight: FontWeight.w800),
      titleLarge: TextStyle(color: DoodhColors.ink, fontWeight: FontWeight.w700),
      titleMedium: TextStyle(color: DoodhColors.ink, fontWeight: FontWeight.w700),
      bodyLarge: TextStyle(color: DoodhColors.ink, height: 1.4),
      bodyMedium: TextStyle(color: DoodhColors.muted, height: 1.4),
      labelLarge: TextStyle(fontWeight: FontWeight.w700),
    ),
    cardTheme: const CardThemeData(
      color: Colors.white,
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: DoodhRadii.md,
        side: BorderSide(color: DoodhColors.line),
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: Colors.white,
      border: OutlineInputBorder(
        borderRadius: DoodhRadii.sm,
        borderSide: const BorderSide(color: DoodhColors.line),
      ),
      enabledBorder: OutlineInputBorder(
        borderRadius: DoodhRadii.sm,
        borderSide: const BorderSide(color: DoodhColors.line),
      ),
      focusedBorder: const OutlineInputBorder(
        borderRadius: DoodhRadii.sm,
        borderSide: BorderSide(color: DoodhColors.teal, width: 2),
      ),
      contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: const Size(0, 48),
        padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
        shape: const RoundedRectangleBorder(borderRadius: DoodhRadii.sm),
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        minimumSize: const Size(0, 48),
        shape: const RoundedRectangleBorder(borderRadius: DoodhRadii.sm),
      ),
    ),
    navigationBarTheme: NavigationBarThemeData(
      backgroundColor: Colors.white,
      indicatorColor: DoodhColors.mint,
      labelTextStyle: WidgetStateProperty.all(
        const TextStyle(fontSize: 12, fontWeight: FontWeight.w700),
      ),
    ),
    dividerTheme: const DividerThemeData(color: DoodhColors.line, space: 1),
  );
}
