import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/integrations/integrations_controller.dart';
import 'package:doodh_direct_mobile/features/integrations/integrations_models.dart';
import 'package:doodh_direct_mobile/features/integrations/integrations_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('Integrations configuration screen', () {
    testWidgets('types a new SMTP host and the value sticks', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      final host = find.byType(TextField).at(2);
      expect(tester.widget<TextField>(host).controller!.text, 'smtp.example.com');

      await tester.enterText(host, 'relay.internal.example.com');
      await tester.pump();

      // Regression: the value used to be reverted to the server value on every
      // rebuild triggered by onChanged -> setState.
      expect(
        tester.widget<TextField>(host).controller!.text,
        'relay.internal.example.com',
      );
    });

    testWidgets('toggles the Use SSL/TLS switch', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      final tile = find.byType(SwitchListTile);
      expect(tester.widget<SwitchListTile>(tile).value, isTrue);

      await tester.tap(tile);
      await tester.pump();

      expect(tester.widget<SwitchListTile>(tile).value, isFalse);
    });

    testWidgets('enables save only when a valid change exists', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // No edits yet -> Save disabled.
      expect(_saveButton(tester).onPressed, isNull);

      // Typing a new host enables Save.
      await tester.enterText(find.byType(TextField).at(2), 'smtp-new.example.com');
      await tester.pump();
      expect(_saveButton(tester).onPressed, isNotNull);

      // Reverting to the original value disables Save again.
      await tester.enterText(find.byType(TextField).at(2), 'smtp.example.com');
      await tester.pump();
      expect(_saveButton(tester).onPressed, isNull);
    });

    testWidgets('keeps a typed value across a rebuild from another field', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'edited@example.com');
      await tester.pump();

      // Toggling the switch triggers a full rebuild.
      await tester.tap(find.byType(SwitchListTile));
      await tester.pump();

      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        'edited@example.com',
      );
    });

    testWidgets('sends the edited values to the backend on save', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'new@example.com');
      await tester.enterText(find.byType(TextField).at(2), 'relay.example.com');
      await tester.enterText(find.byType(TextField).at(3), '465');
      await tester.tap(find.byType(SwitchListTile));
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(controller.lastRequest!.emailFromAddress, 'new@example.com');
      expect(controller.lastRequest!.emailHost, 'relay.example.com');
      expect(controller.lastRequest!.emailPort, 465);
      expect(controller.lastRequest!.emailUseSsl, isFalse);
      // Untouched secrets stay omitted so the backend keeps them.
      expect(controller.lastRequest!.emailPassword, isNull);
      expect(controller.lastRequest!.razorpayKeySecret, isNull);
    });

    testWidgets('sends newly entered secrets with the save', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(5), 'new-smtp-secret');
      await tester.enterText(find.byType(TextField).at(8), 'new-key-secret');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(controller.lastRequest!.emailPassword, 'new-smtp-secret');
      expect(controller.lastRequest!.razorpayKeySecret, 'new-key-secret');
      expect(controller.lastRequest!.razorpayWebhookSecret, isNull);
      expect(controller.lastRequest!.googleMapsApiKey, isNull);
    });

    testWidgets('shows both Google Maps keys under one section', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // One Google Maps section card with BOTH keys (labels as requested).
      expect(find.text('Google Maps'), findsOneWidget);
      expect(find.text('Google Maps Server-Side Key'), findsOneWidget);
      expect(find.text('Google Maps Web Client Key'), findsOneWidget);
      expect(
        find.text(
          'Used by backend/server integrations. Write-only — leave blank to '
          'keep the existing key.',
        ),
        findsOneWidget,
      );
      expect(
        find.text(
          'Used by the web client/browser for Google Maps. The web app '
          'fetches this key at runtime — changing it takes effect after a '
          'client refresh, no rebuild needed.',
        ),
        findsOneWidget,
      );

      // The server-side key is write-only (blank even when configured), while
      // the web client key is client-visible and pre-filled from the server.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(10)).controller!.text,
        isEmpty,
      );
      expect(
        tester
            .widget<TextField>(find.byType(TextField).at(11))
            .controller!
            .text,
        'AIza-web-client-key-123',
      );
    });

    testWidgets('saves an edited Google Maps web client key', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // The web client key is client-visible, so typing a replacement counts
      // as a change and is sent with the save.
      await tester.enterText(
        find.byType(TextField).at(11),
        'AIza-new-web-client-key',
      );
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(controller.lastRequest!.googleMapsWebClientKey, 'AIza-new-web-client-key');
      // Untouched secrets stay omitted.
      expect(controller.lastRequest!.googleMapsApiKey, isNull);
      expect(controller.lastRequest!.emailPassword, isNull);
    });

    testWidgets('never reveals the stored secrets in the UI', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // Every secret is configured server-side but the backend never returns
      // the value, so each field is always blank...
      expect(
        tester.widget<TextField>(find.byType(TextField).at(5)).controller!.text,
        isEmpty,
      );
      expect(
        tester.widget<TextField>(find.byType(TextField).at(8)).controller!.text,
        isEmpty,
      );
      expect(
        tester.widget<TextField>(find.byType(TextField).at(9)).controller!.text,
        isEmpty,
      );
      expect(
        tester.widget<TextField>(find.byType(TextField).at(10)).controller!.text,
        isEmpty,
      );
      // ...and the masked placeholders are what the admin sees instead.
      expect(find.text('••••••••••••'), findsNWidgets(4));
    });

    testWidgets('leaving the secrets blank preserves the stored secrets', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // Change a visible field only; leave every secret untouched.
      await tester.enterText(find.byType(TextField).at(0), 'edited@example.com');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      // Omitted secrets tell the backend to keep the existing ones.
      expect(controller.lastRequest!.emailFromAddress, 'edited@example.com');
      expect(controller.lastRequest!.emailPassword, isNull);
      expect(controller.lastRequest!.razorpayKeySecret, isNull);
      expect(controller.lastRequest!.razorpayWebhookSecret, isNull);
      expect(controller.lastRequest!.googleMapsApiKey, isNull);
    });

    testWidgets('clearing a field sends an empty string to clear the override', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // The Razorpay Key ID came from the server; erasing it clears the
      // database override so the environment fallback applies again.
      await tester.enterText(find.byType(TextField).at(7), '');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.lastRequest!.razorpayKeyId, isEmpty);
      // After the successful save the form re-hydrates with the cleared value.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(7)).controller!.text,
        isEmpty,
      );
    });

    testWidgets('blocks save until the SMTP port is valid', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      // Out of range -> error text and Save disabled.
      await tester.enterText(find.byType(TextField).at(3), '70000');
      await tester.pump();
      expect(
        find.text('Enter a valid port between 1 and 65535.'),
        findsOneWidget,
      );
      expect(_saveButton(tester).onPressed, isNull);

      // Empty -> different message and still disabled.
      await tester.enterText(find.byType(TextField).at(3), '');
      await tester.pump();
      expect(find.text('Enter a port between 1 and 65535.'), findsOneWidget);
      expect(_saveButton(tester).onPressed, isNull);

      // Valid port -> error clears and Save enables.
      await tester.enterText(find.byType(TextField).at(3), '465');
      await tester.pump();
      expect(find.text('Enter a valid port between 1 and 65535.'), findsNothing);
      expect(find.text('Enter a port between 1 and 65535.'), findsNothing);
      expect(_saveButton(tester).onPressed, isNotNull);
    });

    testWidgets('successful save updates the form from the response', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(
          configuration: _configuration(emailFromAddress: 'old@example.com'),
        ),
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'saved@example.com');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      // The form re-hydrates from the PUT response and every secret clears.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        'saved@example.com',
      );
      expect(
        tester.widget<TextField>(find.byType(TextField).at(5)).controller!.text,
        isEmpty,
      );
      // The form now matches the server state, so Save disables again.
      expect(_saveButton(tester).onPressed, isNull);
      expect(find.text('Integration settings saved.'), findsOneWidget);
    });

    testWidgets('failed save keeps the edited values and shows the error', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
        failSave: true,
      );
      await _pump(tester, controller);

      await tester.enterText(find.byType(TextField).at(0), 'keep@example.com');
      await tester.pump();

      await tester.tap(find.text('Save settings'));
      await tester.pumpAndSettle();

      expect(controller.saveCount, 1);
      expect(
        find.text('EmailFromAddress must be a valid email address.'),
        findsOneWidget,
      );
      // The user's edits are NOT silently reset on failure.
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).controller!.text,
        'keep@example.com',
      );
    });

    testWidgets('sends a test email through the configured relay', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller);

      await tester.tap(find.text('Send test email'));
      await tester.pumpAndSettle();

      expect(controller.testCount, 1);
      expect(find.text('Test email sent successfully.'), findsOneWidget);
    });

    testWidgets('owner can edit the configuration', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller, roles: const ['OWNER']);

      expect(find.text('Save settings'), findsOneWidget);
      expect(find.text('Send test email'), findsOneWidget);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isTrue,
      );
    });

    testWidgets('system admin can edit the configuration', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller, roles: const ['SYSTEM_ADMIN']);

      expect(find.text('Save settings'), findsOneWidget);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isTrue,
      );
    });

    testWidgets('blocks users without read permission', (tester) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(tester, controller, permissions: const []);

      expect(find.text('Access denied'), findsOneWidget);
      expect(find.text('Save settings'), findsNothing);
    });

    testWidgets('read-only users see disabled fields and no save button', (
      tester,
    ) async {
      final controller = _SeededIntegrationsController(
        IntegrationsState(configuration: _configuration()),
      );
      await _pump(
        tester,
        controller,
        permissions: const [kIntegrationsReadPermission],
      );

      expect(find.text('Read-only view'), findsOneWidget);
      expect(find.text('Save settings'), findsNothing);
      expect(find.text('Send test email'), findsNothing);
      expect(
        tester.widget<TextField>(find.byType(TextField).at(0)).enabled,
        isFalse,
      );
      expect(
        tester.widget<TextField>(find.byType(TextField).at(5)).enabled,
        isFalse,
      );
      expect(
        tester.widget<SwitchListTile>(find.byType(SwitchListTile)).onChanged,
        isNull,
      );
    });
  });
}

/// The integrations form is much taller than the OTP one (three sections with
/// 12 fields), so a tall surface keeps every child of the [ListView] mounted
/// and lets tests reach the bottom buttons and the Google Maps card directly.
Future<void> _pump(
  WidgetTester tester,
  _SeededIntegrationsController controller, {
  List<String> permissions = const [
    kIntegrationsReadPermission,
    kIntegrationsManagePermission,
  ],
  List<String> roles = const ['OWNER'],
}) async {
  await tester.binding.setSurfaceSize(const Size(900, 3000));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      key: UniqueKey(),
      overrides: [
        integrationsControllerProvider.overrideWith(() => controller),
        sessionControllerProvider.overrideWith(
          () => _SeededSessionController(permissions: permissions, roles: roles),
        ),
      ],
      child: MaterialApp(
        theme: ThemeData(useMaterial3: true),
        home: const IntegrationConfigurationScreen(),
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

class _SeededIntegrationsController extends IntegrationsController {
  _SeededIntegrationsController(this.initialState, {this.failSave = false});

  final IntegrationsState initialState;
  final bool failSave;

  int loadCount = 0;
  int saveCount = 0;
  int testCount = 0;
  UpdateIntegrationConfigurationRequest? lastRequest;

  @override
  IntegrationsState build() => initialState;

  @override
  Future<void> load() async {
    loadCount++;
  }

  @override
  Future<bool> save(UpdateIntegrationConfigurationRequest request) async {
    saveCount++;
    lastRequest = request;
    if (failSave) {
      state = state.copyWith(
        isSaving: false,
        errorMessage: 'EmailFromAddress must be a valid email address.',
        fieldErrors: const {
          'emailFromAddress': 'EmailFromAddress must be a valid email address.',
        },
      );
      return false;
    }

    // The backend merges the request into the stored configuration and returns
    // the effective result, which the screen re-hydrates from.
    state = state.copyWith(
      configuration: _merged(initialState.configuration!, request),
      isSaving: false,
      savedMessage: 'Integration settings saved.',
    );
    return true;
  }

  @override
  Future<void> test() async {
    testCount++;
    state = state.copyWith(
      isTesting: false,
      testMessage: 'Test email sent successfully.',
    );
  }
}

/// Applies a [request] over [current] the way the backend PUT does: a null
/// request field keeps the current value; an empty request string clears it.
IntegrationConfiguration _merged(
  IntegrationConfiguration current,
  UpdateIntegrationConfigurationRequest request,
) {
  String? pick(String? next, String? original) {
    if (next == null) return original;
    final trimmed = next.trim();
    return trimmed.isEmpty ? null : trimmed;
  }

  return IntegrationConfiguration(
    emailFromAddress: pick(request.emailFromAddress, current.emailFromAddress),
    emailFromName: pick(request.emailFromName, current.emailFromName),
    emailHost: pick(request.emailHost, current.emailHost),
    emailPort: request.emailPort ?? current.emailPort,
    emailUserName: pick(request.emailUserName, current.emailUserName),
    emailUseSsl: request.emailUseSsl ?? current.emailUseSsl,
    emailPasswordConfigured: current.emailPasswordConfigured,
    emailIsConfigured: current.emailIsConfigured,
    inviteUrlBase: pick(request.inviteUrlBase, current.inviteUrlBase),
    razorpayKeyId: pick(request.razorpayKeyId, current.razorpayKeyId),
    razorpayKeySecretConfigured: current.razorpayKeySecretConfigured,
    razorpayWebhookSecretConfigured: current.razorpayWebhookSecretConfigured,
    razorpayIsConfigured: current.razorpayIsConfigured,
    googleMapsBaseUrl: pick(request.googleMapsBaseUrl, current.googleMapsBaseUrl),
    googleMapsApiKeyConfigured: current.googleMapsApiKeyConfigured,
    googleMapsIsConfigured: current.googleMapsIsConfigured,
    googleMapsWebClientKey: pick(
      request.googleMapsWebClientKey,
      current.googleMapsWebClientKey,
    ),
  );
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({required this.permissions, required this.roles});

  final List<String> permissions;
  final List<String> roles;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'integrations-user-1',
        displayName: 'Setup Manager',
        email: null,
        mobile: '9999999999',
        roles: roles,
        permissions: permissions,
        branchIds: const [7],
      ),
      accessToken: 'integrations-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

IntegrationConfiguration _configuration({
  String? emailFromAddress = 'no-reply@example.com',
  String? emailFromName = 'DoodhDirect',
  String? emailHost = 'smtp.example.com',
  int emailPort = 587,
  String? emailUserName = 'smtp-user',
  bool emailUseSsl = true,
  bool emailPasswordConfigured = true,
  bool emailIsConfigured = true,
  String? inviteUrlBase = 'https://app.example.com',
  String? razorpayKeyId = 'rzp_test_1234567890',
  bool razorpayKeySecretConfigured = true,
  bool razorpayWebhookSecretConfigured = true,
  bool razorpayIsConfigured = true,
  String? googleMapsBaseUrl = 'https://maps.googleapis.com/maps/api',
  bool googleMapsApiKeyConfigured = true,
  bool googleMapsIsConfigured = true,
  String? googleMapsWebClientKey = 'AIza-web-client-key-123',
}) => IntegrationConfiguration(
  emailFromAddress: emailFromAddress,
  emailFromName: emailFromName,
  emailHost: emailHost,
  emailPort: emailPort,
  emailUserName: emailUserName,
  emailUseSsl: emailUseSsl,
  emailPasswordConfigured: emailPasswordConfigured,
  emailIsConfigured: emailIsConfigured,
  inviteUrlBase: inviteUrlBase,
  razorpayKeyId: razorpayKeyId,
  razorpayKeySecretConfigured: razorpayKeySecretConfigured,
  razorpayWebhookSecretConfigured: razorpayWebhookSecretConfigured,
  razorpayIsConfigured: razorpayIsConfigured,
  googleMapsBaseUrl: googleMapsBaseUrl,
  googleMapsApiKeyConfigured: googleMapsApiKeyConfigured,
  googleMapsIsConfigured: googleMapsIsConfigured,
  googleMapsWebClientKey: googleMapsWebClientKey,
);
