// VersionMenu：受控版本选择抽屉（仅已安装版本，卡片式列表）
// 触发按钮由外部 Home 提供；右键卡片弹出管理菜单（版本设置 / 删除版本）
// 删除结果通过 onSuccess / onError 回调交由父页面 Notice 展示

import { useState } from "preact/hooks";
import { SlidersHorizontal, Trash2 } from "lucide";
import { friendlyError } from "../errorMessages";
import { removeVersion } from "../api";
import type { VersionInfo } from "../types";
import { Drawer } from "./Drawer/Drawer";
import { ContextMenu, type ContextMenuItem } from "./ContextMenu";
import { ConfirmDialog } from "./ConfirmDialog";
import { Button } from "./Button/Button";
import { Tag } from "./Tag/Tag";
import { EmptyState } from "./EmptyState/EmptyState";
import { Spinner } from "./Spinner/Spinner";
import grassIcon from "../assets/grass-block.png";
import styles from "./VersionMenu.module.css";

interface Props {
  open: boolean;
  onClose: () => void;
  versions: VersionInfo[];
  selected: string;
  loading: boolean;
  onSelect: (id: string) => void;
  /** 打开版本设置抽屉 */
  onManageVersion: (id: string) => void;
  /** 版本列表发生变更（删除后刷新） */
  onVersionChanged: () => void;
  /** 跳转到下载页（空态行动按钮） */
  onGoDownload: () => void;
  /** 删除成功回调（父页面以 Notice 展示，3 秒自动消失） */
  onSuccess: (text: string) => void;
  /** 删除失败回调（父页面以 Notice 展示；参数为友好错误与原始错误） */
  onError: (text: string, raw?: string) => void;
}

export function VersionMenu({
  open,
  onClose,
  versions,
  selected,
  loading,
  onSelect,
  onManageVersion,
  onVersionChanged,
  onGoDownload,
  onSuccess,
  onError,
}: Props) {
  // 仅展示已安装版本
  const installed = versions.filter((v) => v.installed);
  // 右键菜单
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number; id: string } | null>(null);
  /** 删除确认对话框 */
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);

  if (!open) return null;

  const doDelete = async () => {
    const id = confirmDelete;
    if (!id) return;
    setConfirmDelete(null);
    try {
      await removeVersion(id);
      onVersionChanged();
      onSuccess(`版本「${id}」已删除`);
    } catch (e) {
      onError(friendlyError(e), String(e));
    }
  };

  const buildMenu = (id: string): ContextMenuItem[] => [
    {
      label: "版本设置",
      icon: SlidersHorizontal,
      onClick: () => onManageVersion(id),
    },
    {
      label: "删除版本",
      icon: Trash2,
      danger: true,
      onClick: () => {
        setConfirmDelete(id);
        setCtxMenu(null);
      },
    },
  ];

  return (
    <>
      <Drawer open={open} onClose={onClose} title="选择版本">
        <div class={styles.tip}>右键版本可管理设置，选择后自动关闭</div>
        <div class={styles.list}>
          {loading && (
            <div class={styles.loading}>
              <Spinner size="sm" />
              正在加载版本…
            </div>
          )}
          {!loading && installed.length === 0 && (
            <EmptyState
              title="尚无已安装版本"
              desc="前往下载页安装后，即可在此选择"
              action={
                <Button variant="standard" onClick={onGoDownload}>
                  前往下载
                </Button>
              }
            />
          )}
          {installed.map((v) => (
            <button
              key={v.id}
              type="button"
              class={`${styles.card} ${v.id === selected ? styles.active : ""}`}
              onClick={() => {
                onSelect(v.id);
                onClose();
              }}
              onContextMenu={(e) => {
                e.preventDefault();
                setCtxMenu({ x: e.clientX, y: e.clientY, id: v.id });
              }}
            >
              <span class={styles.nameLine}>
                <img class={styles.icon} src={grassIcon} alt="" draggable={false} />
                <span class={styles.name}>{v.id}</span>
              </span>
              <span class={styles.meta}>
                {v.version_type && <Tag variant="default">{v.version_type}</Tag>}
                {v.release_time && <span class={styles.date}>{v.release_time}</span>}
              </span>
            </button>
          ))}
        </div>
      </Drawer>
      {ctxMenu && (
        <ContextMenu
          x={ctxMenu.x}
          y={ctxMenu.y}
          items={buildMenu(ctxMenu.id)}
          onClose={() => setCtxMenu(null)}
        />
      )}
      <ConfirmDialog
        open={confirmDelete !== null}
        title="删除版本"
        message={`确定要删除版本「${confirmDelete ?? ""}」吗？此操作不可撤销。`}
        confirmLabel="删除"
        danger
        onConfirm={doDelete}
        onCancel={() => setConfirmDelete(null)}
      />
    </>
  );
}
