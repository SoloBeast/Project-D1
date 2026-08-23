import 'package:doodh_direct_mobile/features/auth/auth_repository.dart';
import 'package:doodh_direct_mobile/features/auth/session_controller.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'api_client.dart';

ApiClient authenticatedApiClient(Ref ref) => ApiClient(
  baseUrl: apiBaseUrl,
  refreshAccessToken: () =>
      ref.read(sessionControllerProvider.notifier).refreshAccessToken(),
);
