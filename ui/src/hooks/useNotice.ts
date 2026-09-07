// useNotice：页面级成功/错误提示的统一状态管理
// - 成功提示 3 秒自动消失，错误提示常驻需手动关闭（或写明 autoMs）
// - 组件渲染请配合 <Notice> 使用，错误正文可选中复制

import { useCallback, useEffect, useRef, useState } from "preact/hooks";

export interface NoticeState {
  text: string;
  kind: "ok" | "err" | "info";
  /** 原始错误（可展开复制），仅 err 时存在 */
  raw?: string;
  /** 点击重试的回调（err 时可选） */
  onRetry?: () => void;
}

export function useNotice(defaultAutoMs = 3000) {
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const timer = useRef<number | null>(null);

  const clear = useCallback(() => {
    if (timer.current) window.clearTimeout(timer.current);
    timer.current = null;
    setNotice(null);
  }, []);

  const show = useCallback(
    (n: NoticeState | string, autoMs: number | null = defaultAutoMs) => {
      if (timer.current) window.clearTimeout(timer.current);
      const state: NoticeState = typeof n === "string" ? { text: n, kind: "info" } : n;
      setNotice(state);
      if (autoMs !== null) {
        timer.current = window.setTimeout(() => setNotice(null), autoMs);
      }
    },
    [defaultAutoMs],
  );

  // 卸载时清理计时器
  useEffect(() => () => {
    if (timer.current) window.clearTimeout(timer.current);
  }, []);

  return { notice, show, clear, ok: show, err: show };
}
