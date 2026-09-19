// 版本管理：仅展示已安装版本（右键可管理设置/删除）
// 下载新版本请前往「下载」页；安装完成后本页自动刷新

import { useCallback, useEffect, useState } from "preact/hooks";
import { SlidersHorizontal, Trash2 } from "lucide";
import { friendlyError } from "../errorMessages";
import { listInstalled, removeVersion } from "../api";
import { useInstallDone } from "../hooks/useEvents";
import { useNotice } from "../hooks/useNotice";
import { fmtBytes } from "../utils/format";
import { VersionSettingsDrawer } from "../components/VersionSettingsDrawer";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ContextMenu, type ContextMenuItem } from "../components/ContextMenu";
import { Button } from "../components/Button/Button";
import { Tag } from "../components/Tag/Tag";
import { MaybeNotice } from "../components/Notice/Notice";
import { EmptyState } from "../components/EmptyState/EmptyState";
import { Spinner } from "../components/Spinner/Spinner";
import grassIcon from "../assets/grass-block.png";
import type { InstalledVersion } from "../types";
import styles from "./Versions.module.css";

interface Props {
  /** 跳转到下载页 */
  onGoDownload: () => void;
}

export function Versions({ onGoDownload }: Props) {
  const [installed, setInstalled] = useState<InstalledVersion[]>([]);
  const [loading, setLoading] = useState(true);
  /** 列表加载失败原因（区别于空列表） */
  const [loadError, setLoadError] = useState<string | null>(null);
  const { notice, show, clear } = useNotice();
  // 右键菜单 + 版本设置抽屉
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number; id: string } | null>(null);
  const [settingsVersion, setSettingsVersion] = useState<string | null>(null);
  /** 删除确认对话框 */
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setInstalled(await listInstalled());
      setLoadError(null);
    } catch (e) {
      setLoadError(friendlyError(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    refresh();
  }, [refresh]);

  // 安装完成后自动刷新（下载页的安装同样生效）
  useInstallDone(undefined, refresh);

  const doRemove = async () => {
    const id = confirmDelete;
    if (!id) return;
    setConfirmDelete(null);
    try {
      await removeVersion(id);
      await refresh();
      show({ text: `版本「${id}」已删除`, kind: "ok" });
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    }
  };

  const buildMenu = (id: string): ContextMenuItem[] => [
    {
      label: "版本设置",
      icon: SlidersHorizontal,
      onClick: () => setSettingsVersion(id),
    },
    {
      label: "删除版本",
      icon: Trash2,
      danger: true,
      onClick: () => setConfirmDelete(id),
    },
  ];

  return (
    <div class="page">
      <header class="page-header">
        <h1 class="page-title">版本</h1>
        <p class="page-desc">
          管理本机已安装的版本。右键版本可修改启动设置或删除；需要新版本请前往下载页。
        </p>
      </header>

      <MaybeNotice notice={notice} onClose={clear} />

      <div class="page-toolbar">
        <span class={styles.count}>共 {installed.length} 个已安装版本</span>
        <Button variant="primary" className={styles.toolbarAction} onClick={onGoDownload}>
          前往下载
        </Button>
      </div>

      <div class={styles.list}>
        {loading && (
          <div class={styles.loading}>
            <Spinner size="sm" />
            正在读取已安装版本…
          </div>
        )}
        {!loading && loadError && (
          <EmptyState
            error
            title="加载失败"
            desc={loadError}
            action={
              <Button variant="standard" onClick={refresh}>
                重新加载
              </Button>
            }
          />
        )}
        {!loading && !loadError && installed.length === 0 && (
          <EmptyState
            title="暂无已安装版本"
            desc="前往下载页安装后，即可在此管理"
            action={
              <Button variant="standard" onClick={onGoDownload}>
                前往下载
              </Button>
            }
          />
        )}
        {installed.map((v) => (
          <div
            class={styles.item}
            key={v.id}
            onContextMenu={(e) => {
              e.preventDefault();
              setCtxMenu({ x: e.clientX, y: e.clientY, id: v.id });
            }}
          >
            <div class={styles.info}>
              <img class={styles.icon} src={grassIcon} alt="" draggable={false} />
              <span class={styles.name}>{v.id}</span>
              <Tag variant="success">已安装</Tag>
            </div>
            <div class={styles.rowActions}>
              <span class={styles.size}>{fmtBytes(v.size_bytes)}</span>
              <Button variant="subtle" size="sm" onClick={() => setSettingsVersion(v.id)}>
                版本设置
              </Button>
              <Button variant="danger" size="sm" onClick={() => setConfirmDelete(v.id)}>
                删除
              </Button>
            </div>
          </div>
        ))}
      </div>

      {ctxMenu && (
        <ContextMenu
          x={ctxMenu.x}
          y={ctxMenu.y}
          items={buildMenu(ctxMenu.id)}
          onClose={() => setCtxMenu(null)}
        />
      )}
      {settingsVersion && (
        <VersionSettingsDrawer
          open={Boolean(settingsVersion)}
          versionId={settingsVersion}
          onClose={() => setSettingsVersion(null)}
        />
      )}

      <ConfirmDialog
        open={confirmDelete !== null}
        title="删除版本"
        message={`确定要删除版本「${confirmDelete ?? ""}」吗？此操作不可撤销。`}
        confirmLabel="删除"
        danger
        onConfirm={doRemove}
        onCancel={() => setConfirmDelete(null)}
      />
    </div>
  );
}
