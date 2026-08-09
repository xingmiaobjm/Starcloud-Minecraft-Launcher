// 插件页：GUI 插件列表（view / replacement）+ 核心插件列表

import { useEffect, useState } from "preact/hooks";
import { listPlugins } from "../api";
import type { GuiPluginInfo } from "../types";

interface Props {
  plugins: GuiPluginInfo[];
}

export function Plugins({ plugins }: Props) {
  const [core, setCore] = useState<string[]>([]);

  useEffect(() => {
    listPlugins()
      .then(setCore)
      .catch(() => setCore([]));
  }, []);

  return (
    <div>
      <div class="page-title">插件</div>
      <div class="page-desc">GUI 插件可挂载视图或替换整个界面；核心插件在加载器初始化时自动加载</div>

      <div class="page-subtitle">GUI 插件</div>
      <div class="list" style="margin-bottom:24px">
        {plugins.length === 0 && (
          <div class="empty">暂无 GUI 插件，可将插件放入 plugins 目录</div>
        )}
        {plugins.map((p) => (
          <div class="list-item" key={p.id}>
            <div>
              <div class="primary">
                {p.title || p.name}
                <span class="tag">{p.gui_type === "view" ? "视图" : "替换"}</span>
                <span class="tag">{p.has_native ? "原生模块" : "纯前端"}</span>
                {p.compatible ? (
                  <span class="tag" style="color:var(--accent)">兼容</span>
                ) : (
                  <span class="tag" style="color:var(--danger)">不兼容</span>
                )}
              </div>
              <div class="secondary">
                {p.id} v{p.version}
                {p.reason ? ` · ${p.reason}` : ""}
              </div>
            </div>
          </div>
        ))}
      </div>

      <div class="page-subtitle">核心插件</div>
      <div class="list">
        {core.length === 0 && <div class="empty">无核心插件</div>}
        {core.map((id) => (
          <div class="list-item" key={id}>
            <div>
              <div class="primary">{id}</div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
