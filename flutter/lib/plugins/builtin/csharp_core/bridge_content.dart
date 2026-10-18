/// 桥接版内容管理：本地文件与 Modrinth 安装都落到 C#。
library;

import 'dart:async';

import '../../../bridge/core_bridge.dart';
import '../../../contracts/content.dart';

class BridgeContentManager implements ContentManager {
  BridgeContentManager(this._bridge);

  final CoreBridge _bridge;

  @override
  Future<VersionContent> detail(String versionId) async {
    final result = await _bridge.request(
      'version.detail',
      {'versionId': versionId},
    );
    return _contentFrom(result);
  }

  @override
  Future<List<ContentFile>> toggle(
    String versionId,
    ContentKind kind,
    String fileName,
    bool enabled,
  ) async {
    final result = await _bridge.request('version.toggleContent', {
      'versionId': versionId,
      'kind': kind.wire,
      'fileName': fileName,
      'enabled': enabled,
    });
    return _filesFrom(result);
  }

  @override
  Future<List<ContentFile>> delete(
    String versionId,
    ContentKind kind,
    String fileName,
  ) async {
    final result = await _bridge.request('version.deleteContent', {
      'versionId': versionId,
      'kind': kind.wire,
      'fileName': fileName,
    });
    return _filesFrom(result);
  }

  @override
  Future<void> openFolder(String versionId, ContentKind kind) async {
    await _bridge.request('version.openContentFolder', {
      'versionId': versionId,
      'kind': kind.wire,
    });
  }

  @override
  Future<List<ContentSearchHit>> search({
    required ContentKind kind,
    required String query,
    String? gameVersion,
    String? loader,
    int offset = 0,
    int limit = 20,
  }) async {
    final result = await _bridge.requestWithTimeout(
      'content.search',
      {
        'kind': kind.wire,
        'query': query,
        'gameVersion': ?gameVersion,
        'loader': ?loader,
        'offset': offset,
        'limit': limit,
      },
      const Duration(seconds: 60),
    );
    if (result is! Map<String, Object?>) return const [];
    final hits = result['hits'];
    if (hits is! List<Object?>) return const [];
    final out = <ContentSearchHit>[];
    for (final item in hits) {
      if (item is! Map<String, Object?>) continue;
      final projectId = item['projectId'] as String?;
      if (projectId == null || projectId.isEmpty) continue;
      final categories = item['displayCategories'];
      out.add(ContentSearchHit(
        projectId: projectId,
        slug: item['slug'] as String? ?? projectId,
        title: item['title'] as String? ?? projectId,
        description: item['description'] as String? ?? '',
        author: item['author'] as String? ?? '',
        downloads: (item['downloads'] as num?)?.toInt() ?? 0,
        iconUrl: item['iconUrl'] as String?,
        categories: categories is List<Object?>
            ? categories.whereType<String>().toList()
            : const [],
        webUrl: item['webUrl'] as String?,
      ));
    }
    return out;
  }

  @override
  Future<ContentInstallResult> install({
    required ContentKind kind,
    required String projectId,
    required String title,
    required String targetVersionId,
    void Function(String stage)? onProgress,
  }) async {
    final sub = _bridge.events.listen((event) {
      if (event.name != 'content.progress') return;
      onProgress?.call(event.params['stage'] as String? ?? '');
    });
    try {
      final result = await _bridge.requestWithTimeout(
        'content.install',
        {
          'kind': kind.wire,
          'projectId': projectId,
          'title': title,
          'targetVersionId': targetVersionId,
        },
        const Duration(minutes: 10),
      );
      return _installFrom(result);
    } finally {
      await sub.cancel();
    }
  }

  static VersionContent _contentFrom(Object? raw) {
    final map = raw is Map<String, Object?> ? raw : const <String, Object?>{};
    return VersionContent(
      versionId: map['versionId'] as String? ?? '',
      gameRoot: map['gameRoot'] as String? ?? '',
      isolated: map['isolated'] == true,
      mcVersion: map['mcVersion'] as String? ?? '',
      loader: map['loader'] as String? ?? 'vanilla',
      loaderVersion: map['loaderVersion'] as String?,
      lastPlayedAt: map['lastPlayedAt'] as String?,
      mods: _fileList(map['mods']),
      resourcepacks: _fileList(map['resourcepacks']),
      shaderpacks: _fileList(map['shaderpacks']),
    );
  }

  static List<ContentFile> _filesFrom(Object? raw) {
    if (raw is Map<String, Object?>) return _fileList(raw['files']);
    return const [];
  }

  static List<ContentFile> _fileList(Object? raw) {
    if (raw is! List<Object?>) return const [];
    final out = <ContentFile>[];
    for (final item in raw) {
      if (item is! Map<String, Object?>) continue;
      final fileName = item['fileName'] as String?;
      if (fileName == null || fileName.isEmpty) continue;
      out.add(ContentFile(
        fileName: fileName,
        displayName: item['displayName'] as String? ?? fileName,
        enabled: item['enabled'] != false,
        sizeBytes: (item['sizeBytes'] as num?)?.toInt() ?? 0,
        path: item['path'] as String? ?? '',
      ));
    }
    return out;
  }

  static ContentInstallResult _installFrom(Object? raw) {
    final map = raw is Map<String, Object?> ? raw : const <String, Object?>{};
    final installed = <InstalledContent>[];
    final installedRaw = map['installed'];
    if (installedRaw is List<Object?>) {
      for (final item in installedRaw) {
        if (item is! Map<String, Object?>) continue;
        installed.add(InstalledContent(
          projectId: item['projectId'] as String? ?? '',
          title: item['title'] as String? ?? '',
          fileName: item['fileName'] as String? ?? '',
          versionNumber: item['versionNumber'] as String?,
          path: item['path'] as String?,
        ));
      }
    }
    final missing = <MissingContent>[];
    final missingRaw = map['missing'];
    if (missingRaw is List<Object?>) {
      for (final item in missingRaw) {
        if (item is! Map<String, Object?>) continue;
        missing.add(MissingContent(
          projectId: item['projectId'] as String? ?? '',
          reason: item['reason'] as String? ?? '',
        ));
      }
    }
    return ContentInstallResult(installed: installed, missing: missing);
  }
}
