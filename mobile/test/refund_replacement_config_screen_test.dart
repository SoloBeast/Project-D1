import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_controller.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_models.dart';
import 'package:doodh_direct_mobile/features/refund_replacement_config/refund_replacement_config_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('Refund / Replacement window configuration screen', () {
    testWidgets('types a new window and the value sticks', (tester) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      final field = find.byType(TextField).at(0);
      expect(tester.widget<TextField>(field).controller!.text, '48');

      await tester.enterText(field, '120');
      await tester.pump();

      // Regression: the value used to be reverted to the server value on every
      // rebuild triggered by onChanged -> setState.
      expect(tester.widget<TextField>(field).controller!.text, '120');
    });

    testWidgets('enables save only when a valid change exists', (tester) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // No edits yet -> Save disabled.
      expect(_saveButton(tester).onPressed, isNull);

      // Typing a new window enables Save.
      await tester.enterText(find.byType(TextField).at(0), '72');
      await tester.pump();
      expect(_saveButton(tester).onPressed, isNotNull);

      // Reverting to the original value disables Save again.
      await tester.enterText(find.byType(TextField).at(0), '48');
      await tester.pump();
      expect(_saveButton(tester).onPressed, isNull);
    });

    testWidgets('sends the edited hours to the backend on save', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), '72');
      await tester.pump();
      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(controller.lastRequest!.windowHours, 72);
    });

    testWidgets('successful save re-hydrates and disables save', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), '5');
      await tester.pump();
      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      // The form re-hydrates from the configuration returned by the PUT.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        '5',
      );
      expect(find.text('Refund/replacement window settings saved.'), findsOneWidget);
      // The form now matches the server state, so Save disables again.
      expect(_saveButton(tester).onPressed, isNull);
    });

    testWidgets('failed save keeps the edits and shows the error', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
        failSave: true,
      );
      await _pump(tester, controller);

      // A client-valid value that the backend/controller rejects, so the
      // failure is exercised through the save path (not local validation).
      await tester.enterText(find.byType(TextField).at(0), '72');
      await tester.pump();
      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      // Rendered both as the error banner and as the field-level error text.
      expect(
        find.text('WindowHours must be between 1 and 8760.'),
        findsWidgets,
      );
      // The user's edits are NOT silently reset on failure.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        '72',
      );
    });

    testWidgets('shows a validation error when the field is emptied', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), '');
      await tester.pump();
      await tester.tap(find.text('Save settings'));
      await tester.pump();

      expect(controller.saveCount, 0);
      expect(find.text('Enter the window in whole hours.'), findsOneWidget);
    });

    testWidgets('rejects an out-of-range window before calling the backend', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), '0');
      await tester.pump();
      await tester.tap(find.text('Save settings'));
      await tester.pump();

      expect(controller.saveCount, 0);
      expect(
        find.text('Enter a window between 1 and 8760 hours.'),
        findsOneWidget,
      );
    });

    testWidgets('owner can edit the configuration', (tester) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller, roles: const ['OWNER']);

      expect(find.text('Save settings'), findsOneWidget);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isTrue,
      );
    });

    testWidgets('system admin can edit the configuration', (tester) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller, roles: const ['SYSTEM_ADMIN']);

      expect(find.text('Save settings'), findsOneWidget);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isTrue,
      );
    });

    testWidgets('blocks users without read permission', (tester) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller, permissions: const []);

      expect(find.text('Access denied'), findsOneWidget);
      expect(find.text('Save settings'), findsNothing);
    });

    testWidgets('read-only users see disabled fields and no save button', (
      tester,
    ) async {
      final controller = _SeededRefundReplacementConfigController(
        RefundReplacementConfigState(configuration: _configuration()),
      );
      await _pump(
        tester,
        controller,
        permissions: const [kRefundReplacementConfigReadPermission],
      );

      expect(find.text('Read-only view'), findsOneWidget);
      expect(find.text('Save settings'), findsNothing);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isFalse,
      );
    });
  });
}

Future<void> _pump(
  WidgetTester tester,
  _SeededRefundReplacementConfigController controller, {
  List<String> permissions = const [
    kRefundReplacementConfigReadPermission,
    kRefundReplacementConfigManagePermission,
  ],
  List<String> roles = const ['OWNER'],
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        refundReplacementConfigControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(permissions: permissions, roles: roles),
        ),
      ],
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: const RefundReplacementConfigScreen(),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
}

/// Locates the Save button (a [FilledButton] wrapping the label) so tests can
/// inspect its enabled/disabled state.
FilledButton _saveButton(WidgetTester tester) => tester.widget<FilledButton>(
  find.ancestor(
    of: find.text('Save settings'),
    matching: find.byWidgetPredicate((widget) => widget is FilledButton),
  ),
);

class _SeededRefundReplacementConfigController
    extends RefundReplacementConfigController {
  _SeededRefundReplacementConfigController(this.initialState, {this.failSave = false});

  final RefundReplacementConfigState initialState;
  final bool failSave;

  int loadCount = 0;
  int saveCount = 0;
  UpdateRefundReplacementConfigurationRequest? lastRequest;

  @override
  RefundReplacementConfigState build() => initialState;

  @override
  Future<void> load() async {
    loadCount++;
  }

  @override
  Future<bool> save(UpdateRefundReplacementConfigurationRequest request) async {
    saveCount++;
    lastRequest = request;
    if (failSave) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'WindowHours must be between 1 and 8760.',
        fieldErrors: const {
          'windowHours': 'WindowHours must be between 1 and 8760.',
        },
      );
      return false;
    }

    final current = initialState.configuration!;
    state = state.copyWith(
      configuration: RefundReplacementConfiguration(
        windowHours: request.windowHours ?? current.windowHours,
        status: 'Configured',
      ),
      isSaving: false,
      savedMessage: 'Refund/replacement window settings saved.',
    );
    return true;
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({required this.permissions, required this.roles});

  final List<String> permissions;
  final List<String> roles;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'refund-replacement-config-user-1',
        displayName: 'Setup Manager',
        email: null,
        mobile: '9999999999',
        roles: roles,
        permissions: permissions,
        branchIds: const [7],
      ),
      accessToken: 'refund-replacement-config-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

RefundReplacementConfiguration _configuration({int windowHours = 48}) =>
    RefundReplacementConfiguration(
      windowHours: windowHours,
      status: 'Configured',
    );
