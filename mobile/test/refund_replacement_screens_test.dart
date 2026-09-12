import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_repository.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('customer refund/replacement screens', () {
    testWidgets(
      'renders the customer request screen across eligibility states',
      (tester) async {
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
          _SeededRefundReplacementController(
            const RefundReplacementState(isLoading: true),
          ),
          settle: false,
        );
        expect(find.byType(CircularProgressIndicator), findsOneWidget);

        final ineligibleController = _SeededRefundReplacementController(
          RefundReplacementState(
            eligibility: eligibility(
              isEligible: false,
              ineligibleReason: 'The request window has closed.',
            ),
          ),
        );
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
          ineligibleController,
        );
        expect(find.text('Request not available'), findsOneWidget);
        expect(find.text('The request window has closed.'), findsOneWidget);

        await tester.tap(find.text('Refresh'));
        await tester.pumpAndSettle();
        expect(ineligibleController.loadedEligibilityFor, 'delivery-1');

        final eligibleController = _SeededRefundReplacementController(
          RefundReplacementState(eligibility: eligibility()),
        );
        await _pump(
          tester,
          const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
          eligibleController,
        );
        expect(find.text('Submit request'), findsOneWidget);
        expect(find.text('Attach proof image'), findsOneWidget);
        expect(eligibleController.loadedEligibilityFor, 'delivery-1');
      },
    );

    testWidgets('validates the customer request form before submitting', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(eligibility: eligibility()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(deliveryId: 'delivery-1'),
        controller,
      );

      await tester.tap(find.text('Submit request'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(find.text('Enter a reason for your request.'), findsOneWidget);
      expect(controller.submittedReason, isNull);

      // Let the first snackbar expire so the next validation message is
      // actually visible instead of queued behind it. The snackbar's 4s
      // duration timer only starts once its entrance animation completes, so
      // advance past both the entrance and the duration before settling.
      await tester.pump(const Duration(seconds: 5));
      await tester.pump(const Duration(seconds: 5));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField).first, 'Milk was sour');
      await tester.pump();
      await tester.tap(find.text('Submit request'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(
        find.text('Attach a proof image before submitting.'),
        findsOneWidget,
      );
      expect(controller.submittedReason, isNull);
    });

    testWidgets('skips the proof requirement for the milk-test rejection flow', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementController(
        RefundReplacementState(eligibility: eligibility()),
      );
      await _pump(
        tester,
        const CustomerRefundReplacementScreen(
          deliveryId: 'delivery-1',
          milkTestId: 'test-1',
        ),
        controller,
      );

      expect(
        find.text(
          'You rejected the doorstep milk test. You can raise a refund or replacement request for this delivery right away.',
        ),
        findsOneWidget,
      );
      expect(
        find.text('No proof image is required for this request.'),
        findsOneWidget,
      );
      expect(find.text('Attach proof image'), findsNothing);
      expect(controller.loadedEligibilityFor, 'delivery-1');
    });

    testWidgets('renders the customer request list and detail', (
      tester,
    ) async {
      await _pump(
        tester,
        const CustomerRefundReplacementListScreen(),
        _SeededRefundReplacementController(const RefundReplacementState()),
      );
      expect(find.text('No requests yet'), findsOneWidget);
      expect(
        find.text(
          'Refund or replacement requests you raise for your deliveries will appear here.',
        ),
        findsOneWidget,
      );

      await _pump(
        tester,
        const CustomerRefundReplacementListScreen(),
        _SeededRefundReplacementController(
          RefundReplacementState(customerRequests: [request()]),
        ),
      );
      expect(find.text('RR-2026-0001'), findsOneWidget);
      expect(find.text('Refund · Requested after delivery'), findsOneWidget);
      expect(find.text('Order ORD-1001'), findsOneWidget);

      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(selectedCustomerRequest: request()),
        ),
      );
      expect(find.text('Request details'), findsOneWidget);
      expect(find.text('RR-2026-0001'), findsOneWidget);
      expect(find.text('Decision'), findsOneWidget);
      expect(
        find.text('Your request is awaiting a decision from the support team.'),
        findsOneWidget,
      );
    });

    testWidgets('loads protected proof images and falls back to a lock icon', (
      tester,
    ) async {
      final seeded = request(images: [image()]);
      final repository = _FakeRefundReplacementRepository();

      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(selectedCustomerRequest: seeded),
        ),
        repository: repository,
      );

      expect(repository.contentRequests, hasLength(1));
      expect(repository.contentRequests.single.token, 'refund-token');
      expect(repository.contentRequests.single.requestId, 'req-1');
      expect(repository.contentRequests.single.imageId, 'image-1');
      expect(find.text('Proof images'), findsOneWidget);
      expect(find.byType(Image), findsOneWidget);

      final failingRepository = _FakeRefundReplacementRepository()
        ..imageContent = (_, _, _) =>
            throw const ApiException(403, 'FORBIDDEN', 'no access');

      await _pump(
        tester,
        const CustomerRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(selectedCustomerRequest: seeded),
        ),
        repository: failingRepository,
      );
      expect(failingRepository.contentRequests, hasLength(1));
      expect(find.byIcon(Icons.lock_outline), findsOneWidget);
    });
  });

  group('staff refund/replacement screens', () {
    testWidgets('drives the staff list filter and decision lifecycle', (
      tester,
    ) async {
      final listController = _SeededRefundReplacementController(
        RefundReplacementState(staffPage: pageOf()),
      );
      await _pump(
        tester,
        const StaffRefundReplacementListScreen(branchId: 7),
        listController,
        session: _managerSession,
      );

      expect(find.text('Refund / replacement requests'), findsOneWidget);
      expect(find.text('RR-2026-0001'), findsOneWidget);
      expect(find.text('Order ORD-1001 · Asha Rao'), findsOneWidget);
      expect(find.text('Proof image missing'), findsOneWidget);

      await tester.tap(find.widgetWithText(ChoiceChip, 'Pending'));
      await tester.pumpAndSettle();
      expect(listController.staffBranchId, 7);
      expect(listController.staffStatusFilter, RefundReplacementStatus.pending);

      final detailController = _SeededRefundReplacementController(
        RefundReplacementState(
          selectedStaffRequest: request(
            status: RefundReplacementStatus.pending,
          ),
        ),
      );
      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        detailController,
        session: _managerSession,
      );

      expect(find.text('Request details'), findsOneWidget);
      expect(
        find.text('A proof image is required for this request.'),
        findsOneWidget,
      );
      expect(find.widgetWithText(FilledButton, 'Approve'), findsOneWidget);
      expect(find.text('Reject'), findsOneWidget);

      await tester.tap(find.widgetWithText(FilledButton, 'Approve'));
      await tester.pumpAndSettle();
      expect(find.text('Approve request'), findsOneWidget);

      await tester.tap(
        find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(FilledButton, 'Approve'),
        ),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(detailController.decidedAction, 'approve');
      expect(find.text('Request approved.'), findsOneWidget);

      // Let the snackbar expire before pumping the next screen so no timers
      // leak across the test's widget tree replacements.
      await tester.pump(const Duration(seconds: 5));
      await tester.pumpAndSettle();

      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(
            selectedStaffRequest: request(
              status: RefundReplacementStatus.approved,
              includeDecision: true,
            ),
          ),
        ),
        session: _managerSession,
      );
      expect(find.text('Mark completed'), findsOneWidget);

      await _pump(
        tester,
        const StaffRefundReplacementDetailScreen(requestId: 'req-1'),
        _SeededRefundReplacementController(
          RefundReplacementState(
            selectedStaffRequest: request(
              status: RefundReplacementStatus.rejected,
              includeDecision: true,
            ),
          ),
        ),
        session: _managerSession,
      );
      expect(
        find.text('This request is final and no further action is available.'),
        findsOneWidget,
      );
    });
  });
}

Future<_FakeRefundReplacementRepository> _pump(
  WidgetTester tester,
  Widget screen,
  _SeededRefundReplacementController controller, {
  _FakeRefundReplacementRepository? repository,
  AuthSession? session,
  bool settle = true,
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  final fakeRepository = repository ?? _FakeRefundReplacementRepository();
  await tester.pumpWidget(
    ProviderScope(
      // A fresh key forces a new ProviderContainer per pump so that re-seeding
      // a different state between pumps is actually applied.
      key: UniqueKey(),
      overrides: [
        refundReplacementControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(session: session),
        ),
        refundReplacementRepositoryProvider.overrideWithValue(fakeRepository),
      ],
      child: MaterialApp(theme: ThemeData(useMaterial3: true), home: screen),
    ),
  );
  await tester.pump();
  if (settle) {
    await tester.pumpAndSettle();
  }
  return fakeRepository;
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({this.session});

  final AuthSession? session;

  @override
  SessionState build() => SessionState.authenticated(session ?? _session);
}

/// Seeds the controller with a fixed state and records the load/decision calls
/// the screens make, so widget tests never hit the network.
class _SeededRefundReplacementController extends RefundReplacementController {
  _SeededRefundReplacementController(this.seed);

  final RefundReplacementState seed;

  String? loadedEligibilityFor;
  bool loadedCustomerList = false;
  String? loadedCustomerRequestId;
  int? staffBranchId;
  RefundReplacementStatus? staffStatusFilter;
  String? loadedStaffRequestId;
  RefundReplacementType? submittedType;
  String? submittedReason;
  String? decidedAction;

  @override
  RefundReplacementState build() => seed;

  @override
  Future<void> loadEligibility(String deliveryId) async {
    loadedEligibilityFor = deliveryId;
  }

  @override
  Future<bool> submitRequest(
    String deliveryId, {
    required RefundReplacementType type,
    required String reason,
    String? remarks,
    String? milkTestId,
  }) async {
    submittedType = type;
    submittedReason = reason;
    return true;
  }

  @override
  Future<void> loadCustomerRequests() async {
    loadedCustomerList = true;
  }

  @override
  Future<void> loadCustomerRequest(String requestId) async {
    loadedCustomerRequestId = requestId;
  }

  @override
  Future<void> loadStaffPage({
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) async {
    staffBranchId = branchId;
    staffStatusFilter = status;
  }

  @override
  Future<void> loadStaffRequest(String requestId) async {
    loadedStaffRequestId = requestId;
  }

  @override
  Future<bool> approve(String requestId, {String? remarks}) async {
    decidedAction = 'approve';
    return true;
  }

  @override
  Future<bool> reject(String requestId, {String? remarks}) async {
    decidedAction = 'reject';
    return true;
  }

  @override
  Future<bool> complete(String requestId, {String? remarks}) async {
    decidedAction = 'complete';
    return true;
  }
}

class _FakeRefundReplacementRepository extends RefundReplacementRepository {
  _FakeRefundReplacementRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  ApiByteResponse Function(String token, String requestId, String imageId)?
  imageContent;
  final List<_ImageContentRequest> contentRequests = [];

  @override
  Future<ApiByteResponse> getImageContent(
    String token,
    String requestId,
    String imageId,
  ) async {
    contentRequests.add(
      _ImageContentRequest(
        token: token,
        requestId: requestId,
        imageId: imageId,
      ),
    );
    final handler = imageContent;
    if (handler == null) {
      return ApiByteResponse(
        bytes: Uint8List.fromList(const [
          0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
          0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
          0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
          0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        ]),
        contentType: 'image/png',
        fileName: 'proof.png',
      );
    }
    return handler(token, requestId, imageId);
  }
}

class _ImageContentRequest {
  const _ImageContentRequest({
    required this.token,
    required this.requestId,
    required this.imageId,
  });

  final String token;
  final String requestId;
  final String imageId;
}

RefundReplacementEligibility eligibility({
  bool isEligible = true,
  String? ineligibleReason,
  bool proofImageRequired = true,
  bool hasActiveRequest = false,
  bool isMilkTestRejectedFlow = false,
}) => RefundReplacementEligibility(
  isEligible: isEligible,
  ineligibleReason: ineligibleReason,
  windowHours: 48,
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  hasActiveRequest: hasActiveRequest,
  proofImageRequired: proofImageRequired,
  isMilkTestRejectedFlow: isMilkTestRejectedFlow,
);

RefundReplacementImage image() => RefundReplacementImage(
  imageId: 'image-1',
  fileName: 'proof.jpg',
  contentType: 'image/jpeg',
  fileSize: 2048,
  uploadedAtUtc: DateTime.utc(2026, 8, 17, 9, 5),
);

RefundReplacementRequest request({
  RefundReplacementStatus status = RefundReplacementStatus.pending,
  RefundReplacementType type = RefundReplacementType.refund,
  RefundReplacementSource source = RefundReplacementSource.customerPostDelivery,
  bool includeDecision = false,
  List<RefundReplacementImage> images = const [],
}) => RefundReplacementRequest(
  requestId: 'req-1',
  requestNumber: 'RR-2026-0001',
  orderId: 101,
  orderPublicId: 'order-pub-1',
  orderNumber: 'ORD-1001',
  deliveryId: 'delivery-1',
  deliveryNumber: 'DEL-2001',
  customerId: 55,
  customerName: 'Asha Rao',
  customerMobile: '9876543210',
  branchId: 7,
  branchCode: 'BR-07',
  branchName: 'Bengaluru Dairy',
  milkTestId: null,
  milkTestPublicId: null,
  type: type,
  source: source,
  status: status,
  reason: 'Milk was sour',
  remarks: null,
  submittedAtUtc: DateTime.utc(2026, 8, 17, 9),
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  decidedByUserId: includeDecision ? 9 : null,
  decidedByName: includeDecision ? 'Support Lead' : null,
  decidedAtUtc: includeDecision ? DateTime.utc(2026, 8, 17, 11) : null,
  decisionRemarks: includeDecision ? 'Reviewed by support' : null,
  completedByUserId: null,
  completedByName: null,
  completedAtUtc: null,
  completionRemarks: null,
  proofImageRequired: true,
  images: images,
);

RefundReplacementListItem listItem({
  RefundReplacementStatus status = RefundReplacementStatus.pending,
  bool proofImageRequired = true,
  int imageCount = 0,
}) => RefundReplacementListItem(
  requestId: 'req-1',
  requestNumber: 'RR-2026-0001',
  orderId: 101,
  orderPublicId: 'order-pub-1',
  orderNumber: 'ORD-1001',
  deliveryId: 'delivery-1',
  deliveryNumber: 'DEL-2001',
  customerId: 55,
  customerName: 'Asha Rao',
  customerMobile: '9876543210',
  branchId: 7,
  branchCode: 'BR-07',
  branchName: 'Bengaluru Dairy',
  milkTestId: null,
  milkTestPublicId: null,
  type: RefundReplacementType.refund,
  source: RefundReplacementSource.customerPostDelivery,
  status: status,
  reason: 'Milk was sour',
  remarks: null,
  submittedAtUtc: DateTime.utc(2026, 8, 17, 9),
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  proofImageRequired: proofImageRequired,
  imageCount: imageCount,
);

RefundReplacementPage pageOf() => RefundReplacementPage(
  items: [listItem()],
  page: 1,
  pageSize: 20,
  totalCount: 1,
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'cust-1',
    displayName: 'Asha Rao',
    email: null,
    mobile: '9876543210',
    roles: ['CUSTOMER'],
    permissions: ['REFUND_REPLACEMENT.READ'],
    branchIds: [7],
  ),
  accessToken: 'refund-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);

final _managerSession = AuthSession(
  user: const AuthUser(
    publicUserId: 'staff-1',
    displayName: 'Support Lead',
    email: null,
    mobile: '9999999999',
    roles: ['CUSTOMER_SUPPORT'],
    permissions: [
      'REFUND_REPLACEMENT.READ',
      'REFUND_REPLACEMENT.MANAGE_BRANCH',
    ],
    branchIds: [7],
  ),
  accessToken: 'manager-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
