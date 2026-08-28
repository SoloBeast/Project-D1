import 'dart:typed_data';

import 'package:doodh_direct_mobile/core/network/api_client.dart';

import 'milk_test_models.dart';

class MilkTestRepository {
  MilkTestRepository({required this.api});

  final ApiClient api;

  Future<CustomerMilkTest?> getForCustomer(
    String token,
    String deliveryId,
  ) async {
    final response = await api.get(
      '/api/v1/deliveries/$deliveryId/milk-test',
      accessToken: token,
    );
    final data = response['data'];
    return data is Map<String, dynamic>
        ? CustomerMilkTest.fromJson(data)
        : null;
  }

  Future<CustomerMilkTest> request(String token, String deliveryId) async =>
      CustomerMilkTest.fromJson(
        (await api.post(
              '/api/v1/deliveries/$deliveryId/milk-test',
              accessToken: token,
            ))['data']
            as Map<String, dynamic>,
      );

  Future<StaffMilkTest?> getForStaff(String token, String deliveryId) async {
    final response = await api.get(
      '/api/v1/delivery/$deliveryId/milk-test',
      accessToken: token,
    );
    final data = response['data'];
    return data is Map<String, dynamic> ? StaffMilkTest.fromJson(data) : null;
  }

  /// Fetches the raw bytes of a protected milk test image over the
  /// authenticated channel. The browser cannot send the JWT with
  /// [Image.network], so content is loaded here and rendered from bytes.
  Future<ApiByteResponse> getImageContent(
    String token,
    String milkTestId,
    String imageId,
  ) => api.getBytes(
    '/api/v1/milk-tests/$milkTestId/images/$imageId/content',
    accessToken: token,
  );

  Future<MilkTestImage> uploadImage(
    String token,
    String milkTestId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async => MilkTestImage.fromJson(
    (await api.postMultipart(
          '/api/v1/milk-tests/$milkTestId/images',
          fieldName: 'image',
          bytes: bytes,
          fileName: fileName,
          contentType: contentType,
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  /// Staff deletes an image while the test is still Requested (editable).
  Future<StaffMilkTest> deleteImage(
    String token,
    String milkTestId,
    String imageId,
  ) async => StaffMilkTest.fromJson(
    (await api.delete(
          '/api/v1/milk-tests/$milkTestId/images/$imageId',
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  /// Staff replaces an image while the test is still Requested (editable).
  Future<MilkTestImage> replaceImageAsStaff(
    String token,
    String milkTestId,
    String imageId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async => MilkTestImage.fromJson(
    (await api.putMultipart(
          '/api/v1/milk-tests/$milkTestId/images/$imageId',
          fieldName: 'image',
          bytes: bytes,
          fileName: fileName,
          contentType: contentType,
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  /// Customer replaces an image while reviewing a completed test.
  Future<MilkTestImage> replaceImageAsCustomer(
    String token,
    String milkTestId,
    String imageId, {
    required Uint8List bytes,
    required String fileName,
    required String contentType,
  }) async => MilkTestImage.fromJson(
    (await api.postMultipart(
          '/api/v1/milk-tests/$milkTestId/images/$imageId',
          fieldName: 'image',
          bytes: bytes,
          fileName: fileName,
          contentType: contentType,
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<StaffMilkTest> complete(
    String token,
    String milkTestId, {
    required List<MilkTestParameter> parameters,
    String? remarks,
  }) async => StaffMilkTest.fromJson(
    (await api.post(
          '/api/v1/milk-tests/$milkTestId/complete',
          body: {
            'parameters': parameters
                .map((parameter) => parameter.toJson())
                .toList(growable: false),
            'remarks': remarks,
          },
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );

  Future<CustomerMilkTest> confirm(
    String token,
    String milkTestId, {
    String? remarks,
  }) => _decide(token, milkTestId, 'confirm', remarks);

  Future<CustomerMilkTest> reject(
    String token,
    String milkTestId, {
    String? remarks,
  }) => _decide(token, milkTestId, 'reject', remarks);

  Future<CustomerMilkTest> _decide(
    String token,
    String milkTestId,
    String decision,
    String? remarks,
  ) async => CustomerMilkTest.fromJson(
    (await api.post(
          '/api/v1/milk-tests/$milkTestId/$decision',
          body: {'remarks': remarks},
          accessToken: token,
        ))['data']
        as Map<String, dynamic>,
  );
}
