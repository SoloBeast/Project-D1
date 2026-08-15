export 'auth_repository.dart' show UserRole, UserRoleLabel;

import 'auth_repository.dart';

class SessionState {
  const SessionState.loading()
      : status = SessionStatus.loading,
        session = null,
        errorMessage = null;

  const SessionState.unauthenticated({this.errorMessage})
      : status = SessionStatus.unauthenticated,
        session = null;

  const SessionState.authenticated(this.session)
      : status = SessionStatus.authenticated,
        errorMessage = null;

  final SessionStatus status;
  final AuthSession? session;
  final String? errorMessage;

  bool get isLoading => status == SessionStatus.loading;
  bool get isAuthenticated => session != null && status == SessionStatus.authenticated;
  UserRole? get role => session?.user.primaryRole;
  String? get publicUserId => session?.user.publicUserId;
}

enum SessionStatus { loading, unauthenticated, authenticated }
