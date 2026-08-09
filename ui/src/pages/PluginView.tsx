// 插件视图：通过 furry:// 自定义协议加载 view 插件前端

interface Props {
  pluginId: string;
}

export function PluginView({ pluginId }: Props) {
  const src = `furry://views/${encodeURIComponent(pluginId)}/index.html`;
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
