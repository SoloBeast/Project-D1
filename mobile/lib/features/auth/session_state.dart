enum UserRole {
  customer,
  delivery,
  dairy,
  owner,
  admin,
}

extension UserRoleLabel on UserRole {
  String get label => switch (this) {
        UserRole.customer => 'Customer',
        UserRole.delivery => 'Delivery',
        UserRole.dairy => 'Dairy',
        UserRole.owner => 'Owner',
        UserRole.admin => 'Admin',
      };
}

class SessionState {
  const SessionState.unauthenticated()
      : isAuthenticated = false,
        role = null,
        publicUserId = null;

  const SessionState.authenticated({required this.role, required this.publicUserId})
      : isAuthenticated = true;

  final bool isAuthenticated;
  final UserRole? role;
  final String? publicUserId;
}
