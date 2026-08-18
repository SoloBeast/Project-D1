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
import 'package:doodh_direct_mobile/features/payments/payment_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_controller.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_models.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_repository.dart';
import 'package:doodh_direct_mobile/features/subscriptions/subscription_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
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

    test('retry posts method and fresh-attempt idempotency header', () async {
      final client = MockClient((request) async {
        expect(request.method, 'POST');
        expect(
          request.url.path,
          '/api/v1/subscriptions/subscription-1/retry-payment',
        );
        expect(request.headers['Authorization'], 'Bearer customer-token');
        expect(request.headers['Idempotency-Key'], 'subscription-retry-1');
        expect(jsonDecode(request.body), {'paymentMethod': 'Razorpay'});
        return successResponse({
          'subscription': subscriptionJson(status: 'PaymentPending'),
          'payment': paymentJson(
            publicId: 'payment-retry-1',
            method: 'Razorpay',
            status: 'Pending',
          ),
        }, statusCode: 201);
      });
      final repository = testRepository(client);

      final result = await repository.retryPayment(
        token: 'customer-token',
        subscriptionId: 'subscription-1',
        paymentMethod: PaymentMethod.razorpay,
        idempotencyKey: 'subscription-retry-1',
      );

      expect(result.subscription.status, SubscriptionStatus.paymentPending);
      expect(result.payment.publicId, 'payment-retry-1');
      expect(result.payment.method, PaymentMethod.razorpay);
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
      'retry adopts payment, upserts subscription, and uses new keys',
      () async {
        final repository = _FakeSubscriptionRepository();
        final container = await authenticatedContainer(repository);
        addTearDown(container.dispose);

        final controller = container.read(
          subscriptionControllerProvider.notifier,
        );
        final first = await controller.retryPayment(
          'subscription-1',
          PaymentMethod.wallet,
        );
        final second = await controller.retryPayment(
          'subscription-1',
          PaymentMethod.razorpay,
        );

        expect(first, isNotNull);
        expect(second, isNotNull);
        expect(repository.retrySubscriptionIds, [
          'subscription-1',
          'subscription-1',
        ]);
        expect(repository.retryMethods, [
          PaymentMethod.wallet,
          PaymentMethod.razorpay,
        ]);
        expect(repository.retryIdempotencyKeys, hasLength(2));
        expect(
          repository.retryIdempotencyKeys,
          everyElement(startsWith('mobile-subscription-retry-')),
        );
        expect(repository.retryIdempotencyKeys.toSet(), hasLength(2));
        expect(
          container.read(paymentControllerProvider).payment?.publicId,
          'payment-retry-2',
        );
        final state = container.read(subscriptionControllerProvider);
        expect(state.subscriptions, hasLength(1));
        expect(state.selectedSubscription?.publicId, 'subscription-1');
        expect(state.selectedSubscription?.status, SubscriptionStatus.active);
      },
    );

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

  group('subscription product selection', () {
    test('deduplicates available products by immutable public ID', () {
      final replacement = productFixture('product-1', 'Replacement Milk');

      final products = deduplicateAvailableSubscriptionProducts([
        _product,
        replacement,
      ]);

      expect(products, hasLength(1));
      expect(products.single, same(_product));
    });

    test('preserves selection when catalogue replaces the Dart object', () {
      final replacement = productFixture('product-1', 'Replacement Milk');

      expect(
        resolveAvailableSubscriptionProductId([replacement], 'product-1'),
        'product-1',
      );
    });

    test('falls back predictably when selection disappears', () {
      final firstAvailable = productFixture('product-2', 'Toned Milk');
      final secondAvailable = productFixture('product-3', 'Cow Milk');

      expect(
        resolveAvailableSubscriptionProductId([
          firstAvailable,
          secondAvailable,
        ], 'product-1'),
        'product-2',
      );
      expect(resolveAvailableSubscriptionProductId([], 'product-1'), isNull);
    });
  });

  group('subscription address selection', () {
    test('deduplicates active addresses by immutable public ID', () {
      final replacement = addressFixture(
        publicId: 'address-1',
        label: 'Replacement Home',
      );
      final inactive = addressFixture(
        publicId: 'address-2',
        label: 'Inactive',
        isActive: false,
      );

      final addresses = deduplicateActiveSubscriptionAddresses([
        _address,
        replacement,
        inactive,
      ]);

      expect(addresses, hasLength(1));
      expect(addresses.single, same(_address));
    });

    test('preserves selection when repository replaces the Dart object', () {
      final replacement = addressFixture(
        publicId: 'address-1',
        label: 'Replacement Home',
      );

      expect(
        resolveActiveSubscriptionAddressId([replacement], 'address-1'),
        'address-1',
      );
    });

    test('falls back to active default, first active address, then null', () {
      final first = addressFixture(publicId: 'address-2', label: 'Office');
      final defaultAddress = addressFixture(
        publicId: 'address-3',
        label: 'Parents',
        isDefault: true,
      );

      expect(
        resolveActiveSubscriptionAddressId([
          first,
          defaultAddress,
        ], 'inactive-address'),
        'address-3',
      );
      expect(
        resolveActiveSubscriptionAddressId([first], 'inactive-address'),
        'address-2',
      );
      expect(resolveActiveSubscriptionAddressId([], 'address-1'), isNull);
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
        await tester.scrollUntilVisible(
          find.text('Prepaid estimate'),
          300,
          scrollable: find.byType(Scrollable).first,
        );
        expect(find.text('Prepaid estimate'), findsOneWidget);
        await tester.scrollUntilVisible(
          find.text('Continue to payment'),
          300,
          scrollable: find.byType(Scrollable).first,
        );
        expect(find.text('Continue to payment'), findsOneWidget);
      },
    );

    testWidgets('setup tolerates duplicate address IDs across rebuilds', (
      tester,
    ) async {
      await pumpScreen(
        tester,
        const SubscriptionSetupScreen(),
        catalogueState: CatalogueState(products: [_product]),
        customerState: CustomerState(
          addresses: [
            _address,
            addressFixture(publicId: 'address-1', label: 'Replacement Home'),
          ],
        ),
      );

      expect(find.text('Home - Pune'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pump();
      expect(tester.takeException(), isNull);
    });

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
      expect(find.text('Retry payment'), findsNothing);
      expect(find.text('View delivery calendar'), findsOneWidget);
      expect(find.text('Complete Payment'), findsNothing);
    });

    testWidgets(
      'pending detail shows amount and deliberate completion actions',
      (tester) async {
        await tester.binding.setSurfaceSize(const Size(800, 1400));
        addTearDown(() => tester.binding.setSurfaceSize(null));

        final pendingSubscription = SubscriptionDetails.fromJson(
          subscriptionJson(status: 'PaymentPending'),
        );
        await pumpScreen(
          tester,
          const SubscriptionDetailScreen(subscriptionId: 'subscription-1'),
          subscriptionState: SubscriptionState(
            subscriptions: [pendingSubscription],
            selectedSubscription: pendingSubscription,
          ),
        );

        expect(find.text('Status: Payment Pending'), findsOneWidget);
        expect(find.text('Amount Due: ₹2025.00'), findsOneWidget);
        expect(find.text('Complete Payment'), findsNWidgets(2));
        expect(find.text('DoodhDirect Wallet'), findsOneWidget);
        expect(find.text('Razorpay'), findsOneWidget);
        expect(find.text('Development payment'), findsOneWidget);
        expect(find.text('Retry Payment'), findsNothing);
      },
    );

    testWidgets('detail offers Wallet and Razorpay retry only after failure', (
      tester,
    ) async {
      final failedSubscription = SubscriptionDetails.fromJson(
        subscriptionJson(status: 'PaymentFailed'),
      );
      await pumpScreen(
        tester,
        const SubscriptionDetailScreen(subscriptionId: 'subscription-1'),
        subscriptionState: SubscriptionState(
          subscriptions: [failedSubscription],
          selectedSubscription: failedSubscription,
        ),
      );

      await tester.scrollUntilVisible(
        find.widgetWithText(FilledButton, 'Retry Payment'),
        250,
        scrollable: find.byType(Scrollable).first,
      );
      expect(find.text('Retry Payment'), findsNWidgets(2));
      expect(find.text('DoodhDirect Wallet'), findsOneWidget);
      expect(find.text('Razorpay'), findsOneWidget);
      expect(find.text('Development payment'), findsOneWidget);
      expect(find.text('Pause subscription'), findsNothing);
      expect(find.text('Resume subscription'), findsNothing);
    });

    testWidgets('reloads subscription after payment result route returns', (
      tester,
    ) async {
      final pendingSubscription = SubscriptionDetails.fromJson(
        subscriptionJson(status: 'PaymentPending'),
      );
      final retryResult = CreatedSubscription.fromJson({
        'subscription': subscriptionJson(status: 'Active'),
        'payment': paymentJson(publicId: 'payment-retry-1'),
      });
      late _SeededSubscriptionController controller;
      final router = GoRouter(
        initialLocation: '/subscriptions/subscription-1',
        routes: [
          GoRoute(
            path: '/subscriptions/:subscriptionId',
            builder: (context, state) => SubscriptionDetailScreen(
              subscriptionId: state.pathParameters['subscriptionId']!,
            ),
          ),
          GoRoute(
            path: '/payments/:paymentId/result',
            builder: (context, state) =>
                const Scaffold(body: Center(child: Text('Payment result'))),
          ),
        ],
      );
      addTearDown(router.dispose);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            subscriptionControllerProvider.overrideWith(
              () => controller = _SeededSubscriptionController(
                SubscriptionState(
                  subscriptions: [pendingSubscription],
                  selectedSubscription: pendingSubscription,
                ),
                retryResult: retryResult,
              ),
            ),
          ],
          child: MaterialApp.router(routerConfig: router),
        ),
      );
      await tester.pumpAndSettle();
      final initialLoads = controller.loadedSubscriptionIds.length;
      await tester.drag(find.byType(ListView), const Offset(0, -600));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Complete Payment'));
      await tester.pumpAndSettle();
      expect(find.text('Payment result'), findsOneWidget);

      router.pop();
      await tester.pumpAndSettle();

      expect(controller.loadedSubscriptionIds, hasLength(initialLoads + 1));
      expect(controller.loadedSubscriptionIds.last, 'subscription-1');
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

Map<String, dynamic> paymentJson({
  String publicId = 'payment-1',
  String method = 'Wallet',
  String? provider,
  String status = 'Success',
}) => {
  'publicId': publicId,
  'orderId': null,
  'orderNumber': null,
  'subscriptionId': 'subscription-1',
  'method': method,
  'provider': provider ?? (method == 'Development' ? 'Mock' : method),
  'status': status,
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
      paymentRepositoryProvider.overrideWithValue(_FakePaymentRepository()),
      subscriptionRepositoryProvider.overrideWithValue(repository),
    ],
  );
  container.read(sessionControllerProvider);
  container.listen(paymentControllerProvider, (_, _) {});
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

class _FakePaymentRepository extends PaymentRepository {
  _FakePaymentRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  @override
  Future<List<PaymentCapability>> getCapabilities(String token) async => const [
    PaymentCapability(
      method: PaymentMethod.wallet,
      provider: 'Wallet',
      label: 'DoodhDirect Wallet',
      isAvailable: true,
      unavailableReason: null,
    ),
    PaymentCapability(
      method: PaymentMethod.razorpay,
      provider: 'Razorpay',
      label: 'Razorpay',
      isAvailable: true,
      unavailableReason: null,
    ),
  ];
}

class _FakeSubscriptionRepository extends SubscriptionRepository {
  _FakeSubscriptionRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  String? lastToken;
  String? lastIdempotencyKey;
  final retrySubscriptionIds = <String>[];
  final retryMethods = <PaymentMethod>[];
  final retryIdempotencyKeys = <String>[];

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

  @override
  Future<CreatedSubscription> retryPayment({
    required String token,
    required String subscriptionId,
    required PaymentMethod paymentMethod,
    required String idempotencyKey,
  }) async {
    retrySubscriptionIds.add(subscriptionId);
    retryMethods.add(paymentMethod);
    retryIdempotencyKeys.add(idempotencyKey);
    final attempt = retrySubscriptionIds.length;
    return CreatedSubscription.fromJson({
      'subscription': subscriptionJson(
        status: attempt == 1 ? 'PaymentPending' : 'Active',
      ),
      'payment': paymentJson(
        publicId: 'payment-retry-$attempt',
        method: paymentMethod.apiValue,
        status: attempt == 1 ? 'Pending' : 'Success',
      ),
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
  _SeededSubscriptionController(this.initialState, {this.retryResult});

  final SubscriptionState initialState;
  final CreatedSubscription? retryResult;
  final loadedSubscriptionIds = <String>[];

  @override
  SubscriptionState build() => initialState;

  @override
  Future<void> loadSubscriptions() async {}

  @override
  Future<void> loadSubscription(String subscriptionId) async {
    loadedSubscriptionIds.add(subscriptionId);
  }

  @override
  Future<void> loadCalendar(String subscriptionId) async {}

  @override
  Future<CreatedSubscription?> retryPayment(
    String subscriptionId,
    PaymentMethod paymentMethod,
  ) async => retryResult;
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

CustomerAddress addressFixture({
  required String publicId,
  required String label,
  bool isDefault = false,
  bool isActive = true,
}) => CustomerAddress(
  publicId: publicId,
  label: label,
  addressLine1: '2 Main Street',
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
  isDefault: isDefault,
  isActive: isActive,
);

CatalogueProduct productFixture(String publicId, String name) =>
    CatalogueProduct(
      publicId: publicId,
      sku: 'MILK-$publicId',
      name: name,
      description: null,
      category: _product.category,
      unitOfMeasure: _product.unitOfMeasure,
      price: _product.price,
      isActive: true,
      branchAvailability: _product.branchAvailability,
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
