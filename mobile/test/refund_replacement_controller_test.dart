import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement/refund_replacement_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('refund/replacement controller', () {
    test('loads the server eligibility answer', () async {
      final repository = _FakeRefundReplacementRepository();
      final container = await authenticatedContainer(repository);
      addTearDown(container.dispose);

      await container
          .read(refundReplacementControllerProvider.notifier)
          .loadEligibility('delivery-1');

      final state = container.read(refundReplacementControllerProvider);
      expect(state.eligibility?.isEligible, isTrue);
      expect(state.eligibility?.windowHours, 48);
      expect(state.isLoading, isFalse);
      expect(repository.lastToken, 'refund-token');
    });

    test('submits a request and appends the uploaded proof image', () async {
      final repository = _FakeRefundReplacementRepository();
      final container = await authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(refundReplacementControllerProvider.notifier);

      expect(
        await controller.submitRequest(
          'delivery-1',
          type: RefundReplacementType.replacement,
          reason: 'Milk was sour',
          remarks: 'Delivered at 6am',
          milkTestId: 'test-1',
        ),
        isTrue,
      );
      expect(repository.submittedType, RefundReplacementType.replacement);
      expect(repository.submittedReason, 'Milk was sour');
      expect(repository.submittedMilkTestId, 'test-1');
      expect(
        container.read(refundReplacementControllerProvider).selectedCustomerRequest?.requestId,
        'req-1',
      );

      expect(
        await controller.uploadImage(
          'req-1',
          bytes: Uint8List.fromList(const [0xff, 0xd8, 0xff]),
          fileName: 'proof.jpg',
          contentType: 'image/jpeg',
        ),
        isTrue,
      );
      expect(repository.uploadCount, 1);
      expect(repository.lastUploadRequestId, 'req-1');
      final state = container.read(refundReplacementControllerProvider);
      expect(state.selectedCustomerRequest?.images, hasLength(1));
      expect(state.isSaving, isFalse);
    });

    test('loads the customer list and a single request', () async {
      final repository = _FakeRefundReplacementRepository();
      final container = await authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(refundReplacementControllerProvider.notifier);

      await controller.loadCustomerRequests();
      expect(
        container.read(refundReplacementControllerProvider).customerRequests,
        hasLength(1),
      );

      await controller.loadCustomerRequest('req-1');
      expect(
        container.read(refundReplacementControllerProvider).selectedCustomerRequest?.requestId,
        'req-1',
      );
    });

    test('loads the branch-scoped staff page and advances the lifecycle', () async {
      final repository = _FakeRefundReplacementRepository();
      final container = await authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(refundReplacementControllerProvider.notifier);

      await controller.loadStaffPage(
        branchId: 7,
        status: RefundReplacementStatus.pending,
        page: 1,
      );
      final page = container.read(refundReplacementControllerProvider).staffPage;
      expect(page?.items, hasLength(1));
      expect(repository.staffPageCalls, 1);

      await controller.loadStaffRequest('req-1');
      expect(
        container.read(refundReplacementControllerProvider).selectedStaffRequest?.requestId,
        'req-1',
      );

      expect(await controller.approve('req-1'), isTrue);
      expect(repository.decidedAction, 'approve');
      expect(
        container.read(refundReplacementControllerProvider).selectedStaffRequest?.status,
        RefundReplacementStatus.approved,
      );

      expect(await controller.reject('req-1'), isTrue);
      expect(repository.decidedAction, 'reject');

      expect(await controller.complete('req-1'), isTrue);
      expect(repository.decidedAction, 'complete');
      expect(
        container.read(refundReplacementControllerProvider).selectedStaffRequest?.status,
        RefundReplacementStatus.completed,
      );
    });

    test('maps API, network, processing, and unauthenticated failures', () async {
      final forbidden = await authenticatedContainer(
        _FailingRefundReplacementRepository(
          ApiException(403, 'FORBIDDEN', 'Refund request access denied.'),
        ),
      );
      addTearDown(forbidden.dispose);
      await forbidden
          .read(refundReplacementControllerProvider.notifier)
          .loadEligibility('delivery-1');
      expect(
        forbidden.read(refundReplacementControllerProvider).isUnauthorized,
        isTrue,
      );
      expect(
        forbidden.read(refundReplacementControllerProvider).errorMessage,
        'Refund request access denied.',
      );

      final offline = await authenticatedContainer(
        _FailingRefundReplacementRepository(
          const ApiNetworkException('socket closed'),
        ),
      );
      addTearDown(offline.dispose);
      await offline
          .read(refundReplacementControllerProvider.notifier)
          .loadEligibility('delivery-1');
      expect(offline.read(refundReplacementControllerProvider).isOffline, isTrue);
      expect(
        offline.read(refundReplacementControllerProvider).errorMessage,
        contains('Check your connection'),
      );

      final processing = await authenticatedContainer(
        _FailingRefundReplacementRepository(const FormatException('bad date')),
      );
      addTearDown(processing.dispose);
      await processing
          .read(refundReplacementControllerProvider.notifier)
          .loadEligibility('delivery-1');
      expect(
        processing.read(refundReplacementControllerProvider).isOffline,
        isFalse,
      );
      expect(
        processing.read(refundReplacementControllerProvider).errorMessage,
        contains('could not be processed'),
      );

      final guest = await unauthenticatedContainer(
        _FakeRefundReplacementRepository(),
      );
      addTearDown(guest.dispose);
      await guest
          .read(refundReplacementControllerProvider.notifier)
          .loadEligibility('delivery-1');
      expect(
        guest.read(refundReplacementControllerProvider).eligibility,
        isNull,
      );
      expect(guest.read(refundReplacementControllerProvider).isLoading, isFalse);
    });
  });
}

Future<ProviderContainer> authenticatedContainer(
  RefundReplacementRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      refundReplacementRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

Future<ProviderContainer> unauthenticatedContainer(
  RefundReplacementRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_GuestRepository()),
      refundReplacementRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}

class _GuestRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;
}

class _FakeRefundReplacementRepository extends RefundReplacementRepository {
  _FakeRefundReplacementRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;
  int uploadCount = 0;
  String? lastUploadRequestId;
  int staffPageCalls = 0;
  RefundReplacementType? submittedType;
  String? submittedReason;
  String? submittedMilkTestId;
  String? decidedAction;
  String? decidedRequestId;

  @override
  Future<RefundReplacementEligibility> getEligibility(
    String token,
    String deliveryId,
  ) async {
    lastToken = token;
    return eligibility();
  }

  @override
  Future<RefundReplacementRequest> submit(
    String token,
    String deliveryId, {
    required RefundReplacementType type,
    required String reason,
    String? remarks,
    String? milkTestId,
  }) async {
    lastToken = token;
    submittedType = type;
    submittedReason = reason;
    submittedMilkTestId = milkTestId;
    return request();
  }

  @override
  Future<List<RefundReplacementRequest>> listForCustomer(String token) async {
    lastToken = token;
    return [request()];
  }

  @override
  Future<RefundReplacementRequest?> getForCustomer(
    String token,
    String requestId,
  ) async {
    lastToken = token;
    return request();
  }

  @override
  Future<RefundReplacementImage> uploadImage(
    String token,
    String requestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async {
    lastToken = token;
    uploadCount++;
    lastUploadRequestId = requestId;
    return image();
  }

  @override
  Future<RefundReplacementPage> listForStaff(
    String token, {
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) async {
    lastToken = token;
    staffPageCalls++;
    return pageOf();
  }

  @override
  Future<RefundReplacementRequest> getForStaff(
    String token,
    String requestId,
  ) async {
    lastToken = token;
    return request();
  }

  @override
  Future<RefundReplacementRequest> approve(
    String token,
    String requestId, {
    String? remarks,
  }) async {
    lastToken = token;
    decidedAction = 'approve';
    decidedRequestId = requestId;
    return request(status: RefundReplacementStatus.approved);
  }

  @override
  Future<RefundReplacementRequest> reject(
    String token,
    String requestId, {
    String? remarks,
  }) async {
    lastToken = token;
    decidedAction = 'reject';
    decidedRequestId = requestId;
    return request(status: RefundReplacementStatus.rejected);
  }

  @override
  Future<RefundReplacementRequest> complete(
    String token,
    String requestId, {
    String? remarks,
  }) async {
    lastToken = token;
    decidedAction = 'complete';
    decidedRequestId = requestId;
    return request(status: RefundReplacementStatus.completed);
  }
}

class _FailingRefundReplacementRepository extends RefundReplacementRepository {
  _FailingRefundReplacementRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<RefundReplacementEligibility> getEligibility(
    String token,
    String deliveryId,
  ) async => throw failure;

  @override
  Future<RefundReplacementPage> listForStaff(
    String token, {
    int? branchId,
    RefundReplacementStatus? status,
    int page = 1,
    int pageSize = 20,
  }) async => throw failure;
}

RefundReplacementEligibility eligibility() => RefundReplacementEligibility(
  isEligible: true,
  ineligibleReason: null,
  windowHours: 48,
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  hasActiveRequest: false,
  proofImageRequired: true,
  isMilkTestRejectedFlow: false,
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
  decisionRemarks: includeDecision ? 'Approved after review' : null,
  completedByUserId: null,
  completedByName: null,
  completedAtUtc: null,
  completionRemarks: null,
  proofImageRequired: true,
  images: images,
);

RefundReplacementListItem listItem() => RefundReplacementListItem(
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
  status: RefundReplacementStatus.pending,
  reason: 'Milk was sour',
  remarks: null,
  submittedAtUtc: DateTime.utc(2026, 8, 17, 9),
  deadlineUtc: DateTime.utc(2026, 8, 19, 9),
  proofImageRequired: true,
  imageCount: 0,
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
