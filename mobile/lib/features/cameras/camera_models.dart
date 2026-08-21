enum CameraStreamProtocol {
  hls,
  webRtc,
  unknown;

  static CameraStreamProtocol fromApi(Object? value) => switch (
    value?.toString().trim().toLowerCase()
  ) {
    'hls' => CameraStreamProtocol.hls,
    'webrtc' => CameraStreamProtocol.webRtc,
    _ => CameraStreamProtocol.unknown,
  };

  String get apiValue => switch (this) {
    CameraStreamProtocol.hls => 'Hls',
    CameraStreamProtocol.webRtc => 'WebRtc',
    CameraStreamProtocol.unknown => 'Unknown',
  };

  String get label => switch (this) {
    CameraStreamProtocol.hls => 'HLS',
    CameraStreamProtocol.webRtc => 'WebRTC',
    CameraStreamProtocol.unknown => 'Unknown',
  };
}

class PublicCamera {
  const PublicCamera({
    required this.cameraId,
    required this.displayName,
    required this.displayOrder,
    required this.isAvailable,
  });

  factory PublicCamera.fromJson(Map<String, dynamic> json) => PublicCamera(
    cameraId: json['cameraId'] as String,
    displayName: json['displayName'] as String,
    displayOrder: (json['displayOrder'] as num).toInt(),
    isAvailable: json['isAvailable'] as bool,
  );

  final String cameraId;
  final String displayName;
  final int displayOrder;
  final bool isAvailable;
}

class CameraStreamDescriptor {
  const CameraStreamDescriptor({
    required this.protocol,
    required this.playbackUri,
    required this.expiresAtUtc,
    required this.isDevelopmentStream,
  });

  factory CameraStreamDescriptor.fromJson(Map<String, dynamic> json) =>
      CameraStreamDescriptor(
        protocol: CameraStreamProtocol.fromApi(json['protocol']),
        playbackUri: Uri.parse(json['playbackUri'] as String),
        expiresAtUtc: DateTime.parse(json['expiresAtUtc'] as String).toUtc(),
        isDevelopmentStream: json['isDevelopmentStream'] as bool,
      );

  final CameraStreamProtocol protocol;
  final Uri playbackUri;
  final DateTime expiresAtUtc;
  final bool isDevelopmentStream;

  bool get isExpired => isExpiredAt(DateTime.now().toUtc());

  bool isExpiredAt(DateTime nowUtc) =>
      !expiresAtUtc.isAfter(nowUtc.toUtc());
}

class PublicCameraStream {
  const PublicCameraStream({
    required this.cameraId,
    required this.displayName,
    required this.stream,
  });

  factory PublicCameraStream.fromJson(Map<String, dynamic> json) =>
      PublicCameraStream(
        cameraId: json['cameraId'] as String,
        displayName: json['displayName'] as String,
        stream: CameraStreamDescriptor.fromJson(
          json['stream'] as Map<String, dynamic>,
        ),
      );

  final String cameraId;
  final String displayName;
  final CameraStreamDescriptor stream;
}

class ManagedCamera {
  const ManagedCamera({
    required this.cameraId,
    required this.branchId,
    required this.branchName,
    required this.internalIdentifier,
    required this.displayName,
    required this.isPublic,
    required this.isActive,
    required this.displayOrder,
    required this.protocol,
    required this.providerCode,
    required this.providerStreamReference,
    required this.createdAt,
    required this.updatedAt,
  });

  factory ManagedCamera.fromJson(Map<String, dynamic> json) => ManagedCamera(
    cameraId: json['cameraId'] as String,
    branchId: (json['branchId'] as num).toInt(),
    branchName: json['branchName'] as String,
    internalIdentifier: json['internalIdentifier'] as String,
    displayName: json['displayName'] as String,
    isPublic: json['isPublic'] as bool,
    isActive: json['isActive'] as bool,
    displayOrder: (json['displayOrder'] as num).toInt(),
    protocol: CameraStreamProtocol.fromApi(json['protocol']),
    providerCode: json['providerCode'] as String,
    providerStreamReference: json['providerStreamReference'] as String,
    createdAt: DateTime.parse(json['createdAt'] as String),
    updatedAt: DateTime.parse(json['updatedAt'] as String),
  );

  final String cameraId;
  final int branchId;
  final String branchName;
  final String internalIdentifier;
  final String displayName;
  final bool isPublic;
  final bool isActive;
  final int displayOrder;
  final CameraStreamProtocol protocol;
  final String providerCode;
  final String providerStreamReference;
  final DateTime createdAt;
  final DateTime updatedAt;
}

class SaveCameraRequest {
  const SaveCameraRequest({
    required this.branchId,
    required this.internalIdentifier,
    required this.displayName,
    required this.isPublic,
    required this.isActive,
    required this.displayOrder,
    required this.protocol,
    required this.providerCode,
    required this.providerStreamReference,
  });

  final int branchId;
  final String internalIdentifier;
  final String displayName;
  final bool isPublic;
  final bool isActive;
  final int displayOrder;
  final CameraStreamProtocol protocol;
  final String providerCode;
  final String providerStreamReference;

  Map<String, dynamic> toCreateJson() => {
    'branchId': branchId,
    'internalIdentifier': internalIdentifier.trim(),
    'displayName': displayName.trim(),
    'isPublic': isPublic,
    'displayOrder': displayOrder,
    'protocol': protocol.apiValue,
    'providerCode': providerCode.trim(),
    'providerStreamReference': providerStreamReference.trim(),
  };

  Map<String, dynamic> toUpdateJson() => {
    ...toCreateJson(),
    'isActive': isActive,
  };
}
