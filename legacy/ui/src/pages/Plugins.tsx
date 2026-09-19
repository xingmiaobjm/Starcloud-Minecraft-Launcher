// 插件页：GUI 插件列表（view / shell / mixed）+ 安装（URL/本地 zip）+ 删除（二次确认）+ 启停
// furry:// v2：插件可声明多个具名视图（gui.views）与主壳（gui.shell），
// 视图可嵌入 page/drawer/home/settings/sidebar 等插槽。

import { useEffect, useState } from "preact/hooks";
import { AppWindow, Puzzle } from "lucide";
import {
  getGuiPlugins,
  installPlugin,
  listPlugins,
  removePlugin,
  setPluginEnabled,
} from "../api";
import { Button } from "../components/Button/Button";
import { Card } from "../components/Card/Card";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Input } from "../components/Input/Input";
import { MaybeNotice } from "../components/Notice/Notice";
import { Spinner } from "../components/Spinner/Spinner";
import { Switch } from "../components/Switch/Switch";
import { Tag } from "../components/Tag/Tag";
import { Icon } from "../components/Icon/Icon";
import { friendlyError } from "../errorMessages";
import { useNotice } from "../hooks/useNotice";
import type { GuiPluginInfo } from "../types";
import styles from "./Plugins.module.css";

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
  return <Tag variant={p.gui_type === "shell" ? "accent" : "default"}>{label}</Tag>;
}

/** 图标占位（Morphicons 线性图标）：主壳用窗口图形，其余用拼图 */
function PluginGlyph({ shell }: { shell: boolean }) {
  return <Icon icon={shell ? AppWindow : Puzzle} size={22} strokeWidth={1.5} />;
}

export function Plugins() {
  const [gui, setGui] = useState<GuiPluginInfo[]>([]);
  const [guiLoading, setGuiLoading] = useState(true);
  const [guiLoadError, setGuiLoadError] = useState<string | null>(null);
  const [core, setCore] = useState<string[]>([]);
  const [coreLoadError, setCoreLoadError] = useState<string | null>(null);
  const [source, setSource] = useState("");
  const [installing, setInstalling] = useState(false);
  // busyKey = `${操作}:${插件 id}`，只禁用正在操作的插件控件
  const [busyKeys, setBusyKeys] = useState<Set<string>>(new Set());
  const [pendingRemove, setPendingRemove] = useState<GuiPluginInfo | null>(null);

  const { notice, show, clear } = useNotice();

  const isBusy = (key: string) => busyKeys.has(key);
  const withBusy = async (key: string, fn: () => Promise<void>) => {
    setBusyKeys((s) => new Set(s).add(key));
    try {
      await fn();
    } finally {
      setBusyKeys((s) => {
        const next = new Set(s);
        next.delete(key);
        return next;
      });
    }
  };

  const refreshGui = async () => {
    setGuiLoading(true);
    try {
      setGui(await getGuiPlugins());
      setGuiLoadError(null);
    } catch (e) {
      setGuiLoadError(friendlyError(e));
    } finally {
      setGuiLoading(false);
    }
  };

  const refreshCore = async () => {
    try {
      setCore(await listPlugins());
      setCoreLoadError(null);
    } catch (e) {
      setCoreLoadError(friendlyError(e));
    }
  };

  useEffect(() => {
    refreshGui();
    refreshCore();
  }, []);

  const handleInstall = async () => {
    const src = source.trim();
    if (!src || installing) return;
    clear();
    setInstalling(true);
    try {
      const info = await installPlugin(src);
      show({ text: `已安装 ${info.title || info.name} v${info.version}`, kind: "ok" });
      setSource("");
      await refreshGui();
    } catch (e) {
      show({ text: `安装插件失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
    } finally {
      setInstalling(false);
    }
  };

  const handleToggle = async (p: GuiPluginInfo) => {
    clear();
    await withBusy(`toggle:${p.id}`, async () => {
      try {
        await setPluginEnabled(p.id, !p.enabled);
        await refreshGui();
      } catch (e) {
        show({
          text: `${p.enabled ? "禁用" : "启用"}插件失败：${friendlyError(e)}`,
          kind: "err",
          raw: String(e),
        });
      }
    });
  };

  const confirmRemove = async () => {
    if (!pendingRemove) return;
    const target = pendingRemove;
    const targetName = target.title || target.name;
    setPendingRemove(null);
    clear();
    await withBusy(`remove:${target.id}`, async () => {
      try {
        await removePlugin(target.id);
        show({ text: `已删除 ${targetName}`, kind: "ok" });
        await refreshGui();
      } catch (e) {
        show({ text: `删除插件失败：${friendlyError(e)}`, kind: "err", raw: String(e) });
      }
    });
  };

  const shellPlugin = gui.find((p) => p.gui_type === "shell" && p.compatible);

  return (
    <div class="page">
      <div class="page-header">
        <h1 class="page-title">插件</h1>
        <p class="page-desc">
          GUI 插件可声明多个视图挂载到界面各处，或整体接管主窗口；核心插件在加载器初始化时自动加载
        </p>
      </div>

      <MaybeNotice notice={notice} onClose={clear} />

      {shellPlugin && (
        <div class={styles.shellWarning} role="note">
          主壳插件「{shellPlugin.title || shellPlugin.name}」将接管整个主界面，重启启动器后生效；
          如需恢复默认界面，请将其禁用或删除。
        </div>
      )}

      <Card>
        <div class={styles.cardTitle}>安装 GUI 插件</div>
        <div class={styles.installRow}>
          <Input
            className={styles.installInput}
            aria-label="插件来源"
            maxLength={2048}
            placeholder="插件 zip 的 URL 或本地路径…"
            value={source}
            onInput={(e) => setSource((e.target as HTMLInputElement).value)}
            onKeyDown={(e) => e.key === "Enter" && handleInstall()}
          />
          <Button onClick={handleInstall} disabled={!source.trim()} busy={installing}>
            安装
          </Button>
        </div>
        <div class={styles.installHint}>
          插件包需为 zip 格式，包含 plugin.json 清单文件并通过兼容性校验，启用后才会加载
        </div>
      </Card>

      <h2 class="page-section-title">GUI 插件</h2>
      {guiLoadError ? (
        <EmptyState
          error
          title="加载失败"
          desc={guiLoadError}
          action={
            <Button variant="standard" onClick={refreshGui}>
              重新加载
            </Button>
          }
        />
      ) : guiLoading && gui.length === 0 ? (
        <div class={styles.listLoading} role="status">
          <Spinner />
          <span>正在加载插件列表…</span>
        </div>
      ) : gui.length === 0 ? (
        <EmptyState
          icon={<PluginGlyph shell={false} />}
          title="暂无 GUI 插件"
          desc="可安装 zip 插件包，或将其放入 plugins 目录"
        />
      ) : (
        <div class={styles.pluginList}>
          {gui.map((p) => {
            const name = p.title || p.name;
            return (
              <Card className={styles.pluginCard} key={p.id}>
                <div class={styles.pluginIcon} aria-hidden="true">
                  <PluginGlyph shell={p.gui_type === "shell"} />
                </div>
                <div class={styles.pluginBody}>
                  <div class={styles.pluginName}>
                    <span class={styles.pluginTitle}>{name}</span>
                    <Tag>{`v${p.version}`}</Tag>
                    <TypeTag p={p} />
                    <Tag variant={p.has_native ? "warning" : "default"}>
                      {p.has_native ? "增强版（含原生组件）" : "网页插件"}
                    </Tag>
                    <Tag variant={p.compatible ? "success" : "danger"}>
                      {p.compatible ? "兼容" : "不兼容"}
                    </Tag>
                    {!p.enabled && <Tag>已禁用</Tag>}
                  </div>
                  <div class={styles.pluginMeta}>
                    <span class={styles.pluginId}>{p.id}</span>
                    {p.reason && <span class={styles.pluginReason}>{p.reason}</span>}
                  </div>
                  {(p.views?.length ?? 0) > 0 && (
                    <div class={styles.pluginViews}>
                      <span class={styles.viewsLabel}>视图</span>
                      {p.views?.map((v) => (
                        <span class={styles.viewItem} key={v.id}>
                          {v.title || v.id}
                          {v.slots.length > 0 && (
                            <span class={styles.viewSlots}>
                              {v.slots.map((s) => SLOT_LABELS[s] ?? s).join(" · ")}
                            </span>
                          )}
                        </span>
                      ))}
                    </div>
                  )}
                </div>
                <div class={styles.pluginActions}>
                  <Switch
                    checked={p.enabled}
                    onChange={() => handleToggle(p)}
                    disabled={isBusy(`toggle:${p.id}`)}
                    aria-label={p.enabled ? `禁用 ${name}` : `启用 ${name}`}
                  />
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={() => setPendingRemove(p)}
                    disabled={isBusy(`toggle:${p.id}`)}
                    busy={isBusy(`remove:${p.id}`)}
                  >
                    删除
                  </Button>
                </div>
              </Card>
            );
          })}
        </div>
      )}

      <h2 class="page-section-title">核心插件</h2>
      {coreLoadError ? (
        <EmptyState
          error
          title="加载失败"
          desc={coreLoadError}
          action={
            <Button variant="standard" onClick={refreshCore}>
              重新加载
            </Button>
          }
        />
      ) : (
        <Card padding={false}>
          {core.length === 0 && <div class={styles.coreEmpty}>无核心插件</div>}
          {core.map((id) => (
            <div class={styles.coreItem} key={id}>
              <span class={styles.coreItemId}>{id}</span>
            </div>
          ))}
        </Card>
      )}

      <ConfirmDialog
        open={pendingRemove !== null}
        title="删除插件"
        message={`将删除插件「${
          pendingRemove ? pendingRemove.title || pendingRemove.name : ""
        }」并移除其文件，此操作无法撤销。`}
        confirmLabel="删除"
        danger
        onConfirm={confirmRemove}
        onCancel={() => setPendingRemove(null)}
      />
    </div>
  );
}
