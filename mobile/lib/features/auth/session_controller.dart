import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'auth_repository.dart';
import 'session_state.dart';

final authRepositoryProvider = Provider<AuthRepository>((ref) => AuthRepository());

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
    final session = await _repository.restore();
    state = session == null
        ? const SessionState.unauthenticated()
        : SessionState.authenticated(session);
  }

  Future<bool> login(String login, String password) => _run(
        () => _repository.login(login, password),
      );

  Future<bool> register({
    required String displayName,
    required String? email,
    required String? mobile,
    required String password,
  }) =>
      _run(
        () => _repository.register(
          displayName: displayName,
          email: email,
          mobile: mobile,
          password: password,
        ),
      );

  Future<void> sendOtp(String mobile, {required bool registration}) =>
      _repository.sendOtp(mobile, registration: registration);

  Future<bool> verifyOtp(
    String mobile,
    String code, {
    required bool registration,
  }) =>
      _run(
        () => _repository.verifyOtp(
          mobile,
          code,
          registration: registration,
        ),
      );

  Future<void> refresh() async {
    final current = state.session;
    if (current == null) return;

    try {
      state = SessionState.authenticated(await _repository.refresh(current));
    } on ApiException catch (error) {
      if (error.statusCode == 401) {
        await expireSession();
        return;
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
        errorMessage: 'Unable to reach DoodhDirect. Check your connection and try again.',
      );
      return false;
    }
  }
}
