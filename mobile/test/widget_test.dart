import 'package:doodh_direct_mobile/app/app.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('unauthenticated session has no role', () {
    const session = SessionState.unauthenticated();

    expect(session.isAuthenticated, isFalse);
    expect(session.role, isNull);
  });

  test('role labels are stable for navigation', () {
    expect(UserRole.customer.label, 'Customer');
    expect(UserRole.delivery.label, 'Delivery');
    expect(UserRole.admin.label, 'Admin');
  });

  testWidgets('foundation session routes to role workspace and signs out',
      (tester) async {
    await tester.pumpWidget(const ProviderScope(child: DoodhDirectApp()));
    await tester.pumpAndSettle();

    expect(find.text('DoodhDirect'), findsOneWidget);
    expect(find.text('Open foundation session'), findsOneWidget);

    await tester.tap(find.text('Open foundation session'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Delivery'));
    await tester.pumpAndSettle();

    expect(find.text('Delivery workspace'), findsOneWidget);
    expect(find.text('Delivery workspace ready'), findsOneWidget);

    await tester.tap(find.byTooltip('Sign out'));
    await tester.pumpAndSettle();

    expect(find.text('Open foundation session'), findsOneWidget);
  });
}
