import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'session_state.dart';

final sessionControllerProvider =
    NotifierProvider<SessionController, SessionState>(SessionController.new);

class SessionController extends Notifier<SessionState> {
  @override
  SessionState build() => const SessionState.unauthenticated();

  void useFoundationSession(UserRole role) {
    state = SessionState.authenticated(
      role: role,
      publicUserId: 'phase-0-foundation-user',
    );
  }

  void signOut() {
    state = const SessionState.unauthenticated();
  }
}
