// 插件视图：通过 furry:// v2 协议加载 view 插件前端（具名视图）
// URL：furry://view/{plugin_id}/{view_id}/  — 根请求自动路由到声明入口（plugin.json 的 gui.views[].entry）

interface Props {
  pluginId: string;
  /** 视图 ID，默认 "main" */
  viewId?: string;
}

export function PluginView({ pluginId, viewId = "main" }: Props) {
  const src = `furry://view/${encodeURIComponent(pluginId)}/${encodeURIComponent(viewId)}/`;
  return (
    <div class="page">
      <iframe
        class="plugin-frame"
        src={src}
        title={pluginId}
        sandbox="allow-scripts allow-same-origin"
      />
    </div>
  );
}
