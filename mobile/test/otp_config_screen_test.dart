import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/otp_config/otp_config_controller.dart';
import 'package:doodh_direct_mobile/features/otp_config/otp_config_models.dart';
import 'package:doodh_direct_mobile/features/otp_config/otp_config_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('OTP provider configuration screen', () {
    testWidgets('types a new widget id and the value sticks', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      final field = find.byType(TextField).at(0);
      expect(tester.widget<TextField>(field).controller!.text, '6f2c4a1b9e3d');

      await tester.enterText(field, 'new-widget');
      await tester.pump();

      // Regression: the value used to be reverted to the server value on every
      // rebuild triggered by onChanged -> setState.
      expect(tester.widget<TextField>(field).controller!.text, 'new-widget');
    });

    testWidgets('toggles the OTP delivery enabled switch', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration(enabled: true)),
      );
      await _pump(tester, controller);

      final tile = find.byType(SwitchListTile);
      expect(tester.widget<SwitchListTile>(tile).value, isTrue);

      await tester.tap(tile);
      await tester.pump();

      expect(tester.widget<SwitchListTile>(tile).value, isFalse);
    });

    testWidgets('changes the environment through the dropdown', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.tap(
        find.byType(DropdownButtonFormField<OtpProviderEnvironment>),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Production').last);
      await tester.pumpAndSettle();

      expect(
        find.descendant(
          of: find.byType(DropdownButtonFormField<OtpProviderEnvironment>),
          matching: find.text('Production'),
        ),
        findsOneWidget,
      );
    });

    testWidgets('enables save only when a valid change exists', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // No edits yet -> Save disabled.
      expect(_saveButton(tester).onPressed, isNull);

      // Typing a new widget id enables Save.
      await tester.enterText(find.byType(TextField).at(0), 'changed');
      await tester.pump();
      expect(_saveButton(tester).onPressed, isNotNull);

      // Reverting to the original value disables Save again.
      await tester.enterText(find.byType(TextField).at(0), '6f2c4a1b9e3d');
      await tester.pump();
      expect(_saveButton(tester).onPressed, isNull);
    });

    testWidgets('keeps a typed value across a rebuild from another field', (
      tester,
    ) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'typed-id');
      await tester.pump();

      // Toggling the switch triggers a full rebuild.
      await tester.tap(find.byType(SwitchListTile));
      await tester.pump();

      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        'typed-id',
      );
    });

    testWidgets('sends the edited values to the backend on save', (
      tester,
    ) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'edited-widget');
      await tester.pump();
      await tester.tap(find.byType(SwitchListTile));
      await tester.pump();
      await tester.tap(find.byType(DropdownButtonFormField<OtpProviderEnvironment>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Production').last);
      await tester.pumpAndSettle();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(controller.lastRequest!.widgetId, 'edited-widget');
      expect(controller.lastRequest!.enabled, isFalse);
      expect(
        controller.lastRequest!.environment,
        OtpProviderEnvironment.production,
      );
      expect(controller.lastRequest!.authKey, isNull);
    });

    testWidgets('sends a newly entered auth key with the save', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(1), 'new-secret-key');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(controller.lastRequest!.authKey, 'new-secret-key');
    });

    testWidgets('never reveals the stored auth key in the UI', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration(configured: true)),
      );
      await _pump(tester, controller);

      // The provider is configured (a key exists server-side) but the field is
      // always blank because the backend never returns the secret.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(1)).controller!.text,
        isEmpty,
      );
      // The masked placeholder is what the admin sees instead.
      expect(find.text('••••••••••••'), findsOneWidget);
    });

    testWidgets('leaving the auth key blank preserves the stored key', (
      tester,
    ) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // Change the widget id only; leave the auth key untouched.
      await tester.enterText(find.byType(TextField).at(0), 'new-widget');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      // An omitted authKey tells the backend to keep the existing key.
      expect(controller.lastRequest!.authKey, isNull);
      expect(controller.lastRequest!.widgetId, 'new-widget');
    });

    testWidgets('does not send the masked placeholder as an auth key', (
      tester,
    ) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // The masked hint is only a visual placeholder, never a value.
      expect(find.text('••••••••••••'), findsOneWidget);

      await tester.enterText(find.byType(TextField).at(0), 'x');
      await tester.pump();
      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.lastRequest!.authKey, isNull);
      expect(controller.lastRequest!.widgetId, 'x');
    });

    testWidgets('successful save updates the form from the response', (
      tester,
    ) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration(widgetId: 'old-id')),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'saved-id');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      // The form re-hydrates from the PUT response and the auth key clears.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        'saved-id',
      );
      expect(
        tester.widget<TextField>(find.byType(TextField).at(1)).controller!.text,
        isEmpty,
      );
      // The form now matches the server state, so Save disables again.
      expect(_saveButton(tester).onPressed, isNull);
    });

    testWidgets('failed save keeps the edited values and shows the error', (
      tester,
    ) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
        failSave: true,
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'keep-me');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(
        find.text('The MSG91 AuthKey cannot be empty when provided.'),
        findsOneWidget,
      );
      // The user's edits are NOT silently reset on failure.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        'keep-me',
      );
    });

    testWidgets('owner can edit the configuration', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller, roles: const ['OWNER']);

      expect(find.text('Save settings'), findsOneWidget);
      expect(find.text('Send test OTP'), findsOneWidget);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isTrue,
      );
    });

    testWidgets('system admin can edit the configuration', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller, roles: const ['SYSTEM_ADMIN']);

      expect(find.text('Save settings'), findsOneWidget);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isTrue,
      );
    });

    testWidgets('blocks users without read permission', (tester) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(tester, controller, permissions: const []);

      expect(find.text('Access denied'), findsOneWidget);
      expect(find.text('Save settings'), findsNothing);
    });

    testWidgets('read-only users see disabled fields and no save button', (
      tester,
    ) async {
      final controller = _SeededOtpConfigController(
        OtpConfigState(configuration: _configuration()),
      );
      await _pump(
        tester,
        controller,
        permissions: const [kOtpProviderReadPermission],
      );

      expect(find.text('Read-only view'), findsOneWidget);
      expect(find.text('Save settings'), findsNothing);
      expect(find.text('Send test OTP'), findsNothing);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isFalse,
      );
      expect(
        tester.widget<SwitchListTile>(find.byType(SwitchListTile)).onChanged,
        isNull,
      );
      expect(
        tester
            .widget<TextField>(find.byType(TextField).at(1))
            .enabled,
        isFalse,
      );
    });
  });
}

Future<void> _pump(
  WidgetTester tester,
  _SeededOtpConfigController controller, {
  List<String> permissions = const [
    kOtpProviderReadPermission,
    kOtpProviderManagePermission,
  ],
  List<String> roles = const ['OWNER'],
}) async {
  await tester.binding.setSurfaceSize(const Size(800, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        otpConfigControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(permissions: permissions, roles: roles),
        ),
      ],
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: const OtpProviderConfigScreen(),
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

class _SeededOtpConfigController extends OtpConfigController {
  _SeededOtpConfigController(this.initialState, {this.failSave = false});

  final OtpConfigState initialState;
  final bool failSave;

  int loadCount = 0;
  int saveCount = 0;
  int testCount = 0;
  UpdateOtpProviderConfigurationRequest? lastRequest;

  @override
  OtpConfigState build() => initialState;

  @override
  Future<void> load() async {
    loadCount++;
  }

  @override
  Future<bool> save(UpdateOtpProviderConfigurationRequest request) async {
    saveCount++;
    lastRequest = request;
    if (failSave) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'The MSG91 AuthKey cannot be empty when provided.',
      );
      return false;
    }

    final current = initialState.configuration!;
    state = state.copyWith(
      configuration: OtpProviderConfiguration(
        provider: current.provider,
        enabled: request.enabled ?? current.enabled,
        widgetId: request.widgetId ?? current.widgetId,
        environment: request.environment ?? current.environment,
        configured: current.configured,
        status: current.status,
      ),
      isSaving: false,
      savedMessage: 'OTP provider settings saved.',
    );
    return true;
  }

  @override
  Future<void> test() async {
    testCount++;
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
        publicUserId: 'otp-provider-user-1',
        displayName: 'Setup Manager',
        email: null,
        mobile: '9999999999',
        roles: roles,
        permissions: permissions,
        branchIds: const [7],
      ),
      accessToken: 'otp-provider-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

OtpProviderConfiguration _configuration({
  String? widgetId = '6f2c4a1b9e3d',
  bool enabled = true,
  OtpProviderEnvironment? environment = OtpProviderEnvironment.test,
  bool configured = true,
}) => OtpProviderConfiguration(
  provider: 'MSG91',
  enabled: enabled,
  widgetId: widgetId,
  environment: environment,
  configured: configured,
  status: configured ? 'Ready to send OTPs' : 'Not Configured',
);
