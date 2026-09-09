import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/orders/guest_cart_storage.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'auth_repository.dart';
import 'session_state.dart';

final authRepositoryProvider = Provider<AuthRepository>(
  (ref) => AuthRepository(),
);

final sessionControllerProvider =
    NotifierProvider<SessionController, SessionState>(SessionController.new);

class SessionController extends Notifier<SessionState> {
  AuthRepository get _repository => ref.read(authRepositoryProvider);

  @override
  SessionState build() {
    Future.microtask(_restore);
    return const SessionState.loading();
  }

  Future<void> _restore() async {
    // A guest state is never persisted as a session, so restoration can only
    // ever yield a real authenticated session or no session at all. When no
    // session exists (fresh browser, logged-out device, expired/invalid
    // session) the device falls back to browsing as a guest instead of being
    // forced to the login screen. Guests who navigated away to log in have the
    // guest cart restored independently by the order controller.
    final session = await _repository.restore();
    state = session == null
        ? const SessionState.guest()
        : SessionState.authenticated(session);
  }

  /// Enters the browsing-only guest mode. No session is created or persisted and
  /// no backend request is made — the guest cart lives client-side under its own
  /// storage key and is preserved across guest navigation and app restarts.
  /// If a real authenticated session already exists, the current session is kept.
  void enterAsGuest() {
    if (state.isAuthenticated) return;
    state = const SessionState.guest();
  }

  /// Exits guest mode. If a real session exists it is kept; otherwise the state
  /// is moved back to unauthenticated (e.g. after an expired or failed sign-in).
  /// The guest cart is intentionally NOT cleared here — it survives until a
  /// successful authoritative payment or an explicit logout.
  void exitGuest() {
    if (state.isAuthenticated) return;
    state = const SessionState.unauthenticated();
  }

  Future<bool> login(String login, String password) =>
      _run(() => _repository.login(login, password));

  Future<bool> register({
    required String displayName,
    required String? email,
    required String? mobile,
    required String password,
  }) => _run(
    () => _repository.register(
      displayName: displayName,
      email: email,
      mobile: mobile,
      password: password,
    ),
  );

  Future<String> sendOtp(String mobile) => _repository.sendOtp(mobile);

  /// Requests a fresh OTP for a previously sent challenge, replacing the current
  /// reqId. Used by the "Send a new code" action on the OTP screen.
  Future<String> retryOtp(String mobile) => _repository.retryOtp(mobile);

  /// Establishes a session produced outside the standard login/register flows — e.g. the session
  /// returned by the employee invitation completion endpoint — so the employee is immediately
  /// routed to their assigned role workspace. Persists the session for subsequent launches.
  Future<void> establishSession(AuthSession session) async {
    await _repository.saveSession(session);
    state = SessionState.authenticated(session);
  }

  /// Verifies the mobile OTP and applies the server-decided outcome:
  /// - Existing user (any role): the returned session is persisted and the
  ///   state moves to authenticated.
  /// - Verified mobile with no account yet: the state stays unauthenticated and
  ///   the caller receives the `requiresOnboarding` result carrying the verified
  ///   mobile + reqId to drive the customer-onboarding (create password) step.
  Future<OtpVerificationResult> verifyOtp(
    String mobile,
    String code,
    String reqId,
  ) async {
    state = const SessionState.loading();
    try {
      final result = await _repository.verifyOtp(mobile, code, reqId);
      if (!result.requiresOnboarding && result.session != null) {
        state = SessionState.authenticated(result.session!);
      } else {
        state = const SessionState.unauthenticated();
      }
      return result;
    } on ApiException catch (error) {
      state = SessionState.unauthenticated(errorMessage: error.message);
      rethrow;
    } on Object {
      state = const SessionState.unauthenticated(
        errorMessage:
            'Unable to reach DoodhDirect. Check your connection and try again.',
      );
      rethrow;
    }
  }

  /// Completes customer onboarding for a verified mobile that had no account.
  /// The backend creates the Customer, sets the password, and returns a fresh
  /// session, which moves the state to authenticated.
  Future<AuthSession> completeOtpRegistration({
    required String mobile,
    required String reqId,
    required String newPassword,
  }) async {
    state = const SessionState.loading();
    try {
      final session = await _repository.completeOtpRegistration(
        mobile: mobile,
        reqId: reqId,
        newPassword: newPassword,
      );
      state = SessionState.authenticated(session);
      return session;
    } on ApiException catch (error) {
      state = SessionState.unauthenticated(errorMessage: error.message);
      rethrow;
    } on Object {
      state = const SessionState.unauthenticated(
        errorMessage:
            'Unable to reach DoodhDirect. Check your connection and try again.',
      );
      rethrow;
    }
  }

  Future<void> refresh() async {
    await refreshAccessToken();
  }

  /// Sets a password on an account that currently has none. The backend returns
  /// a refreshed session (persisted by the repository) reflecting the new
  /// `hasPassword` capability, which replaces the current one.
  Future<AuthSession> setPassword(String newPassword) async {
    final current = state.session;
    if (current == null) {
      throw StateError('Cannot set a password without an active session.');
    }
    final updated = await _repository.setPassword(current, newPassword);
    state = SessionState.authenticated(updated);
    return updated;
  }

  /// Changes the password after verifying the current one. The session stays
  /// active; only errors (e.g. wrong current password) surface to the caller.
  Future<void> changePassword({
    required String currentPassword,
    required String newPassword,
  }) async {
    final current = state.session;
    if (current == null) {
      throw StateError('Cannot change the password without an active session.');
    }
    await _repository.changePassword(
      current,
      currentPassword: currentPassword,
      newPassword: newPassword,
    );
  }

  /// Requests a password-reset OTP for the account's canonical mobile number.
  Future<String> forgotPassword(String mobile) => _repository.forgotPassword(mobile);

  /// Verifies a password-reset OTP without creating a session.
  Future<void> verifyResetOtp({
    required String destination,
    required String code,
    required String reqId,
  }) => _repository.verifyResetOtp(
    destination: destination,
    code: code,
    reqId: reqId,
  );

  /// Resets the password after the server has verified the OTP. The backend
  /// creates a fresh session, so the user lands authenticated.
  Future<AuthSession> resetPassword({
    required String mobile,
    required String reqId,
    required String newPassword,
  }) async {
    final updated = await _repository.resetPassword(
      mobile: mobile,
      reqId: reqId,
      newPassword: newPassword,
    );
    state = SessionState.authenticated(updated);
    return updated;
  }

  /// Stages a new (or first) email and returns the reqId for
  /// [verifyEmailChange]. The session is unchanged until verification.
  Future<EmailChangeRequested> requestEmailChange(String newEmail) async {
    final current = state.session;
    if (current == null) {
      throw StateError('Cannot change the email without an active session.');
    }
    return _repository.requestEmailChange(current, newEmail);
  }

  /// Verifies the email OTP, committing the staged email. The returned session
  /// (persisted by the repository) replaces the current one.
  Future<AuthSession> verifyEmailChange(String code, String reqId) async {
    final current = state.session;
    if (current == null) {
      throw StateError('Cannot verify the email without an active session.');
    }
    final updated = await _repository.verifyEmailChange(current, code, reqId);
    state = SessionState.authenticated(updated);
    return updated;
  }

  Future<MobileChangeRequested> requestMobileChange(String newMobile) async {
    final current = state.session;
    if (current == null) {
      throw StateError('Cannot change the mobile without an active session.');
    }
    return _repository.requestMobileChange(current, newMobile);
  }

  Future<AuthSession> verifyMobileChange(
    String mobile,
    String code,
    String reqId,
  ) async {
    final current = state.session;
    if (current == null) {
      throw StateError('Cannot verify the mobile without an active session.');
    }
    final updated = await _repository.verifyMobileChange(current, mobile, code, reqId);
    state = SessionState.authenticated(updated);
    return updated;
  }

  Future<String?> refreshAccessToken() {
    final current = state.session;
    if (current == null) return Future.value(null);
    final inFlight = _refreshInFlight;
    if (inFlight != null) return inFlight;

    final operation = _refreshAccessToken(current);
    _refreshInFlight = operation;
    return operation.whenComplete(() => _refreshInFlight = null);
  }

  Future<String?>? _refreshInFlight;

  Future<String?> _refreshAccessToken(AuthSession current) async {
    try {
      final refreshed = await _repository.refresh(current);
      state = SessionState.authenticated(refreshed);
      return refreshed.accessToken;
    } on ApiException catch (error) {
      if (error.statusCode == 401) {
        await expireSession();
        return null;
      }
      rethrow;
    }
  }

  Future<void> signOut() async {
    final current = state.session;
    state = const SessionState.unauthenticated();
    if (current == null) return;

    try {
      await _repository.logout(current);
    } on Object {
      await _repository.clear();
    }

    // Per existing logout semantics the device-local cart is cleared so a
    // previous customer's items never leak into a later guest or account.
    await ref.read(guestCartStorageProvider).clear();
  }

  Future<void> expireSession() async {
    await _repository.clear();
    state = const SessionState.unauthenticated(
      errorMessage: 'Your session expired. Sign in again.',
    );
  }

  void clearError() {
    if (!state.isAuthenticated && state.errorMessage != null) {
      state = const SessionState.unauthenticated();
    }
  }

  Future<bool> _run(Future<AuthSession> Function() operation) async {
    state = const SessionState.loading();
    try {
      state = SessionState.authenticated(await operation());
      return true;
    } on ApiException catch (error) {
      state = SessionState.unauthenticated(errorMessage: error.message);
      return false;
    } on Object {
      state = const SessionState.unauthenticated(
        errorMessage:
            'Unable to reach DoodhDirect. Check your connection and try again.',
      );
      return false;
    }
  }
}
