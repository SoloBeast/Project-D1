import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/staff_vacation_refund_screens.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/vacation_refund_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/vacation_refund_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/vacation_refund_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('staff vacation-refund queue', () {
    testWidgets('lists branch requests with their review state', (
      tester,
    ) async {
      await _pumpList(
        tester,
        state: VacationRefundStaffState(
          staffRequests: [
            _request(status: 'Pending', number: 'VR/000001'),
            _request(status: 'Completed', number: 'VR/000002'),
          ],
        ),
      );

      expect(find.text('Vacation refunds'), findsOneWidget);
      expect(find.text('VR/000001'), findsOneWidget);
      expect(find.text('VR/000002'), findsOneWidget);
      expect(find.text('Pending'), findsOneWidget);
      expect(find.text('Completed'), findsOneWidget);
    });

    testWidgets('pending detail offers approve and reject only', (
      tester,
    ) async {
      await _pumpDetail(
        tester,
        state: VacationRefundStaffState(
          selectedStaffRequest: _request(status: 'Pending'),
        ),
      );

      expect(find.text('VR/000001'), findsOneWidget);
      expect(
        find.text(
          'Refund requested — under support review. No refund issued yet.',
        ),
        findsOneWidget,
      );
      expect(find.widgetWithText(OutlinedButton, 'Reject'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Approve'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Complete'), findsNothing);
    });

    testWidgets('approved detail offers completion with the settlement notice',
        (tester) async {
      await _pumpDetail(
        tester,
        state: VacationRefundStaffState(
          selectedStaffRequest: _request(status: 'Approved'),
        ),
      );

      expect(find.text('Approved'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Complete'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Approve'), findsNothing);
      expect(find.widgetWithText(OutlinedButton, 'Reject'), findsNothing);
      expect(
        find.textContaining('settled only on completion'),
        findsOneWidget,
      );
    });

    testWidgets('terminal detail offers no actions', (tester) async {
      await _pumpDetail(
        tester,
        state: VacationRefundStaffState(
          selectedStaffRequest: _request(status: 'Completed'),
        ),
      );

      expect(find.text('Completed'), findsOneWidget);
      expect(find.textContaining('credited to your wallet'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Approve'), findsNothing);
      expect(find.widgetWithText(OutlinedButton, 'Reject'), findsNothing);
      expect(find.widgetWithText(FilledButton, 'Complete'), findsNothing);
    });

    testWidgets('reject forwards the dialog remarks to the backend',
        (tester) async {
      final controller = _RecordingVacationRefundStaffController(
        VacationRefundStaffState(
          selectedStaffRequest: _request(status: 'Pending'),
        ),
      );
      await _pumpDetail(tester, controller: controller);

      await tester.tap(find.widgetWithText(OutlinedButton, 'Reject'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.byType(TextField),
        ),
        'Dates verified against the subscription',
      );
      await tester.tap(find.widgetWithText(FilledButton, 'Reject'));
      await tester.pumpAndSettle();

      expect(controller.decisions, ['reject:req-1']);
      expect(
        controller.remarks,
        ['Dates verified against the subscription'],
      );
    });

    testWidgets('employee-originated request hides actions for non-admins',
        (tester) async {
      await _pumpDetail(
        tester,
        state: VacationRefundStaffState(
          selectedStaffRequest: _request(
            status: 'Pending',
            isEmployeeOriginated: true,
          ),
        ),
      );

      expect(find.widgetWithText(FilledButton, 'Approve'), findsNothing);
      expect(find.widgetWithText(OutlinedButton, 'Reject'), findsNothing);
      expect(
        find.textContaining('can only be decided by an Owner'),
        findsOneWidget,
      );
    });

    testWidgets('read-only branch role sees the queue without actions',
        (tester) async {
      await _pump(
        tester,
        const StaffVacationRefundDetailScreen(requestId: 'req-1'),
        _RecordingVacationRefundStaffController(
          VacationRefundStaffState(
            selectedStaffRequest: _request(status: 'Pending'),
          ),
        ),
        session: _readOnlySession,
      );

      expect(find.text('VR/000001'), findsOneWidget);
      expect(find.widgetWithText(FilledButton, 'Approve'), findsNothing);
      expect(find.widgetWithText(OutlinedButton, 'Reject'), findsNothing);
    });
  });
}

VacationRefundRequest _request({
  required String status,
  String number = 'VR/000001',
  bool isEmployeeOriginated = false,
}) => VacationRefundRequest(
  requestId: 'req-1',
  requestNumber: number,
  subscriptionPublicId: 'sub-1',
  customerId: 55,
  customerName: 'Asha Rao',
  customerMobile: '9876543210',
  branchId: 7,
  branchCode: 'BR-07',
  branchName: 'Bengaluru Dairy',
  reason: 'Customer vacation',
  status: VacationRefundStatus.parse(status),
  occurrenceCount: 5,
  grossPerOccurrence: 100.0,
  totalAmount: 500.0,
  deductionPercentage: 0.0,
  payableAmountSnapshot: 700.0,
  totalEntitlementSnapshot: 7,
  currency: 'INR',
  submittedAt: DateTime(2026, 10, 28, 9, 30),
  occurrences: const [],
  isEmployeeOriginated: isEmployeeOriginated,
);

final _managerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Dairy Manager',
    email: null,
    mobile: '9999999999',
    roles: ['DAIRY_MANAGER'],
    permissions: [
      'REFUND_REPLACEMENT.READ_BRANCH',
      'REFUND_REPLACEMENT.MANAGE_BRANCH',
    ],
    branchIds: [7],
  ),
  accessToken: 'manager-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _readOnlySession = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-2',
    displayName: 'Delivery Manager',
    email: null,
    mobile: '9999999998',
    roles: ['DELIVERY_MANAGER'],
    permissions: ['REFUND_REPLACEMENT.READ_BRANCH'],
    branchIds: [7],
  ),
  accessToken: 'reader-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

Future<void> _pumpList(
  WidgetTester tester, {
  required VacationRefundStaffState state,
}) => _pump(
  tester,
  const StaffVacationRefundListScreen(),
  _RecordingVacationRefundStaffController(state),
);

Future<void> _pumpDetail(
  WidgetTester tester, {
  VacationRefundStaffState? state,
  VacationRefundStaffController? controller,
}) => _pump(
  tester,
  const StaffVacationRefundDetailScreen(requestId: 'req-1'),
  controller ?? _RecordingVacationRefundStaffController(state!),
);

Future<void> _pump(
  WidgetTester tester,
  Widget screen,
  VacationRefundStaffController controller, {
  AuthSession? session,
}) {
  return tester.pumpWidget(
    ProviderScope(
      overrides: [
        vacationRefundStaffControllerProvider.overrideWith(
          () => controller,
        ),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: session ?? _managerSession),
        ),
        vacationRefundRepositoryProvider.overrideWithValue(
          VacationRefundRepository(
            api: ApiClient(
              client: MockClient(
                (_) async => http.Response('Not Found', 404),
              ),
              baseUrl: 'https://api.example.test',
            ),
          ),
        ),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({this.session});

  final AuthSession? session;

  @override
  SessionState build() => SessionState.authenticated(session!);
}

/// Seeded controller that records decisions instead of hitting the network,
/// so widget tests exercise the real screen wiring end to end.
class _RecordingVacationRefundStaffController
    extends VacationRefundStaffController {
  _RecordingVacationRefundStaffController(VacationRefundStaffState seed)
    : _seed = seed;

  final VacationRefundStaffState _seed;
  final decisions = <String>[];
  final remarks = <String?>[];

  @override
  VacationRefundStaffState build() => _seed;

  @override
  Future<bool> approve(String requestId, {String? remarks}) async {
    decisions.add('approve:$requestId');
    this.remarks.add(remarks);
    return true;
  }

  @override
  Future<bool> reject(String requestId, {String? remarks}) async {
    decisions.add('reject:$requestId');
    this.remarks.add(remarks);
    return true;
  }

  @override
  Future<bool> complete(String requestId, {String? remarks}) async {
    decisions.add('complete:$requestId');
    this.remarks.add(remarks);
    return true;
  }
}
