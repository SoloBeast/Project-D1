import 'dart:async';

import 'package:doodh_direct_mobile/features/branding/branding_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Total time the /restore splash may hold the app before auto-advancing,
/// even if the branding request is still in flight.
const Duration brandingSplashMaxDuration = Duration(seconds: 6);

/// How long the startup animation is allowed to play before the app advances.
/// The gate that shows it is always tap-skippable, so this is a ceiling —
/// the configured GIF should be cut to fit comfortably inside it.
const Duration brandingAnimationHoldDuration = Duration(seconds: 3);

/// The shared startup presentation: active animation → static logo → bundled
/// wordmark, with optional loading chrome. Used by both the `/restore` splash
/// and the [BrandingGate] overlay.
class BrandingPresentation extends ConsumerWidget {
  const BrandingPresentation({super.key, this.showLoadingChrome = true});

  /// Whether the progress indicator, "Loading…" caption, and Skip button are
  /// shown (true on the splash; the post-restore gate shows a minimal
  /// presentation with only a skip affordance).
  final bool showLoadingChrome;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final branding = ref.watch(brandingControllerProvider);
    final theme = Theme.of(context);

    final Widget visual;
    if (branding.animationBytes != null) {
      // Animated GIF (and animated WebP) decode natively via Image.
      visual = Image.memory(
        branding.animationBytes!,
        fit: BoxFit.contain,
        gaplessPlayback: true,
        errorBuilder: (_, _, _) => _StaticLogo(branding: branding),
      );
    } else {
      visual = _StaticLogo(branding: branding);
    }

    return Container(
      width: double.infinity,
      color: theme.colorScheme.surface,
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Expanded(
            child: Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 420),
                child: visual,
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.only(bottom: 32),
            child: Column(
              children: [
                if (showLoadingChrome) ...[
                  SizedBox(
                    width: 22,
                    height: 22,
                    child: CircularProgressIndicator(strokeWidth: 2.4),
                  ),
                  const SizedBox(height: 16),
                  Text(
                    'Loading DoodhDirect…',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                  ),
                ],
                TextButton(
                  onPressed: () =>
                      ref.read(brandingControllerProvider.notifier).refresh(),
                  child: const Text('Skip'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  /// Skip affordance: ask the router to leave the restore screen. The
  /// redirect is pure — re-evaluating it immediately resolves to Login, the
  /// storefront, or home.
  /// Skip affordance: force the branding refresh to complete immediately;
  /// the router redirect takes over as soon as the session state resolves.
}

/// The branded startup screen shown while the stored session is restored
/// (route `/restore`).
///
/// It renders the active logo/startup animation when available (remote or
/// last-known-good cache) and falls back to the bundled static presentation
/// otherwise. It never decides routing: the GoRouter redirect owns that. A
/// tap skips, and a hard timer bounds the wait so the app always reaches
/// Login or Guest Home.
///
/// NOTE: session restore is local and fast; once routing advances away the
/// [BrandingGate] keeps the animation visible for its configured hold, so a
/// short GIF still plays out even though this screen unmounts quickly.
class BrandingSplashScreen extends ConsumerStatefulWidget {
  const BrandingSplashScreen({super.key});

  @override
  ConsumerState<BrandingSplashScreen> createState() =>
      _BrandingSplashScreenState();
}

class _BrandingSplashScreenState extends ConsumerState<BrandingSplashScreen> {
  Timer? _maxDurationTimer;

  @override
  void initState() {
    super.initState();
    // Bound the total splash time regardless of branding/network state.
    _maxDurationTimer = Timer(brandingSplashMaxDuration, () {
      if (mounted) {
        ref.read(brandingControllerProvider.notifier).refresh();
      }
    });
  }

  @override
  void dispose() {
    _maxDurationTimer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return const BrandingPresentation();
  }
}

class _StaticLogo extends StatelessWidget {
  const _StaticLogo({required this.branding});

  final BrandingState branding;

  @override
  Widget build(BuildContext context) {
    if (branding.logoBytes != null) {
      return Image.memory(
        branding.logoBytes!,
        fit: BoxFit.contain,
        gaplessPlayback: true,
        errorBuilder: (_, _, _) => const _Wordmark(),
      );
    }
    return const _Wordmark();
  }
}

/// Bundled static fallback: the branded wordmark. Always available — no
/// network, no cache, no configuration required.
class _Wordmark extends StatelessWidget {
  const _Wordmark();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(
          Icons.water_drop_rounded,
          size: 72,
          color: theme.colorScheme.primary,
        ),
        const SizedBox(height: 12),
        Text(
          'DoodhDirect',
          style: theme.textTheme.headlineMedium?.copyWith(
            fontWeight: FontWeight.w800,
            color: theme.colorScheme.primary,
          ),
        ),
      ],
    );
  }
}
