import 'dart:convert';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_controller.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_models.dart';
import 'package:doodh_direct_mobile/features/customer/customer_controller.dart';
import 'package:doodh_direct_mobile/features/customer/customer_models.dart';
import 'package:doodh_direct_mobile/features/payments/payment_controller.dart';
import 'package:doodh_direct_mobile/features/payments/payment_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  group('subscription models', () {
    test('serialize create and update requests using API values', () {
      final create = CreateSubscriptionRequest(
        productId: 'product-1',
        addressId: 'address-1',
        quantity: 1.125,
        startDate: DateTime(2026, 8, 17, 14, 30),
        deliveryDays: const {DeliveryWeekday.monday, DeliveryWeekday.wednesday},
        totalEntitlement: 30,
        paymentMethod: PaymentMethod.wallet,
      );
      const update = UpdateSubscriptionRequest(
        quantity: 0.75,
        addressId: 'address-2',
        deliveryDays: {DeliveryWeekday.friday},
      );

      expect(create.toJson(), {
        'productId': 'product-1',
        'addressId': 'address-1',
        'quantity': 1.125,
        'startDate': '2026-08-17',
        'deliveryDays': ['Monday', 'Wednesday'],
        'totalEntitlement': 30,
        'paymentMethod': 'Wallet',
      });
      expect(update.toJson(), {
        'quantity': 0.75,
        'addressId': 'address-2',
        'deliveryDays': ['Friday'],
      });
    });

    test('parse subscription, payment, and delivery semantics', () {
      final created = CreatedSubscription.fromJson({
        'subscription': subscriptionJson(),
        'payment': paymentJson(),
      });
      final delivery = SubscriptionDelivery.fromJson(deliveryJson());

      expect(created.subscription.status, SubscriptionStatus.active);
      expect(created.subscription.status.canPause, isTrue);
      expect(created.subscription.scheduleLabel, 'Mon, Wed, Fri');
      expect(
        created.subscription.formattedQuantity,
        '1.125 litre per delivery',
      );
      expect(created.subscription.entitlementProgress, closeTo(0.2, 0.0001));
      expect(created.payment.isSubscriptionPayment, isTrue);
      expect(created.payment.method, PaymentMethod.wallet);
      expect(delivery.status.canSkip, isTrue);
      expect(delivery.quantity, 1.125);
      expect(
        SubscriptionStatus.fromApi('unexpected'),
        SubscriptionStatus.unknown,
      );
      expect(
        SubscriptionDeliveryStatus.fromApi('unexpected'),
        SubscriptionDeliveryStatus.unknown,
      );
    });
  });

  group('subscription repository', () {
    test('create posts finite entitlement and idempotency header', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/v1/subscriptions');
        expect(request.headers['Authorization'], 'Bearer customer-token');
        expect(request.headers['Idempotency-Key'], 'subscription-attempt-1');
        expect(jsonDecode(request.body), {
          'productId': 'product-1',
          'addressId': 'address-1',
          'quantity': 1.125,
          'startDate': '2026-08-17',
          'deliveryDays': ['Monday', 'Wednesday'],
          'totalEntitlement': 30,
          'paymentMethod': 'Wallet',
        });
        return successResponse({
          'subscription': subscriptionJson(),
          'payment': paymentJson(),
        }, statusCode: 201);
      });
      final repository = testRepository(client);

      final result = await repository.create(
        token: 'customer-token',
        request: CreateSubscriptionRequest(
          productId: 'product-1',
          addressId: 'address-1',
          quantity: 1.125,
          startDate: DateTime(2026, 8, 17),
          deliveryDays: const {
            DeliveryWeekday.monday,
            DeliveryWeekday.wednesday,
          },
          totalEntitlement: 30,
          paymentMethod: PaymentMethod.wallet,
        ),
        idempotencyKey: 'subscription-attempt-1',
      );

      expect(result.subscription.publicId, 'subscription-1');
      expect(result.payment.subscriptionId, 'subscription-1');
    });

    test(
      'uses customer detail, lifecycle, update, skip, and calendar routes',
      () async {
        final requests = <String>[];
        final client = MockClient((request) async {
          requests.add('${request.method} ${request.url.path}');
          expect(request.headers['Authorization'], 'Bearer customer-token');
          if (request.method == 'PATCH') {
            expect(jsonDecode(request.body), {
              'quantity': 0.75,
              'addressId': null,
              'deliveryDays': null,
            });
          }
          if (request.url.path.endsWith('/skip')) {
            expect(jsonDecode(request.body), {'deliveryId': 'delivery-1'});
            return successResponse(deliveryJson(status: 'Skipped'));
          }
          if (request.url.path.endsWith('/calendar')) {
            return successResponse([deliveryJson()]);
          }
          if (request.url.path == '/api/v1/subscriptions') {
            return successResponse([subscriptionJson()]);
          }
          return successResponse(subscriptionJson());
        });
        final repository = testRepository(client);

        await repository.getMine('customer-token');
        await repository.get('customer-token', 'subscription-1');
        await repository.update(
          token: 'customer-token',
          subscriptionId: 'subscription-1',
          request: const UpdateSubscriptionRequest(quantity: 0.75),
        );
        await repository.pause('customer-token', 'subscription-1');
        await repository.resume('customer-token', 'subscription-1');
        await repository.cancel('customer-token', 'subscription-1');
        await repository.skip(
          token: 'customer-token',
          subscriptionId: 'subscription-1',
          deliveryId: 'delivery-1',
        );
        await repository.getCalendar('customer-token', 'subscription-1');

        expect(requests, [
          'GET /api/v1/subscriptions',
          'GET /api/v1/subscriptions/subscription-1',
          'PATCH /api/v1/subscriptions/subscription-1',
          'POST /api/v1/subscriptions/subscription-1/pause',
          'POST /api/v1/subscriptions/subscription-1/resume',
          'POST /api/v1/subscriptions/subscription-1/cancel',
          'POST /api/v1/subscriptions/subscription-1/skip',
          'GET /api/v1/subscriptions/subscription-1/calendar',
        ]);
      },
    );
  });

  group('subscription controller', () {
    test('adopts the payment returned by subscription creation', () async {
      final repository = _FakeSubscriptionRepository();
      final container = await authenticatedContainer(repository);
      addTearDown(container.dispose);

      final created = await container
          .read(subscriptionControllerProvider.notifier)
          .create(createRequest);

      expect(created, isNotNull);
      expect(repository.lastToken, 'customer-token');
      expect(repository.lastIdempotencyKey, startsWith('mobile-subscription-'));
      expect(
        container.read(paymentControllerProvider).payment?.publicId,
        'payment-1',
      );
      expect(
        container
            .read(subscriptionControllerProvider)
            .selectedSubscription
            ?.publicId,
        'subscription-1',
      );
    });

    test(
      'preserves API error message without marking the state offline',
      () async {
        final container = await authenticatedContainer(
          _FailingSubscriptionRepository(
            ApiException(
              422,
              'SUBSCRIPTION_RULE',
              'Delivery count is invalid.',
            ),
          ),
        );
        addTearDown(container.dispose);

        await container
            .read(subscriptionControllerProvider.notifier)
            .loadSubscriptions();
        final state = container.read(subscriptionControllerProvider);

        expect(state.isLoading, isFalse);
        expect(state.isOffline, isFalse);
        expect(state.errorMessage, 'Delivery count is invalid.');
      },
    );

    test('maps transport failures to the offline state', () async {
      final container = await authenticatedContainer(
        _FailingSubscriptionRepository(Exception('socket closed')),
      );
      addTearDown(container.dispose);

      await container
          .read(subscriptionControllerProvider.notifier)
          .loadSubscriptions();
      final state = container.read(subscriptionControllerProvider);

      expect(state.isLoading, isFalse);
      expect(state.isOffline, isTrue);
      expect(state.errorMessage, contains('Check your connection'));
    });
  });

  group('subscription screens', () {
    testWidgets(
      'setup shows eligible products, addresses, and finite controls',
      (tester) async {
        await pumpScreen(
          tester,
          const SubscriptionSetupScreen(),
          catalogueState: CatalogueState(products: [_product]),
          customerState: CustomerState(addresses: [_address]),
        );

        expect(find.text('New subscription'), findsOneWidget);
        expect(find.text('Whole Milk'), findsOneWidget);
        expect(find.text('Home - Pune'), findsOneWidget);
        expect(find.text('Total deliveries'), findsOneWidget);
        expect(find.text('Prepaid estimate'), findsOneWidget);
        await tester.scrollUntilVisible(
          find.text('Continue to payment'),
          300,
          scrollable: find.byType(Scrollable).first,
        );
        expect(find.text('Continue to payment'), findsOneWidget);
      },
    );

    testWidgets('list renders entitlement and schedule from server state', (
      tester,
    ) async {
      await pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: SubscriptionState(subscriptions: [_subscription]),
      );

      expect(find.text('Subscriptions'), findsOneWidget);
      expect(find.text('Whole Milk'), findsOneWidget);
      expect(find.textContaining('24 deliveries remaining'), findsOneWidget);
      expect(find.textContaining('Mon, Wed, Fri'), findsOneWidget);
      expect(find.byTooltip('Create subscription'), findsOneWidget);
    });

    testWidgets('detail gates actions from active server status', (
      tester,
    ) async {
      await pumpScreen(
        tester,
        const SubscriptionDetailScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          subscriptions: [_subscription],
          selectedSubscription: _subscription,
        ),
      );

      expect(find.text('6 of 30 deliveries used'), findsOneWidget);
      expect(find.text('24 deliveries remaining'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      await tester.scrollUntilVisible(
        find.text('View delivery calendar'),
        300,
        scrollable: find.byType(Scrollable).first,
      );
      expect(find.text('Update schedule'), findsOneWidget);
      expect(find.text('Pause subscription'), findsOneWidget);
      expect(find.text('Cancel subscription'), findsOneWidget);
      expect(find.text('Resume subscription'), findsNothing);
      expect(find.text('View delivery calendar'), findsOneWidget);
    });

    testWidgets('calendar offers skip only for scheduled deliveries', (
      tester,
    ) async {
      await pumpScreen(
        tester,
        const SubscriptionCalendarScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          calendar: [
            _delivery,
            SubscriptionDelivery.fromJson(deliveryJson(status: 'Delivered')),
          ],
        ),
      );

      expect(find.text('Delivery calendar'), findsOneWidget);
      expect(find.text('17/08/2026'), findsNWidgets(2));
      expect(find.textContaining('Scheduled'), findsOneWidget);
      expect(find.textContaining('Delivered'), findsOneWidget);
      expect(find.byTooltip('Skip delivery'), findsOneWidget);
    });

    testWidgets('list distinguishes API and offline empty states', (
      tester,
    ) async {
      await pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: const SubscriptionState(
          errorMessage: 'Subscription service rejected the request.',
        ),
      );
      expect(
        find.text('Subscription service rejected the request.'),
        findsOneWidget,
      );

      await pumpScreen(
        tester,
        const SubscriptionListScreen(),
        subscriptionState: const SubscriptionState(
          isOffline: true,
          errorMessage: 'Unable to reach DoodhDirect.',
        ),
      );
      expect(find.text('You are offline'), findsOneWidget);
    });
  });
}

final createRequest = CreateSubscriptionRequest(
  productId: 'product-1',
  addressId: 'address-1',
  quantity: 1.125,
  startDate: DateTime(2026, 8, 17),
  deliveryDays: const {DeliveryWeekday.monday},
  totalEntitlement: 30,
  paymentMethod: PaymentMethod.wallet,
);

final _subscription = SubscriptionDetails.fromJson(subscriptionJson());
final _delivery = SubscriptionDelivery.fromJson(deliveryJson());
Map<String, dynamic> subscriptionJson({String status = 'Active'}) => {
  'publicId': 'subscription-1',
  'status': status,
  'productId': 'product-1',
  'productSku': 'MILK-1L',
  'productName': 'Whole Milk',
  'unitOfMeasure': 'litre',
  'quantity': 1.125,
  'unitPrice': 60,
  'payableAmount': 2025,
  'startDate': '2026-08-17',
  'endDate': '2026-10-23',
  'totalEntitlement': 30,
  'usedEntitlement': 6,
  'remainingEntitlement': 24,
  'addressId': 'address-1',
  'address': 'Home, 1 Main Street, Pune 411001',
  'branchId': 'branch-1',
  'branchCode': 'CENTRAL',
  'branchName': 'Central Dairy',
  'schedules': [
    {'dayOfWeek': 'Monday'},
    {'dayOfWeek': 'Wednesday'},
    {'dayOfWeek': 'Friday'},
  ],
  'activatedAtUtc': '2026-08-16T10:00:00Z',
  'pausedAtUtc': null,
  'cancelledAtUtc': null,
  'completedAtUtc': null,
  'createdAtUtc': '2026-08-16T09:00:00Z',
};

Map<String, dynamic> paymentJson() => {
  'publicId': 'payment-1',
  'orderId': null,
  'orderNumber': null,
  'subscriptionId': 'subscription-1',
  'method': 'Wallet',
  'status': 'Success',
  'amount': 2025,
  'refundedAmount': 0,
  'currency': 'INR',
  'gatewayOrderId': null,
  'gatewayPaymentId': null,
  'gatewayKeyId': null,
  'failureCode': null,
  'failureMessage': null,
  'expiresAtUtc': '2026-08-16T11:00:00Z',
  'verifiedAtUtc': '2026-08-16T10:00:00Z',
  'createdAtUtc': '2026-08-16T09:00:00Z',
};

Map<String, dynamic> deliveryJson({String status = 'Scheduled'}) => {
  'publicId': status == 'Scheduled' ? 'delivery-1' : 'delivery-2',
  'scheduledDate': '2026-08-17',
  'quantity': 1.125,
  'status': status,
  'branchId': 'branch-1',
  'branchCode': 'CENTRAL',
  'branchName': 'Central Dairy',
  'address': 'Home, 1 Main Street, Pune 411001',
  'statusChangedAtUtc': null,
};

http.Response successResponse(Object data, {int statusCode = 200}) =>
    http.Response(
      jsonEncode({'success': true, 'data': data, 'errors': []}),
      statusCode,
      headers: {'content-type': 'application/json'},
    );

SubscriptionRepository testRepository(http.Client client) =>
    SubscriptionRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

Future<ProviderContainer> authenticatedContainer(
  SubscriptionRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      subscriptionRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  await Future<void>.delayed(Duration.zero);
  return container;
}

Future<void> pumpScreen(
  WidgetTester tester,
  Widget screen, {
  SubscriptionState subscriptionState = const SubscriptionState(),
  CatalogueState catalogueState = const CatalogueState(),
  CustomerState customerState = const CustomerState(),
}) async {
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        subscriptionControllerProvider.overrideWith(
          () => _SeededSubscriptionController(subscriptionState),
        ),
        catalogueControllerProvider.overrideWith(
          () => _SeededCatalogueController(catalogueState),
        ),
        customerControllerProvider.overrideWith(
          () => _SeededCustomerController(customerState),
        ),
      ],
      child: MaterialApp(home: screen),
    ),
  );
  await tester.pumpAndSettle();
}

class _AuthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => _session;
}

class _FakeSubscriptionRepository extends SubscriptionRepository {
  _FakeSubscriptionRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;
  String? lastIdempotencyKey;

  @override
  Future<CreatedSubscription> create({
    required String token,
    required CreateSubscriptionRequest request,
    required String idempotencyKey,
  }) async {
    lastToken = token;
    lastIdempotencyKey = idempotencyKey;
    return CreatedSubscription.fromJson({
      'subscription': subscriptionJson(),
      'payment': paymentJson(),
    });
  }
}

class _FailingSubscriptionRepository extends SubscriptionRepository {
  _FailingSubscriptionRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<List<SubscriptionDetails>> getMine(String token) async =>
      throw failure;
}

class _SeededSubscriptionController extends SubscriptionController {
  _SeededSubscriptionController(this.initialState);

  final SubscriptionState initialState;

  @override
  SubscriptionState build() => initialState;

  @override
  Future<void> loadSubscriptions() async {}

  @override
  Future<void> loadSubscription(String subscriptionId) async {}

  @override
  Future<void> loadCalendar(String subscriptionId) async {}
}

class _SeededCatalogueController extends CatalogueController {
  _SeededCatalogueController(this.initialState);

  final CatalogueState initialState;

  @override
  CatalogueState build() => initialState;

  @override
  Future<void> load() async {}
}

class _SeededCustomerController extends CustomerController {
  _SeededCustomerController(this.initialState);

  final CustomerState initialState;

  @override
  CustomerState build() => initialState;

  @override
  Future<void> load() async {}
}

const _product = CatalogueProduct(
  publicId: 'product-1',
  sku: 'MILK-1L',
  name: 'Whole Milk',
  description: null,
  category: ProductCategory(
    publicId: 'category-1',
    code: 'MILK',
    name: 'Milk',
    description: null,
    isActive: true,
  ),
  unitOfMeasure: 'litre',
  price: 60,
  isActive: true,
  branchAvailability: [
    BranchAvailability(
      branchId: 'branch-1',
      branchCode: 'CENTRAL',
      branchName: 'Central Dairy',
      isAvailable: true,
      maxDailyQuantity: 100,
    ),
  ],
);

const _address = CustomerAddress(
  publicId: 'address-1',
  label: 'Home',
  addressLine1: '1 Main Street',
  addressLine2: null,
  locality: 'Central',
  city: 'Pune',
  state: 'Maharashtra',
  pinCode: '411001',
  landmark: null,
  deliveryInstructions: null,
  contactName: 'Customer',
  contactMobile: '9999999999',
  latitude: 18.5204,
  longitude: 73.8567,
  isDefault: true,
  isActive: true,
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'customer-1',
    displayName: 'Test Customer',
    email: 'customer@example.test',
    mobile: null,
    roles: ['CUSTOMER'],
    permissions: [],
    branchIds: [],
  ),
  accessToken: 'customer-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
