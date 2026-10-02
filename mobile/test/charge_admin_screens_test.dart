import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:doodh_direct_mobile/core/widgets/state_panel.dart';
import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:doodh_direct_mobile/features/auth/session_state.dart';
import 'package:doodh_direct_mobile/features/setup/charge_controller.dart';
import 'package:doodh_direct_mobile/features/setup/charge_models.dart';
import 'package:doodh_direct_mobile/features/setup/charge_repository.dart';
import 'package:doodh_direct_mobile/features/setup/charge_screens.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeChargeRepository implements ChargeRepository {
  _FakeChargeRepository({this.charges, this.exception});

  List<Charge>? charges;
  ApiException? exception;
  final List<CreateChargeRequest> createCalls = [];
  final List<(String, UpdateChargeRequest)> updateCalls = [];
  final List<(String, bool)> setActiveCalls = [];
  final List<(String, bool)> setApplicabilityCalls = [];
  final List<String> deleteCalls = [];

  ApiException? nextException;

  void _throwIfSet() {
    final error = nextException ?? exception;
    if (error != null) {
      nextException = null;
      throw error;
    }
  }

  Charge _charge({
    String publicId = 'c-1',
    String chargeType = 'GST',
    String chargeCode = 'GST-5',
    String? description = 'GST five percent',
    double percentage = 5,
    bool isActive = true,
    bool applicableOnAll = true,
    bool isUsed = false,
  }) => Charge(
    publicId: publicId,
    chargeType: chargeType,
    chargeCode: chargeCode,
    description: description,
    percentage: percentage,
    isActive: isActive,
    applicableOnAll: applicableOnAll,
    isUsed: isUsed,
    createdAt: DateTime(2026, 9, 28),
    updatedAt: DateTime(2026, 9, 28),
  );

  @override
  Future<List<Charge>> list(String accessToken) async {
    _throwIfSet();
    return charges ??= [_charge()];
  }

  @override
  Future<Charge> get(String accessToken, String publicId) async {
    _throwIfSet();
    return _charge(publicId: publicId);
  }

  @override
  Future<Charge> create(String accessToken, CreateChargeRequest request) async {
    _throwIfSet();
    createCalls.add(request);
    return _charge(
      publicId: 'c-new',
      chargeType: request.chargeType,
      chargeCode: request.chargeCode,
      description: request.description,
      percentage: request.percentage,
    );
  }

  @override
  Future<Charge> update(
    String accessToken,
    String publicId,
    UpdateChargeRequest request,
  ) async {
    _throwIfSet();
    updateCalls.add((publicId, request));
    return _charge(
      publicId: publicId,
      chargeType: request.chargeType,
      description: request.description,
      percentage: request.percentage,
    );
  }

  @override
  Future<Charge> setActive(
    String accessToken,
    String publicId,
    bool isActive,
  ) async {
    _throwIfSet();
    setActiveCalls.add((publicId, isActive));
    charges = [
      for (final item in charges ?? <Charge>[])
        if (item.publicId == publicId)
          Charge(
            publicId: item.publicId,
            chargeType: item.chargeType,
            chargeCode: item.chargeCode,
            description: item.description,
            percentage: item.percentage,
            isActive: isActive,
            applicableOnAll: item.applicableOnAll,
            isUsed: item.isUsed,
            createdAt: item.createdAt,
            updatedAt: item.updatedAt,
          )
        else
          item,
    ];
    return _charge(publicId: publicId, isActive: isActive);
  }

  @override
  Future<Charge> setApplicability(
    String accessToken,
    String publicId,
    bool applicableOnAll,
  ) async {
    // Record the attempt first so rejection tests can assert the call was
    // made (same convention as delete).
    setApplicabilityCalls.add((publicId, applicableOnAll));
    _throwIfSet();
    charges = [
      for (final item in charges ?? <Charge>[])
        if (item.publicId == publicId)
          Charge(
            publicId: item.publicId,
            chargeType: item.chargeType,
            chargeCode: item.chargeCode,
            description: item.description,
            percentage: item.percentage,
            isActive: item.isActive,
            applicableOnAll: applicableOnAll,
            isUsed: item.isUsed,
            createdAt: item.createdAt,
            updatedAt: item.updatedAt,
          )
        else
          item,
    ];
    return _charge(publicId: publicId, applicableOnAll: applicableOnAll);
  }

  @override
  Future<void> delete(String accessToken, String publicId) async {
    deleteCalls.add(publicId);
    _throwIfSet();
    charges = [
      for (final item in charges ?? <Charge>[])
        if (item.publicId != publicId) item,
    ];
  }
}

class _SeededSessionController extends SessionController {
  _SeededSessionController({required this.permissions});

  final List<String> permissions;

  @override
  SessionState build() => SessionState.authenticated(
    AuthSession(
      user: AuthUser(
        publicUserId: 'charge-admin-user',
        displayName: 'System Administrator',
        email: null,
        mobile: '9999999999',
        roles: const ['SYSTEM_ADMIN'],
        permissions: permissions,
        branchIds: const [],
      ),
      accessToken: 'charge-admin-token',
      refreshToken: 'refresh-token',
      accessTokenExpiresAtUtc: DateTime.utc(2099),
      refreshTokenExpiresAtUtc: DateTime.utc(2099, 2),
    ),
  );
}

Future<ProviderContainer> _pumpList(
  WidgetTester tester, {
  required _FakeChargeRepository repository,
  List<String> permissions = const [
    'SETUP.TAX_CHARGES.READ',
    'SETUP.TAX_CHARGES.MANAGE',
  ],
}) async {
  final container = ProviderContainer(
    overrides: [
      chargeRepositoryProvider.overrideWithValue(repository),
      sessionControllerProvider.overrideWith(
        () => _SeededSessionController(permissions: permissions),
      ),
    ],
  );
  addTearDown(container.dispose);
  // Wide surface: the 980px management table would otherwise push the
  // trailing toggles/actions off the default 800px test viewport.
  await tester.binding.setSurfaceSize(const Size(1280, 900));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: ChargeListScreen()),
    ),
  );
  await tester.pump();
  return container;
}

/// Charge seed for list tests: public ids auto-increment (c-1, c-2, …) in
/// list order so toggles can assert against the expected row.
class _ChargeSeed {
  const _ChargeSeed(this.chargeCode, {this.applicableOnAll = true});

  final String chargeCode;
  final String chargeType = 'GST';
  final bool isActive = true;
  final bool applicableOnAll;
}

/// The bare toggle switch inside its Tooltip wrapper (byTooltip finds the
/// Tooltip, not the Switch itself).
Finder _switchInTooltip(String message, {int index = 0}) => find
    .descendant(
      of: find.byTooltip(message),
      matching: find.byType(Switch),
    )
    .at(index);

/// The in-grid cell editor by its hint text (row fields are TextFields now,
/// so `find.text` cannot see their values).
Finder _cellField(String hint) => find.byWidgetPredicate(
  (widget) =>
      widget is TextField && widget.decoration?.hintText == hint,
);

String _cellValue(WidgetTester tester, String hint) =>
    tester.widget<TextField>(_cellField(hint)).controller!.text;

/// An active, item-level (Applicable on All = false) charge for toggle tests.
Charge _itemCharge(String chargeCode, {String publicId = 'c-1'}) => Charge(
  publicId: publicId,
  chargeType: 'GST',
  chargeCode: chargeCode,
  description: null,
  percentage: 12,
  isActive: true,
  applicableOnAll: false,
  isUsed: false,
  createdAt: DateTime(2026, 9, 28),
  updatedAt: DateTime(2026, 9, 28),
);

void main() {
  testWidgets('list loads and renders charge rows', (tester) async {
    final repo = _FakeChargeRepository();
    await _pumpList(tester, repository: repo);

    expect(find.text('GST-5'), findsOneWidget);
    // Type edits inline now: the value lives in the row's cell editor.
    expect(_cellValue(tester, 'Type e.g. GST'), 'GST');
    // Status is a bare toggle now (no Active text): the tooltip carries it.
    expect(find.byTooltip('Deactivate GST-5'), findsOneWidget);
    expect(_cellValue(tester, 'Description'), 'GST five percent');
    // No Edit button — editing happens directly in the row.
    expect(find.byTooltip('Edit GST-5'), findsNothing);
  });

  testWidgets('empty state invites creating a charge', (tester) async {
    final repo = _FakeChargeRepository(charges: []);
    await _pumpList(tester, repository: repo);

    expect(find.text('No charges configured'), findsOneWidget);
    expect(find.text('New charge'), findsWidgets);
  });

  testWidgets('error state offers retry and retry reloads', (tester) async {
    final repo = _FakeChargeRepository(
      exception: const ApiException(500, 'SERVER_DOWN', 'Server error'),
    );
    await _pumpList(tester, repository: repo);

    expect(find.byType(ErrorStatePanel), findsOneWidget);
    repo.exception = null;
    await tester.tap(find.text('Retry'));
    await tester.pump();
    expect(find.text('GST-5'), findsOneWidget);
  });

  testWidgets('create charge posts the form values and refreshes', (
    tester,
  ) async {
    final repo = _FakeChargeRepository();
    final container = await _pumpList(tester, repository: repo);
    final controller = container.read(chargeControllerProvider.notifier);

    final created = await controller.create(
      const CreateChargeRequest(
        chargeType: 'Service',
        chargeCode: 'SC',
        description: 'Service charge',
        percentage: 5,
      ),
    );
    await tester.pump();

    expect(created?.chargeCode, 'SC');
    expect(repo.createCalls.single.chargeCode, 'SC');
    expect(find.text('Charge SC created.'), findsOneWidget);
  });

  testWidgets('edit charge posts updates for the publicId', (tester) async {
    final repo = _FakeChargeRepository();
    final container = await _pumpList(tester, repository: repo);
    final controller = container.read(chargeControllerProvider.notifier);

    final updated = await controller.update(
      'c-1',
      const UpdateChargeRequest(
        chargeType: 'GST',
        description: 'Updated GST',
        percentage: 6.5,
      ),
    );

    expect(updated?.percentage, 6.5);
    expect(repo.updateCalls.single.$1, 'c-1');
  });

  testWidgets('active and inactive chips reflect the server flag', (
    tester,
  ) async {
    final repo = _FakeChargeRepository(
      charges: [
        Charge(
          publicId: 'c-1',
          chargeType: 'GST',
          chargeCode: 'GST-ACTIVE',
          description: null,
          percentage: 5,
          isActive: true,
          isUsed: false,
          createdAt: DateTime(2026, 9, 28),
          updatedAt: DateTime(2026, 9, 28),
        ),
        Charge(
          publicId: 'c-2',
          chargeType: 'Service',
          chargeCode: 'SC-INACTIVE',
          description: null,
          percentage: 5,
          isActive: false,
          isUsed: false,
          createdAt: DateTime(2026, 9, 28),
          updatedAt: DateTime(2026, 9, 28),
        ),
      ],
    );
    await _pumpList(tester, repository: repo);

    // Status is a bare toggle per row: tooltip only, no status text and no
    // Deactivate/Activate buttons anymore.
    expect(find.byTooltip('Deactivate GST-ACTIVE'), findsOneWidget);
    expect(find.byTooltip('Activate SC-INACTIVE'), findsOneWidget);
    expect(find.text('Active'), findsNothing);
    expect(find.text('Inactive'), findsNothing);
    expect(find.text('Deactivate'), findsNothing);
    expect(find.text('Activate'), findsNothing);
  });

  testWidgets('activate calls the dedicated endpoint', (tester) async {
    final repo = _FakeChargeRepository(
      charges: [
        Charge(
          publicId: 'c-2',
          chargeType: 'Service',
          chargeCode: 'SC',
          description: null,
          percentage: 5,
          isActive: false,
          isUsed: false,
          createdAt: DateTime(2026, 9, 28),
          updatedAt: DateTime(2026, 9, 28),
        ),
      ],
    );
    await _pumpList(tester, repository: repo);

    await tester.tap(find.byTooltip('Activate SC'));
    await tester.pumpAndSettle();

    expect(repo.setActiveCalls, [('c-2', true)]);
    // The row's status switch flips ON after the dedicated endpoint responds.
    expect(find.byTooltip('Deactivate SC'), findsOneWidget);
    expect(find.byTooltip('Activate SC'), findsNothing);
  });

  testWidgets('deactivate calls the dedicated endpoint', (tester) async {
    final repo = _FakeChargeRepository();
    await _pumpList(tester, repository: repo);

    await tester.tap(find.byTooltip('Deactivate GST-5'));
    await tester.pump();

    expect(repo.setActiveCalls, [('c-1', false)]);
  });

  testWidgets('delete requires confirmation before calling the API', (
    tester,
  ) async {
    final repo = _FakeChargeRepository();
    await _pumpList(tester, repository: repo);

    await tester.tap(find.byTooltip('Delete GST-5'));
    await tester.pump();
    expect(repo.deleteCalls, isEmpty);

    await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
    await tester.pump();

    expect(repo.deleteCalls, ['c-1']);
  });

  testWidgets('used-charge deletion surfaces the server business error', (
    tester,
  ) async {
    final repo = _FakeChargeRepository();
    await _pumpList(tester, repository: repo);
    // The delete call alone fails with the backend business rule.
    repo.nextException = const ApiException(
      422,
      'BUSINESS_RULE',
      "Charge 'GST-5' has been applied to orders and can only be deactivated, not deleted.",
    );

    await tester.tap(find.byTooltip('Delete GST-5'));
    await tester.pump();
    await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
    await tester.pumpAndSettle();
    await tester.pumpAndSettle();

    expect(repo.deleteCalls, ['c-1']);
    // The server business rule surfaces in both the list error banner and the
    // snackbar — the admin is told to deactivate instead of delete.
    expect(find.textContaining('only be deactivated'), findsNWidgets(2));
  });

  testWidgets('applicable-on-all toggle renders the server state', (
    tester,
  ) async {
    final seeds = [
      const _ChargeSeed('GLOBAL-5', applicableOnAll: true),
      const _ChargeSeed('ITEM-12', applicableOnAll: false),
    ];
    final repo = _FakeChargeRepository(
      charges: [
        for (var i = 0; i < seeds.length; i++)
          Charge(
            publicId: 'c-${i + 1}',
            chargeType: seeds[i].chargeType,
            chargeCode: seeds[i].chargeCode,
            description: null,
            percentage: 5,
            isActive: seeds[i].isActive,
            applicableOnAll: seeds[i].applicableOnAll,
            isUsed: false,
            createdAt: DateTime(2026, 9, 28),
            updatedAt: DateTime(2026, 9, 28),
          ),
      ],
    );
    await _pumpList(tester, repository: repo);

    // The column header carries the only 'Applicable on All' text; each row
    // shows a bare mode switch reflecting its server state.
    expect(find.text('Applicable on All'), findsOneWidget);
    final modeSwitches = tester
        .widgetList<Switch>(
          find.descendant(
            of: find.byTooltip('Applicable on All'),
            matching: find.byType(Switch),
          ),
        )
        .toList(growable: false);
    expect(modeSwitches.map((s) => s.value), [true, false]);
  });

  testWidgets('toggling applicability calls the dedicated endpoint', (
    tester,
  ) async {
    final repo = _FakeChargeRepository(charges: [_itemCharge('ITEM-12')]);
    await _pumpList(tester, repository: repo);

    // Tap the bare mode switch (tooltip finder — the switch has no text).
    final switchFinder = find.byTooltip('Applicable on All');
    expect(switchFinder, findsOneWidget);
    expect(tester.widget<Switch>(_switchInTooltip('Applicable on All')).value, isFalse);
    await tester.tap(switchFinder);
    await tester.pumpAndSettle();

    expect(repo.setApplicabilityCalls, [('c-1', true)]);
    expect(tester.widget<Switch>(_switchInTooltip('Applicable on All')).value, isTrue);
  });

  testWidgets('rejected applicability toggle keeps server state and shows '
      'the server error', (tester) async {
    final repo = _FakeChargeRepository(charges: [_itemCharge('ITEM-12')]);
    await _pumpList(tester, repository: repo);
    repo.nextException = const ApiException(
      422,
      'BUSINESS_RULE',
      'Cannot enable Applicable on All because this charge is assigned to one '
          'or more products. Remove the charge from those products first.',
    );

    await tester.tap(find.byTooltip('Applicable on All'));
    await tester.pumpAndSettle();

    expect(repo.setApplicabilityCalls, [('c-1', true)]);
    // The switch renders the unchanged server state — never a fake success —
    // and the server business rule is visible on the list.
    expect(
      tester.widget<Switch>(_switchInTooltip('Applicable on All')).value,
      isFalse,
    );
    expect(find.byType(DoodhErrorBanner), findsOneWidget);
    // The server business rule surfaces in both the list error banner and the
    // snackbar (same convention as the used-charge delete test).
    expect(
      find.textContaining('Remove the charge from those products'),
      findsNWidgets(2),
    );
  });

  testWidgets('read-only user cannot toggle applicability', (tester) async {
    final repo = _FakeChargeRepository(charges: [_itemCharge('ITEM-12')]);
    await _pumpList(
      tester,
      repository: repo,
      permissions: const ['SETUP.TAX_CHARGES.READ'],
    );

    final switchWidget = tester.widget<Switch>(
      _switchInTooltip('Applicable on All'),
    );
    expect(switchWidget.onChanged, isNull);
    final statusWidget = tester.widget<Switch>(
      _switchInTooltip('Deactivate ITEM-12'),
    );
    expect(statusWidget.onChanged, isNull);
  });

  testWidgets('read-only user sees no manage actions', (tester) async {
    final repo = _FakeChargeRepository();
    await _pumpList(
      tester,
      repository: repo,
      permissions: const ['SETUP.TAX_CHARGES.READ'],
    );

    expect(find.text('GST-5'), findsOneWidget);
    expect(find.text('Deactivate'), findsNothing);
    expect(find.text('Activate'), findsNothing);
    expect(find.byTooltip('Delete GST-5'), findsNothing);
    expect(find.byTooltip('Edit GST-5'), findsNothing);
    expect(find.byTooltip('Save GST-5'), findsNothing);
  });

  testWidgets('editing the percentage inline saves through update', (
    tester,
  ) async {
    final repo = _FakeChargeRepository();
    await _pumpList(tester, repository: repo);

    // No save action while the row is pristine.
    expect(find.byTooltip('Save GST-5'), findsNothing);
    await tester.enterText(_cellField('%'), '6.5');
    await tester.pump();
    expect(find.byTooltip('Save GST-5'), findsOneWidget);

    await tester.tap(find.byTooltip('Save GST-5'));
    await tester.pumpAndSettle();

    expect(repo.updateCalls.length, 1);
    expect(repo.updateCalls.single.$1, 'c-1');
    expect(repo.updateCalls.single.$2.percentage, 6.5);
    // Type/description ride along unchanged.
    expect(repo.updateCalls.single.$2.chargeType, 'GST');
  });

  testWidgets('discard restores the server values', (tester) async {
    final repo = _FakeChargeRepository();
    await _pumpList(tester, repository: repo);

    await tester.enterText(_cellField('%'), '9');
    await tester.pump();
    expect(find.byTooltip('Save GST-5'), findsOneWidget);

    await tester.tap(find.byTooltip('Discard changes'));
    await tester.pump();

    expect(_cellValue(tester, '%'), '5');
    expect(find.byTooltip('Save GST-5'), findsNothing);
    expect(repo.updateCalls, isEmpty);
  });

  testWidgets('New charge adds a draft row that creates on save', (
    tester,
  ) async {
    final repo = _FakeChargeRepository();
    await _pumpList(tester, repository: repo);

    Finder draftField(String hint) => find.descendant(
      of: find.byKey(const ValueKey('charge-draft')),
      matching: _cellField(hint),
    );

    await tester.tap(find.widgetWithText(FilledButton, 'New charge'));
    await tester.pumpAndSettle();
    // Blank draft editors sit above the table rows.
    expect(draftField('Code e.g. GST-5'), findsOneWidget);

    // Save stays disabled until the draft validates.
    Finder saveButton() => find.byWidgetPredicate(
      (widget) =>
          widget is IconButton && widget.tooltip == 'Save new charge',
    );
    expect(tester.widget<IconButton>(saveButton()).onPressed, isNull);
    await tester.enterText(draftField('Code e.g. GST-5'), 'SC');
    await tester.enterText(draftField('Type e.g. GST'), 'Service');
    await tester.enterText(draftField('%'), '5');
    await tester.pump();

    await tester.tap(find.byTooltip('Save new charge'));
    await tester.pumpAndSettle();

    expect(repo.createCalls.length, 1);
    expect(repo.createCalls.single.chargeCode, 'SC');
    expect(repo.createCalls.single.chargeType, 'Service');
    expect(repo.createCalls.single.percentage, 5);
    // The draft closes on success.
    expect(_cellField('Code e.g. GST-5'), findsNothing);
  });

  testWidgets('used charge locks code and type but edits the rest', (
    tester,
  ) async {
    final used = Charge(
      publicId: 'c-9',
      chargeType: 'GST',
      chargeCode: 'GST-USED',
      description: 'old',
      percentage: 5,
      isActive: true,
      applicableOnAll: true,
      isUsed: true,
      createdAt: DateTime(2026, 9, 28),
      updatedAt: DateTime(2026, 9, 28),
    );
    final repo = _FakeChargeRepository(charges: [used]);
    await _pumpList(tester, repository: repo);

    // Locked type renders as a read-only pill, not an editor.
    expect(find.text('GST'), findsOneWidget);
    expect(_cellField('Type e.g. GST'), findsNothing);
    await tester.enterText(_cellField('Description'), 'new');
    await tester.pump();
    await tester.tap(find.byTooltip('Save GST-USED'));
    await tester.pumpAndSettle();

    expect(repo.updateCalls.single.$2.description, 'new');
  });

  testWidgets('server validation errors are shown with the field name', (
    tester,
  ) async {
    final repo = _FakeChargeRepository();
    final container = await _pumpList(tester, repository: repo);
    final controller = container.read(chargeControllerProvider.notifier);
    // Simulate the backend validation failure for this create only.
    repo.nextException = const ApiException(
      400,
      'VALIDATION_ERROR',
      "A charge with code 'DUP' already exists.",
      field: 'ChargeCode',
    );

    final result = await controller.create(
      const CreateChargeRequest(
        chargeType: 'GST',
        chargeCode: 'DUP',
        description: null,
        percentage: 5,
      ),
    );
    await tester.pump();

    expect(result, isNull);
    final state = container.read(chargeControllerProvider);
    expect(state.fieldErrors['chargeCode'], contains('already exists'));
    expect(find.byType(ChargeListScreen), findsOneWidget);
  });
}
