import 'dart:async';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/features/branding/branding_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// How long the startup animation is allowed to play before the app is
/// revealed. The gate is always tap-skippable, so this is a ceiling — the
/// configured GIF should be cut to fit comfortably inside it.
const Duration brandingAnimationHoldDuration = Duration(seconds: 3);

/// Latches per process once the startup gate has been dismissed, so the
/// animation hold only ever happens on app start — never again on navigation
/// or hot reloads.
class BrandingGateCompleted extends Notifier<bool> {
  @override
  bool build() => false;

  void complete() => state = true;
}

final brandingGateCompletedProvider =
    NotifierProvider<BrandingGateCompleted, bool>(
      BrandingGateCompleted.new,
    );

/// Startup gate rendered above the router content via `MaterialApp.builder`.
///
/// Why an overlay instead of holding the `/restore` route: session restore is
/// local-first and finishes in milliseconds, so the router leaves the splash
/// almost instantly — a GIF shown only there would blink for a single frame.
/// This gate instead *reveals* whatever screen the router has landed on after
/// the configured animation has had its moment:
///
///  * no animation configured → child is returned untouched (zero overhead,
///    identical behavior to before branding existed);
///  * animation configured → the child is shown underneath an opaque
///    presentation playing the GIF for [brandingAnimationHoldDuration]; a tap
///    skips immediately and interaction with the underlying screen is blocked
///    until dismissal;
///  * routing, session restoration, token refresh, and deep links are never
///    touched — the router finishes its work behind the overlay.
class BrandingGate extends ConsumerStatefulWidget {
  const BrandingGate({super.key, required this.child});

  final Widget child;

  @override
  ConsumerState<BrandingGate> createState() => _BrandingGateState();
}

class _BrandingGateState extends ConsumerState<BrandingGate> {
  Timer? _holdTimer;

  @override
  void dispose() {
    _holdTimer?.cancel();
    super.dispose();
  }

  void _dismiss() {
    _holdTimer?.cancel();
    if (!ref.read(brandingGateCompletedProvider)) {
      ref.read(brandingGateCompletedProvider.notifier).complete();
    }
  }

  @override
  Widget build(BuildContext context) {
    final completed = ref.watch(brandingGateCompletedProvider);
    final branding = ref.watch(brandingControllerProvider);
    final animation = branding.animationBytes;

    // Nothing to hold: pass straight through.
    if (completed || animation == null) {
      return widget.child;
    }

    // Start (or keep) the hold timer for this process-lifetime gate.
    _holdTimer ??= Timer(brandingAnimationHoldDuration, _dismiss);

    return Stack(
      children: [
        // The destination screen finishes building/loading underneath; block
        // interaction until the reveal so a tap meant for the animation is
        // never double-delivered to a button behind it.
        IgnorePointer(child: widget.child),
        Positioned.fill(
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTap: _dismiss,
            child: _StartupAnimationView(animationBytes: animation),
          ),
        ),
      ],
    );
  }
}

/// Minimal full-screen presentation for the gate: just the animation and a
/// subtle skip affordance — no loading chrome (the app is already ready
/// underneath).
class _StartupAnimationView extends StatelessWidget {
  const _StartupAnimationView({required this.animationBytes});

  final Uint8List animationBytes;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
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
                child: Image.memory(
                  animationBytes,
                  fit: BoxFit.contain,
                  gaplessPlayback: true,
                  // A malformed asset must never trap the app behind the
                  // gate; the error builder reveals the app immediately via
                  // the latched state on the next build.
                  errorBuilder: (_, _, _) => const _GateErrorFallback(),
                ),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.only(bottom: 28),
            child: Text(
              'Tap to continue',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Shown if the animation bytes fail to decode: an empty frame whose build
/// schedules the gate release so the user is never stuck.
class _GateErrorFallback extends ConsumerWidget {
  const _GateErrorFallback();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      ref.read(brandingGateCompletedProvider.notifier).complete();
    });
    return const SizedBox.shrink();
  }
}
