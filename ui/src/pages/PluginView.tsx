// 插件视图：通过 furry:// v2 协议加载 view 插件前端（具名视图）
// URL：furry://view/{plugin_id}/{view_id}/ — 根请求自动路由到声明入口（plugin.json 的 gui.views[].entry）
// 加载中显示骨架；超时/出错显示失败态并支持重新挂载 iframe。

import { useEffect, useState } from "preact/hooks";
import { getGuiPlugins } from "../api";
import { Button } from "../components/Button/Button";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Spinner } from "../components/Spinner/Spinner";
import styles from "./PluginView.module.css";

/** 加载超时（毫秒）：超时仍未触发 load 视为加载失败（iframe 的 error 事件多数场景不触发） */
const LOAD_TIMEOUT_MS = 8000;

type LoadStatus = "loading" | "ok" | "error";

interface Props {
  pluginId: string;
  /** 视图 ID，默认 "main" */
  viewId?: string;
}

export function PluginView({ pluginId, viewId = "main" }: Props) {
  const [status, setStatus] = useState<LoadStatus>("loading");
  const [frameKey, setFrameKey] = useState(0);
  const [pluginName, setPluginName] = useState<string | null>(null);

  const src = `furry://view/${encodeURIComponent(pluginId)}/${encodeURIComponent(viewId)}/`;

  // 插件展示名（iframe 标题与失败文案用，避免向用户暴露技术 id）
  useEffect(() => {
    let cancelled = false;
    getGuiPlugins()
      .then((list) => {
        if (cancelled) return;
        const p = list.find((x) => x.id === pluginId);
        setPluginName(p ? p.title || p.name : null);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [pluginId]);

  // 每次挂载/重新加载都重置状态，并启动加载超时兜底
  useEffect(() => {
    setStatus("loading");
    const timer = window.setTimeout(() => {
      setStatus((s) => (s === "loading" ? "error" : s));
    }, LOAD_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [src, frameKey]);

  return (
    <div class={styles.view}>
      <iframe
        key={frameKey}
        class={styles.iframe}
        src={src}
        title={pluginName ? `${pluginName}（插件视图）` : "插件视图"}
        sandbox="allow-scripts allow-forms allow-popups"
        onLoad={() => setStatus("ok")}
        onError={() => setStatus("error")}
      />
      {status === "loading" && (
        <div class={styles.overlay}>
          <Spinner size="lg" />
          <span class={styles.overlayText}>正在加载插件视图…</span>
        </div>
      )}
      {status === "error" && (
        <div class={styles.overlay}>
          <EmptyState
            error
            title={pluginName ? `「${pluginName}」视图加载失败` : "插件视图加载失败"}
            desc="插件页面长时间未响应，可能是插件已被禁用、卸载或文件损坏"
            action={
              <Button variant="standard" onClick={() => setFrameKey((k) => k + 1)}>
                重新加载插件
              </Button>
            }
          />
        </div>
      )}
    </div>
  );
}
