import 'package:doodh_direct_mobile/core/widgets/doodh_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  final clipboardLog = <MethodCall>[];

  setUp(() {
    clipboardLog.clear();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, (call) async {
          clipboardLog.add(call);
          return null;
        });
  });

  tearDown(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, null);
  });

  String? copiedText() {
    for (final call in clipboardLog) {
      if (call.method == 'Clipboard.setData') {
        return (call.arguments as Map)['text'] as String?;
      }
    }
    return null;
  }

  testWidgets('copy button copies the exact reference and confirms', (
    tester,
  ) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: CopyReferenceButton(
            value: 'ORD/DB0001/26-27/000123',
            message: 'Order number copied',
          ),
        ),
      ),
    );

    // Accessible action with a tooltip.
    expect(find.byTooltip('Copy'), findsOneWidget);

    await tester.tap(find.byTooltip('Copy'));
    await tester.pump();

    expect(copiedText(), 'ORD/DB0001/26-27/000123');
    expect(find.text('Order number copied'), findsOneWidget);
  });

  testWidgets('subscription reference copies verbatim, never a rewritten id', (
    tester,
  ) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: CopyReferenceButton(
            value: 'SUB/DB0001/26-27/000003',
            message: 'Subscription reference copied',
          ),
        ),
      ),
    );

    await tester.tap(find.byTooltip('Copy'));
    await tester.pump();

    expect(copiedText(), 'SUB/DB0001/26-27/000003');
    expect(find.text('Subscription reference copied'), findsOneWidget);
  });

  testWidgets('copyReference helper copies and shows the given message', (
    tester,
  ) async {
    late BuildContext captured;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) {
              captured = context;
              return const SizedBox.shrink();
            },
          ),
        ),
      ),
    );

    await copyReference(
      captured,
      value: 'SUB/DB0001/26-27/000003',
      message: 'Subscription reference copied',
    );
    await tester.pump();

    expect(copiedText(), 'SUB/DB0001/26-27/000003');
    expect(find.text('Subscription reference copied'), findsOneWidget);
  });
}
