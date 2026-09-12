import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_repository.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('refund replacement config controller', () {
    test('loads the configuration with the session token', () async {
      final repository = _FakeRefundReplacementConfigRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(
        refundReplacementConfigControllerProvider.notifier,
      );

      await controller.load();
      final state = container.read(refundReplacementConfigControllerProvider);

      expect(state.configuration?.windowHours, 48);
      expect(state.configuration?.status, 'Configured');
      expect(state.isLoading, isFalse);
      expect(repository.lastToken, 'refund-replacement-token');
    });

    test('save succeeds, stores the response and shows a saved message', () async {
      final repository = _FakeRefundReplacementConfigRepository();
      final container = await _authenticatedContainer(repository);
      addTearDown(container.dispose);
      final controller = container.read(
        refundReplacementConfigControllerProvider.notifier,
      );

      final saved = await controller.save(
        const UpdateRefundReplacementConfigurationRequest(windowHours: 5),
      );
      final state = container.read(refundReplacementConfigControllerProvider);

      expect(saved, isTrue);
      expect(state.configuration?.windowHours, 5);
      expect(state.savedMessage, 'Refund/replacement window settings saved.');
      expect(state.isSaving, isFalse);
      expect(repository.lastRequest?.windowHours, 5);
    });

    test('save failure surfaces the error message and maps field errors', () async {
      final container = await _authenticatedContainer(
        _FailingRefundReplacementConfigRepository(
          const ApiException(
            400,
            'VALIDATION_ERROR',
            'WindowHours must be between 1 and 8760.',
            field: 'windowHours',
          ),
        ),
      );
      addTearDown(container.dispose);
      final controller = container.read(
        refundReplacementConfigControllerProvider.notifier,
      );

      final saved = await controller.save(
        const UpdateRefundReplacementConfigurationRequest(windowHours: 99999),
      );
      final state = container.read(refundReplacementConfigControllerProvider);

      expect(saved, isFalse);
      expect(state.errorMessage, 'WindowHours must be between 1 and 8760.');
      expect(
        state.fieldErrors,
        {'windowHours': 'WindowHours must be between 1 and 8760.'},
      );
      expect(state.isSaving, isFalse);
    });

    test('load failure surfaces a friendly error message', () async {
      final container = await _authenticatedContainer(
        _FailingRefundReplacementConfigRepository(
          StateError('network down'),
        ),
      );
      addTearDown(container.dispose);
      final controller = container.read(
        refundReplacementConfigControllerProvider.notifier,
      );

      await controller.load();
      final state = container.read(refundReplacementConfigControllerProvider);

      expect(state.configuration, isNull);
      expect(
        state.errorMessage,
        'Unable to load the refund/replacement window settings. Check '
        'your connection and try again.',
      );
      expect(state.isLoading, isFalse);
    });

    test('does not call the repository without an authenticated session', () async {
      final repository = _FakeRefundReplacementConfigRepository();
      final container = ProviderContainer(
        overrides: [
          authRepositoryProvider.overrideWithValue(_UnauthenticatedRepository()),
          refundReplacementConfigRepositoryProvider.overrideWithValue(repository),
        ],
      );
      addTearDown(container.dispose);
      container.read(refundReplacementConfigControllerProvider);

      await container
          .read(refundReplacementConfigControllerProvider.notifier)
          .load();
      final saved = await container
          .read(refundReplacementConfigControllerProvider.notifier)
          .save(
            const UpdateRefundReplacementConfigurationRequest(windowHours: 5),
          );

      expect(saved, isFalse);
      expect(repository.callCount, 0);
    });
  });
}

Future<ProviderContainer> _authenticatedContainer(
  RefundReplacementConfigRepository repository,
) async {
  final container = ProviderContainer(
    overrides: [
      authRepositoryProvider.overrideWithValue(_AuthenticatedRepository()),
      refundReplacementConfigRepositoryProvider.overrideWithValue(repository),
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

class _FakeRefundReplacementConfigRepository
    extends RefundReplacementConfigRepository {
  _FakeRefundReplacementConfigRepository()
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  int callCount = 0;
  String? lastToken;
  UpdateRefundReplacementConfigurationRequest? lastRequest;

  @override
  Future<RefundReplacementConfiguration> get(String accessToken) async {
    callCount++;
    lastToken = accessToken;
    return const RefundReplacementConfiguration(
      windowHours: 48,
      status: 'Configured',
    );
  }

  @override
  Future<RefundReplacementConfiguration> update(
    String accessToken,
    UpdateRefundReplacementConfigurationRequest request,
  ) async {
    callCount++;
    lastToken = accessToken;
    lastRequest = request;
    return RefundReplacementConfiguration(
      windowHours: request.windowHours ?? 48,
      status: 'Configured',
    );
  }
}

class _FailingRefundReplacementConfigRepository
    extends RefundReplacementConfigRepository {
  _FailingRefundReplacementConfigRepository(this.failure)
    : super(api: ApiClient(baseUrl: 'https://api.example.test'));

  final Object failure;

  @override
  Future<RefundReplacementConfiguration> get(String accessToken) async =>
      throw failure;

  @override
  Future<RefundReplacementConfiguration> update(
    String accessToken,
    UpdateRefundReplacementConfigurationRequest request,
  ) async => throw failure;
}

final _session = AuthSession(
  user: const AuthUser(
    publicUserId: 'owner-1',
    displayName: 'Owner',
    email: 'owner@example.test',
    mobile: null,
    roles: ['OWNER'],
    permissions: [
      'SETUP.REFUND_REPLACEMENT.READ',
      'SETUP.REFUND_REPLACEMENT.MANAGE',
    ],
    branchIds: [7],
  ),
  accessToken: 'refund-replacement-token',
  refreshToken: 'refresh-token',
  accessTokenExpiresAtUtc: DateTime.utc(2099),
  refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
);
