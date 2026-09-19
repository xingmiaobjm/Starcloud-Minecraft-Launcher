// SchedulerDrawer：调度中心抽屉
// - 任务列表支持拖拽调整优先级（HTML5 DnD），详情抽屉内提供「上移 / 下移」键盘友好替代
// - 正在执行（running）的任务锁定，禁止拖拽，带视觉标识与尝试反馈
// - 删除支持三种入口：右键菜单 / 列表行悬停按钮 / 详情抽屉，均经确认对话框

import { useRef, useState } from "preact/hooks";
import {
  Box,
  ChevronRight,
  Coffee,
  Download,
  GripVertical,
  List,
  Lock,
  Package,
  Play,
  Trash2,
} from "lucide";
import type { TaskInfo } from "../types";
import { removeTask } from "../api";
import { friendlyError } from "../errorMessages";
import { Drawer } from "./Drawer/Drawer";
import { Icon } from "./Icon/Icon";
import { TaskDetailDrawer } from "./TaskDetailDrawer";
import { ContextMenu, type ContextMenuItem } from "./ContextMenu";
import { ConfirmDialog } from "./ConfirmDialog";
import { EmptyState } from "./EmptyState/EmptyState";
import { MaybeNotice } from "./Notice/Notice";
import { useNotice } from "../hooks/useNotice";
import { fmtBytes, fmtEta } from "../utils/format";
import styles from "./SchedulerDrawer.module.css";

/** 锁定提示气泡自动消失时长（毫秒） */
const LOCK_TIP_MS = 1600;
/** 拖拽松手后忽略 click 的时长（毫秒），避免拖动误触详情 */
const DRAG_CLICK_SUPPRESS_MS = 300;

interface Props {
  open: boolean;
  onClose: () => void;
  tasks: TaskInfo[];
  /** 拖拽重排后回调（新顺序 id 列表，调用方持久化优先级） */
  onReorder: (ids: string[]) => void;
}

/** 任务类型图标（Morphicons 线性图标） */
const KIND_ICONS: Record<TaskInfo["kind"], typeof Play> = {
  launch: Play,
  download: Package,
  fabric: Box,
  java: Coffee,
  install: Download,
};

function KindIcon({ kind }: { kind: TaskInfo["kind"] }) {
  return <Icon icon={KIND_ICONS[kind]} size={16} strokeWidth={1.6} />;
}

/** 锁定图标 */
function LockIcon() {
  return <Icon icon={Lock} size={12} strokeWidth={1.6} />;
}

/** 删除图标 */
function TrashIcon() {
  return <Icon icon={Trash2} size={14} strokeWidth={1.6} />;
}

/** 任务状态文案 */
function statusLabel(status: TaskInfo["status"]): string {
  switch (status) {
    case "running":
      return "执行中";
    case "error":
      return "失败";
    default:
      return "完成";
  }
}

/** 更新时间格式化：当日显示 HH:MM，跨日显示 MM-DD HH:MM */
function fmtTime(ms: number): string {
  const d = new Date(ms);
  const now = new Date();
  const hhmm = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  return d.toDateString() === now.toDateString()
    ? hhmm
    : `${d.getMonth() + 1}-${d.getDate()} ${hhmm}`;
}

export function SchedulerDrawer({ open, onClose, tasks, onReorder }: Props) {
  const [dragId, setDragId] = useState<string | null>(null);
  const [overId, setOverId] = useState<string | null>(null);
  const [shakeId, setShakeId] = useState<string | null>(null);
  const [lockTip, setLockTip] = useState<string | null>(null);
  const tipTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // 点击任务行打开详情面板（记录 id，实时从 tasks 取最新数据）
  const [detailId, setDetailId] = useState<string | null>(null);
  // 右键菜单（删除任务 / 打开详情）
  const [ctxMenu, setCtxMenu] = useState<{ x: number; y: number; task: TaskInfo } | null>(null);
  // 拖拽结束后短暂忽略 click（避免拖动松手误触详情）
  const dragEndAt = useRef(0);
  /** 删除确认对话框 */
  const [confirmDelete, setConfirmDelete] = useState<TaskInfo | null>(null);
  // 删除失败等提示（替代 alert）
  const { notice, show, clear } = useNotice();

  if (!open) return null;

  const detailTask = detailId ? tasks.find((t) => t.id === detailId) ?? null : null;
  const detailIndex = detailTask ? tasks.findIndex((t) => t.id === detailTask.id) : -1;

  /** 拖拽重排：把 dragId 移动到 targetId 之前 */
  const commitReorder = (dragId: string, targetId: string) => {
    const from = tasks.findIndex((t) => t.id === dragId);
    const to = tasks.findIndex((t) => t.id === targetId);
    if (from < 0 || to < 0 || from === to) return;
    const next = [...tasks];
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    onReorder(next.map((t) => t.id));
  };

  /** 上移/下移：与相邻任务交换位置（拖拽排序的键盘友好替代） */
  const moveTask = (t: TaskInfo, dir: -1 | 1) => {
    const from = tasks.findIndex((x) => x.id === t.id);
    const to = from + dir;
    if (from < 0 || to < 0 || to >= tasks.length) return;
    const next = [...tasks];
    [next[from], next[to]] = [next[to], next[from]];
    onReorder(next.map((x) => x.id));
  };

  /** 尝试拖拽锁定任务时的反馈：shake + 提示气泡 */
  const lockedFeedback = (id: string) => {
    setShakeId(id);
    setLockTip("执行中的任务不可调整优先级");
    if (tipTimer.current) clearTimeout(tipTimer.current);
    tipTimer.current = setTimeout(() => {
      setShakeId(null);
      setLockTip(null);
    }, LOCK_TIP_MS);
  };

  /** 删除任务（确认后调用后端，列表经 tasks-updated 事件自动刷新） */
  const handleDeleteTask = (t: TaskInfo) => {
    setConfirmDelete(t);
    setCtxMenu(null);
  };

  const doDelete = async () => {
    const t = confirmDelete;
    if (!t) return;
    setConfirmDelete(null);
    try {
      await removeTask(t.id);
    } catch (e) {
      show({ text: friendlyError(e), kind: "err", raw: String(e) });
    }
  };

  /** 任务行右键菜单项 */
  const buildTaskMenu = (t: TaskInfo): ContextMenuItem[] => [
    {
      label: "打开详情",
      icon: ChevronRight,
      onClick: () => setDetailId(t.id),
    },
    {
      label: "删除任务",
      icon: Trash2,
      danger: true,
      onClick: () => handleDeleteTask(t),
    },
  ];

  return (
    <>
      <Drawer open={open} onClose={onClose} title="调度中心" width="440px" closeId="scheduler-close">
        <MaybeNotice notice={notice} onClose={clear} className={styles.notice} />
        <div class={styles.tip}>拖动任务调整优先级 · 执行中的任务已锁定</div>
        {lockTip && (
          <div class={styles.tipWarn} role="status">
            {lockTip}
          </div>
        )}

        {tasks.length === 0 ? (
          <EmptyState
            icon={<Icon icon={List} size={22} strokeWidth={1.5} />}
            title="暂无任务"
            desc="安装、下载或启动游戏时，任务会显示在这里"
          />
        ) : (
          <div class={styles.list}>
            {tasks.map((t) => {
              const locked = t.status === "running";
              const cls = [
                styles.item,
                locked ? styles.locked : "",
                t.id === dragId ? styles.dragging : "",
                t.id === overId ? styles.dragOver : "",
                t.id === shakeId ? styles.shake : "",
              ]
                .filter(Boolean)
                .join(" ");
              return (
                <div
                  key={t.id}
                  class={cls}
                  draggable={!locked}
                  onDragStart={(e) => {
                    if (e.dataTransfer) {
                      e.dataTransfer.setData("text/plain", t.id);
                      e.dataTransfer.effectAllowed = "move";
                    }
                    setDragId(t.id);
                  }}
                  onDragOver={(e) => {
                    if (locked || !dragId) return;
                    e.preventDefault();
                    setOverId(t.id);
                  }}
                  onDrop={(e) => {
                    e.preventDefault();
                    const dragged = e.dataTransfer?.getData("text/plain") || dragId;
                    if (dragged && dragged !== t.id && !locked) {
                      commitReorder(dragged, t.id);
                    }
                    setDragId(null);
                    setOverId(null);
                  }}
                  onDragEnd={() => {
                    dragEndAt.current = Date.now();
                    setDragId(null);
                    setOverId(null);
                  }}
                  onMouseDown={() => {
                    if (locked) lockedFeedback(t.id);
                  }}
                  onContextMenu={(e) => {
                    if (dragId) return;
                    e.preventDefault();
                    setCtxMenu({ x: e.clientX, y: e.clientY, task: t });
                  }}
                  onClick={() => {
                    // 拖动松手后短暂时间内的 click 视为误触，不打开详情
                    if (Date.now() - dragEndAt.current < DRAG_CLICK_SUPPRESS_MS) return;
                    setDetailId(t.id);
                  }}
                  title={locked ? "执行中，不可调整优先级" : "点击查看详情 · 拖动调整优先级"}
                >
                  <span class={styles.grip} aria-hidden="true">
                    <Icon icon={GripVertical} size={14} strokeWidth={2} />
                  </span>
                  <span
                    class={`${styles.icon} ${
                      t.status === "running"
                        ? styles.iconRunning
                        : t.status === "error"
                          ? styles.iconError
                          : ""
                    }`}
                  >
                    <KindIcon kind={t.kind} />
                  </span>
                  <span class={styles.main}>
                    <span class={styles.title}>{t.title}</span>
                    {locked && t.progress != null ? (
                      <span class={styles.progress}>
                        <span class={styles.progressHead}>
                          <span class={styles.stage}>{t.stage || "下载中…"}</span>
                          <span class={styles.pct}>{t.progress.toFixed(1)}%</span>
                        </span>
                        <span class={styles.bar}>
                          <span class={styles.fill} style={{ width: `${t.progress}%` }} />
                        </span>
                        {(t.speed != null || t.eta != null) && (
                          <span class={styles.progressSub}>
                            {t.speed != null && t.speed > 0 && (
                              <span>速度 {fmtBytes(t.speed)}/s</span>
                            )}
                            <span>剩余 {fmtEta(t.eta)}</span>
                          </span>
                        )}
                      </span>
                    ) : (
                      t.detail && <span class={styles.detail}>{t.detail}</span>
                    )}
                  </span>
                  <span class={styles.right}>
                    <span class={`${styles.status} ${styles[t.status] ?? ""}`}>
                      {statusLabel(t.status)}
                    </span>
                    <span class={styles.time} title={`发起于 ${fmtTime(t.created_at)}`}>
                      {fmtTime(t.created_at)}
                    </span>
                    {locked && (
                      <span class={styles.lock}>
                        <LockIcon />
                      </span>
                    )}
                  </span>
                  {!locked && (
                    <button
                      type="button"
                      class={styles.rowDelete}
                      title="删除任务"
                      aria-label={`删除任务：${t.title}`}
                      onClick={(e) => {
                        e.stopPropagation();
                        handleDeleteTask(t);
                      }}
                    >
                      <TrashIcon />
                    </button>
                  )}
                  <span class={styles.chevron} aria-hidden="true">
                    <Icon icon={ChevronRight} size={12} strokeWidth={1.6} />
                  </span>
                </div>
              );
            })}
          </div>
        )}
      </Drawer>
      <TaskDetailDrawer
        task={detailTask}
        onClose={() => setDetailId(null)}
        onMove={moveTask}
        canMoveUp={detailIndex > 0 && detailTask?.status !== "running"}
        canMoveDown={
          detailIndex >= 0 && detailIndex < tasks.length - 1 && detailTask?.status !== "running"
        }
      />
      {ctxMenu && (
        <ContextMenu
          x={ctxMenu.x}
          y={ctxMenu.y}
          items={buildTaskMenu(ctxMenu.task)}
          onClose={() => setCtxMenu(null)}
        />
      )}
      <ConfirmDialog
        open={confirmDelete !== null}
        title="删除任务"
        message={`确定要删除任务「${confirmDelete?.title ?? ""}」吗？`}
        confirmLabel="删除"
        danger
        onConfirm={doDelete}
        onCancel={() => setConfirmDelete(null)}
      />
    </>
  );
}
