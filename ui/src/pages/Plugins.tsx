// 插件页：GUI 插件列表（view / shell / mixed）+ 安装（URL/本地 zip）+ 删除 + 启停
// furry:// v2：插件可声明多个具名视图（gui.views）与主壳（gui.shell），
// 视图可嵌入 page/drawer/home/settings/sidebar 等插槽。

import { useEffect, useState } from "preact/hooks";
import { getGuiPlugins, installPlugin, listPlugins, removePlugin, setPluginEnabled } from "../api";
import type { GuiPluginInfo } from "../types";

const SLOT_LABELS: Record<string, string> = {
  page: "页面",
  drawer: "抽屉",
  home: "首页",
  settings: "设置",
  sidebar: "侧栏",
  shell: "主壳",
};

function TypeTag({ p }: { p: GuiPluginInfo }) {
  const label = p.gui_type === "shell" ? "主壳" : p.gui_type === "mixed" ? "视图+主壳" : "视图";
  return <span class="tag">{label}</span>;
}

export function Plugins() {
  const [gui, setGui] = useState<GuiPluginInfo[]>([]);
  const [core, setCore] = useState<string[]>([]);
  const [source, setSource] = useState("");
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ text: string; err?: boolean } | null>(null);

  const refreshGui = async () => {
    setGui(await getGuiPlugins().catch(() => []));
  };

  useEffect(() => {
    refreshGui();
    listPlugins()
      .then(setCore)
      .catch(() => setCore([]));
  }, []);

  const handleInstall = async () => {
    const src = source.trim();
    if (!src) return;
    setBusy(true);
    setMsg(null);
    try {
      const info = await installPlugin(src);
      setMsg({ text: `已安装 ${info.title || info.name} v${info.version}` });
      setSource("");
      await refreshGui();
    } catch (e) {
      setMsg({ text: String(e), err: true });
    }
    setBusy(false);
  };

  const handleRemove = async (p: GuiPluginInfo) => {
    setMsg(null);
    try {
      await removePlugin(p.id);
      setMsg({ text: `已删除 ${p.title || p.name}` });
      await refreshGui();
    } catch (e) {
      setMsg({ text: String(e), err: true });
    }
  };

  const handleToggle = async (p: GuiPluginInfo) => {
    setMsg(null);
    try {
      await setPluginEnabled(p.id, !p.enabled);
      await refreshGui();
    } catch (e) {
      setMsg({ text: String(e), err: true });
    }
  };

  const shellPlugin = gui.find((p) => p.gui_type === "shell" && p.compatible);

  return (
    <div class="page">
      <div class="page-title">插件</div>
      <div class="page-desc">GUI 插件可声明多个视图挂载到任意界面位置，或整体接管主窗口；核心插件在加载器初始化时自动加载</div>

      {msg && <div class={`notice ${msg.err ? "err" : "ok"}`}>{msg.text}</div>}

      {shellPlugin && (
        <div class="notice">
          主壳插件「{shellPlugin.title || shellPlugin.name}」将接管整个主界面，重启启动器后生效；
          如需恢复默认界面，请将其禁用或删除。
        </div>
      )}

      <div class="card" style="margin-bottom:24px">
        <div class="section-title">安装 GUI 插件</div>
        <div style="display:flex;gap:10px;align-items:center">
          <input
            style="flex:1"
            placeholder="插件 zip 的 URL 或本地路径…"
            value={source}
            onInput={(e) => setSource((e.target as HTMLInputElement).value)}
            onKeyDown={(e) => e.key === "Enter" && handleInstall()}
          />
          <button class="btn-primary" onClick={handleInstall} disabled={busy || !source.trim()}>
            {busy ? "安装中…" : "安装"}
          </button>
        </div>
        <div class="hint" style="margin-top:6px">
          插件包须为 zip 格式，内含根级 plugin.json（清单规范见 docs/PLUGIN_DEV_GUIDE.md）
        </div>
      </div>

      <div class="page-subtitle">GUI 插件</div>
      <div class="list page-scroll" style="margin-bottom:24px">
        {gui.length === 0 && (
          <div class="empty">暂无 GUI 插件，可安装或放入 plugins 目录</div>
        )}
        {gui.map((p) => (
          <div class="list-item" key={p.id}>
            <div>
              <div class="primary">
                {p.title || p.name}
                <TypeTag p={p} />
                <span class="tag">{p.has_native ? "原生模块" : "纯前端"}</span>
                {p.compatible ? (
                  <span class="tag" style="color:var(--accent)">兼容</span>
                ) : (
                  <span class="tag" style="color:var(--danger)">不兼容</span>
                )}
                {!p.enabled && <span class="tag">已禁用</span>}
              </div>
              <div class="secondary">
                {p.id} v{p.version}
                {p.reason ? ` · ${p.reason}` : ""}
              </div>
              {(p.views?.length ?? 0) > 0 && (
                <div class="secondary" style="margin-top:6px">
                  视图：
                  {p.views?.map((v) => (
                    <span key={v.id} style="margin-right:10px">
                      {v.title || v.id}
                      {v.slots.length > 0 && (
                        <span style="color:var(--text-faint)">
                          {" "}
                          [{v.slots.map((s) => SLOT_LABELS[s] ?? s).join(" · ")}]
                        </span>
                      )}
                    </span>
                  ))}
                </div>
              )}
            </div>
            <div style="display:flex;gap:8px;align-items:center">
              <button class="btn" onClick={() => handleToggle(p)}>
                {p.enabled ? "禁用" : "启用"}
              </button>
              <button class="btn-danger" onClick={() => handleRemove(p)}>删除</button>
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
