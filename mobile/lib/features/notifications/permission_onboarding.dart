import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:geolocator/geolocator.dart';

import '../auth/session_controller.dart';
import '../auth/session_state.dart';
import 'notification_controller.dart';
import 'push_notification_gateway.dart';

/// Secure-storage flag recording that the notification rationale has been
/// presented on this installation. Device-level (not per user): the OS
/// permission itself is device-level, so one presentation per install is
/// enough and re-login never re-prompts.
class PermissionOnboardingStore {
  PermissionOnboardingStore({FlutterSecureStorage? storage})
    : _storage = storage ?? const FlutterSecureStorage();

  static const rationaleShownKey =
      'permissions.notification-rationale-shown.v1';

  final FlutterSecureStorage _storage;

  Future<bool> hasShownRationale() async {
    try {
      return await _storage.read(key: rationaleShownKey) == '1';
    } on Object {
      // Fail closed: when we cannot remember, do not nag on every launch.
      return true;
    }
  }

  Future<void> setRationaleShown() async {
    try {
      await _storage.write(key: rationaleShownKey, value: '1');
    } on Object {
      // Best-effort only; the in-launch guard still prevents repeats.
    }
  }
}

final permissionOnboardingStoreProvider =
    Provider<PermissionOnboardingStore>(
      (ref) => PermissionOnboardingStore(),
    );

/// Opens the OS app-settings page. Never throws: callers always present a
/// manual alternative alongside, so a missing settings surface must not
/// break the flow. Uses the existing Geolocator integration — no new
/// permission package is introduced for this.
Future<void> openAppSettings() async {
  try {
    await Geolocator.openAppSettings();
  } on Object {
    // No settings surface available; the caller explains manual options.
  }
}

/// Presents the notification rationale sheet. Returns true when the customer
/// taps "Enable notifications", false otherwise (including swipe dismiss).
Future<bool> showNotificationPermissionRationale(BuildContext context) async {
  final result = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (sheetContext) => _PermissionSheet(
      icon: Icons.notifications_active_outlined,
      title: 'Stay updated on your milk',
      body:
          'Enable notifications to track your order status, payments, '
          'subscriptions and delivery updates as they happen.',
      bullets: const [
        'Order placed, paid and delivered updates',
        'Subscription pause, skip and vacation confirmations',
        'Delivery arrival and OTP alerts',
      ],
      primaryLabel: 'Enable notifications',
      secondaryLabel: 'Not now',
    ),
  );
  return result ?? false;
}

/// Follow-up shown when the OS reports denial after a request: the system
/// dialog cannot be shown again, so the only route is app settings.
Future<void> showNotificationSettingsGuidance(BuildContext context) async {
  await showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (sheetContext) => _PermissionSheet(
      icon: Icons.notifications_off_outlined,
      title: 'Notifications are turned off',
      body:
          'System settings are blocking notifications for DoodhDirect. '
          'You can turn them back on any time — your inbox here keeps '
          'working either way.',
      primaryLabel: 'Open settings',
      secondaryLabel: 'Stay in the app',
      onPrimary: () async {
        await openAppSettings();
        return false;
      },
    ),
  );
}

/// Explainer shown before the OS location prompt, only from the
/// address/map-picker flow. Never shown on launch or sign-in.
Future<bool> showLocationRationale(BuildContext context) async {
  final result = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (sheetContext) => const _PermissionSheet(
      icon: Icons.my_location_outlined,
      title: 'Use your current location?',
      body:
          'Your location is used only to help select your delivery '
          'address. You can always pick a point on the map instead.',
      primaryLabel: 'Use my location',
      secondaryLabel: 'Choose manually',
    ),
  );
  return result ?? false;
}

/// Guidance for permanently-denied location: settings route plus the manual
/// fallback that always remains available.
Future<void> showLocationSettingsGuidance(BuildContext context) async {
  await showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (sheetContext) => _PermissionSheet(
      icon: Icons.location_off_outlined,
      title: 'Location is turned off',
      body:
          'System settings are blocking location for DoodhDirect. Open '
          'settings to enable it, or continue by picking your address '
          'on the map.',
      primaryLabel: 'Open settings',
      secondaryLabel: 'Continue manually',
      onPrimary: () async {
        await openAppSettings();
        return false;
      },
    ),
  );
}

class _PermissionSheet extends StatelessWidget {
  const _PermissionSheet({
    required this.icon,
    required this.title,
    required this.body,
    this.bullets = const [],
    required this.primaryLabel,
    required this.secondaryLabel,
    this.onPrimary,
  });

  final IconData icon;
  final String title;
  final String body;
  final List<String> bullets;
  final String primaryLabel;
  final String secondaryLabel;

  /// Overrides the primary action. Return value becomes the sheet result.
  final Future<bool> Function()? onPrimary;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final textTheme = Theme.of(context).textTheme;
    return SafeArea(
      child: SingleChildScrollView(
        padding: EdgeInsets.only(
          left: 24,
          right: 24,
          top: 20,
          bottom: MediaQuery.of(context).viewInsets.bottom + 28,
        ),
        child: Semantics(
          container: true,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Container(
                width: 52,
                height: 52,
                decoration: BoxDecoration(
                  color: colors.primaryContainer,
                  borderRadius: BorderRadius.circular(26),
                ),
                child: Icon(icon, color: colors.onPrimaryContainer, size: 28),
              ),
              const SizedBox(height: 16),
              Semantics(
                header: true,
                child: Text(title, style: textTheme.titleLarge),
              ),
              const SizedBox(height: 8),
              Text(body, style: textTheme.bodyMedium),
              if (bullets.isNotEmpty) ...[
                const SizedBox(height: 12),
                for (final bullet in bullets)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 6),
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Padding(
                          padding: const EdgeInsets.only(top: 2),
                          child: Icon(
                            Icons.check_circle_outline,
                            size: 18,
                            color: colors.primary,
                            semanticLabel: null,
                          ),
                        ),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(bullet, style: textTheme.bodyMedium),
                        ),
                      ],
                    ),
                  ),
              ],
              const SizedBox(height: 20),
              FilledButton(
                autofocus: true,
                onPressed: () async {
                  final result = onPrimary != null
                      ? await onPrimary!()
                      : true;
                  if (context.mounted) Navigator.of(context).pop(result);
                },
                child: Text(primaryLabel),
              ),
              const SizedBox(height: 4),
              TextButton(
                onPressed: () {
                  if (context.mounted) Navigator.of(context).pop(false);
                },
                child: Text(secondaryLabel),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Invisible host that presents the notification rationale exactly once per
/// installation, on the first authenticated home experience where the OS
/// permission is still undecided. Mounted on home screens only, so prompts
/// never appear over sign-in, OTP, onboarding or catalogue flows, and never
/// stack: a per-launch guard plus the persisted flag survive rebuilds,
/// navigation and hot restart.
class PermissionOnboardingHost extends ConsumerStatefulWidget {
  const PermissionOnboardingHost({super.key});

  @override
  ConsumerState<PermissionOnboardingHost> createState() =>
      _PermissionOnboardingHostState();
}

class _PermissionOnboardingHostState
    extends ConsumerState<PermissionOnboardingHost> {
  static bool _presentedThisLaunch = false;
  static bool _checkInFlight = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _maybePresent());
  }

  Future<void> _maybePresent() async {
    // Synchronous entry guard: concurrent triggers (session + notification
    // state updates from the same sign-in) collapse into one check, so the
    // sheet can never stack with itself.
    if (_presentedThisLaunch || _checkInFlight || !mounted) return;
    _checkInFlight = true;
    try {
      await _maybePresentGuarded();
    } finally {
      _checkInFlight = false;
    }
  }

  Future<void> _maybePresentGuarded() async {
    final controller = ref.read(notificationControllerProvider.notifier);
    await controller.refreshPermissionStatus();
    if (!mounted) return;
    if (!await controller.shouldShowPermissionRationale()) return;
    if (!mounted) return;
    _presentedThisLaunch = true;
    final enable = await showNotificationPermissionRationale(context);
    await controller.markPermissionRationaleShown();
    if (!mounted) return;
    if (enable) {
      final status = await controller.requestPushPermission();
      if (!mounted) return;
      if (status == PushPermissionStatus.denied) {
        await showNotificationSettingsGuidance(context);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    // Session restoration is asynchronous and listener ordering is not
    // guaranteed, so the host re-checks on every relevant change: session
    // arrival, and any notification-state change (which is where the fresh
    // OS permission status lands). All paths funnel through the guarded
    // single-flight check above.
    ref.listen<SessionState>(sessionControllerProvider, (_, next) {
      if (next.isAuthenticated) _maybePresent();
    });
    ref.listen<NotificationState>(
      notificationControllerProvider,
      (_, _) => _maybePresent(),
    );
    return const SizedBox.shrink();
  }
}
