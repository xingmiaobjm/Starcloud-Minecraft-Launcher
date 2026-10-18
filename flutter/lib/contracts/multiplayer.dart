/// 联机契约：把陶瓦联机与第三方联机 SDK 归一成同一套状态。
///
/// 实现方：`starcloud.csharp_core`（C# 的 MultiplayerService）。
/// 消费方：联机页。UI 不解析任何 SDK 协议，只渲染阶段与可用操作。
library;

enum MultiplayerPhase {
  idle,
  hostScanning,
  hostStarting,
  hostReady,
  guestConnecting,
  guestEntering,
  guestReady,
  failed,
}

class MultiplayerProviderInfo {
  const MultiplayerProviderInfo({
    required this.id,
    required this.displayName,
    required this.vendor,
    this.vendorUrl,
    this.description,
    this.builtIn = false,
  });

  final String id;
  final String displayName;
  final String vendor;
  final String? vendorUrl;
  final String? description;
  final bool builtIn;
}

class MultiplayerCoreStatus {
  const MultiplayerCoreStatus({
    required this.installed,
    this.version,
    required this.sessionRunning,
  });

  final bool installed;
  final String? version;
  final bool sessionRunning;
}

class MultiplayerState {
  const MultiplayerState({
    required this.phase,
    this.room,
    this.address,
    this.difficulty,
    this.players = const [],
    this.errorKind,
    this.error,
    this.isBusy = false,
  });

  final MultiplayerPhase phase;

  /// 房主侧：邀请码。
  final String? room;

  /// 加入方：虚拟局域网地址。
  final String? address;
  final String? difficulty;
  final List<String> players;
  final String? errorKind;
  final String? error;

  /// 处于"正在建房/进房"的中间态。
  final bool isBusy;
}

class MultiplayerSnapshot {
  const MultiplayerSnapshot({
    required this.providers,
    required this.activeId,
    required this.nickname,
    required this.nodeRegion,
    required this.core,
    required this.state,
    this.lastError,
  });

  final List<MultiplayerProviderInfo> providers;
  final String activeId;
  final String nickname;

  /// auto / cn / global
  final String nodeRegion;
  final MultiplayerCoreStatus core;
  final MultiplayerState state;
  final String? lastError;
}

class MultiplayerProgress {
  const MultiplayerProgress({
    required this.stage,
    required this.bytes,
    this.total,
    this.speedBps = 0,
  });

  final String stage;
  final int bytes;
  final int? total;
  final double speedBps;
}

abstract class MultiplayerControl {
  Future<MultiplayerSnapshot> snapshot();

  /// 状态推送（桥接侧轮询到变化时广播）。
  Stream<MultiplayerSnapshot> get changes;

  Future<MultiplayerSnapshot> selectProvider(String id);
  Future<MultiplayerSnapshot> setNickname(String name);
  Future<MultiplayerSnapshot> setNodeRegion(String region);

  /// 安装当前提供方的联机核心（进度走回调）。
  Future<void> installCore({void Function(MultiplayerProgress progress)? onProgress});

  Future<MultiplayerSnapshot> host();
  Future<MultiplayerSnapshot> join(String room);
  Future<MultiplayerSnapshot> idle();
  Future<MultiplayerSnapshot> stop();

  Future<String> logs();
  Future<List<String>> nodes();
}
