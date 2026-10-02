import 'dart:async';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/branding/branding_cache.dart';
import 'package:doodh_direct_mobile/features/branding/branding_models.dart';
import 'package:doodh_direct_mobile/features/branding/branding_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

/// How long the splash waits for the branding request before falling back to
/// the cached (or bundled) presentation. Short by design: branding must never
/// delay reaching Login or Guest Home. Exposed as a provider so tests can
/// shrink it.
const Duration brandingFetchTimeout = Duration(seconds: 4);

final brandingFetchTimeoutProvider = Provider<Duration>(
  (ref) => brandingFetchTimeout,
);

final brandingControllerProvider =
    NotifierProvider<BrandingController, BrandingState>(
      BrandingController.new,
    );

enum BrandingSource { remote, cached, fallback }

class BrandingState {
  const BrandingState({
    this.configuration,
    this.logoBytes,
    this.animationBytes,
    this.source = BrandingSource.fallback,
    this.isReady = false,
  });

  /// The active branding metadata, or null when nothing is known yet.
  final BrandingConfiguration? configuration;

  /// Decoded logo bytes when the logo is configured (rendered via
  /// `Image.memory` so no separate media request races the splash).
  final Uint8List? logoBytes;

  /// Decoded startup-animation bytes (GIF) when configured.
  final Uint8List? animationBytes;

  /// Where the current presentation came from — useful for tests and for
  /// choosing whether to revalidate in the background later.
  final BrandingSource source;

  /// True once the controller has made its best effort (remote, cache, or
  /// timeout) so the splash knows the fallback is final.
  final bool isReady;

  BrandingState copyWith({
    BrandingConfiguration? configuration,
    bool clearConfiguration = false,
    Uint8List? logoBytes,
    Uint8List? animationBytes,
    BrandingSource? source,
    bool? isReady,
  }) => BrandingState(
    configuration: clearConfiguration ? null : configuration ?? this.configuration,
    logoBytes: logoBytes ?? this.logoBytes,
    animationBytes: animationBytes ?? this.animationBytes,
    source: source ?? this.source,
    isReady: isReady ?? this.isReady,
  );
}

/// Startup branding controller.
///
/// Contract (mirrors the approved startup state machine):
///  * fetch the public branding with a short timeout;
///  * on success, download the active assets and persist last-known-good;
///  * on timeout/failure, replay the cached configuration + bytes;
///  * with no cache either, stay on the bundled static fallback;
///  * never throw — the splash always becomes [BrandingState.isReady] and the
///    normal session redirect proceeds.
class BrandingController extends Notifier<BrandingState> {
  /// Set when the owning container disposes so async continuations can stop
  /// writing state (Riverpod throws when a disposed notifier is written to).
  bool _disposed = false;

  @override
  BrandingState build() {
    ref.onDispose(() => _disposed = true);
    Future.microtask(_load);
    return const BrandingState();
  }

  BrandingRepository get _repository => ref.read(brandingRepositoryProvider);

  BrandingCache get _cache => ref.read(brandingCacheProvider);

  Duration get _fetchTimeout => ref.read(brandingFetchTimeoutProvider);

  Future<void> _load() async {
    // 1. Paint the cached presentation immediately (if any) so the splash is
    //    never blank while the network attempt runs.
    final cached = await _cache.readConfiguration();
    if (cached != null) {
      _safeSet(BrandingState(
        configuration: cached,
        logoBytes: await _cache.readLogoBytes(),
        animationBytes: await _cache.readAnimationBytes(),
        source: BrandingSource.cached,
      ));
    }

    // 2. Try to refresh. Bounded so a hanging request cannot stall startup.
    try {
      final remote = await _repository
          .getPublic()
          .timeout(_fetchTimeout);

      Uint8List? logoBytes;
      Uint8List? animationBytes;
      if (remote.logo != null) {
        logoBytes = await _fetchAsset(remote.logo!.url);
      }
      if (remote.startupAnimation != null) {
        animationBytes = await _fetchAsset(remote.startupAnimation!.url);
      }

      state = BrandingState(
        configuration: remote,
        logoBytes: logoBytes,
        animationBytes: animationBytes,
        source: BrandingSource.remote,
        isReady: true,
      );

      if (_disposed) return;

      // 3. Persist last-known-good only on a fully successful refresh so a
      //    half-failed download never poisons the cache.
      await _cache.writeConfiguration(remote);
      if (logoBytes != null) {
        await _cache.writeLogoBytes(logoBytes);
      } else {
        await _cache.clearLogoBytes();
      }
      if (animationBytes != null) {
        await _cache.writeAnimationBytes(animationBytes);
      } else {
        await _cache.clearAnimationBytes();
      }
    } on ApiException catch (error) {
      // 404 = branding not configured (expected pre-first-upload state) —
      // treat like any other failure: cached → fallback.
      await _failToCache(cached, error.statusCode == 404);
    } on ApiNetworkException {
      await _failToCache(cached, false);
    } on TimeoutException {
      await _failToCache(cached, false);
    } on Object {
      await _failToCache(cached, false);
    }
  }

  /// Writes state only while the container is alive.
  void _safeSet(BrandingState next) {
    if (_disposed) return;
    state = next;
  }

  Future<void> _failToCache(
    BrandingConfiguration? cached,
    bool wasNotConfigured,
  ) async {
    if (cached == null) {
      // No cache: final fallback. Mark ready so the splash yields to the
      // normal redirect immediately.
      _safeSet(
        const BrandingState(source: BrandingSource.fallback, isReady: true),
      );
      return;
    }

    // Keep whatever cached bytes were painted; if the server reports the
    // asset removed, drop those bytes and fall back to static for that slot.
    if (wasNotConfigured) {
      if (state.logoBytes != null && cached.logo != null) {
        await _cache.clearLogoBytes();
      }
      if (state.animationBytes != null && cached.startupAnimation != null) {
        await _cache.clearAnimationBytes();
      }
    }
    _safeSet(BrandingState(
      configuration: wasNotConfigured ? null : cached,
      logoBytes: wasNotConfigured ? null : state.logoBytes,
      animationBytes: wasNotConfigured ? null : state.animationBytes,
      source: BrandingSource.fallback,
      isReady: true,
    ));
  }

  Future<Uint8List?> _fetchAsset(String relativeUrl) async {
    try {
      final bytes = await _repository
          .getAssetBytes(relativeUrl)
          .timeout(_fetchTimeout);
      return bytes.bytes;
    } on Object {
      // A failed asset download degrades to the static fallback for that
      // slot; it must never fail the whole splash.
      return null;
    }
  }

  /// Forces a revalidation (used by the admin screen after a change).
  Future<void> refresh() => _load();
}
