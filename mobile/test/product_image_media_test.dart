import 'dart:convert';
import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';
import 'package:doodh_direct_mobile/core/network/media_url.dart';
import 'package:doodh_direct_mobile/core/widgets/product_widgets.dart';
import 'package:doodh_direct_mobile/features/catalogue/catalogue_repository.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

/// Backend `ProductImageResult` payload — exactly what
/// `PUT/DELETE /api/v1/admin/products/{id}/image` answers with. It must parse
/// as `ProductImageResult`, never as `CatalogueProduct` (a `TypeError` there
/// used to surface as a bogus connectivity error).
Map<String, dynamic> productImageResultJson() => {
      'productId': '7d886065-72d8-4c2d-bf20-addb6e3e5ed2',
      'imageId': '3d83b42e-e07a-4bc7-bb71-20806e71c8a3',
      'fileName': '3.png',
      'contentType': 'image/png',
      'fileSize': 2032342,
      'uploadedAtUtc': '2026-09-27T19:22:29.1315418Z',
    };

CatalogueRepository repositoryWith(MockClient client) => CatalogueRepository(
      api: ApiClient(client: client, baseUrl: 'https://api.example.test'),
    );

void main() {
  group('product image operation response parsing', () {
    test(
      'upsertProductImage parses the ProductImageResult payload without throwing',
      () async {
        Object? seenMethod;
        Uri? seenUrl;
        String? seenAuth;
        final client = MockClient((request) async {
          seenMethod = request.method;
          seenUrl = request.url;
          seenAuth = request.headers['Authorization'];
          return http.Response(
            jsonEncode({
              'success': true,
              'data': productImageResultJson(),
              'errors': [],
            }),
            200,
            headers: {'content-type': 'application/json'},
          );
        });

        final result = await repositoryWith(client).upsertProductImage(
          '7d886065-72d8-4c2d-bf20-addb6e3e5ed2',
          'admin-token',
          bytes: Uint8List.fromList(const [0x89, 0x50, 0x4E, 0x47]),
          fileName: 'product-image.png',
          contentType: 'image/png',
        );

        expect(seenMethod, 'PUT');
        expect(
          seenUrl.toString(),
          'https://api.example.test/api/v1/admin/products/'
          '7d886065-72d8-4c2d-bf20-addb6e3e5ed2/image',
        );
        expect(seenAuth, 'Bearer admin-token');
        expect(result.productId, '7d886065-72d8-4c2d-bf20-addb6e3e5ed2');
        expect(result.imageId, '3d83b42e-e07a-4bc7-bb71-20806e71c8a3');
        expect(result.fileName, '3.png');
        expect(result.contentType, 'image/png');
        expect(result.fileSize, 2032342);
        expect(
          result.uploadedAtUtc,
          DateTime.utc(2026, 9, 27, 19, 22, 29).add(
            const Duration(milliseconds: 131, microseconds: 541),
          ),
        );
      },
    );

    test(
      'removeProductImage parses the ProductImageResult payload without throwing',
      () async {
        Object? seenMethod;
        Uri? seenUrl;
        final client = MockClient((request) async {
          seenMethod = request.method;
          seenUrl = request.url;
          return http.Response(
            jsonEncode({
              'success': true,
              'data': productImageResultJson(),
              'errors': [],
            }),
            200,
            headers: {'content-type': 'application/json'},
          );
        });

        final result = await repositoryWith(client).removeProductImage(
          '7d886065-72d8-4c2d-bf20-addb6e3e5ed2',
          'admin-token',
        );

        expect(seenMethod, 'DELETE');
        expect(
          seenUrl.toString(),
          'https://api.example.test/api/v1/admin/products/'
          '7d886065-72d8-4c2d-bf20-addb6e3e5ed2/image',
        );
        expect(result.contentType, 'image/png');
        expect(result.fileSize, 2032342);
      },
    );

    test('a real API error still surfaces as ApiException', () async {
      final client = MockClient((request) async => http.Response(
            jsonEncode({
              'success': false,
              'data': null,
              'message': 'Request validation failed.',
              'errors': [
                {
                  'code': 'IMAGE_INVALID',
                  'field': 'image',
                  'message': 'Only valid JPEG, PNG, or WebP images are allowed.',
                },
              ],
            }),
            422,
            headers: {'content-type': 'application/json'},
          ));

      await expectLater(
        repositoryWith(client).upsertProductImage(
          'p-1',
          'admin-token',
          bytes: Uint8List(3),
          fileName: 'payload.pdf',
          contentType: 'application/pdf',
        ),
        throwsA(
          isA<ApiException>()
              .having((error) => error.statusCode, 'statusCode', 422)
              .having((error) => error.code, 'code', 'IMAGE_INVALID')
              .having((error) => error.message, 'message', contains('JPEG')),
        ),
      );
    });
  });

  group('resolveMediaUrl', () {
    test('returns null for null, blank, and whitespace-only input', () {
      expect(resolveMediaUrl(null), isNull);
      expect(resolveMediaUrl(''), isNull);
      expect(resolveMediaUrl('   '), isNull);
    });

    test('leaves absolute URLs unchanged', () {
      expect(
        resolveMediaUrl('https://cdn.example.test/img/milk.png'),
        'https://cdn.example.test/img/milk.png',
      );
      expect(
        resolveMediaUrl(' http://img.example.test/milk.png '),
        'http://img.example.test/milk.png',
      );
    });

    test(
      'resolves a relative product image path against the configured API base URL',
      () {
        // Default compile-time base (DOOHDIRECT_API_URL): http://localhost:5209
        expect(
          resolveMediaUrl('/api/v1/products/p-1/image'),
          'http://localhost:5209/api/v1/products/p-1/image',
        );
        // Whitespace around the relative path is tolerated.
        expect(
          resolveMediaUrl('  /api/v1/products/p-1/image  '),
          'http://localhost:5209/api/v1/products/p-1/image',
        );
      },
    );
  });

  group('DoodhProductImage media URL resolution', () {
    testWidgets(
      'resolves a relative imageUrl against the API base for NetworkImage',
      (tester) async {
        await tester.pumpWidget(
          const MaterialApp(
            home: Scaffold(
              body: DoodhProductImage(imageUrl: '/api/v1/products/p-1/image'),
            ),
          ),
        );
        await tester.pump();

        final image = tester.widget<Image>(find.byType(Image).first);
        expect(image.image, isA<NetworkImage>());
        expect(
          (image.image as NetworkImage).url,
          'http://localhost:5209/api/v1/products/p-1/image',
        );
      },
    );

    testWidgets('keeps the branded fallback for a blank imageUrl', (tester) async {
      await tester.pumpWidget(
        const MaterialApp(
          home: Scaffold(body: DoodhProductImage(imageUrl: '   ')),
        ),
      );
      await tester.pump();

      expect(find.byType(Image), findsNothing);
      expect(find.text('DoodhDirect'), findsOneWidget);
    });
  });
}
