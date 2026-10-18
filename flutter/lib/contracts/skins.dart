/// 皮肤契约：本地皮肤库（离线/微软账户）与皮肤站衣柜（第三方账户）。
library;

/// 皮肤型号常量（与 C# SkinModels 对齐）。
abstract final class SkinModels {
  static const String auto = 'auto';
  static const String classic = 'classic';
  static const String slim = 'slim';
}

class SkinEntry {
  const SkinEntry({
    required this.id,
    required this.name,
    required this.model,
    required this.fileName,
    required this.path,
    this.source = 'file',
    this.sourceUrl,
    this.width = 0,
    this.height = 0,
    this.addedAt,
  });

  final String id;
  final String name;

  /// auto / classic / slim
  final String model;
  final String fileName;

  /// 本地 PNG 绝对路径（UI 直接 Image.file）。
  final String path;
  final String source;
  final String? sourceUrl;
  final int width;
  final int height;
  final String? addedAt;
}

/// 皮肤站衣柜里的一张皮肤。
class StationSkin {
  const StationSkin({
    required this.id,
    required this.name,
    required this.model,
    this.url,
    this.slim = false,
  });

  final String id;
  final String name;
  final String model;
  final String? url;
  final bool slim;
}

abstract class SkinManager {
  Future<List<SkinEntry>> list();

  Future<SkinEntry> importPath(String path, {String? name});

  Future<SkinEntry> importUrl(String url, {String? name});

  Future<SkinEntry> rename(String id, String name);

  Future<SkinEntry> setModel(String id, String model);

  Future<void> delete(String id);

  /// 指定/取消账户皮肤（skinId = null 表示取消）；model 是账户级型号覆盖。
  Future<void> assignToAccount(String uuid, String? skinId, {String? model});

  /// 第三方账户的皮肤站衣柜（需要该账户本次登录的凭证）。
  Future<List<StationSkin>> stationList(String accountUuid);

  /// 应用皮肤站上的一张皮肤；返回可展示的结果说明。
  Future<String> stationApply(String accountUuid, String skinId);
}
