import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_controller.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_models.dart';
import 'package:doodh_direct_mobile/features/setup/number_series_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('number series controller', () {
    test('loads series with the session token', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      await controller.load();
      final state = container.read(numberSeriesControllerProvider);

      expect(state.series.single.code, 'CUST');
      expect(state.isLoading, isFalse);
      expect(repository.lastToken, 'number-series-token');
    });

    test('previews a template without advancing the counter', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      await controller.previewTemplate('CUST', 'CUST/{NUMBER:0000}', nextNumber: 5);
      final state = container.read(numberSeriesControllerProvider);

      expect(state.preview?.code, 'CUST');
      expect(state.preview?.formattedNumber, 'CUST/0005');
      expect(state.preview?.nextNumber, 5);
      expect(state.isPreviewing, isFalse);
      expect(repository.lastPreviewRequest?.nextNumber, 5);
      expect(repository.lastPreviewRequest?.scope, isNull);
    });

    test('previews a scoped template with the scope forwarded', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      await controller.previewTemplate(
        'ORD',
        'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
        scope: 'MAIN',
      );
      final state = container.read(numberSeriesControllerProvider);

      expect(state.preview?.scopeKey, 'MAIN');
      expect(repository.lastPreviewRequest?.scope, 'MAIN');
    });

    test('create returns the created series and refreshes the list', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      final created = await controller.create(_createRequest);
      final state = container.read(numberSeriesControllerProvider);

      expect(created?.code, 'CUST');
      expect(state.savedMessage, 'Series CUST created.');
      expect(state.series.single.code, 'CUST');
      expect(state.isSaving, isFalse);
      expect(repository.lastCreateRequest?.template, 'CUST/{NUMBER:0000}');
      expect(repository.listCount, 1);
    });

    test('update returns the updated series and refreshes the list', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      final updated = await controller.update('CUST', _updateRequest);
      final state = container.read(numberSeriesControllerProvider);

      expect(updated?.code, 'CUST');
      expect(state.savedMessage, 'Series CUST updated.');
      expect(repository.lastCode, 'CUST');
      expect(repository.lastScope, isNull);
      expect(
        repository.lastUpdateRequest?.resetPolicy,
        NumberSeriesResetPolicy.monthly,
      );
      expect(state.isSaving, isFalse);
    });

    test('update forwards the scope for scoped series', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      final updated = await controller.update(
        'ORD',
        _scopedUpdateRequest,
        scope: 'MAIN',
      );

      expect(updated?.code, 'ORD');
      expect(repository.lastScope, 'MAIN');
    });

    test('setActive toggles activation and refreshes the list', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      final deactivated = await controller.setActive('CUST', false);
      var state = container.read(numberSeriesControllerProvider);

      expect(deactivated?.isActive, isFalse);
      expect(state.savedMessage, 'Series CUST deactivated.');
      expect(repository.lastCode, 'CUST');
      expect(repository.lastIsActive, isFalse);

      final activated = await controller.setActive('CUST', true);
      state = container.read(numberSeriesControllerProvider);

      expect(activated?.isActive, isTrue);
      expect(state.savedMessage, 'Series CUST activated.');
      expect(repository.lastIsActive, isTrue);
    });

    test('setActive forwards the scope for scoped series', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      final deactivated = await controller.setActive('ORD', false, scope: 'MAIN');

      expect(deactivated?.isActive, isFalse);
      expect(repository.lastCode, 'ORD');
      expect(repository.lastScope, 'MAIN');
    });

    test('maps ApiException field errors into state.fieldErrors', () async {
      final container = await _authenticatedContainer(
        _FailingNumberSeriesRepository(
          const ApiException(
            400,
            'VALIDATION_ERROR',
            'Template is required.',
            field: 'Template',
          ),
        ),
      );
      addTearDown(container.dispose);
      final controller = container.read(numberSeriesControllerProvider.notifier);

      final created = await controller.create(_createRequest);
      final state = container.read(numberSeriesControllerProvider);

      expect(created, isNull);
      expect(state.errorMessage, 'Template is required.');
      expect(state.fieldErrors, {'template': 'Template is required.'});
      expect(state.isSaving, isFalse);
    });

    test('does not call repository without an authenticated session', () async {
      final repository = _FakeNumberSeriesRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(_UnauthenticatedRepository()),
          numberSeriesRepositoryProvider.overrideWithValue(repository),
        ],
      );
      addTearDown(container.dispose);
      container.read(numberSeriesControllerProvider);

      await container.read(numberSeriesControllerProvider.notifier).load();
      await container
          .read(numberSeriesControllerProvider.notifier)
          .create(_createRequest);

      expect(repository.callCount, 0);
    });
  });
}

Future<ProviderContainer> _authenticatedContainer(
  NumberSeriesRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      numberSeriesRepositoryProvider.overrideWithValue(repository),
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

class _UnauthenticatedRepository extends AuthRepository {
  @override
  Future<AuthSession?> restore() async => null;
}

class _FakeNumberSeriesRepository extends NumberSeriesRepository {
  _FakeNumberSeriesRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int callCount = 0;
  int listCount = 0;
  String? lastToken;
  String? lastCode;
  String? lastScope;
  bool? lastIsActive;
  CreateNumberSeriesRequest? lastCreateRequest;
  UpdateNumberSeriesRequest? lastUpdateRequest;
  NumberSeriesPreviewRequest? lastPreviewRequest;

  @override
  Future<List<NumberSeries>> list(String token) async {
    callCount++;
    listCount++;
    lastToken = token;
    return [_series()];
  }

  @override
  Future<NumberSeries> get(String token, String code, {String? scope}) async {
    callCount++;
    lastToken = token;
    lastCode = code;
    lastScope = scope;
    return _series(code: code, scope: scope);
  }

  @override
  Future<NumberSeriesPreview> preview(
    String token,
    NumberSeriesPreviewRequest request,
  ) async {
    callCount++;
    lastToken = token;
    lastPreviewRequest = request;
    return NumberSeriesPreview(
      code: request.code,
      template: request.template,
      nextNumber: request.nextNumber ?? 1,
      formattedNumber:
          '${request.code}/${(request.nextNumber ?? 1).toString().padLeft(4, '0')}',
      scopeKey: request.scope,
    );
  }

  @override
  Future<NumberSeries> create(
    String token,
    CreateNumberSeriesRequest request,
  ) async {
    callCount++;
    lastToken = token;
    lastCreateRequest = request;
    return _series(code: request.code, scope: request.scopeKey);
  }

  @override
  Future<NumberSeries> update(
    String token,
    String code,
    UpdateNumberSeriesRequest request, {
    String? scope,
  }) async {
    callCount++;
    lastToken = token;
    lastCode = code;
    lastScope = scope;
    lastUpdateRequest = request;
    return _series(code: code, scope: scope);
  }

  @override
  Future<NumberSeries> setActive(
    String token,
    String code,
    bool isActive, {
    String? scope,
  }) async {
    callCount++;
    lastToken = token;
    lastCode = code;
    lastScope = scope;
    lastIsActive = isActive;
    return _series(code: code, isActive: isActive, scope: scope);
  }
}

class _FailingNumberSeriesRepository extends NumberSeriesRepository {
  _FailingNumberSeriesRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<List<NumberSeries>> list(String token) async => throw failure;

  @override
  Future<NumberSeries> get(String token, String code, {String? scope}) async =>
      throw failure;

  @override
  Future<NumberSeriesPreview> preview(
    String token,
    NumberSeriesPreviewRequest request,
  ) async => throw failure;

  @override
  Future<NumberSeries> create(
    String token,
    CreateNumberSeriesRequest request,
  ) async => throw failure;

  @override
  Future<NumberSeries> update(
    String token,
    String code,
    UpdateNumberSeriesRequest request, {
    String? scope,
  }) async => throw failure;

  @override
  Future<NumberSeries> setActive(
    String token,
    String code,
    bool isActive, {
    String? scope,
  }) async => throw failure;
}

NumberSeries _series({
  String code = 'CUST',
  bool isActive = true,
  String? scope,
}) => NumberSeries(
  code: code,
  description: scope == null
      ? 'Customer account numbers'
      : 'Main branch order numbers',
  template: scope == null
      ? 'CUST/{NUMBER:0000}'
      : 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
  startingNumber: 1,
  lastUsedNumber: 1000,
  incrementBy: 1,
  resetPolicy: scope == null
      ? NumberSeriesResetPolicy.never
      : NumberSeriesResetPolicy.financialYear,
  isActive: isActive,
  scopeKey: scope,
  nextNumber: 'CUST/1001',
);

const _createRequest = CreateNumberSeriesRequest(
  code: 'CUST',
  description: 'Customer account numbers',
  template: 'CUST/{NUMBER:0000}',
  startingNumber: 1,
  incrementBy: 1,
  resetPolicy: NumberSeriesResetPolicy.never,
);

const _updateRequest = UpdateNumberSeriesRequest(
  description: 'Customer account numbers',
  template: 'CUST/{NUMBER:0000}',
  startingNumber: 1,
  incrementBy: 1,
  resetPolicy: NumberSeriesResetPolicy.monthly,
);

const _scopedUpdateRequest = UpdateNumberSeriesRequest(
  description: 'Main branch order numbers',
  template: 'ORD/{SCOPE}/{FY}/{NUMBER:000000}',
  startingNumber: 1,
  incrementBy: 1,
  resetPolicy: NumberSeriesResetPolicy.financialYear,
);

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'owner-1',
    displayName: 'Owner',
    email: 'owner@example.test',
    mobile: null,
    roles: ['OWNER'],
    permissions: [
      'SETUP.NUMBER_SERIES.READ',
      'SETUP.NUMBER_SERIES.MANAGE',
    ],
    branchIds: [7],
  ),
  accessToken: 'number-series-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
