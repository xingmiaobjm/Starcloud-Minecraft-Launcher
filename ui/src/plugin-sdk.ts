// GUI 插件前端 SDK - 供 furry:// 视图插件在 iframe 内使用
//
// 插件前端通过 Tauri 注入的 `window.__TAURI_INTERNALS__` 与后端通信；
// 本 SDK 仅做轻量封装，避免插件重复造轮子。

/**
 * 调用后端命令（带错误归一化）
 *
 * 在 iframe（furry:// 协议）内也可使用 —— Tauri 会将 invoke 桥接到宿主窗口。
 */
export async function invokeSafe<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
  // @ts-expect-error 从 Tauri 注入的全局对象取 invoke（运行时可用）
  const core = window.__TAURI_INTERNALS__;
  if (core && typeof core.invoke === "function") {
    return (await core.invoke(cmd, args)) as T;
  }
  // 开发兜底：直接经 @tauri-apps/api
  const { invoke } = await import("@tauri-apps/api/core");
  return (await invoke(cmd, args)) as T;
}

/**
 * 订阅后端事件（返回取消函数）
 */
export async function listenEvent<T>(
  event: string,
  handler: (payload: T) => void,
): Promise<() => void> {
  const { listen } = await import("@tauri-apps/api/event");
  return listen<T>(event, (e) => handler(e.payload));
}

/** 获取插件运行上下文（当前插件 ID） */
export function getContext(): { pluginId: string } {
  const m = /^furry:\/\/(?:views|replacement)\/([^/]+)\//.exec(window.location.href);
  return { pluginId: m?.[1] ?? "unknown" };
}
