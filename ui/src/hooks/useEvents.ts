// 共享 Tauri 事件订阅 hook：把"download-progress / install-done"样板收拢
// 各页面按 jobKey 过滤自己的任务；key 省略时接收全部进度

import { useEffect, useState } from "preact/hooks";
import { listen } from "@tauri-apps/api/event";
import type { InstallDonePayload, ProgressPayload } from "../types";

/** 订阅下载进度（可按 jobKey 过滤）。未在 Tauri 环境时安全返回 null。 */
export function useProgress(jobKey?: string): ProgressPayload | null {
  const [progress, setProgress] = useState<ProgressPayload | null>(null);

  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    const un = listen<ProgressPayload>("download-progress", (e) => {
      if (!jobKey || e.payload.version_id === jobKey) setProgress(e.payload);
    });
    un.then((u) => u());
    return () => {
      un.then((u) => u());
    };
  }, [jobKey]);

  return progress;
}

/** 订阅安装完成事件（可按 jobKey 过滤），触发回调 */
export function useInstallDone(jobKey: string | undefined, onDone: (p: InstallDonePayload) => void) {
  useEffect(() => {
    if (!("__TAURI_INTERNALS__" in window)) return;
    const un = listen<InstallDonePayload>("install-done", (e) => {
      if (!jobKey || e.payload.job_id === jobKey || e.payload.version_id === jobKey) onDone(e.payload);
    });
    un.then((u) => u());
    return () => {
      un.then((u) => u());
    };
  }, [jobKey, onDone]);
}
