import 'package:file_saver/file_saver.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'admin_report_repository.dart';

final reportExportSaverProvider = Provider<ReportExportSaver>(
  (ref) => const FileSaverReportExportSaver(),
);

abstract interface class ReportExportSaver {
  Future<String> save(ReportExportFile file);
}

class FileSaverReportExportSaver implements ReportExportSaver {
  const FileSaverReportExportSaver();

  @override
  Future<String> save(ReportExportFile file) {
    final name = _fileNameParts(file.fileName);
    final contentType = file.contentType.split(';').first.trim();
    return FileSaver.instance.saveFile(
      name: name.baseName,
      bytes: file.bytes,
      fileExtension: name.extension,
      mimeType: MimeType.custom,
      customMimeType: contentType.isEmpty
          ? 'application/octet-stream'
          : contentType,
    );
  }
}

({String baseName, String extension}) _fileNameParts(String fileName) {
  final normalized = fileName.trim().replaceAll('\\', '/').split('/').last;
  if (normalized.isEmpty) {
    throw const FormatException('The export filename is empty.');
  }

  final extensionIndex = normalized.lastIndexOf('.');
  if (extensionIndex <= 0 || extensionIndex == normalized.length - 1) {
    return (baseName: normalized, extension: '');
  }
  return (
    baseName: normalized.substring(0, extensionIndex),
    extension: normalized.substring(extensionIndex + 1),
  );
}
