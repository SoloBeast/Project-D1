import 'package:doodh_direct_mobile/core/network/media_url.dart';
import 'package:doodh_direct_mobile/features/branding/branding_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// The DoodhDirect brand mark used in app headers.
///
/// Business ask: the logo sits BESIDE the "DoodhDirect" name — never as a
/// standalone image. Renders the business-uploaded logo when branding is
/// configured (splash has already fetched the bytes); otherwise falls back to
/// the bundled drop icon + wordmark. Prefers the startup controller's cached
/// bytes; when that preload missed (cache/timeout) but the server HAS a
/// configured logo, it loads the logo directly — headers must not downgrade
/// to the generic icon while branding exists.
class DoodhBrandMark extends ConsumerWidget {
  const DoodhBrandMark({
    super.key,
    this.height = 28,
    this.showWordmark = true,
  });

  /// Renders the logo at this height (fits the width proportionally).
  final double height;

  /// Whether the "DoodhDirect" wordmark is shown next to the mark. Surfaces
  /// that draw their own name text (login, home header) pass `false` and pair
  /// this widget with their styled name beside it.
  final bool showWordmark;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final branding = ref.watch(brandingControllerProvider);
    final theme = Theme.of(context);

    if (branding.logoBytes != null) {
      final logo = Image.memory(
        branding.logoBytes!,
        height: height,
        fit: BoxFit.contain,
        gaplessPlayback: true,
        errorBuilder: (_, _, _) =>
            _fallback(theme, color: theme.colorScheme.primary),
      );
      return _compose(logo, theme);
    }

    // Preload missed (cache miss / startup timeout) but the server HAS a
    // configured logo: load it directly from the API URL. The startup
    // controller's failure must not permanently downgrade every header to
    // the generic drop icon when branding exists.
    final logoAsset = branding.configuration?.logo;
    final resolvedUrl = resolveMediaUrl(logoAsset?.url);
    if (resolvedUrl != null) {
      final logo = Image.network(
        resolvedUrl,
        height: height,
        fit: BoxFit.contain,
        errorBuilder: (_, _, _) =>
            _fallback(theme, color: theme.colorScheme.primary),
      );
      return _compose(logo, theme);
    }

    return _fallback(theme, color: theme.colorScheme.primary);
  }

  /// Pairs the logo with the wordmark when this surface draws only the mark;
  /// surfaces that draw their own styled name take the bare logo.
  Widget _compose(Widget logo, ThemeData theme) {
    if (!showWordmark) return logo;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        logo,
        const SizedBox(width: 8),
        Flexible(
          child: Text(
            'DoodhDirect',
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.titleLarge,
          ),
        ),
      ],
    );
  }

  Widget _fallback(ThemeData theme, {required Color color}) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Icon(Icons.water_drop_rounded, color: color, size: height),
      if (showWordmark) ...[
        const SizedBox(width: 8),
        Flexible(
          child: Text(
            'DoodhDirect',
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.titleLarge,
          ),
        ),
      ],
    ],
  );
}
