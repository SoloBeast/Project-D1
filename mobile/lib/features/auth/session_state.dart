export 'auth_repository.dart' show UserRole, UserRoleLabel;

import 'auth_repository.dart';

class SessionState {
  const SessionState.loading()
    : status = SessionStatus.loading,
      session = null,
      errorMessage = null;

  /// A browsing-only state with no authenticated identity. The guest may use the
  /// public storefront (home, catalogue, product details, cart, checkout review)
  /// but every account-dependent action requires sign in. Guests never hold a
  /// session and never trigger token refresh or protected API calls.
  const SessionState.guest()
    : status = SessionStatus.guest,
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
  bool get isGuest => status == SessionStatus.guest && session == null;
  bool get isAuthenticated =>
      session != null && status == SessionStatus.authenticated;
  UserRole? get role => session?.user.primaryRole;
  String? get publicUserId => session?.user.publicUserId;
}

enum SessionStatus { loading, guest, unauthenticated, authenticated }
