import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:yaml/yaml.dart';

void main() {
  test('OpenAPI starter parses and resolves local component references', () {
    final file = File('../Document/11_openapi_starter.yaml');
    expect(file.existsSync(), isTrue);

    final source = file.readAsStringSync();
    final document = loadYaml(source);

    expect(document, isA<YamlMap>());
    final root = document as YamlMap;
    expect(root['openapi'], '3.0.3');
    expect(root['paths'], isA<YamlMap>());
    expect(root['components'], isA<YamlMap>());

    final references = RegExp(r"\$ref:\s*'(#/components/[^']+)'")
        .allMatches(source)
        .map((match) => match.group(1)!)
        .toSet();

    for (final reference in references) {
      expect(
        _resolveReference(root, reference),
        isNotNull,
        reason: 'Missing local OpenAPI reference $reference',
      );
    }
  });

  test('OpenAPI paths declare every template parameter', () {
    final root = loadYaml(
      File('../Document/11_openapi_starter.yaml').readAsStringSync(),
    ) as YamlMap;
    final paths = root['paths'] as YamlMap;

    for (final entry in paths.entries) {
      final path = entry.key as String;
      final pathItem = entry.value as YamlMap;
      final templateNames = RegExp(r'\{([^}]+)\}')
          .allMatches(path)
          .map((match) => match.group(1)!)
          .toSet();
      if (templateNames.isEmpty) continue;

      final pathParameters = _parameterNames(root, pathItem['parameters']);
      for (final operation in pathItem.entries.where(
        (entry) => const {
          'get',
          'post',
          'put',
          'patch',
          'delete',
          'options',
          'head',
          'trace',
        }.contains(entry.key),
      )) {
        final operationMap = operation.value as YamlMap;
        final declaredNames = {
          ...pathParameters,
          ..._parameterNames(root, operationMap['parameters']),
        };
        expect(
          declaredNames,
          containsAll(templateNames),
          reason: '$path ${operation.key} is missing a path parameter',
        );
      }
    }
  });
}

Object? _resolveReference(YamlMap root, String reference) {
  Object? current = root;
  for (final segment in reference.substring(2).split('/')) {
    if (current is! YamlMap) return null;
    current = current[segment];
  }
  return current;
}

Set<String> _parameterNames(YamlMap root, Object? rawParameters) {
  if (rawParameters is! YamlList) return const {};

  return rawParameters
      .map((rawParameter) {
        if (rawParameter is! YamlMap) return null;
        final reference = rawParameter[r'$ref'];
        final parameter = reference is String
            ? _resolveReference(root, reference)
            : rawParameter;
        if (parameter is! YamlMap || parameter['in'] != 'path') return null;
        return parameter['name'] as String?;
      })
      .whereType<String>()
      .toSet();
}
